using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Runner;
using AgentStudio.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// SM-9 in the monolith: a runner credential carries an explicit project set and
/// a 30-day default lifetime, and reads a card only while it holds that card's lease.
/// </summary>
public sealed class RunnerCredentialBindingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-binding-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;

    public RunnerCredentialBindingTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_new_runner_credential_expires_after_thirty_days_and_rotation_starts_a_new_lifetime()
    {
        var enrolled = Enroll("coding-host", ["alpha"]);

        Assert.Equal(Start.UtcDateTime.AddDays(30), enrolled.Credential.ExpiresAt);
        Assert.Equal(Start.UtcDateTime.AddDays(30), _store.AuthenticateRunner(enrolled.Secret)!.CredentialExpiresAt);
        _clock.Advance(TimeSpan.FromDays(20));
        var rotated = _store.RotateRunner(enrolled.Runner.Id, new RunnerRotateRequest(null, null));
        Assert.Equal(Start.UtcDateTime.AddDays(50), rotated.Credential.ExpiresAt);
        // Overlap: the previous credential keeps working until its own end or a revoke.
        Assert.NotNull(_store.AuthenticateRunner(enrolled.Secret));
        _clock.Advance(TimeSpan.FromDays(11));
        Assert.Null(_store.AuthenticateRunner(enrolled.Secret));
        Assert.NotNull(_store.AuthenticateRunner(rotated.Secret));

        var explicitEnd = Start.UtcDateTime.AddDays(40);
        var chosen = _store.EnrollRunner(_store.CreateEnrollment(
            new RunnerEnrollmentRequest("short-host", null, null, explicitEnd, ["alpha"])).Code);
        Assert.Equal(explicitEnd, chosen.Credential.ExpiresAt);
    }

    [Fact]
    public void An_enrollment_binds_the_projects_it_names_and_none_named_binds_none()
    {
        var bound = _store.AuthenticateRunner(Enroll("alpha-host", ["alpha"]).Secret)!;
        var unbound = _store.AuthenticateRunner(Enroll("idle-host", null).Secret)!;
        var everything = _store.AuthenticateRunner(Enroll("all-host", [RunnerProjectBinding.AllProjects]).Secret)!;

        Assert.True(RunnerProjectBinding.Allows(bound, "alpha"));
        Assert.False(RunnerProjectBinding.Allows(bound, "beta"));
        Assert.Empty(unbound.Projects);
        Assert.False(RunnerProjectBinding.Allows(unbound, "alpha"));
        Assert.True(RunnerProjectBinding.Allows(everything, "beta"));
        Assert.False(RunnerProjectBinding.Allows(bound, null));
        // No runner credential: the rest of the access model decides.
        Assert.True(RunnerProjectBinding.Allows((RunnerPrincipal?)null, "beta"));
    }

    [Fact]
    public void A_runner_enrolled_before_binding_keeps_all_projects_and_no_expiry_until_the_owner_acts()
    {
        var enrolled = Enroll("legacy-host", ["alpha"]);
        // The state file of an installation from before SM-9: no projects, no expiry.
        var path = Path.Combine(_store.SecurityFolder, "access-state.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        var runner = state["Runners"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == enrolled.Runner.Id)!;
        runner.AsObject().Remove("Projects");
        runner["Credentials"]![0]!["ExpiresAt"] = null;
        File.WriteAllText(path, state.ToJsonString());
        var upgraded = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        _clock.Advance(TimeSpan.FromDays(400));

        var principal = upgraded.AuthenticateRunner(enrolled.Secret)!;
        Assert.Null(principal.CredentialExpiresAt);
        Assert.Equal([RunnerProjectBinding.AllProjects], principal.Projects);
        Assert.Null(upgraded.ListRunners().Single().Projects);

        upgraded.UpdateRunnerProjects(enrolled.Runner.Id, ["beta"]);
        Assert.Equal(["beta"], upgraded.AuthenticateRunner(enrolled.Secret)!.Projects);
    }

    [Fact]
    public async Task A_runner_reads_a_card_only_while_it_holds_the_lease()
    {
        var holder = Enroll("holder-host", ["alpha"]);
        var other = Enroll("other-host", ["alpha"]);
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance, () => _clock.GetUtcNow().UtcDateTime);
        Assert.True(leases.TryAcquire(new RunLeaseAcquireRequest(
            "ALP-1", holder.Runner.Id, holder.Runner.Name, "holder-host", 1, "codex")).Granted);

        var held = await ReadCardAsync(leases, holder.Secret, "ALP-1");
        var foreign = await ReadCardAsync(leases, other.Secret, "ALP-1");
        var unleased = await ReadCardAsync(leases, holder.Secret, "ALP-2");

        Assert.True(held.Called);
        Assert.Equal((403, "runner-card-not-leased", false), (foreign.Status, foreign.Error, foreign.Called));
        Assert.Equal((403, "runner-card-not-leased", false), (unleased.Status, unleased.Error, unleased.Called));
        Assert.Equal(Start.UtcDateTime.AddDays(30).ToString("O"),
            held.Context.Response.Headers[AccessSecurityMiddleware.RunnerCredentialExpiresHeader].ToString());
    }

    private (RunnerServiceIdentity Runner, RunnerCredential Credential, string Secret) Enroll(string name, IReadOnlyList<string>? projects)
        => _store.EnrollRunner(_store.CreateEnrollment(new RunnerEnrollmentRequest(name, null, null, null, projects)).Code);

    private sealed record Outcome(HttpContext Context, int Status, string? Error, bool Called);

    /// <summary>A versioned card read as the alias middleware leaves it: the legacy path plus the original route.</summary>
    private async Task<Outcome> ReadCardAsync(RunLeaseService leases, string secret, string taskKey)
    {
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; },
            _config, _store, leases: leases, clock: _clock);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/tasks/" + taskKey;
        context.Items[AgentStudio.Host.StudioV1LegacyRouteAlias.OriginalPathItem] = $"/api/v1/projects/alpha/tasks/{taskKey}";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.test");
        context.Request.Headers.Authorization = new StringValues("Bearer " + secret);
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/tasks/{id}"), 0,
            new EndpointMetadataCollection([new HttpMethodMetadata(["GET"])]), "card"));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        string? error = null;
        if (context.Response.Body.Length > 0)
        {
            context.Response.Body.Position = 0;
            using var document = JsonDocument.Parse(context.Response.Body);
            error = document.RootElement.TryGetProperty("error", out var code) ? code.GetString() : null;
        }
        return new Outcome(context, context.Response.StatusCode, error, called);
    }
}
