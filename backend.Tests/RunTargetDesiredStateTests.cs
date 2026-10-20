using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// IP-1 run-target protocol on the Studio side: edge-hosted declarations, the
/// desired state a host polls (claiming the AGT-3176 deploy job with a bounded
/// lease), skip markers for unreadable projects, explicit removals, first-claim
/// domain ownership per host, sanitized observed state, and the deploy-scoped,
/// host-bound routes.
/// </summary>
public sealed class RunTargetDesiredStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-target-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly string _first = "registry.example/app@sha256:" + new string('a', 64);
    private readonly string _second = "registry.example/app@sha256:" + new string('b', 64);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void EdgeDeclaration_CarriesHostingWithDefaults()
    {
        var service = Build();

        var stage = service.Declarations("demo")!.Single(item => item.Id == "stage");

        Assert.Equal("edge", stage.Destination);
        Assert.NotNull(stage.Hosting);
        Assert.Equal(["stage.example.com"], stage.Hosting!.Domains);
        Assert.Equal("acme", stage.Hosting.Edge);
        Assert.Equal("/healthz", stage.Hosting.HealthPath);
        Assert.Equal("info", stage.Hosting.Variables["LOG_LEVEL"]);
        Assert.Equal(new RunTargetResources(0.5, 256, 256), stage.Hosting.Resources);
        Assert.Null(service.Declarations("demo")!.Single(item => item.Id == "legacy").Hosting);
    }

    [Theory]
    [InlineData("domains", "[\"Bad Domain\"]")]
    [InlineData("domains", "[\"a.example.com\",\"a.example.com\"]")]
    [InlineData("edge", "\"tunnel\"")]
    [InlineData("healthPath", "\"/../admin\"")]
    [InlineData("variables", "{\"DATABASE_URL\":\"shadow\"}")]
    [InlineData("variables", "{\"DATABASE_URL_FILE\":\"/tmp/shadow\"}")]
    [InlineData("variables", "{\"lower\":\"x\"}")]
    [InlineData("resources", "{\"memoryMb\":8}")]
    public void EdgeDeclaration_RejectsUnsafeHostingFields(string field, string json)
    {
        var service = Build(stageOverride: (field, json));

        Assert.Throws<JsonException>(() => service.Declarations("demo"));
    }

    [Fact]
    public void DesiredState_ClaimsTheOpenJob_RenewsItsLeaseOnRepeatPolls_AndLegacyClaimSkipsIt()
    {
        var service = Build();
        Queue(service, "stage", _first);

        var first = Assert.Single(service.DesiredState("run-host").Environments);
        _clock.Advance(TimeSpan.FromMinutes(4));
        var second = Assert.Single(service.DesiredState("run-host").Environments);
        _clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(_first, first.Artifact);
        Assert.NotNull(first.JobId);
        Assert.Equal(first.LeaseToken, second.LeaseToken);
        Assert.Equal(["DATABASE_URL"], first.SecretRefs);
        Assert.Equal(["stage.example.com"], first.Domains);
        Assert.Null(service.Claim("run-host"));
        Assert.True(service.Report("demo", first.JobId!, new(first.LeaseToken!, true, "health-ok")));

        var settled = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal((_first, (string?)null), (settled.Artifact, settled.JobId));
        Assert.Empty(service.DesiredState("other-host").Environments);
    }

    [Fact]
    public void DesiredState_AfterFailedHealth_PointsAtTheRollbackJob_ThenThePreviousArtifact()
    {
        var service = Build();
        Deploy(service, _first, healthy: true);
        Deploy(service, _second, healthy: false);

        var rollback = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal(_first, rollback.Artifact);
        Assert.True(rollback.IsRollback);
        Assert.True(service.Report("demo", rollback.JobId!, new(rollback.LeaseToken!, true, "health-ok")));

        Assert.Equal(_first, Assert.Single(service.DesiredState("run-host").Environments).Artifact);
    }

    [Theory]
    [InlineData(true, false, "job")]
    [InlineData(false, true, "history")]
    [InlineData(false, false, "none")]
    public void Policy_OpenJobWins_ThenLastHealthy_ElseNothing(bool openJob, bool healthyHistory, string expected)
    {
        var declaration = Build().Declarations("demo")!.Single(item => item.Id == "stage");
        var now = _clock.GetUtcNow().UtcDateTime;
        var job = openJob ? new DeployJob("j", "demo", "stage", "run-host", _second, "container", "edge", null,
            [], 8080, _first, "claimed", now, now, "lease") : null;
        DeploymentHistoryItem[] history =
        [
            new(now, healthyHistory ? "healthy" : "failed", _first, null, null),
            new(now, "failed", _second, _first, "health-http-failed"),
        ];

        var spec = RunTargetDesiredStatePolicy.Project("demo", declaration, job, history);

        Assert.Equal(expected switch { "job" => _second, "history" => _first, _ => null }, spec.Artifact);
        Assert.Equal(openJob ? "lease" : null, spec.LeaseToken);
    }

    [Fact]
    public void ObservedState_KeepsOnlyTheClosedVocabulary_ForEnvironmentsOnThatHost()
    {
        var service = Build();
        var now = _clock.GetUtcNow().UtcDateTime;
        var events = Enumerable.Range(0, 30).Select(index => new RunTargetEvent(now, "crash-restart", _first))
            .Append(new RunTargetEvent(now.AddDays(1), "health-ok", "sensitive-value"))
            .Append(new RunTargetEvent(now, "raw-host-output", null)).ToArray();

        var accepted = service.RecordObserved("run-host", new(
        [
            new("demo", "stage", "crash-loop", _first, "not-a-digest", 7, now.AddHours(1), events),
            new("demo", "legacy", "running", _first, null, 0, now, []),
            new("demo", "stage-unknown", "running", _first, null, 0, now, []),
        ]));
        Assert.Equal(0, service.RecordObserved("other-host", new(
            [new("demo", "stage", "running", _second, null, 0, now, [])])));

        Assert.Equal(1, accepted);
        var observed = service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Observed;
        Assert.NotNull(observed);
        Assert.Equal(("crash-loop", _first, (string?)null, now), (observed!.State, observed.RunningArtifact,
            observed.PreviousArtifact, observed.ObservedAt));
        Assert.Equal(RunTargetDesiredStatePolicy.MaxEventsPerEnvironment, observed.Events.Count);
        Assert.Equal((now, "health-ok", (string?)null), (observed.Events[^1].At, observed.Events[^1].Kind, observed.Events[^1].Artifact));
        Assert.DoesNotContain("sensitive-value", JsonSerializer.Serialize(service.Read("demo")));
        Assert.DoesNotContain("raw-host-output", JsonSerializer.Serialize(service.Read("demo")));
    }

    [Fact]
    public async Task Routes_RequireTheDeployScope_AndTheCallersOwnHostId()
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
        (string Id, string Secret) Enroll(string name, string scope)
        {
            var enrolled = security.EnrollRunner(security.CreateEnrollment(
                new RunnerEnrollmentRequest(name, [scope], null, null)).Code);
            return (enrolled.Runner.Id, enrolled.Secret);
        }
        async Task<HttpStatusCode> Get(string secret, string host)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await client.GetAsync($"/api/deploy/run-targets/{host}/desired-state");
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/deploy/run-targets/run-host/desired-state")).StatusCode);
        var claimOnly = Enroll("run-host-claim", RunnerScopes.Claim);
        Assert.Equal(HttpStatusCode.Forbidden, await Get(claimOnly.Secret, claimOnly.Id));
        var (runnerId, deploy) = Enroll("run-host", RunnerScopes.Deploy);
        Assert.Equal(HttpStatusCode.Forbidden, await Get(deploy, "some-other-host"));
        Assert.Equal(HttpStatusCode.OK, await Get(deploy, runnerId));
        using var observed = await client.PostAsJsonAsync($"/api/deploy/run-targets/{runnerId}/observed-state",
            new RunTargetObservedReport([]));
        Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
    }

    [Fact]
    public void BrokenDeclaration_SkipsTheProject_AndSendsNoRemoval()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        var stage = Path.Combine(DeclarationDirectory, "stage.json");
        var valid = File.ReadAllText(stage);

        foreach (var broken in new[] { "{ \"id\": \"stage\", ", "{\"id\": 5}", "{\"name\": \"no id\"}" })
        {
            File.WriteAllText(stage, broken);
            _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 2);

            var desired = service.DesiredState("run-host");

            Assert.Empty(desired.Environments);
            Assert.Empty(desired.Removals);
            Assert.Equal(new RunTargetSkip("demo", "declaration-invalid"), Assert.Single(desired.Skipped));
        }
        File.WriteAllText(stage, valid);
        var restored = service.DesiredState("run-host");
        Assert.Equal("stage", Assert.Single(restored.Environments).EnvironmentId);
        Assert.Empty(restored.Removals);
        Assert.Empty(restored.Skipped);
    }

    [Fact]
    public void MissingCheckout_SkipsTheProject_AndSendsNoRemoval()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        var moved = _root + "-moved";
        Directory.Move(Repo, moved);
        try
        {
            _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 2);
            var desired = service.DesiredState("run-host");

            Assert.Empty(desired.Environments);
            Assert.Empty(desired.Removals);
            Assert.Contains(Assert.Single(desired.Skipped).Reason, new[] { "checkout-missing", "declarations-missing" });
        }
        finally
        {
            Directory.Move(moved, Repo);
        }
        Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Empty(service.DesiredState("run-host").Removals);
    }

    [Fact]
    public void UnreadableDeployState_SkipsTheProject()
    {
        var service = Build();
        var path = Path.Combine(_root, "workspace", "deployments", "demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var desired = service.DesiredState("run-host");

        Assert.Equal(new RunTargetSkip("demo", "state-unreadable"), Assert.Single(desired.Skipped));
        Assert.Empty(desired.Environments);
    }

    [Fact]
    public void DeletedEnvironment_IsRemovedAfterTheGrace_UntilTheHostConfirms()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        File.Delete(Path.Combine(DeclarationDirectory, "stage.json"));
        var now = _clock.GetUtcNow().UtcDateTime;
        RunTargetObservedReport Removed() => new([new("demo", "stage", "removed", null, null, 0, now, [])]);

        Assert.Empty(service.DesiredState("run-host").Removals);
        Assert.Equal(0, service.RecordObserved("run-host", Removed()));
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace);

        Assert.Equal(new RunTargetRemoval("demo", "stage"), Assert.Single(service.DesiredState("run-host").Removals));
        Assert.Single(service.DesiredState("run-host").Removals);
        Assert.Empty(service.DesiredState("other-host").Removals);
        Assert.Equal(0, service.RecordObserved("other-host", Removed()));
        Assert.Equal(1, service.RecordObserved("run-host", Removed()));
        Assert.Empty(service.DesiredState("run-host").Removals);
    }

    [Fact]
    public void EnvironmentMissingOnlyBriefly_IsNeverRemoved()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        var stage = Path.Combine(DeclarationDirectory, "stage.json");
        var valid = File.ReadAllText(stage);
        File.Delete(stage);
        Assert.Empty(service.DesiredState("run-host").Removals);

        File.WriteAllText(stage, valid);
        Assert.Single(service.DesiredState("run-host").Environments);
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 2);

        var desired = service.DesiredState("run-host");
        Assert.Empty(desired.Removals);
        Assert.Single(desired.Environments);
    }

    [Fact]
    public void Domains_StayWithTheFirstClaimOnTheHost_AcrossProjects()
    {
        var service = Build(extra: ("aaa", "aaa.example.com"));
        Assert.Equal(2, service.DesiredState("run-host").Environments.Count);
        // "aaa" sorts first and now declares the domain demo claimed first.
        WriteExtra("aaa", "stage.example.com");

        var desired = service.DesiredState("run-host");

        var demo = desired.Environments.Single(item => item.Project == "demo");
        var intruder = desired.Environments.Single(item => item.Project == "aaa");
        Assert.Equal(["stage.example.com"], demo.Domains);
        Assert.Empty(demo.RefusedDomains!);
        Assert.Empty(intruder.Domains);
        Assert.Equal(["stage.example.com"], intruder.RefusedDomains!);
        var claims = JsonSerializer.Deserialize<Dictionary<string, RunTargetDomainClaim>>(File.ReadAllText(
            Path.Combine(_root, "workspace", "deployments", "run-targets", "run-host.json")), JsonSerializerOptions.Web)!;
        Assert.Equal(("demo", "stage"), (claims["stage.example.com"].Project, claims["stage.example.com"].EnvironmentId));

        // The owner releases the domain by no longer declaring it.
        var stage = Path.Combine(DeclarationDirectory, "stage.json");
        File.WriteAllText(stage, File.ReadAllText(stage).Replace("\"stage.example.com\"", "\"demo.example.com\"", StringComparison.Ordinal));
        Assert.Equal(["stage.example.com"], service.DesiredState("run-host").Environments.Single(item => item.Project == "aaa").Domains);
    }

    [Fact]
    public void Domain_OfAnUnreadableProject_StaysReserved()
    {
        var service = Build(extra: ("aaa", "aaa.example.com"));
        service.DesiredState("run-host");
        File.WriteAllText(Path.Combine(DeclarationDirectory, "stage.json"), "{ broken");
        WriteExtra("aaa", "stage.example.com");

        var desired = service.DesiredState("run-host");

        Assert.Equal(["stage.example.com"], Assert.Single(desired.Environments).RefusedDomains!);
        Assert.Equal("demo", Assert.Single(desired.Skipped).Project);
    }

    [Fact]
    public void JobLease_RenewsWhilePolled_ButExpiresAfterTheBound_AndQueuesTheRollback()
    {
        var service = Build();
        Deploy(service, _first, healthy: true);
        Queue(service, "stage", _second);
        var claimed = Assert.Single(service.DesiredState("run-host").Environments);
        for (var minutes = 4; minutes < RunTargetDesiredStatePolicy.MaxJobLease.TotalMinutes; minutes += 4)
        {
            _clock.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal(claimed.LeaseToken, Assert.Single(service.DesiredState("run-host").Environments).LeaseToken);
        }
        _clock.Advance(TimeSpan.FromMinutes(4));

        var expired = Assert.Single(service.DesiredState("run-host").Environments);

        Assert.Equal((_first, (string?)null), (expired.Artifact, expired.JobId));
        Assert.False(service.Report("demo", claimed.JobId!, new(claimed.LeaseToken!, true, "health-ok")));
        var history = service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").History;
        Assert.Equal(("failed", _second, "deploy-step-failed"), (history[0].Status, history[0].Artifact, history[0].Evidence));
        var rollback = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal((_first, true), (rollback.Artifact, rollback.IsRollback));
        Assert.NotNull(rollback.JobId);
    }

    [Fact]
    public void DesiredState_SavesOnlyWhatChanged()
    {
        var service = Build();
        service.DesiredState("run-host");
        var state = Path.Combine(_root, "workspace", "deployments", "demo.json");
        var claims = Path.Combine(_root, "workspace", "deployments", "run-targets", "run-host.json");
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(state, written);
        File.SetLastWriteTimeUtc(claims, written);

        _clock.Advance(TimeSpan.FromMinutes(1));
        service.DesiredState("run-host");

        Assert.Equal(written, File.GetLastWriteTimeUtc(state));
        Assert.Equal(written, File.GetLastWriteTimeUtc(claims));
    }

    [Fact]
    public void CrashRollbackOnTheHost_QueuesOneRollbackJob()
    {
        var service = Build();
        Deploy(service, _first, healthy: true);
        Deploy(service, _second, healthy: true);
        var now = _clock.GetUtcNow().UtcDateTime;
        var report = new RunTargetObservedReport([new("demo", "stage", "running", _first, _second, 8, now,
            [new(now, "crash-rollback", _second), new(now, "rolled-back", _first)])]);

        Assert.Equal(1, service.RecordObserved("run-host", report));
        Assert.Equal(1, service.RecordObserved("run-host", report));

        var job = Assert.Single(service.Read("demo")!.Jobs, item => item.Status == "queued");
        Assert.Equal((_first, true, "crash-rollback"), (job.Artifact, job.IsRollback, job.RequestedBy));
        var desired = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal((_first, true), (desired.Artifact, desired.IsRollback));
    }

    [Fact]
    public void Policy_Placement_RemovesOnlyAfterTheGrace_AndOnlyOnItsHost()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var grace = RunTargetDesiredStatePolicy.RemovalGrace;
        RunTargetPlacement[] placements =
        [
            new("kept", "run-host", now.AddMinutes(-3)),
            new("new-gone", "run-host"),
            new("almost", "run-host", now - grace + TimeSpan.FromSeconds(1)),
            new("due", "run-host", now - grace),
            new("due", "other-host", now - grace),
        ];

        var decision = RunTargetDesiredStatePolicy.Place(placements, "run-host", ["kept", "added"], now);

        Assert.Equal(["due"], decision.Removals);
        Assert.Equal(
        [
            new("kept", "run-host"), new("new-gone", "run-host", now), placements[2], placements[3], placements[4],
            new("added", "run-host"),
        ], decision.Placements);
        Assert.False(RunTargetDesiredStatePolicy.AcceptsRemoval(new("kept", "run-host"), now));
        Assert.True(RunTargetDesiredStatePolicy.AcceptsRemoval(placements[3], now));
    }

    [Theory]
    [InlineData("queued", 0, 0, true, RunTargetLeaseAction.Claim)]
    [InlineData("claimed", 1, 1, true, RunTargetLeaseAction.Renew)]
    [InlineData("claimed", 6, 6, true, RunTargetLeaseAction.Claim)]
    [InlineData("claimed", 1, 29, true, RunTargetLeaseAction.Renew)]
    [InlineData("claimed", 1, 30, true, RunTargetLeaseAction.Expire)]
    [InlineData("claimed", 1, 1, false, RunTargetLeaseAction.Claim)]
    public void Policy_Lease_ClaimsRenewsOrExpires(string status, int claimedMinutesAgo, int firstClaimedMinutesAgo,
        bool hasLease, RunTargetLeaseAction expected)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var job = new DeployJob("j", "demo", "stage", "run-host", _first, "container", "edge", null, [], 8080, null,
            status, now, status == "claimed" ? now.AddMinutes(-claimedMinutesAgo) : null, hasLease ? "lease" : null,
            FirstClaimedAt: status == "claimed" ? now.AddMinutes(-firstClaimedMinutesAgo) : null);

        Assert.Equal(expected, RunTargetDesiredStatePolicy.Lease(job, now));
    }

    [Theory]
    [InlineData("demo", true, "demo")]
    [InlineData("demo", false, "aaa")]
    [InlineData("gone", true, "gone")]
    [InlineData("gone", false, "aaa")]
    public void Policy_ClaimDomains_FirstClaimWins_UnseenOwnersKeepWhileTheHostMayRouteThem(string owner,
        bool ownerStillDeclares, string expected)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, RunTargetDomainClaim>
            { ["app.example.com"] = new(owner, "prod", now.AddDays(-1)) };
        var hosted = new List<RunTargetDomainCandidate> { new("aaa", "prod", ["app.example.com"]) };
        if (owner == "demo") hosted.Add(new("demo", "prod", ownerStillDeclares ? ["app.example.com"] : []));

        var result = RunTargetDesiredStatePolicy.ClaimDomains(claims, hosted, _ => ownerStillDeclares, now);

        Assert.Equal(expected, result["app.example.com"].Project);
    }

    private string Repo => Path.Combine(_root, "repo");
    private string DeclarationDirectory => Path.Combine(Repo, "docs", "deployments", "environments");

    private void WriteExtra(string project, string domain)
    {
        var directory = Path.Combine(_root, "repo-" + project, "docs", "deployments", "environments");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "prod.json"), JsonSerializer.Serialize(new
        {
            id = "prod", name = "Prod", kind = "other", targetHostId = "run-host", address = $"https://{domain}",
            template = "container", destination = "edge", domains = new[] { domain },
        }));
    }

    private void Deploy(ProjectEnvironmentService service, string artifact, bool healthy)
    {
        Queue(service, "stage", artifact);
        var spec = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.True(service.Report("demo", spec.JobId!, new(spec.LeaseToken!, healthy, healthy ? "health-ok" : "health-http-failed")));
    }

    /// <summary>Seeds an open deploy job in the durable queue, as a release or an operator rollback would.</summary>
    private void Queue(ProjectEnvironmentService service, string environment, string artifact)
    {
        var path = Path.Combine(_root, "workspace", "deployments", "demo.json");
        var state = File.Exists(path)
            ? JsonSerializer.Deserialize<ProjectEnvironmentService.State>(File.ReadAllText(path), JsonSerializerOptions.Web)!
            : new ProjectEnvironmentService.State();
        state.Jobs.Add(new DeployJob(Guid.NewGuid().ToString("N"), "demo", environment, "run-host", artifact,
            "container", "edge", null, ["DATABASE_URL"], 8080,
            state.History.LastOrDefault(row => row.Item.Status is "healthy" or "rolled-back")?.Item.Artifact,
            "queued", _clock.GetUtcNow().UtcDateTime));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonSerializerOptions.Web));
    }

    private ProjectEnvironmentService Build((string Field, string Json)? stageOverride = null,
        params (string Project, string Domain)[] extra)
    {
        var repo = Path.Combine(_root, "repo");
        var watch = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(watch);
        if (!Directory.Exists(Path.Combine(repo, ".git")))
        {
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-b", "develop");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(repo, "initial"), "initial");
            Git(repo, "add", ".");
            Git(repo, "commit", "-m", "initial");
        }
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        var stage = new Dictionary<string, object>
        {
            ["id"] = "stage", ["name"] = "Stage", ["kind"] = "stage", ["targetHostId"] = "run-host",
            ["address"] = "https://stage.example.com", ["template"] = "container", ["destination"] = "edge",
            ["healthUrl"] = "https://stage.example.com/healthz", ["secretRefs"] = new[] { "DATABASE_URL" },
            ["domains"] = new[] { "stage.example.com" },
            ["variables"] = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" },
            ["resources"] = new { cpus = 0.5, memoryMb = 256 },
        };
        if (stageOverride is { } item) stage[item.Field] = JsonDocument.Parse(item.Json).RootElement;
        File.WriteAllText(Path.Combine(declarations, "stage.json"), JsonSerializer.Serialize(stage));
        File.WriteAllText(Path.Combine(declarations, "legacy.json"), JsonSerializer.Serialize(new
        {
            id = "legacy", name = "Legacy", kind = "other", targetHostId = "run-host",
            address = "http://127.0.0.1:8081", template = "container", destination = "8081",
        }));
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = repo, ["WatchPaths:0:RepositoryPath"] = repo,
        };
        foreach (var (index, (project, domain)) in extra.Index())
        {
            WriteExtra(project, domain);
            var extraWatch = Path.Combine(_root, "workspace", "projects", project);
            Directory.CreateDirectory(extraWatch);
            var extraRepo = Path.Combine(_root, "repo-" + project);
            values[$"WatchPaths:{index + 1}:Name"] = project;
            values[$"WatchPaths:{index + 1}:Path"] = extraWatch;
            values[$"WatchPaths:{index + 1}:RootPath"] = extraRepo;
            values[$"WatchPaths:{index + 1}:RepositoryPath"] = extraRepo;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return new(config, scanner, git, settings, _clock);
    }

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
