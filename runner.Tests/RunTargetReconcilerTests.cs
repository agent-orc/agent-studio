using AgentRunner.RunTargets;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// IP-1 desired-state reconciliation against a fake container runtime: first
/// start, upgrade with swap, failed health check keeping the old revision,
/// rollback onto the kept revision, crash restarts with backoff and crash-loop
/// rollback, removal only on an explicit signal, first-claim domain ownership,
/// and secret values that reach only per-revision files on tmpfs.
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
    private readonly string _runtime;
    private readonly RunTargetStateStore _store;
    private readonly RunTargetReconciler _reconciler;
    private bool _memoryBacked = true;

    public RunTargetReconcilerTests()
    {
        _store = new RunTargetStateStore(_workspace.Directory("state"));
        _runtime = _workspace.Directory("secrets-runtime");
        _reconciler = Reconciler(RunTargetOptions.DefaultEdgeImage);
    }

    public void Dispose() => _workspace.Dispose();

    private RunTargetReconciler Reconciler(string edgeImage) => new(_docker,
        new FakeRunTargetSecrets(new() { ["demo/database-url"] = _secret }),
        new RunTargetSecretFiles(_runtime, _ => _memoryBacked), _store,
        new RunTargetReconcilerSettings(edgeImage, new RunTargetEdgeSettings("ops@example.com", false, []),
            null, HealthAttempts: 3),
        _clock, (_, _) => Task.CompletedTask, _log.Add);

    private static string Key => RunTargetNames.AppKey("demo", "stage");

    private static RunTargetEnvironmentSpec Spec(string? artifact, string? jobId = "job-1", bool rollback = false,
        string[]? domains = null)
        => new("demo", "stage", "stage", artifact, 8080, "/healthz", domains ?? ["stage.example.com"], "acme",
            new Dictionary<string, string> { ["LOG_LEVEL"] = "info" }, ["database-url"],
            new RunTargetResources(0.5, 256, 128), jobId, jobId is null ? null : "lease-" + jobId, rollback);

    private Task<RunTargetCycleResult> Converge(params RunTargetEnvironmentSpec[] specs) => Desired(specs);

    private Task<RunTargetCycleResult> Desired(RunTargetEnvironmentSpec[] specs, RunTargetRemoval[]? removals = null,
        RunTargetSkip[]? skipped = null)
        => _reconciler.ReconcileAsync(new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime, specs,
            skipped ?? [], removals ?? []), CancellationToken.None);

    private static string Slot(RunTargetEnvironmentSpec spec)
        => RunTargetNames.AppKey(spec.Project, spec.EnvironmentId) + "-" + RunTargetNames.Revision(spec);

    private string SecretDirectory(RunTargetEnvironmentSpec spec) => Path.Combine(_runtime, Slot(spec));

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
    public async Task UnchangedDesiredState_IsIdempotent_AndDoesNotRewriteTheHostState()
    {
        await Converge(Spec(First));
        await Converge(Spec(First, jobId: null));
        var calls = _docker.Calls.Count;
        var statePath = Path.Combine(_store.Directory, "state.json");
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(statePath, written);

        var result = await Converge(Spec(First, jobId: null));

        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("up ") || call.StartsWith("pull "));
        Assert.Empty(result.JobReports);
        Assert.Equal("running", Assert.Single(result.Observations).State);
        Assert.Equal(written, File.GetLastWriteTimeUtc(statePath));
    }

    [Fact]
    public async Task Upgrade_SwapsAfterHealth_StopsButKeepsThePreviousRevision_AndDropsItsSecretFiles()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        await Converge(first);
        Assert.True(Directory.Exists(SecretDirectory(first)));

        var result = await Converge(second);

        Assert.Contains($"reverse_proxy {Slot(second)}:8080", Caddyfile);
        Assert.Contains("stop " + Slot(first), _docker.Calls);
        Assert.DoesNotContain("down " + Slot(first), _docker.Calls);
        Assert.False(Directory.Exists(SecretDirectory(first)));
        Assert.True(Directory.Exists(SecretDirectory(second)));
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
        Assert.False(Directory.Exists(SecretDirectory(second)));
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
        Assert.True(Directory.Exists(SecretDirectory(second)));
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
    public async Task CrashLoopAfterASwap_RollsBackToTheKeptRevision_AndHoldsItUntilADeployJob()
    {
        var first = Spec(First, jobId: null);
        await Converge(first);
        await Converge(Spec(Second, jobId: "job-2"));
        var running = Spec(Second, jobId: null);
        var crashing = Slot(running);

        // Docker's three restarts plus the host's own: four inside the window.
        _docker.Crash(crashing);
        Assert.Equal("starting", Assert.Single((await Converge(running)).Observations).State);
        Assert.DoesNotContain($"reverse_proxy {Slot(first)}:8080", Caddyfile);
        _clock.Advance(TimeSpan.FromSeconds(15));
        _docker.Crash(crashing);
        var result = await Converge(running);

        Assert.Contains($"reverse_proxy {Slot(first)}:8080", Caddyfile);
        Assert.Contains("stop " + crashing, _docker.Calls);
        var observed = Assert.Single(result.Observations);
        Assert.Equal(("running", First, Second), (observed.State, observed.RunningArtifact, observed.PreviousArtifact));
        Assert.Equal([("crash-rollback", Second), ("rolled-back", First)],
            observed.Events.TakeLast(2).Select(item => (item.Kind, item.Artifact)));
        Assert.Contains(_log, line => line.Contains("rolling back to " + First, StringComparison.Ordinal));

        // Studio still names the crashing release without a job: the host keeps the rollback.
        var calls = _docker.Calls.Count;
        result = await Converge(running);
        Assert.DoesNotContain("up " + crashing, _docker.Calls.Skip(calls));
        Assert.Equal(First, Assert.Single(result.Observations).RunningArtifact);

        // A deploy job for it is an explicit decision to try again.
        result = await Converge(Spec(Second, jobId: "redeploy"));
        Assert.Equal(Second, Assert.Single(result.Observations).RunningArtifact);
        Assert.True(Assert.Single(result.JobReports).Healthy);
    }

    [Fact]
    public async Task CrashLoopWithoutAKeptRevision_DoesNotRollBack()
    {
        var spec = Spec(First, jobId: null);
        await Converge(spec);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            _docker.Crash(Slot(spec));
            await Converge(spec);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.DoesNotContain(_store.Load().Apps[Key].Events, item => item.Kind == "crash-rollback");
        Assert.Equal(First, _store.Load().Apps[Key].Current!.Artifact);
    }

    [Fact]
    public async Task SecretValues_AreMountedFromTmpfsFiles_NeverComposeStateLogsOrTheContainerEnvironment()
    {
        var spec = Spec(First);
        await Converge(spec);
        var up = _docker.Ups.Single(item => item.Project != RunTargetNames.EdgeContainer);
        var file = Path.Combine(SecretDirectory(spec), "database-url");
        Assert.Equal(_secret, File.ReadAllText(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(SecretDirectory(spec)));
        }
        Assert.Contains("      - \"DATABASE_URL_FILE=/run/secrets/database-url\"\n", up.ComposeText);
        Assert.Contains($"    secrets:\n      - \"database-url\"\n", up.ComposeText);
        Assert.Contains($"secrets:\n  \"database-url\":\n    file: \"{file}\"\n", up.ComposeText);
        Assert.DoesNotContain(_secret, up.ComposeText);

        // A failing start echoes the value in docker's error output.
        _docker.FailUpWith = $"invalid value DATABASE_URL={_secret}";
        var result = await Converge(Spec(Second, jobId: "job-2"));

        Assert.False(Assert.Single(result.JobReports).Healthy);
        Assert.False(Directory.Exists(SecretDirectory(Spec(Second))));
        Assert.Contains(_log, line => line.Contains("[redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, line => line.Contains(_secret, StringComparison.Ordinal));
        foreach (var path in Directory.EnumerateFiles(_store.Directory, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(_secret, File.ReadAllText(path));
        Assert.DoesNotContain(_secret, System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task SecretFiles_AreNeverWrittenOutsideTmpfs()
    {
        _memoryBacked = false;

        var result = await Converge(Spec(First));

        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("pull "));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_runtime));
        Assert.Equal(RunTargetEvidence.StepFailed, Assert.Single(result.JobReports).Evidence);
        Assert.Contains(_log, line => line.Contains("is not on tmpfs", StringComparison.Ordinal));
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
    public async Task SecretOfAnotherProject_DoesNotResolve()
    {
        // database-url exists only in the demo project's secret root.
        var result = await Converge(Spec(First) with { Project = "shop" });

        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("pull "));
        Assert.Equal(RunTargetEvidence.StepFailed, Assert.Single(result.JobReports).Evidence);
        Assert.Contains(_log, line => line.Contains("shop/stage is missing secret references", StringComparison.Ordinal));
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
    public async Task DomainOwnership_StaysWithTheFirstClaim_WhateverKeySortsFirst()
    {
        var owner = Spec(First, jobId: null);
        await Converge(owner);
        // "aaa" sorts before "demo", which decided ownership before.
        var intruder = Spec(Second, jobId: null) with { Project = "aaa", SecretRefs = [] };
        Assert.True(string.CompareOrdinal(Slot(intruder), Slot(owner)) < 0);

        var result = await Converge(owner, intruder);

        Assert.Contains($"stage.example.com {{\n\tencode zstd gzip\n\treverse_proxy {Slot(owner)}:8080", Caddyfile);
        Assert.DoesNotContain(Slot(intruder), Caddyfile);
        Assert.Equal(RunTargetNames.AppKey("demo", "stage"), _store.Load().DomainOwners["stage.example.com"]);
        Assert.Contains(result.Observations.Single(item => item.Project == "aaa").Events, item => item.Kind == "domain-conflict");

        // Once the owner no longer declares it, the domain is free.
        await Converge(Spec(First, jobId: null, domains: ["demo.example.com"]), intruder);
        Assert.Contains($"stage.example.com {{\n\tencode zstd gzip\n\treverse_proxy {Slot(intruder)}:8080", Caddyfile);
    }

    [Fact]
    public async Task DomainsStudioRefused_AreReportedAsAConflict()
    {
        var result = await Converge(Spec(First) with { Domains = [], RefusedDomains = ["stage.example.com"] });

        Assert.DoesNotContain("stage.example.com", Caddyfile);
        Assert.Contains(Assert.Single(result.Observations).Events, item => item.Kind == "domain-conflict");
        Assert.True(Assert.Single(result.JobReports).Healthy);
    }

    [Fact]
    public async Task EnvironmentStudioNoLongerMentions_KeepsRunning_WithItsRouteAndPreviousRevision()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        await Converge(first);
        await Converge(second);
        var calls = _docker.Calls.Count;

        var result = await Converge();
        await Desired([], skipped: [new RunTargetSkip("demo", "declaration-invalid")]);

        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("down ") || call.StartsWith("stop "));
        Assert.Contains($"reverse_proxy {Slot(second)}:8080", Caddyfile);
        var app = _store.Load().Apps[Key];
        Assert.Equal((Second, First), (app.Current!.Artifact, app.Previous!.Artifact));
        Assert.True(Directory.Exists(SecretDirectory(second)));
        Assert.Empty(result.Observations);
        Assert.Single(_log, line => line.Contains("keeping demo/stage as it is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkippedProject_IsKept_AndTheReasonIsLogged()
    {
        await Converge(Spec(First));

        await Desired([], skipped: [new RunTargetSkip("demo", "declaration-invalid")]);

        Assert.Contains(_log, line => line.Contains("Studio could not read its project (declaration-invalid)", StringComparison.Ordinal));
        Assert.NotNull(_store.Load().Apps[Key].Current);
    }

    [Fact]
    public async Task NoHealthyReleaseInStudio_IsNotARemoval()
    {
        await Converge(Spec(First));
        var calls = _docker.Calls.Count;

        var result = await Converge(Spec(null, jobId: null));

        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("down ") || call.StartsWith("stop "));
        Assert.Equal(("running", First), (Assert.Single(result.Observations).State, result.Observations[0].RunningArtifact));
    }

    [Fact]
    public async Task ExplicitRemoval_TakesBothRevisionsDown_DropsTheRoute_AndConfirms()
    {
        var first = Spec(First);
        var second = Spec(Second, jobId: "job-2");
        await Converge(first);
        await Converge(second);

        var result = await Desired([], removals: [new RunTargetRemoval("demo", "stage")]);

        Assert.Contains("down " + Slot(second), _docker.Calls);
        Assert.Contains("down " + Slot(first), _docker.Calls);
        Assert.Empty(_store.Load().Apps);
        Assert.Empty(_store.Load().DomainOwners);
        Assert.DoesNotContain("reverse_proxy", Caddyfile);
        Assert.False(Directory.Exists(SecretDirectory(second)));
        Assert.Equal(("demo", "stage", "removed"), (result.Observations[0].Project, result.Observations[0].EnvironmentId,
            Assert.Single(result.Observations).State));

        // A repeated signal is confirmed again without touching docker.
        var calls = _docker.Calls.Count;
        result = await Desired([], removals: [new RunTargetRemoval("demo", "stage")]);
        Assert.Equal("removed", Assert.Single(result.Observations).State);
        Assert.DoesNotContain(_docker.Calls.Skip(calls), call => call.StartsWith("down "));
    }

    [Fact]
    public async Task Removal_ThatDockerCannotComplete_KeepsTheAppAndRetries()
    {
        await Converge(Spec(First));
        _docker.DownFails = true;

        var result = await Desired([], removals: [new RunTargetRemoval("demo", "stage")]);

        Assert.Empty(result.Observations);
        Assert.NotNull(_store.Load().Apps[Key].Current);
        Assert.Contains($"reverse_proxy {Slot(Spec(First))}:8080", Caddyfile);
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

    [Fact]
    public async Task UnpinnedEdgeImage_IsNotStarted()
    {
        var result = await Reconciler("caddy:2-alpine").ReconcileAsync(
            new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime, [Spec(First)]), CancellationToken.None);

        Assert.Empty(result.JobReports);
        Assert.Empty(_docker.Ups);
        Assert.Contains(_log, line => line.Contains("is not pinned by sha256 digest", StringComparison.Ordinal));
    }
}
