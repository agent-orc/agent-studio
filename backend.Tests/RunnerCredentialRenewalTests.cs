extern alias Runner;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.GitHubApp;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Protocol = AgentStudio.TaskServer.Contracts.RunnerCredentialRenewalProtocol;

namespace AgentStudio.Tests;

public sealed class RunnerCredentialRenewalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-renewal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
    private static readonly Uri Origin = new("https://studio.test");
    private Dictionary<string, string?> Settings => new()
    {
        ["TaskRepository"] = Path.Combine(_root, "workspace"),
        ["Security:Profile"] = "networked",
        ["Security:RunnerCredentialLifetimeMinutes"] = "3",
        ["AllowedHosts"] = "studio.test",
    };

    [Fact]
    public async Task Http_renewal_requires_window_and_proof_keeps_scope_and_records_refused_replay()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = Origin, AllowAutoRedirect = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrolled = Enroll(store);
        var (successor, proposal) = Candidate();
        var (_, competingProposal) = Candidate();

        using var early = await PostAsync(client, enrolled.Secret, proposal);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(Protocol.Scheduled, Assert.Single(early.Headers.GetValues(Protocol.StatusHeader)));
        _clock.Advance(TimeSpan.FromMinutes(2));

        using var staged = await PostAsync(client, enrolled.Secret, proposal);
        Assert.Equal(HttpStatusCode.NoContent, staged.StatusCode);
        Assert.Equal(proposal.SuccessorId, Assert.Single(staged.Headers.GetValues(Protocol.StagedHeader)));
        using var retry = await PostAsync(client, enrolled.Secret, proposal);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        using var competing = await PostAsync(client, enrolled.Secret, competingProposal);
        Assert.Equal(HttpStatusCode.Conflict, competing.StatusCode);
        Assert.NotNull(store.AuthenticateRunner(enrolled.Secret));
        Assert.Null(store.DescribeRunnerRenewal(Assert.Single(store.ListRunners()))!.LastRenewalAt);

        using var proof = await GetAsync(client, successor);
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        var principal = store.AuthenticateRunner(successor)!;
        Assert.Equal(enrolled.Runner.Id, principal.RunnerId);
        Assert.Equal(enrolled.Credential.Scopes.Order(), principal.Scopes.Order());
        Assert.Equal(enrolled.Runner.Projects, principal.Projects);
        using var predecessor = await GetAsync(client, enrolled.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, predecessor.StatusCode);
        Assert.Empty(store.ControlPlaneAudit.ReadNewest(IsReplay, 10));

        var forged = string.Join('.', enrolled.Secret.Split('.')[..2]) + "." + Guid.NewGuid().ToString("N");
        using var forgedReplay = await PostAsync(client, forged, competingProposal);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedReplay.StatusCode);
        Assert.Empty(store.ControlPlaneAudit.ReadNewest(IsReplay, 10));
        using var replay = await PostAsync(client, enrolled.Secret, competingProposal);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var repeated = await PostAsync(client, enrolled.Secret, proposal);
        Assert.Equal(HttpStatusCode.Unauthorized, repeated.StatusCode);
        var audit = Assert.Single(store.ControlPlaneAudit.ReadNewest(IsReplay, 10));
        Assert.Equal(enrolled.Runner.Id, audit.Actor);
        Assert.Equal("refused", audit.Outcome);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, store.DescribeRunnerRenewal(Assert.Single(store.ListRunners()))!.LastReplayAt);
        Assert.Equal(2, Assert.Single(store.ListRunners()).Credentials.Count);
    }

    [Theory]
    [InlineData("runner")]
    [InlineData("predecessor")]
    [InlineData("successor")]
    [InlineData("expired")]
    [InlineData("retired")]
    public void A_pending_successor_cannot_outlive_revocation_retirement_or_the_predecessors_expiry(string endedBy)
    {
        var store = NewStore(Settings);
        var enrolled = Enroll(store);
        var (successor, proposal) = Candidate();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var actor = store.AuthenticateRunner(enrolled.Secret)!;
        store.RenewRunnerCredential(actor, proposal);
        switch (endedBy)
        {
            case "runner": store.RevokeRunner(enrolled.Runner.Id); break;
            case "predecessor": store.RevokeCredential(enrolled.Runner.Id, actor.CredentialId); break;
            case "successor": store.RevokeCredential(enrolled.Runner.Id, proposal.SuccessorId!); break;
            case "retired": store.RequestRunnerDrain(enrolled.Runner.Id, retireAfterDrain: true); break;
            default: _clock.Advance(TimeSpan.FromMinutes(1)); break;
        }

        var reopened = NewStore(Settings);
        Assert.Null(reopened.AuthenticateRunner(enrolled.Secret));
        Assert.Null(reopened.AuthenticateRunner(successor));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            reopened.RenewRunnerCredential(actor, proposal)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Owner_policy_disables_new_renewals_without_disabling_authentication(bool installation)
    {
        var settings = Settings;
        if (installation) settings["Security:RunnerCredentialRenewalEnabled"] = "false";
        var store = NewStore(settings);
        var enrolled = Enroll(store);
        if (!installation) store.SetRunnerRenewal(enrolled.Runner.Id, enabled: false);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var reopened = NewStore(settings);
        var actor = reopened.AuthenticateRunner(enrolled.Secret)!;

        Assert.Equal(Protocol.Disabled, actor.CredentialRenewalState);
        Assert.Equal("renewal-disabled", Assert.Throws<SecurityOperationException>(() =>
            reopened.RenewRunnerCredential(actor, Candidate().Proposal)).Code);
        var status = reopened.DescribeRunnerRenewal(Assert.Single(reopened.ListRunners()))!;
        Assert.False(status.Enabled);
        Assert.Null(status.NextRenewalAt);
        Assert.Null(status.FailingSince);
    }

    [Fact]
    public void A_registered_successor_can_complete_after_an_owner_disables_new_renewals()
    {
        var store = NewStore(Settings);
        var enrolled = Enroll(store);
        var (successor, proposal) = Candidate();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var actor = store.AuthenticateRunner(enrolled.Secret)!;
        store.RenewRunnerCredential(actor, proposal);
        store.SetRunnerRenewal(enrolled.Runner.Id, enabled: false);

        var restarted = NewStore(Settings);
        Assert.Equal(Protocol.Due, restarted.AuthenticateRunner(enrolled.Secret)!.CredentialRenewalState);
        restarted.RenewRunnerCredential(actor, proposal);
        Assert.Equal(Protocol.Disabled, restarted.AuthenticateRunner(successor)!.CredentialRenewalState);
        Assert.Null(restarted.AuthenticateRunner(enrolled.Secret));
    }

    [Fact]
    public async Task A_host_restart_can_resume_a_registration_and_prove_on_its_next_request()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrolled = Enroll(store);
        var tokenFile = WriteToken(enrolled.Secret);
        var hostClock = new FakeTimeProvider(_clock.GetUtcNow().AddDays(-60));
        using var stopping = new CancellationTokenSource();
        using var firstHost = NewHost(tokenFile, new StopAfterRegistration(factory.Server.CreateHandler(), stopping), hostClock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstHost.GetAsync("/api/auth/runner", stopping.Token));
        Assert.True(File.ReadAllText(tokenFile).Trim() == enrolled.Secret);
        var pending = Assert.Single(Assert.Single(store.ListRunners()).Credentials, c => c.PredecessorId is not null);
        Assert.Null(pending.RenewalProvenAt);

        using (var restarted = NewHost(tokenFile, factory.Server.CreateHandler(), hostClock))
        using (var installed = await restarted.GetAsync("/api/auth/runner"))
            Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        Assert.True(Protocol.TryReadCredentialId(File.ReadAllText(tokenFile).Trim(), out var installedId));
        Assert.Equal(pending.Id, installedId);
        Assert.NotNull(store.AuthenticateRunner(enrolled.Secret));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.renew-*"));

        using var afterInstall = NewHost(tokenFile, factory.Server.CreateHandler(), hostClock);
        using var proof = await afterInstall.GetAsync("/api/auth/runner");
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        Assert.Null(store.AuthenticateRunner(enrolled.Secret));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, store.DescribeRunnerRenewal(Assert.Single(store.ListRunners()))!.LastRenewalAt);
    }

    [Fact]
    public async Task A_full_token_filesystem_preserves_the_live_file_and_reports_the_failure()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrolled = Enroll(store);
        var tokenFile = WriteToken(enrolled.Secret);
        var hostClock = new FakeTimeProvider(_clock.GetUtcNow().AddDays(60));
        var warnings = new List<string>();
        using var host = NewHost(tokenFile, factory.Server.CreateHandler(), hostClock, (file, content) =>
        {
            file.WriteByte(content[0]);
            throw new IOException("No space left on device", 28);
        }, warnings.Add);
        using (var early = await host.GetAsync("/api/auth/runner"))
            Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Empty(warnings);

        _clock.Advance(TimeSpan.FromMinutes(2));
        using var failed = await host.GetAsync("/api/auth/runner");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("automatic runner credential renewal is failing", Assert.Single(warnings));
        Assert.True(File.ReadAllText(tokenFile).Trim() == enrolled.Secret);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.renew-*"));
        var runner = Assert.Single(store.ListRunners());
        Assert.Single(runner.Credentials);
        var metadata = store.DescribeRunnerRenewal(runner)!;
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, metadata.FailingSince);
        Assert.Contains("could not write", metadata.FailureReason);

        var inventory = await factory.Services.GetRequiredService<CredentialInventoryService>().ReadAsync(false, default);
        var item = Assert.Single(inventory.Items, item => item.HostId == runner.Id);
        Assert.Equal(metadata, item.Renewal);
        Assert.Contains(inventory.Warnings, warning => warning.CredentialId == item.Id && warning.Subject == "expiry");

        using var recovered = NewHost(tokenFile, factory.Server.CreateHandler(), hostClock);
        using var install = await recovered.GetAsync("/api/auth/runner");
        using var proof = await recovered.GetAsync("/api/auth/runner");
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        Assert.Null(store.DescribeRunnerRenewal(Assert.Single(store.ListRunners()))!.FailingSince);
    }

    private AccessSecurityStore NewStore(Dictionary<string, string?> settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<AccessSecurityStore>.Instance, _clock);

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseTestSettings(Settings);
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock)));
        });

    private static (RunnerServiceIdentity Runner, RunnerCredential Credential, string Secret) Enroll(AccessSecurityStore store)
        => store.EnrollRunner(store.CreateEnrollment(new("renewal-role", [RunnerScopes.Claim, RunnerScopes.Logs],
            null, null, ["PROJ-001"])).Code);

    private static (string Secret, RunnerCredentialRenewalRequest Proposal) Candidate()
    {
        var secret = Protocol.NewCredential();
        Protocol.TryReadCredentialId(secret, out var id);
        return (secret, new(id, OpaqueSecretHash.Compute(secret)));
    }

    private static bool IsReplay(ControlPlaneAuditEntry entry) => entry.Operation == AccessSecurityStore.RunnerRenewalReplay;

    private string WriteToken(string secret)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "runner.token");
        File.WriteAllText(path, secret);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static HttpClient NewHost(string path, HttpMessageHandler transport, TimeProvider clock,
        Action<FileStream, byte[]>? writeStaged = null, Action<string>? warn = null)
        => new(new Runner::AgentRunner.CredentialExpiryWarningHandler(
            new Runner::AgentRunner.ReloadingPrincipalCredentialHandler(path,
                new Runner::AgentRunner.RunnerCredentialRenewalHandler(path, Origin, transport, clock, writeStaged)),
            warn ?? (_ => { }), clock, renews: true)) { BaseAddress = Origin };

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string bearer)
        => SendAsync(client, HttpMethod.Get, "/api/auth/runner", bearer, null);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string bearer, RunnerCredentialRenewalRequest proposal)
        => SendAsync(client, HttpMethod.Post, Protocol.Path, bearer, JsonContent.Create(proposal));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string bearer,
        HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private sealed class StopAfterRegistration(HttpMessageHandler inner, CancellationTokenSource stopping)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath == Protocol.Path && response.StatusCode == HttpStatusCode.NoContent)
                stopping.Cancel();
            return response;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
