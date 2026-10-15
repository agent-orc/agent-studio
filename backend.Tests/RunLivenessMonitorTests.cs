using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Run-Liveness Slice A executor (concept:
/// docs/concepts/run-liveness-and-slot-semantics.md). Proves the invariant
/// "no zombie survives 60s" end to end against a real workspace:
///
/// <list type="number">
///   <item>Boot adoption: an execution zombie (no live heartbeat, run never
///   finished) is demoted 3-progress -&gt; 2-ready AND its session-resume pointer
///   is cleared, breaking the "No conversation found" launch-fail chain
///   (AGT-2006 / AGT-1945-1939).</item>
///   <item>Phase-aware recovery: a finished-but-post-processing-died card
///   (agent_run_finished present) is re-triggered to 4-auto-review, NOT demoted -
///   the completed agent run is never re-run (AGT-1932).</item>
///   <item>A card with a live pickup-lock owner keeps its heartbeat and is left
///   alone (a healthy foreign/own run is not stolen).</item>
///   <item>Uptime: a just-moved card inside the grace window is left alone.</item>
///   <item>Cross-monitor ownership: a valid steer-pending wait is left to the
///   Slice B timeout instead of being mistaken for a dead process.</item>
///   <item>No work lost: the demotion never tears down a worktree; the task
///   returns to 2-ready under its slug with its evidence intact.</item>
///   <item>Idempotency + the active-job defensive guard.</item>
/// </list>
/// </summary>
public sealed class RunLivenessMonitorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _secondWatchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";
    private const string SecondProjectName = "other";

    public RunLivenessMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-run-liveness-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        _secondWatchPath = Path.Combine(_workspaceRoot, "projects", SecondProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var watchPath in new[] { _watchPath, _secondWatchPath })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(watchPath, state));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task BootAdoption_ExecutionZombie_DemotedToReady_AndResumePointerCleared()
    {
        // A run crashed with the backend mid-execution: 3-progress card, a stale
        // resume pointer, a dead pickup lock, no agent_run_finished signal.
        const string slug = "execution-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-abc", chain: new[] { "sess-abc" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent was mid-run when the backend died");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        // Demoted to 2-ready under its original slug (fresh run retries the task).
        Assert.False(Directory.Exists(folder), "zombie must leave 3-progress");
        var requeued = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(requeued), "interrupted execution run must return to 2-ready");

        var d = Assert.Single(outcomes);
        Assert.Equal(RunLivenessOutcomeKinds.DemotedProcessLost, d.Kind);
        Assert.Equal(RunLivenessReasons.ProcessLost, d.ReasonCode);
        Assert.Equal(TaskStates.Ready, d.TargetState);

        // The resume pointer is cleared: sessionName is emptied and the chain is
        // tombstoned so RunPlanner cannot re-derive the dead id and walk into the
        // "No conversation found" / "no rollout found" launch-fail chain.
        var (sessionName, chain) = ReadSession(Path.Combine(requeued, "task.json"));
        Assert.True(string.IsNullOrEmpty(sessionName), "sessionName must be cleared on process-lost demotion");
        Assert.Equal("(recovery)", chain[^1]);

        // The demotion is never silent: one compact recovery line travels along.
        var log = File.ReadAllText(Path.Combine(requeued, "logs", "cli-output.log"));
        Assert.Contains($"[{RecoveryChatLine.RecoveryTag}]", log);
        Assert.Contains($"requeued to {TaskStates.Ready}", log);
        Assert.Contains("session new", log);

        // Audit row lands in <workspace>/logs/run-liveness.jsonl.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl"));
        Assert.Contains("demoted-process-lost", jsonl);
        Assert.Contains("\"slug\":\"execution-zombie\"", jsonl);
    }

    [Fact]
    public async Task BootAdoption_SettledEnvelope_RecoversToReview_AndPreservesCompletedSession()
    {
        const string slug = "settled-execution";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-completed", chain: ["sess-completed"]);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "completion reached authority before the lane move");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build(configureAuthority: (authority, scanner) =>
            SettleCompletedRun(authority, ResolveTaskKey(scanner, slug)));
        var outcome = Assert.Single(await monitor.AdoptOnBootAsync());

        Assert.Equal(RunLivenessOutcomeKinds.SettledRunRecovered, outcome.Kind);
        Assert.Equal("settled-run-authority", outcome.ReasonCode);
        Assert.Equal(TaskStates.AutoReview, outcome.TargetState);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
        var recovered = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Assert.True(Directory.Exists(recovered));
        var (sessionName, chain) = ReadSession(Path.Combine(recovered, "task.json"));
        Assert.Equal("sess-completed", sessionName);
        Assert.DoesNotContain("(recovery)", chain);
        Assert.DoesNotContain("requeued to 2-ready", File.ReadAllText(Path.Combine(recovered, "logs", "cli-output.log")));
    }

    [Fact]
    public async Task BootAdoption_CorruptSettledEnvelope_FailsClosed_WithoutRequeue()
    {
        const string slug = "corrupt-settled-execution";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-completed", chain: ["sess-completed"]);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "completion reached authority before the lane move");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build(configureAuthority: (authority, scanner) =>
        {
            var taskKey = ResolveTaskKey(scanner, slug);
            SettleCompletedRun(authority, taskKey);
            var digest = authority.GetTaskProjection(taskKey).CurrentRunAttempt!.ResultEnvelopeDigest!;
            // The settlement lives in the journal until a checkpoint folds it
            // into the snapshot this test corrupts.
            authority.CheckpointForMigration();
            var authorityPath = Path.Combine(_workspaceRoot, AttemptAuthorityService.RelativePath);
            var json = File.ReadAllText(authorityPath);
            var corrupted = json.Replace(digest, new string('d', digest.Length), StringComparison.Ordinal);
            Assert.NotEqual(json, corrupted);
            File.WriteAllText(authorityPath, corrupted);
        });
        var outcome = Assert.Single(await monitor.AdoptOnBootAsync());

        Assert.Equal(RunLivenessOutcomeKinds.DemoteFailed, outcome.Kind);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        var (sessionName, chain) = ReadSession(Path.Combine(folder, "task.json"));
        Assert.Equal("sess-completed", sessionName);
        Assert.DoesNotContain("(recovery)", chain);
    }

    [Fact]
    public async Task BootAdoption_FinishedRun_RetriggersPostProcessing_NotDemoted()
    {
        // AGT-1932: the agent run finished (and was merged) and only
        // post-processing died with the backend. The card must NOT be demoted to
        // 2-ready (that would re-run the completed agent) - it is re-triggered to
        // 4-auto-review where post-processing resumes.
        const string slug = "finished-postprocessing-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-done", chain: new[] { "sess-done" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent finished; post-processing was running");
        WriteAgentRunFinishedTimeline(folder);
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.False(Directory.Exists(folder), "card must leave 3-progress");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)),
            "a finished run must NOT be demoted to 2-ready (that would re-run the completed agent)");
        var promoted = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Assert.True(Directory.Exists(promoted), "a finished run must be re-triggered into 4-auto-review");

        var d = Assert.Single(outcomes);
        Assert.Equal(RunLivenessOutcomeKinds.RetriggeredPostProcessing, d.Kind);
        Assert.Equal(RunLivenessReasons.PostProcessingLost, d.ReasonCode);
        Assert.Equal(TaskStates.AutoReview, d.TargetState);

        // The resume pointer is preserved for a finished run (not tombstoned).
        var (sessionName, _) = ReadSession(Path.Combine(promoted, "task.json"));
        Assert.Equal("sess-done", sessionName);

        var log = File.ReadAllText(Path.Combine(promoted, "logs", "cli-output.log"));
        Assert.Contains("[post-processing-recovered]", log);

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl"));
        Assert.Contains("retriggered-post-processing", jsonl);
    }

    [Fact]
    public async Task LiveHeartbeat_CardWithLivePickupLock_IsLeftAlone()
    {
        // A live pickup-lock owner (this process's pid) is a live run-heartbeat;
        // the card is healthy even though it is silent. This is what stops a
        // healthy run (own or a foreign backend sharing the workspace) being
        // stolen, and a healthy post-processing card being demoted.
        const string slug = "healthy-live-run";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-live", chain: new[] { "sess-live" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent working");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log")); // silent, but owned
        WriteLivePickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "a card with a live heartbeat must never be touched");
        Assert.Empty(outcomes); // healthy verdicts are not persisted as actions
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")));
    }

    [Fact]
    public async Task Uptime_FreshCard_WithinGrace_IsLeftAlone()
    {
        // During uptime a card can be heartbeat-less for a beat between the lane
        // move and the run claim. A card whose last activity is inside the grace
        // window must not be demoted yet (it is re-checked next tick).
        const string slug = "just-moved";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: null, chain: Array.Empty<string>());
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "just entered 3-progress"); // mtime = now, inside grace
        // No pickup lock yet (the claim/lock has not landed).

        var (monitor, _) = Build();
        var outcomes = await monitor.SweepAsync(); // uptime path applies the grace

        Assert.True(Directory.Exists(folder), "a fresh card inside the grace must not be demoted");
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task Uptime_NewLogActivityBeforeDemotion_KeepsCardInProgress()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        const string slug = "activity-after-snapshot";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: null, chain: Array.Empty<string>());
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "old run output");
        var log = Path.Combine(folder, "logs", "cli-output.log");
        File.SetLastWriteTimeUtc(log, clock.GetUtcNow().UtcDateTime.AddMinutes(-10));

        var (monitor, _) = Build(time: clock);
        monitor.BeforeDemotionRecheck = _ =>
        {
            File.AppendAllText(log, "new run output");
            File.SetLastWriteTimeUtc(log, clock.GetUtcNow().UtcDateTime);
        };

        var outcomes = await monitor.SweepAsync();

        Assert.Empty(outcomes);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
    }

    [Fact]
    public async Task Uptime_FlatLayoutOrphans_AcrossAllProjects_RequeuedWithJournalFacts()
    {
        // Regression 2026-07-31: after the flat tasks/<bucket>/<key> cutover,
        // ListLaneFolders still enumerated only legacy <lane>/<slug> folders.
        // The hosted sweep therefore saw zero local 3-progress cards and four
        // dead runs remained stranded for 10-39 hours after backend restarts.
        var firstFolder = WriteFlatJobWithSession(
            _watchPath, "DEM-101", "flat-zombie-a", sessionName: "dead-a");
        var secondFolder = WriteFlatJobWithSession(
            _secondWatchPath, "OTH-202", "flat-zombie-b", sessionName: "dead-b");

        var (monitor, _) = Build();
        var outcomes = await monitor.SweepAsync();

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal(RunLivenessOutcomeKinds.DemotedProcessLost, outcome.Kind));
        Assert.Equal(
            new[] { ProjectName, SecondProjectName },
            outcomes.Select(outcome => outcome.ProjectName).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // Flat-layout transitions are metadata-only. The task folders stay put
        // while task.json.state changes back to Ready for a fresh local pickup.
        Assert.Equal(TaskStates.Ready, ReadState(firstFolder));
        Assert.Equal(TaskStates.Ready, ReadState(secondFolder));
        Assert.True(Directory.Exists(firstFolder));
        Assert.True(Directory.Exists(secondFolder));

        var journal = File.ReadAllLines(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl"));
        Assert.Equal(2, journal.Length);
        Assert.Contains(journal, line => line.Contains("\"projectName\":\"demo\"", StringComparison.Ordinal));
        Assert.Contains(journal, line => line.Contains("\"projectName\":\"other\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SteerPendingWait_IsExcludedFromBootAndUptimeRecovery()
    {
        // A steer wait deliberately has no live CLI heartbeat. Slice A must not
        // demote it at its 30s grace (or immediately on boot), because Slice B
        // owns the T=120s auto-answer/blocked decision.
        const string slug = "bounded-steer-wait";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent emitted [[TASK_NEEDS_INPUT: ist iframe schon implementiert?]]");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));
        SteerPendingMarker.Write(folder, new SteerPendingRecord
        {
            WaitStartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(5),
            Kind = SteerPendingKinds.Steer,
            Ask = "ist iframe schon implementiert?",
        });

        var (monitor, _) = Build();
        Assert.Empty(await monitor.SweepAsync());
        Assert.Empty(await monitor.AdoptOnBootAsync());

        Assert.True(Directory.Exists(folder), "Slice A must leave valid steer-pending markers to Slice B");
        Assert.True(SteerPendingMarker.Exists(folder));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")));
    }

    [Fact]
    public async Task MalformedSteerMarker_DoesNotBypassRunLivenessRecovery()
    {
        const string slug = "torn-steer-marker";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "dead run with a torn steer marker");
        File.WriteAllText(Path.Combine(folder, SteerPendingMarker.FileName), "{not-json");

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.Single(outcomes);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)),
            "an unreadable marker must fall through to Slice A instead of waiting forever");
    }

    [Fact]
    public async Task Demotion_PreservesEvidence_ReturnsSameTaskToReady()
    {
        // AGT-1945 "never lose work": the monitor never tears down a worktree, so
        // demotion cannot drop the deliverable. The task returns to 2-ready under
        // its slug carrying its evidence, ready for a clean reissue that reuses
        // the task-owned worktree.
        const string slug = "keep-the-work";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-x", chain: new[] { "sess-x" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "produced partial work before the crash");
        File.WriteAllText(Path.Combine(folder, "results-note.md"), "partial deliverable");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        await monitor.AdoptOnBootAsync();

        var requeued = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(requeued));
        Assert.True(File.Exists(Path.Combine(requeued, "task.json")), "task.json must survive the demotion");
        Assert.True(File.Exists(Path.Combine(requeued, "results-note.md")), "evidence must travel with the demoted folder");
        Assert.True(File.Exists(Path.Combine(requeued, "logs", "cli-output.log")), "logs must survive the demotion");
    }

    [Fact]
    public async Task AdoptOnBoot_IsIdempotentAcrossRuns()
    {
        const string slug = "one-shot-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "no heartbeat");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var first = await monitor.AdoptOnBootAsync();
        Assert.Single(first);

        var len1 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")).Length;

        var second = await monitor.AdoptOnBootAsync();
        Assert.Empty(second); // no candidates remain in 3-progress

        var len2 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")).Length;
        Assert.Equal(len1, len2); // no new audit lines on the rerun
    }

    [Fact]
    public async Task ActiveJob_IsNeverTouched_ViaStatusOverride()
    {
        // The defensive guard: even a silent, lock-less 3-progress card that is
        // the runner's declared active job is never demoted (it is being run by
        // this backend right now).
        const string slug = "running-now";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "mid-stream");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));

        var (monitor, _) = Build();
        monitor.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus
                {
                    ProjectName = ProjectName,
                    Mode = "auto-continuous",
                    ActiveJobId = slug
                }
            }
        };

        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "the active job must never be demoted");
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task Boot_does_not_requeue_known_remote_attempt_before_runner_handshake()
    {
        const string slug = "remote-restart";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "remote", chain: ["remote"]);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "remote CLI may still be alive on its host");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance);
        var acquired = leases.TryAcquire(new RunLeaseAcquireRequest(
            slug,
            "runner-remote",
            "runner-remote",
            "remote-host",
            42,
            "remote",
            RequestedTtlSeconds: 120,
            RepositoryId: ProjectName,
            IdempotencyKey: "remote-restart-acquire"));
        Assert.True(acquired.Granted);
        var lease = acquired.Lease!;
        leases.Release(new RunLeaseReleaseRequest(
            slug,
            lease.LeaseId,
            lease.FencingToken,
            lease.RunnerId,
            lease.AttemptId,
            lease.AuthorityEpoch,
            "remote-restart-release"));

        var (monitor, _) = Build(leases: leases);
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.Empty(outcomes);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
    }

    [Fact]
    public async Task Uptime_does_not_demote_a_keyless_card_leased_under_its_scanner_key()
    {
        // The claim endpoints lease under Key ?? TaskKey ?? Id. A legacy card
        // without a stable key is leased under the composite scanner key, which
        // task.json does not carry (AGT-3075).
        const string slug = "remote-keyless";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: null, chain: []);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "remote run is alive on its host");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance);
        var (monitor, scanner) = Build(leases: leases);
        var taskKey = ResolveTaskKey(scanner, slug);
        Assert.NotEqual(slug, taskKey);
        Assert.True(leases.TryAcquire(new RunLeaseAcquireRequest(
            taskKey, "runner-remote", "runner-remote", "remote-host", 42, "remote",
            RequestedTtlSeconds: 120, RepositoryId: ProjectName,
            IdempotencyKey: "remote-keyless-acquire")).Granted);

        var outcomes = await monitor.SweepAsync();

        Assert.Empty(outcomes);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
    }

    [Fact]
    public async Task Disabled_SkipsScanEntirely()
    {
        const string slug = "would-be-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "no heartbeat");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build(enabled: false);
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "with the monitor disabled nothing moves");
        Assert.Empty(outcomes);
    }

    // ---- harness ---------------------------------------------------------

    private (RunLivenessMonitor Monitor, TaskScannerService Scanner) Build(
        bool enabled = true,
        RunLeaseService? leases = null,
        Action<AttemptAuthorityService, TaskScannerService>? configureAuthority = null,
        TimeProvider? time = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["WatchPaths:1:Name"] = SecondProjectName,
            ["WatchPaths:1:Path"] = _secondWatchPath,
            ["WatchPaths:1:RootPath"] = _workspaceRoot,
            ["WatchPaths:1:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Runner:RunLiveness:Enabled"] = enabled ? "true" : "false",
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var authority = new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance);
        if (configureAuthority is not null)
        {
            configureAuthority(authority, scanner);
            authority = new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance);
        }
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            attemptAuthority: authority);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var pickupLock = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var sp = new ServiceCollection().BuildServiceProvider();

        var monitor = new RunLivenessMonitor(
            scanner, transitions, sessions, pickupLock, chatLog, sp, config, taskAccess,
            NullLogger<RunLivenessMonitor>.Instance,
            leases: leases,
            time: time);
        return (monitor, scanner);
    }

    private static void SettleCompletedRun(AttemptAuthorityService authority, string taskKey)
    {
        const string resultSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var acquired = authority.AcquireRun(
            taskKey,
            "demo-repository",
            sourceAttemptId: null,
            executorId: "runner-a",
            hostId: "host-a",
            requestedTtlSeconds: 120,
            idempotencyKey: $"claim:{taskKey}").RunAttempt!;
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "demo-repository",
            acquired.AttemptId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            resultSha,
            $"refs/agent-studio/results/{taskKey}/{acquired.AttemptId}",
            null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                acquired.AttemptId,
                acquired.LastFence,
                acquired.AuthorityEpoch,
                $"completion:{taskKey}"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
    }

    private string ResolveTaskKey(TaskScannerService scanner, string slug)
    {
        var task = Assert.IsType<TaskInfo>(scanner.FindJob(slug, _watchPath));
        return !string.IsNullOrWhiteSpace(task.Key)
            ? task.Key
            : !string.IsNullOrWhiteSpace(task.TaskKey)
                ? task.TaskKey
                : task.Id;
    }

    private void WriteJobWithSession(string state, string slug, string? sessionName, string[] chain)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var sessionJson = sessionName == null ? "null" : JsonSerializer.Serialize(sessionName);
        var chainJson = JsonSerializer.Serialize(chain);
        var enteredLaneAt = (DateTime.UtcNow - TimeSpan.FromMinutes(10)).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"," +
            $"\"sessionName\":{sessionJson},\"sessionChain\":{chainJson},\"enteredLaneAt\":\"{enteredLaneAt}\"}}");
    }

    private static string WriteFlatJobWithSession(
        string watchPath, string key, string id, string? sessionName)
    {
        var folder = Path.Combine(watchPath, "tasks", "000", key);
        Directory.CreateDirectory(folder);
        var enteredLaneAt = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = id,
            state = TaskStates.Progress,
            order = 1,
            agent = "claude",
            sessionName,
            sessionChain = sessionName == null ? Array.Empty<string>() : new[] { sessionName },
            enteredLaneAt,
        }));
        WriteCliLog(folder, "local CLI died with the backend");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));
        return folder;
    }

    private static void WriteCliLog(string folder, string body)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cli-output.log"),
            $"[12:00:00.000] [stdout] {body}{Environment.NewLine}");
    }

    private static void WriteAgentRunFinishedTimeline(string folder)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "timeline.jsonl"),
            "{\"Ts\":\"2026-07-10T00:00:00Z\",\"Kind\":\"agent_run_finished\",\"Actor\":\"agent\",\"Summary\":\"claude run completed\"}" + Environment.NewLine);
    }

    private void WriteLivePickupLock(string folder) => WritePickupLock(folder, System.Environment.ProcessId);

    // A pid that is (practically) certain not to be a running process on this
    // host, so the same-host liveness probe reads the lock as reclaimable.
    private void WriteDeadPickupLock(string folder) => WritePickupLock(folder, 2_000_000_000);

    private void WritePickupLock(string folder, int pid)
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        lockFile.TryAcquire(folder, new PickupLockOwner
        {
            Pid = pid,
            Hostname = System.Environment.MachineName,
            Role = "primary",
            BackendName = "test-backend",
            ProjectName = ProjectName,
            JobId = Path.GetFileName(folder),
        }, out _);
    }

    private static void SetMtimeOld(string path)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(10));
    }

    private static (string? SessionName, List<string> Chain) ReadSession(string jobJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
        var root = doc.RootElement;
        string? name = root.TryGetProperty("sessionName", out var sn) && sn.ValueKind == JsonValueKind.String
            ? sn.GetString() : null;
        var chain = new List<string>();
        if (root.TryGetProperty("sessionChain", out var sc) && sc.ValueKind == JsonValueKind.Array)
            foreach (var el in sc.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) chain.Add(el.GetString()!);
        return (name, chain);
    }

    private static string? ReadState(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        return doc.RootElement.GetProperty("state").GetString();
    }
}
