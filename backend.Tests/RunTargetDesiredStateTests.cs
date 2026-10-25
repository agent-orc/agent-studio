using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// lease), skip markers for unreadable projects placed on the host, removals
/// that need an operator confirmation or a tombstone for production and stage,
/// first-claim domain ownership per host within operator-owned suffixes,
/// sanitized observed state, and the deploy-scoped, host-bound routes.
/// </summary>
public sealed class RunTargetDesiredStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-target-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly string _first = "registry.example/app@sha256:" + new string('a', 64);
    private readonly string _second = "registry.example/app@sha256:" + new string('b', 64);
    private ProjectSettingsService? _settings;

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

    [Fact]
    public void DesiredState_UsesApprovedHostingSnapshot_WhenDeclarationChangesBeforeDeployment()
    {
        var service = Build();
        var approved = service.Declarations("demo")!.Single(item => item.Id == "stage");
        var job = service.QueueDeployment("demo", approved, _first, "operator", rollback: false);
        Assert.NotNull(job);
        Assert.Equal(approved.Hosting, job.Hosting);
        ChangeStageHosting("domains", "[\"changed.example.com\"]");
        ChangeStageHosting("healthPath", "\"/changed\"");
        ChangeStageHosting("variables", "{\"LOG_LEVEL\":\"debug\"}");
        ChangeStageHosting("resources", "{\"cpus\":2,\"memoryMb\":1024}");

        var pending = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal(_first, pending.Artifact);
        Assert.Equal(["stage.example.com"], pending.Domains);
        Assert.Equal("/healthz", pending.HealthPath);
        Assert.Equal("info", pending.Variables["LOG_LEVEL"]);
        Assert.Equal(new RunTargetResources(0.5, 256, 256), pending.Resources);
        Assert.True(service.Report("demo", pending.JobId!, new(pending.LeaseToken!, true, "health-ok")));

        var settled = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal(["stage.example.com"], settled.Domains);
        Assert.Equal("/healthz", settled.HealthPath);
    }

    [Fact]
    public void Rollback_RestoresEarlierHostingSnapshot_AfterSettingsChange()
    {
        var service = Build();
        var first = service.Declarations("demo")!.Single(item => item.Id == "stage");
        Assert.NotNull(service.QueueDeployment("demo", first, _first, "operator", rollback: false));
        var firstSpec = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.True(service.Report("demo", firstSpec.JobId!, new(firstSpec.LeaseToken!, true, "health-ok")));

        ChangeStageHosting("domains", "[\"second.example.com\"]");
        ChangeStageHosting("variables", "{\"LOG_LEVEL\":\"warn\"}");
        var second = service.Declarations("demo")!.Single(item => item.Id == "stage");
        Assert.NotNull(service.QueueDeployment("demo", second, _second, "operator", rollback: false));
        var secondSpec = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.Equal(["second.example.com"], secondSpec.Domains);
        Assert.True(service.Report("demo", secondSpec.JobId!, new(secondSpec.LeaseToken!, true, "health-ok")));

        ChangeStageHosting("domains", "[\"third.example.com\"]");
        Assert.True(service.Rollback("demo", "stage", "operator"));
        var rollback = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.True(rollback.IsRollback);
        Assert.Equal(_first, rollback.Artifact);
        Assert.Equal(["stage.example.com"], rollback.Domains);
        Assert.Equal("info", rollback.Variables["LOG_LEVEL"]);
        Assert.True(service.Report("demo", rollback.JobId!, new(rollback.LeaseToken!, true, "health-ok")));
        Assert.Equal(["stage.example.com"], Assert.Single(service.DesiredState("run-host").Environments).Domains);
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

        // The run target cannot decide a removal or widen the domains it may claim.
        using var confirm = await client.PostAsJsonAsync("/api/projects/demo/environments/stage/run-target-removal/confirm",
            new RunTargetRemovalConfirmRequest(runnerId));
        using var suffixes = await client.PutAsJsonAsync("/api/projects/demo/run-target-domain-suffixes",
            new RunTargetDomainSuffixesRequest(["com.example"]));
        Assert.Contains(confirm.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains(suffixes.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
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
            // A host the project is not placed on does not hear about it.
            Assert.Empty(service.DesiredState("other-host").Skipped);
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
    public void UnreadableDeployState_SendsNoSpecAndNoRemoval()
    {
        var service = Build();
        var path = Path.Combine(_root, "workspace", "deployments", "demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var desired = service.DesiredState("run-host");

        // The placements are unknown, so no host is told; every host keeps its apps.
        Assert.Empty(desired.Skipped);
        Assert.Empty(desired.Environments);
        Assert.Empty(desired.Removals);
    }

    [Fact]
    public void DeletedStageDeclaration_StaysPendingUntilAnOperatorConfirms_ThenIsRemovedUntilTheHostConfirms()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        File.Delete(Path.Combine(DeclarationDirectory, "stage.json"));
        var now = _clock.GetUtcNow().UtcDateTime;
        RunTargetObservedReport Removed() => new([new("demo", "stage", "removed", null, null, 0, now, [])]);

        Assert.Empty(service.DesiredState("run-host").Removals);
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 10);
        Assert.Empty(service.DesiredState("run-host").Removals);
        Assert.Equal(0, service.RecordObserved("run-host", Removed()));
        var pending = Assert.Single(service.Read("demo")!.RunTargetRemovals!);
        Assert.Equal(("stage", "run-host", "stage", "pending-confirmation", now), (pending.EnvironmentId, pending.HostId,
            pending.Kind, pending.Status, pending.MissingSince));

        Assert.False(service.ConfirmRunTargetRemoval("demo", "stage", "other-host", "operator-1"));
        Assert.True(service.ConfirmRunTargetRemoval("demo", "stage", null, "operator-1"));
        Assert.False(service.ConfirmRunTargetRemoval("demo", "stage", null, "operator-2"));

        Assert.Equal(new RunTargetRemoval("demo", "stage"), Assert.Single(service.DesiredState("run-host").Removals));
        Assert.Equal("removing", Assert.Single(service.Read("demo")!.RunTargetRemovals!).Status);
        Assert.Empty(service.DesiredState("other-host").Removals);
        Assert.Equal(0, service.RecordObserved("other-host", Removed()));
        Assert.Equal(1, service.RecordObserved("run-host", Removed()));
        Assert.Empty(service.DesiredState("run-host").Removals);
        var record = Assert.Single(service.Read("demo")!.RunTargetRemovals!);
        Assert.Equal(("removed", "operator", "operator-1", _clock.GetUtcNow().UtcDateTime),
            (record.Status, record.Decision, record.Actor, record.RemovedAt));
    }

    [Fact]
    public void RenamedOrRetargetedStageDeclaration_RemovesNothingWithoutAConfirmation()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        var stage = Path.Combine(DeclarationDirectory, "stage.json");
        var valid = File.ReadAllText(stage);

        File.WriteAllText(stage, valid.Replace("\"id\":\"stage\"", "\"id\":\"stage-eu\"", StringComparison.Ordinal));
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 2);
        var renamed = service.DesiredState("run-host");
        Assert.Empty(renamed.Removals);
        Assert.Equal("stage-eu", Assert.Single(renamed.Environments).EnvironmentId);
        Assert.Equal(("stage", "pending-confirmation"), (Assert.Single(service.Read("demo")!.RunTargetRemovals!).EnvironmentId,
            service.Read("demo")!.RunTargetRemovals![0].Status));

        File.WriteAllText(stage, valid.Replace("\"targetHostId\":\"run-host\"", "\"targetHostId\":\"new-host\"", StringComparison.Ordinal));
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace * 2);
        Assert.Empty(service.DesiredState("run-host").Removals);
        Assert.Single(service.DesiredState("new-host").Environments);
    }

    [Fact]
    public void Tombstone_RemovesAProductionOrStageEnvironmentWithoutWaiting()
    {
        var service = Build();
        Assert.Single(service.DesiredState("run-host").Environments);
        File.WriteAllText(Path.Combine(DeclarationDirectory, "stage.json"), """{ "id": "stage", "removed": true }""");

        var desired = service.DesiredState("run-host");

        Assert.Empty(desired.Environments);
        Assert.Equal(new RunTargetRemoval("demo", "stage"), Assert.Single(desired.Removals));
        Assert.Equal("tombstone", Assert.Single(service.Read("demo")!.RunTargetRemovals!).Decision);
        Assert.DoesNotContain(service.Declarations("demo")!, item => item.Id == "stage");
    }

    [Fact]
    public void TombstoneForADeclaredId_IsAnInvalidDeclaration()
    {
        var service = Build();
        service.DesiredState("run-host");
        File.WriteAllText(Path.Combine(DeclarationDirectory, "stage-gone.json"), """{ "id": "stage", "removed": true }""");

        var desired = service.DesiredState("run-host");

        Assert.Empty(desired.Removals);
        Assert.Equal(new RunTargetSkip("demo", "declaration-invalid"), Assert.Single(desired.Skipped));
    }

    [Fact]
    public void DeletedOtherKindDeclaration_IsRemovedAfterTheGrace()
    {
        var service = Build();
        var preview = Path.Combine(DeclarationDirectory, "preview.json");
        File.WriteAllText(preview, JsonSerializer.Serialize(new
        {
            id = "preview", name = "Preview", kind = "other", targetHostId = "run-host",
            address = "https://preview.example.com", template = "container", destination = "edge",
            domains = new[] { "preview.example.com" },
        }));
        Assert.Equal(2, service.DesiredState("run-host").Environments.Count);
        File.Delete(preview);

        Assert.Empty(service.DesiredState("run-host").Removals);
        Assert.Equal("pending-grace", Assert.Single(service.Read("demo")!.RunTargetRemovals!).Status);
        _clock.Advance(RunTargetDesiredStatePolicy.RemovalGrace);

        Assert.Equal(new RunTargetRemoval("demo", "preview"), Assert.Single(service.DesiredState("run-host").Removals));
        Assert.Equal("grace", Assert.Single(service.Read("demo")!.RunTargetRemovals!).Decision);
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
    public void Domains_OutsideTheOperatorsSuffixes_AreRefused_AndNeverClaimed()
    {
        var service = Build();
        _settings!.SetRunTargetDomainSuffixes("demo", ["other.example.org"]);

        var spec = Assert.Single(service.DesiredState("run-host").Environments);

        Assert.Empty(spec.Domains);
        Assert.Equal(["stage.example.com"], spec.RefusedDomains!);
        Assert.False(File.Exists(Path.Combine(_root, "workspace", "deployments", "run-targets", "run-host.json")));

        _settings.SetRunTargetDomainSuffixes("demo", ["example.com"]);
        Assert.Equal(["stage.example.com"], Assert.Single(service.DesiredState("run-host").Environments).Domains);
    }

    [Theory]
    [InlineData(new[] { "Example.COM", "*.shop.example.org", "example.com" }, new[] { "example.com", "shop.example.org" })]
    [InlineData(new[] { "com" }, null)]
    [InlineData(new[] { "exa mple.com" }, null)]
    public void Policy_DomainSuffixes_AreNormalizedAndMatchWholeLabels(string[] input, string[]? expected)
    {
        Assert.Equal(expected, RunTargetDesiredStatePolicy.NormalizeDomainSuffixes(input));
        Assert.Equal(["example.com", "a.example.com"], RunTargetDesiredStatePolicy.AllowedDomains(
            ["example.com", "a.example.com", "badexample.com", "example.com.evil.org"], ["example.com"]));
        Assert.Empty(RunTargetDesiredStatePolicy.AllowedDomains(["example.com"], null));
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
    public void CrashRollbackStillListedByTheHost_DoesNotRollBackALaterRedeploy()
    {
        var service = Build();
        Deploy(service, _first, healthy: true);
        Deploy(service, _second, healthy: true);
        var crashedAt = _clock.GetUtcNow().UtcDateTime;
        RunTargetEvent[] crash = [new(crashedAt, "crash-rollback", _second), new(crashedAt, "rolled-back", _first)];
        service.RecordObserved("run-host", new([new("demo", "stage", "running", _first, _second, 8, crashedAt, crash)]));
        var rollback = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.True(service.Report("demo", rollback.JobId!, new(rollback.LeaseToken!, true, "health-ok")));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Deploy(service, _second, healthy: true);

        var redeployedAt = _clock.GetUtcNow().UtcDateTime;
        service.RecordObserved("run-host", new([new("demo", "stage", "running", _second, _first, 8, redeployedAt,
            [.. crash, new(redeployedAt, "swapped", _second)])]));

        Assert.DoesNotContain(service.Read("demo")!.Jobs, item => item.Status == "queued");

        _clock.Advance(TimeSpan.FromMinutes(5));
        var crashedAgainAt = _clock.GetUtcNow().UtcDateTime;
        service.RecordObserved("run-host", new([new("demo", "stage", "running", _first, _second, 13, crashedAgainAt,
            [.. crash, new(redeployedAt, "swapped", _second), new(crashedAgainAt, "crash-rollback", _second)])]));

        var job = Assert.Single(service.Read("demo")!.Jobs, item => item.Status == "queued");
        Assert.Equal((_first, true), (job.Artifact, job.IsRollback));
    }

    [Fact]
    public void Policy_Placement_RemovesProductionAndStageOnlyOnADecision_OtherAfterTheGrace_AndOnlyOnItsHost()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var grace = RunTargetDesiredStatePolicy.RemovalGrace;
        RunTargetPlacement[] placements =
        [
            new("kept", "run-host", now.AddMinutes(-3), "production"),
            new("new-gone", "run-host", Kind: "other"),
            new("almost", "run-host", now - grace + TimeSpan.FromSeconds(1), "other"),
            new("due", "run-host", now - grace, "other"),
            new("prod-gone", "run-host", now.AddDays(-3), "production"),
            new("legacy", "run-host", now.AddDays(-3)),
            new("confirmed", "run-host", now.AddMinutes(-1), "stage", "operator", "operator-1", now),
            new("tombstoned", "run-host", Kind: "production"),
            new("due", "other-host", now - grace, "other"),
        ];

        var decision = RunTargetDesiredStatePolicy.Place(placements, "run-host",
            [new("kept", "production"), new("added", "stage")], new HashSet<string> { "tombstoned" }, now);

        Assert.Equal(["due", "confirmed", "tombstoned"], decision.Removals);
        Assert.Equal(
        [
            new("kept", "run-host", Kind: "production"), new("new-gone", "run-host", now, "other"), placements[2],
            placements[3] with { RemovalDecision = "grace", RemovalDecidedAt = now }, placements[4], placements[5],
            placements[6], new("tombstoned", "run-host", now, "production", "tombstone", null, now), placements[8],
            new("added", "run-host", Kind: "stage"),
        ], decision.Placements);
        Assert.False(RunTargetDesiredStatePolicy.AcceptsRemoval(placements[4]));
        Assert.True(RunTargetDesiredStatePolicy.AcceptsRemoval(placements[6]));
        Assert.Null(RunTargetDesiredStatePolicy.ConfirmRemoval(placements[0] with { MissingSince = null }, "op", now));
        Assert.Equal(("operator", "op"), (RunTargetDesiredStatePolicy.ConfirmRemoval(placements[4], "op", now)!.RemovalDecision,
            RunTargetDesiredStatePolicy.ConfirmRemoval(placements[4], "op", now)!.RemovalActor));
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

    private void ChangeStageHosting(string field, string json)
    {
        var path = Path.Combine(DeclarationDirectory, "stage.json");
        var declaration = JsonNode.Parse(File.ReadAllText(path))!;
        declaration[field] = JsonNode.Parse(json);
        File.WriteAllText(path, declaration.ToJsonString());
    }

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
        // The operator allows the test domains; Domains_OutsideTheOperatorsSuffixes narrows them.
        settings.SetRunTargetDomainSuffixes("demo", ["example.com"]);
        foreach (var (project, _) in extra) settings.SetRunTargetDomainSuffixes(project, ["example.com"]);
        _settings = settings;
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
