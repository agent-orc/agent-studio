using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Clients;
using AgentStudio.Deploy;
using AgentStudio.Projects;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F4: a run host (runner.deploy) sends no claim heartbeat, so its deploy
/// claims, reports and run-target polls are its liveness. The Hosts view and
/// the Deploy area read that liveness from <see cref="RunHostDirectory"/>.
/// </summary>
public sealed class RunHostDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-host-directory-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Lists_only_run_hosts_and_derives_reachability_from_their_last_contact()
    {
        var (security, directory) = Build();
        var host = Enroll(security, "run-host-1", [RunnerScopes.Deploy], ["demo"]);
        Enroll(security, "coding-1", null, ["demo"]);

        var enrolled = Assert.Single(directory.List());
        Assert.Equal((host.Id, "run-host-1", RunHostStates.NoContact), (enrolled.Id, enrolled.Name, enrolled.State));
        Assert.Equal(["demo"], enrolled.Projects);

        Assert.True(directory.RecordContact(Principal(host), DeployHostContact.Poll));
        Assert.Equal(RunHostStates.Reachable, directory.List().Single().State);

        _clock.Advance(RunHostDirectory.ReachableWithin + TimeSpan.FromSeconds(1));
        Assert.Equal(RunHostStates.Unreachable, directory.List().Single().State);
    }

    [Fact]
    public void A_claim_and_a_report_are_saved_with_their_own_time()
    {
        var (security, directory) = Build();
        var host = Enroll(security, "run-host-1", [RunnerScopes.Deploy], ["demo"]);
        var claimedAt = _clock.GetUtcNow().UtcDateTime;
        directory.RecordContact(Principal(host), DeployHostContact.Claim);
        _clock.Advance(TimeSpan.FromSeconds(30));
        directory.RecordContact(Principal(host), DeployHostContact.Report);

        var saved = new RunHostDirectory(NewStore(), NewClients(), _clock).List().Single();
        Assert.Equal(claimedAt, saved.LastClaimAt);
        Assert.Equal(claimedAt.AddSeconds(30), saved.LastReportAt);
        Assert.Equal(saved.LastReportAt, saved.LastSeenAt);
    }

    [Fact]
    public void A_coding_credential_records_no_run_host_contact()
    {
        var (security, directory) = Build();
        var coding = Enroll(security, "coding-1", null, ["demo"]);

        Assert.False(directory.RecordContact(Principal(coding), DeployHostContact.Claim));
        Assert.Null(security.ListRunners().Single().LastClaimAt);
    }

    [Fact]
    public void Environments_name_their_run_host_and_mark_an_unknown_host_id_as_not_enrolled()
    {
        var (security, directory) = Build();
        var host = Enroll(security, "run-host-1", [RunnerScopes.Deploy], ["demo"]);
        directory.RecordContact(Principal(host), DeployHostContact.Poll);
        var response = new ProjectEnvironmentsResponse("demo",
            [View("stage", host.Id), View("production", host.Id), View("legacy", "web-vm")], [], [], []);

        var hosts = directory.Attach(response).RunHosts!;

        Assert.Equal([(host.Id, RunHostStates.Reachable), ("web-vm", RunHostStates.NotEnrolled)],
            hosts.Select(item => (item.Id, item.State)));
    }

    [Fact]
    public void An_environment_that_names_its_run_host_by_enrollment_name_shows_that_host()
    {
        var (security, directory) = Build();
        var host = Enroll(security, "run-host-1", [RunnerScopes.Deploy], ["demo"]);
        directory.RecordContact(Principal(host), DeployHostContact.Claim);
        var response = new ProjectEnvironmentsResponse("demo", [View("stage", "run-host-1")], [], [], []);

        var row = Assert.Single(directory.Attach(response).RunHosts!);

        Assert.Equal(("run-host-1", "run-host-1", RunHostStates.Reachable), (row.Id, row.Name, row.State));
        Assert.NotNull(row.LastClaimAt);
    }

    [Fact]
    public async Task Deploy_claims_and_run_target_polls_keep_the_run_host_reachable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_root, "network-workspace"),
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test") });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var clients = factory.Services.GetRequiredService<ClientIdentityStore>();
        var directory = factory.Services.GetRequiredService<RunHostDirectory>();
        var enrolled = security.EnrollRunner(security.CreateEnrollment(
            new RunnerEnrollmentRequest("run-host-1", [RunnerScopes.Deploy], null, null, [])).Code);
        clients.EnsureEnrolledRunner(enrolled.Runner.Id, enrolled.Runner.Name);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Secret);
        Assert.Equal(RunHostStates.NoContact, directory.List().Single().State);

        _clock.Advance(TimeSpan.FromMinutes(10));
        using var claim = await client.PostAsJsonAsync("/api/deploy/jobs/claim", new DeployClaimRequest(enrolled.Runner.Id));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var afterClaim = directory.List().Single();
        Assert.Equal(RunHostStates.Reachable, afterClaim.State);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, afterClaim.LastSeenAt);
        Assert.Null(afterClaim.LastClaimAt);

        _clock.Advance(TimeSpan.FromMinutes(10));
        using var poll = await client.GetAsync($"/api/deploy/run-targets/{enrolled.Runner.Id}/desired-state");
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal(RunHostStates.Reachable, directory.List().Single().State);

        // Another host's id is refused and records nothing.
        _clock.Advance(TimeSpan.FromMinutes(10));
        using var foreign = await client.PostAsJsonAsync("/api/deploy/jobs/claim", new DeployClaimRequest("other-host"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(RunHostStates.Unreachable, directory.List().Single().State);
    }

    private (AccessSecurityStore Security, RunHostDirectory Directory) Build()
    {
        var security = NewStore();
        return (security, new RunHostDirectory(security, NewClients(), _clock));
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();

    private AccessSecurityStore NewStore() => new(Configuration(), NullLogger<AccessSecurityStore>.Instance, _clock);

    private ClientIdentityStore NewClients() => new(Configuration(), NullLogger<ClientIdentityStore>.Instance);

    private static RunnerServiceIdentity Enroll(AccessSecurityStore security, string name,
        IReadOnlyList<string>? scopes, IReadOnlyList<string> projects)
        => security.EnrollRunner(security.CreateEnrollment(
            new RunnerEnrollmentRequest(name, scopes, null, null, projects)).Code).Runner;

    private static RunnerPrincipal Principal(RunnerServiceIdentity runner)
    {
        var credential = runner.Credentials.Single();
        return new RunnerPrincipal(runner.Id, runner.Name, credential.Id,
            new HashSet<string>(credential.Scopes, StringComparer.Ordinal));
    }

    private static ProjectEnvironmentView View(string id, string hostId)
        => new(new EnvironmentDeclaration(id, id, id, hostId, $"https://{id}.example.com", "container", "edge",
            null, "none", [], 8080), null, "unknown", null, []);
}
