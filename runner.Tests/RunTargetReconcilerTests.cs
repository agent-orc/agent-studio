using AgentRunner.RunTargets;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// IP-1 desired-state reconciliation against a fake container runtime: first
/// start, upgrade with swap, failed health check keeping the old revision,
/// rollback onto the kept revision, crash restarts with backoff, and secret
/// values that never reach a file or a log line.
/// </summary>
public sealed class RunTargetReconcilerTests : IDisposable
{
    private static readonly string First = "registry.example/app@sha256:" + new string('a', 64);
    private static readonly string Second = "registry.example/app@sha256:" + new string('b', 64);
    private static readonly string Third = "registry.example/app@sha256:" + new string('c', 64);

    private readonly TempWorkspace _workspace = new("run-target-reconciler");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeRunTargetDocker _docker = new();
    private readonly List<string> _log = [];
    private readonly string _secret = "db-" + Guid.NewGuid().ToString("N");
    private readonly RunTargetStateStore _store;
    private readonly RunTargetReconciler _reconciler;

    public RunTargetReconcilerTests()
    {
        _store = new RunTargetStateStore(_workspace.Directory("state"));
        _reconciler = new RunTargetReconciler(_docker,
            new FakeRunTargetSecrets(new() { ["database-url"] = _secret }), _store,
            new RunTargetReconcilerSettings("caddy:2-alpine", new RunTargetEdgeSettings("ops@example.com", false, []),
                null, HealthAttempts: 3),
            _clock, (_, _) => Task.CompletedTask, _log.Add);
    }

    public void Dispose() => _workspace.Dispose();

    private static string Key => RunTargetNames.AppKey("demo", "stage");

    private static RunTargetEnvironmentSpec Spec(string? artifact, string? jobId = "job-1", bool rollback = false,
        string[]? domains = null)
        => new("demo", "stage", "stage", artifact, 8080, "/healthz", domains ?? ["stage.example.com"], "acme",
            new Dictionary<string, string> { ["LOG_LEVEL"] = "info" }, ["database-url"],
            new RunTargetResources(0.5, 256, 128), jobId, jobId is null ? null : "lease-" + jobId, rollback);

    private Task<RunTargetCycleResult> Converge(params RunTargetEnvironmentSpec[] specs)
        => _reconciler.ReconcileAsync(new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime, specs), CancellationToken.None);

    private static string Slot(RunTargetEnvironmentSpec spec) => Key + "-" + RunTargetNames.Revision(spec);

    private string Caddyfile => File.ReadAllText(_store.CaddyfilePath);

    [Fact]
    public async Task Start_PullsStartsChecksHealthThroughTheEdge_RoutesTheDomain_AndReportsTheJob()
    {
        var spec = Spec(First);

        var result = await Converge(spec);

        Assert.Equal(RunTargetNames.EdgeContainer, _docker.Ups[0].Project);
        Assert.Contains("pull " + First, _docker.Calls);
        Assert.Contains($"connect {RunTargetNames.AppNetwork(Key)} {RunTargetNames.EdgeContainer}", _docker.Calls);
        Assert.Contains($"probe http://{Slot(spec)}:8080/healthz", _docker.Calls);
        Assert.Contains("stage.example.com {", Caddyfile);
        Assert.Contains($"reverse_proxy {Slot(spec)}:8080", Caddyfile);
        Assert.Equal(new RunTargetJobReport("demo", "job-1", "lease-job-1", true, RunTargetEvidence.HealthOk),
            Assert.Single(result.JobReports));
        var observed = Assert.Single(result.Observations);
        Assert.Equal(("running", First, (string?)null), (observed.State, observed.RunningArtifact, observed.PreviousArtifact));
        Assert.Equal(["pulled", "started", "health-ok", "swapped"], observed.Events.Select(item => item.Kind));
    }

    [Fact]
    public async Task UnchangedDesiredState_IsIdempotent()
    {
        await Converge(Spec(First));
        var calls = _docker.Calls.Count;

        var result = await Converge(Spec(First, jobId: null));

        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("up ") || call.StartsWith("pull "));
        Assert.Empty(result.JobReports);
        Assert.Equal("running", Assert.Single(result.Observations).State);
    }

    [Fact]
    public async Task Upgrade_SwapsAfterHealth_StopsButKeepsThePreviousRevision()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        await Converge(first);

        var result = await Converge(second);

        Assert.Contains($"reverse_proxy {Slot(second)}:8080", Caddyfile);
        Assert.Contains("stop " + Slot(first), _docker.Calls);
        Assert.DoesNotContain("down " + Slot(first), _docker.Calls);
        var observed = Assert.Single(result.Observations);
        Assert.Equal((Second, First), (observed.RunningArtifact, observed.PreviousArtifact));
        Assert.True(Assert.Single(result.JobReports).Healthy);
    }

    [Fact]
    public async Task FailedHealthCheck_KeepsTheOldRevisionServing_AndRemovesTheCandidate()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        await Converge(first);
        var routes = Caddyfile;
        _docker.UnhealthyProjects.Add(Slot(second));

        var result = await Converge(second);

        Assert.Equal(routes, Caddyfile);
        Assert.Equal(3, _docker.Calls.Count(call => call.StartsWith($"probe http://{Slot(second)}")));
        Assert.Contains("down " + Slot(second), _docker.Calls);
        Assert.DoesNotContain("stop " + Slot(first), _docker.Calls);
        Assert.Equal(new RunTargetJobReport("demo", "job-2", "lease-job-2", false, RunTargetEvidence.HealthFailed),
            Assert.Single(result.JobReports));
        var observed = Assert.Single(result.Observations);
        Assert.Equal(("running", First), (observed.State, observed.RunningArtifact));
        Assert.Equal("health-failed", observed.Events[^1].Kind);
    }

    [Fact]
    public async Task FailedHealthCheck_ThenStudioRollbackJob_ConvergesWithoutTouchingTheRunningRevision()
    {
        await Converge(Spec(First));
        _docker.UnhealthyProjects.Add(Slot(Spec(Second)));
        await Converge(Spec(Second, jobId: "job-2"));
        var calls = _docker.Calls.Count;

        // Studio queues the previously healthy artifact after the failed report.
        var result = await Converge(Spec(First, jobId: "rollback-1", rollback: true));

        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("up ") || call.StartsWith("stop "));
        Assert.Equal(new RunTargetJobReport("demo", "rollback-1", "lease-rollback-1", true, RunTargetEvidence.HealthOk),
            Assert.Single(result.JobReports));
    }

    [Fact]
    public async Task Rollback_RestartsTheKeptRevision_WithoutANewSlot_AndDropsTheOneBeforeIt()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        var third = Spec(Third, jobId: "job-3");
        await Converge(first);
        await Converge(second);
        await Converge(third);
        Assert.Contains("down " + Slot(first), _docker.Calls);

        var result = await Converge(Spec(Second, jobId: "rollback-1", rollback: true));

        Assert.Equal(Slot(second), _docker.Ups[^1].Project);
        Assert.Contains($"reverse_proxy {Slot(second)}:8080", Caddyfile);
        Assert.Contains("stop " + Slot(third), _docker.Calls);
        var observed = Assert.Single(result.Observations);
        Assert.Equal((Second, Third), (observed.RunningArtifact, observed.PreviousArtifact));
        Assert.Equal("rolled-back", observed.Events[^1].Kind);
        Assert.True(Assert.Single(result.JobReports).Healthy);
    }

    [Fact]
    public async Task CrashLoop_RestartsWithBackoff_ReportsCrashLoop_AndResetsAfterStableRun()
    {
        var spec = Spec(First, jobId: null);
        await Converge(spec);
        var slot = Slot(spec);
        int Restarts() => _docker.Ups.Count(up => up.Project == slot) - 1;

        _docker.Crash(slot);
        Assert.Equal("starting", Assert.Single((await Converge(spec)).Observations).State);
        Assert.Equal(1, Restarts());

        // Inside the 10 s backoff nothing restarts.
        _docker.Crash(slot);
        _clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("unhealthy", Assert.Single((await Converge(spec)).Observations).State);
        Assert.Equal(1, Restarts());

        RunTargetCycleResult? last = null;
        foreach (var wait in new[] { 10, 20, 40, 80 })
        {
            _clock.Advance(TimeSpan.FromSeconds(wait));
            _docker.Crash(slot);
            last = await Converge(spec);
        }
        Assert.Equal(5, Restarts());
        var looping = Assert.Single(last!.Observations);
        Assert.Equal("crash-loop", looping.State);
        Assert.Single(looping.Events, item => item.Kind == "crash-loop");
        Assert.Equal(RunTargetCrashPolicy.Backoff(5), TimeSpan.FromSeconds(160));

        // The last restart came up; after two quiet minutes the attempts reset.
        _clock.Advance(TimeSpan.FromSeconds(160));
        _docker.Containers[slot] = new(RunTargetContainerState.Running, 3);
        _clock.Advance(RunTargetCrashPolicy.StableAfter);
        Assert.Equal("running", Assert.Single((await Converge(spec)).Observations).State);
        _docker.Crash(slot);
        await Converge(spec);
        Assert.Equal(6, Restarts());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + RunTargetCrashPolicy.Backoff(1),
            _store.Load().Apps[Key].NextRestartAt);
    }

    [Fact]
    public async Task SecretValues_ReachOnlyTheComposeProcessEnvironment_NeverAFileOrALogLine()
    {
        await Converge(Spec(First));
        var up = _docker.Ups.Single(item => item.Project != RunTargetNames.EdgeContainer);
        Assert.Equal(_secret, up.Environment["DATABASE_URL"]);
        Assert.Contains("- \"DATABASE_URL\"", up.ComposeText);

        // A failing start echoes the value in docker's error output.
        _docker.FailUpWith = $"invalid value DATABASE_URL={_secret}";
        var result = await Converge(Spec(Second, jobId: "job-2"));

        Assert.False(Assert.Single(result.JobReports).Healthy);
        Assert.Contains(_log, line => line.Contains("[redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, line => line.Contains(_secret, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(_store.Directory, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(_secret, File.ReadAllText(file));
        Assert.DoesNotContain(_secret, System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingSecretReference_FailsTheJobBeforeAnythingStarts()
    {
        var spec = Spec(First) with { SecretRefs = ["database-url", "stripe-key"] };

        var result = await Converge(spec);

        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("pull "));
        Assert.Equal(new RunTargetJobReport("demo", "job-1", "lease-job-1", false, RunTargetEvidence.StepFailed),
            Assert.Single(result.JobReports));
        Assert.Contains(_log, line => line.Contains("stripe-key", StringComparison.Ordinal));
        Assert.Equal("failed", Assert.Single(result.Observations).State);
    }

    [Fact]
    public async Task InvalidSpec_IsRefusedOnTheHost()
    {
        var result = await Converge(Spec("app:latest"));

        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("pull "));
        Assert.Equal(RunTargetEvidence.StepFailed, Assert.Single(result.JobReports).Evidence);
    }

    [Fact]
    public async Task DomainChange_ReroutesWithoutRestartingTheApp()
    {
        await Converge(Spec(First));
        var ups = _docker.Ups.Count;

        await Converge(Spec(First, jobId: null, domains: ["app.example.com"]));

        Assert.Equal(ups, _docker.Ups.Count);
        Assert.Contains("app.example.com {", Caddyfile);
        Assert.DoesNotContain("stage.example.com", Caddyfile);
        Assert.Equal(2, _docker.Reloads);
    }

    [Fact]
    public async Task EnvironmentLeavingTheHost_IsRemoved_AndItsRouteDropped()
    {
        var spec = Spec(First);
        await Converge(spec);

        var result = await Converge();

        Assert.Contains("down " + Slot(spec), _docker.Calls);
        Assert.Empty(result.Observations);
        Assert.Empty(_store.Load().Apps);
        Assert.DoesNotContain("reverse_proxy", Caddyfile);
    }

    [Fact]
    public async Task EdgeUnavailable_LeavesJobsOpen_AndStartsNothing()
    {
        _docker.EdgeFails = true;

        var result = await Converge(Spec(First));

        Assert.Empty(result.JobReports);
        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("pull "));
        Assert.Contains(_log, line => line.Contains("edge unavailable", StringComparison.Ordinal));
    }
}
