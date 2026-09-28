using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Boot-time stale-progress sweep (pairs with ADR-0020 crash recovery; routing
/// per ADR-0051 failed-pickup elimination, supersedes ADR-0028/0029). Five cases
/// plus the active-job defensive guard:
///
/// <list type="number">
///   <item>Sentinel + stale -> finished missed transition into 4-auto-review
///   with a <c>recovered-from-stuck-progress</c> supervisor chat note.</item>
///   <item>No sentinel + stale + has <c>task.json</c> -> requeued to
///   <c>2-ready</c> so the pickup loop retries the same task (an interrupted run
///   is not a failure). No new orphan card.</item>
///   <item>Empty + stale + no <c>task.json</c> -> archived to <c>7-archive</c> as
///   <c>-debris-&lt;date&gt;</c> (debris, not a runnable task).</item>
///   <item>Fresh -> untouched (progress-first pickup will resume).</item>
///   <item>Re-run on the same lane -> no further changes (idempotency).</item>
///   <item>Active job -> never touched even when stale (defensive guard).</item>
/// </list>
/// </summary>
public sealed class StaleProgressArchiverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public StaleProgressArchiverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-stale-progress-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
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
    public async Task Sweep_StaleFolderWithDoneSentinel_RecoversToReviewAndAppendsChatNote()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "demo-task");
        WriteCliLogWithSentinel(folder, "[[TASK_DONE]]");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, "demo-task");
        Assert.False(Directory.Exists(folder), "source 3-progress folder must be moved");
        Assert.True(Directory.Exists(moved), "job folder must land in 4-review");

        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RecoveredToReview, d.Kind);
        Assert.Equal("DONE", d.SentinelKeyword);
        Assert.Equal(TaskStates.AutoReview, d.TargetState);

        // Chat-log note lands on the moved folder so the protocol pane sees it.
        var log = File.ReadAllText(Path.Combine(moved, "logs", "cli-output.log"));
        Assert.Contains("[recovered-from-stuck-progress]", log);
        Assert.Contains("[supervisor]", log);

        // Decision lands in <workspace>/logs/orphan-recoveries.jsonl.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("recovered-to-review", jsonl);
        Assert.Contains("\"slug\":\"demo-task\"", jsonl);
    }

    [Fact]
    public async Task Sweep_LargeCliLogWithSentinelAtTheEnd_StillRecoversFromTheBoundedTail()
    {
        // AGT-2991: the sweep reads only the newest window of cli-output.log.
        // A multi-MiB log must still yield the sentinel in its last lines.
        WriteJob(TaskStates.Progress, "big-log-task");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "big-log-task");
        var logs = Directory.CreateDirectory(Path.Combine(folder, "logs")).FullName;
        var logPath = Path.Combine(logs, "cli-output.log");
        using (var writer = new StreamWriter(logPath))
        {
            var filler = new string('x', 200);
            for (var i = 0; i < 12_000; i++) writer.WriteLine($"[12:00:00.000] [stdout] working line {i} {filler}");
            writer.WriteLine("[12:30:00.000] [stdout] [[TASK_DONE]]");
        }
        Assert.True(new FileInfo(logPath).Length > 2 * 1024 * 1024);
        SetMtimeOldEnough(logPath);
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RecoveredToReview, d.Kind);
        Assert.Equal("DONE", d.SentinelKeyword);
    }

    [Fact]
    public async Task Sweep_StaleFolderWithJobJsonNoSentinel_IsRequeuedToReadyNotDeadLettered()
    {
        // ADR-0051 (failed-pickup elimination): a stale 3-progress folder that
        // still carries a task.json is a real task whose run was interrupted, not
        // a task that failed. It is requeued to 2-ready so the pickup loop
        // retries the same task. No new orphan card, nothing in failed-pickup.
        WriteJob(TaskStates.Progress, "no-sentinel");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "no-sentinel");
        WriteCliLog(folder, "agent talked but never finished");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder), "source 3-progress folder must be moved");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RequeuedToReady, d.Kind);
        Assert.Equal(TaskStates.Ready, d.TargetState);

        // The same task returns to 2-ready under its original slug.
        var requeued = Path.Combine(_watchPath, TaskStates.Ready, "no-sentinel");
        Assert.True(Directory.Exists(requeued), "interrupted task must return to 2-ready under its original slug");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup, "no-sentinel")),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        // Exactly one compact recovery line travels with the folder so the
        // requeue is never silent, but the chat is not flooded with a fat block.
        var log = File.ReadAllText(Path.Combine(requeued, "logs", "cli-output.log"));
        Assert.Contains($"[{RecoveryChatLine.RecoveryTag}] {RecoveryChatLine.ReasonHostRestart}", log);
        Assert.Contains($"requeued to {TaskStates.Ready}", log);
        // The legacy duplicate supervisor note is gone (one line per recovery).
        Assert.DoesNotContain("[requeued-from-stuck-progress]", log);

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("requeued-to-ready", jsonl);
        Assert.DoesNotContain("moved-to-failed-pickup", jsonl);
    }

    [Fact]
    public async Task Sweep_RealZombie_TaskJsonMtimeBumpedByMetadataEdit_IsStillRequeued()
    {
        // Acceptance regression (bug-3-progress-zombies): a real interrupted run
        // sits in 3-progress past the resume window. A bulk metadata edit (the
        // live trigger was "switch every task to Opus 4.8") rewrites task.json
        // and bumps its FILE mtime to "now". The old activity signature folded
        // that mtime in, so the sweep reported the folder fresh -> 0 actionable
        // -> the zombie stayed stranded for hours. Liveness is now run-bound (log
        // mtimes + the stable enteredLaneAt value), so bumping task.json's mtime
        // must NOT rescue a real zombie.
        const string slug = "metadata-edited-zombie";
        WriteJobWithEnteredLaneAt(TaskStates.Progress, slug, DateTime.UtcNow - TimeSpan.FromHours(3));
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent ran, then the run was interrupted with no sentinel");

        // The run died ~3h ago: its only run-produced log is stale.
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        // task.json keeps its stale enteredLaneAt VALUE, but the metadata edit
        // bumps its file mtime to "now" - the exact shape that defeated the sweep.
        File.SetLastWriteTimeUtc(Path.Combine(folder, "task.json"), DateTime.UtcNow);

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder),
            "a metadata-edit mtime bump must not keep a real zombie in 3-progress");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RequeuedToReady, d.Kind);
        Assert.Equal(TaskStates.Ready, d.TargetState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)),
            "interrupted task must return to 2-ready under its original slug");
    }

    [Fact]
    public async Task Sweep_FreshEnteredLaneNoLogsYet_IsLeftAlone()
    {
        // A folder that only just entered 3-progress (e.g. a parallel pickup
        // whose run has not streamed its first log line yet) has no logs/ files.
        // The enteredLaneAt value floors the activity signal so the sweep does
        // not prematurely requeue a run that is starting up.
        WriteJobWithEnteredLaneAt(TaskStates.Progress, "just-entered", DateTime.UtcNow);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "just-entered");

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "a freshly-entered folder must not be swept before its first log line");
        Assert.Equal(StaleProgressDecisionKinds.Fresh, Assert.Single(decisions).Kind);
    }

    [Fact]
    public async Task HostedService_RunOnce_RequeuesStaleProgressFolderWithoutRestart()
    {
        WriteJob(TaskStates.Progress, "runtime-zombie");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "runtime-zombie");
        WriteCliLog(folder, "runtime run stopped without a sentinel");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build();
        var service = new StaleProgressSweepHostedService(
            archiver,
            new ConfigurationBuilder().Build(),
            NullLogger<StaleProgressSweepHostedService>.Instance);

        var decisions = await service.RunOnceAsync();

        Assert.False(Directory.Exists(folder), "hosted sweep must move the stale 3-progress folder");
        var requeued = Path.Combine(_watchPath, TaskStates.Ready, "runtime-zombie");
        Assert.True(Directory.Exists(requeued), "runtime stale-progress sweep must requeue without a backend restart");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RequeuedToReady, d.Kind);
        Assert.Equal(TaskStates.Ready, d.TargetState);
    }

    [Fact]
    public async Task Sweep_EmptyStaleFolderNoJobJson_IsArchivedAsDebrisNotDeadLettered()
    {
        // ADR-0051 (failed-pickup elimination): an empty stale folder with no
        // task.json is not a runnable task. It is debris and is archived to
        // 7-archive with its evidence intact, never parked in a dead-end lane.
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "empty-shell");
        Directory.CreateDirectory(folder);
        // No task.json, no logs. MeasureFolder treats this as epoch 0 so it
        // always crosses the threshold.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder));
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.ArchivedDebris, d.Kind);
        Assert.Equal(TaskStates.Archive, d.TargetState);
        Assert.NotNull(d.FailedPickupSlug);
        Assert.StartsWith("empty-shell-debris-", d.FailedPickupSlug);

        var archived = Path.Combine(_watchPath, TaskStates.Archive, d.FailedPickupSlug!);
        Assert.True(Directory.Exists(archived), "debris must land in 7-archive");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup, d.FailedPickupSlug!)),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("archived-debris", jsonl);
        Assert.DoesNotContain("moved-to-failed-pickup", jsonl);
    }

    [Fact]
    public async Task Sweep_EmptyStaleFolderWithTwinInDownstreamLane_IsSilentlyRemovedNotArchived()
    {
        // Regression for the 2026-05-12 boot-race (01:34-01:36 UTC, stable
        // restart at 01:27:42): six phantom folders appeared in 3a-failed-pickup
        // after a backend restart while four jobs were finishing their
        // 3-progress -> 4-auto-review -> 5-human-review moves. The boot sweep
        // saw a 3-progress residue with no task.json and minted
        // <slug>-debris-<date> markers for jobs already in 5-human-review.
        // After the fix, the sweep cross-checks downstream lanes
        // (4-auto-review / 5-human-review / 6-completed / 7-archive) and
        // silently deletes the residue instead of minting a phantom card.
        const string slug = "bug-card-delete-button-has-no-effect";

        // The real twin already lives in 5-human-review.
        WriteJob(TaskStates.HumanReview, slug);

        // The mid-move residue in 3-progress has no task.json (mimicking the
        // empty placard-only shape observed in the incident).
        var residueFolder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(residueFolder);

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        // 1. The residue is gone (no phantom card under <slug>-debris-<date>).
        Assert.False(Directory.Exists(residueFolder), "mid-move casualty residue must be deleted");
        var archivedDebris = Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.Archive))
            .Select(Path.GetFileName)
            .Where(name => name != null && name.StartsWith(slug + "-debris-", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(archivedDebris); // acceptance criterion #3: no phantom marker minted

        // 2. The real twin in 5-human-review is untouched.
        var twinFolder = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Assert.True(Directory.Exists(twinFolder), "real twin in 5-human-review must remain intact");
        Assert.True(File.Exists(Path.Combine(twinFolder, "task.json")),
            "real twin's task.json must remain intact");

        // 3. One decision row: MidMoveCasualtyRemoved, pointing to the twin's lane.
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.MidMoveCasualtyRemoved, d.Kind);
        Assert.Equal(slug, d.Slug);
        Assert.Equal(TaskStates.HumanReview, d.TargetState);

        // 4. The JSONL audit log records the silent removal but no debris move.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("mid-move-casualty-removed", jsonl);
        Assert.DoesNotContain("archived-debris", jsonl);
        Assert.DoesNotContain("moved-to-failed-pickup", jsonl);
    }

    [Fact]
    public async Task Sweep_EmptyStaleFolderWithNoDownstreamTwin_StillArchivedAsDebris()
    {
        // Acceptance criterion #2: when the residue has no twin in a later
        // lane, the previous behaviour (archive to 7-archive as debris) must
        // remain unchanged. The cross-lane reconciliation is additive, not a
        // wholesale rewrite of the debris path.
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "lonely-debris");
        Directory.CreateDirectory(folder);
        // No twin in any downstream lane.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder));
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.ArchivedDebris, d.Kind);
        Assert.Equal(TaskStates.Archive, d.TargetState);
        Assert.StartsWith("lonely-debris-debris-", d.FailedPickupSlug);
    }

    [Fact]
    public async Task Sweep_FreshFolder_IsLeftAlone()
    {
        WriteJob(TaskStates.Progress, "fresh");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "fresh");
        WriteCliLog(folder, "still working");
        // mtime stays "now" so the folder is well within the resume window.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "fresh folder must not be moved");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Fresh, d.Kind);

        // Fresh verdicts are not persisted in orphan-recoveries.jsonl.
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")));
    }

    [Fact]
    public async Task Sweep_StaleCliLogButFreshToolCalls_IsLeftAlone()
    {
        // Regression guard for the suchbox-orphan incident (2026-05-07): a
        // claude-code session emitted only tool-use events into
        // logs/tool-calls.jsonl for tens of minutes while logs/cli-output.log
        // stayed quiet. Reading cli-output.log alone misclassified the live
        // folder as orphan and the sweep moved it. The activity signature now
        // spans every file in logs/, so a fresh tool-calls.jsonl keeps the
        // verdict at Fresh.
        WriteJob(TaskStates.Progress, "tool-calling");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "tool-calling");
        WriteCliLog(folder, "long-quiet stdout");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        // tool-calls.jsonl mtime defaults to "now" since we just wrote it.
        var toolCalls = Path.Combine(folder, "logs", "tool-calls.jsonl");
        File.WriteAllText(toolCalls, "{\"ts\":\"now\",\"kind\":\"started\",\"tool\":\"Bash\"}\n");

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "fresh tool-calls.jsonl must keep the folder alive");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Fresh, d.Kind);
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")));
    }

    [Fact]
    public async Task Sweep_StaleCliLogButFreshSessionEvents_IsLeftAlone()
    {
        // Sister case to the tool-calls path: the runner writes a one-line
        // start/continue event into logs/session-events.jsonl at every
        // pickup attempt. A folder where session-events.jsonl was just
        // appended must count as fresh even when cli-output.log mtime is
        // stale (e.g. claude-code session emitted no stdout yet).
        WriteJob(TaskStates.Progress, "just-resumed");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "just-resumed");
        WriteCliLog(folder, "old stdout from a previous attempt");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var sessionEvents = Path.Combine(folder, "logs", "session-events.jsonl");
        File.WriteAllText(sessionEvents, "{\"Ts\":\"now\",\"Kind\":\"continue\",\"Cli\":\"claude\"}\n");

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Equal(StaleProgressDecisionKinds.Fresh, Assert.Single(decisions).Kind);
    }

    [Fact]
    public async Task Sweep_IsIdempotentAcrossRuns()
    {
        WriteJob(TaskStates.Progress, "first-orphan");
        var f1 = Path.Combine(_watchPath, TaskStates.Progress, "first-orphan");
        WriteCliLog(f1, "no sentinel here");
        SetMtimeOldEnough(Path.Combine(f1, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f1, "task.json"));

        WriteJob(TaskStates.Progress, "second-recovered");
        var f2 = Path.Combine(_watchPath, TaskStates.Progress, "second-recovered");
        WriteCliLogWithSentinel(f2, "[[TASK_NEEDS_INPUT:waiting]]");
        SetMtimeOldEnough(Path.Combine(f2, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f2, "task.json"));

        var (archiver, _) = Build();
        var first = await archiver.SweepAsync();
        Assert.Equal(2, first.Count);

        var jsonlLen1 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;

        var second = await archiver.SweepAsync();
        Assert.Empty(second); // no candidates remain in 3-progress

        var jsonlLen2 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;
        Assert.Equal(jsonlLen1, jsonlLen2); // no new lines on the rerun
    }

    [Fact]
    public async Task Sweep_ValidSteerPendingMarker_IsLeftToSliceB()
    {
        WriteJob(TaskStates.Progress, "steer-wait");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "steer-wait");
        WriteCliLogWithSentinel(folder, "[[TASK_NEEDS_INPUT: ist iframe schon implementiert?]]");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));
        SteerPendingMarker.Write(folder, new SteerPendingRecord
        {
            WaitStartedAt = DateTime.UtcNow - TimeSpan.FromHours(5),
            Kind = SteerPendingKinds.Steer,
            Ask = "ist iframe schon implementiert?"
        });

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.Empty(decisions);
        Assert.True(Directory.Exists(folder));
        Assert.True(SteerPendingMarker.Exists(folder));
    }

    [Fact]
    public async Task Sweep_MalformedSteerMarker_FallsThroughToOrdinaryRecovery()
    {
        WriteJob(TaskStates.Progress, "torn-steer-wait");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "torn-steer-wait");
        WriteCliLog(folder, "interrupted run");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));
        File.WriteAllText(Path.Combine(folder, SteerPendingMarker.FileName), "{not-json");

        var (archiver, _) = Build();
        var decision = Assert.Single(await archiver.SweepAsync());

        Assert.Equal(StaleProgressDecisionKinds.RequeuedToReady, decision.Kind);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "torn-steer-wait")));
    }

    [Fact]
    public async Task Sweep_ActiveJobIsNeverTouchedEvenWhenStale()
    {
        WriteJob(TaskStates.Progress, "running-now");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "running-now");
        WriteCliLog(folder, "agent mid-stream");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build();
        archiver.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus
                {
                    ProjectName = ProjectName,
                    Mode = "auto-continuous",
                    ActiveJobId = "running-now"
                }
            }
        };

        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "active job folder must never be moved by the sweep");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Skipped, d.Kind);
    }

    [Fact]
    public async Task AutomatedSweeps_NeverTouchFixtureCards()
    {
        WriteJob(TaskStates.Progress, "RUN-101", fixture: true);
        var progress = Path.Combine(_watchPath, TaskStates.Progress, "RUN-101");
        WriteCliLog(progress, "fixture run has no sentinel");
        SetMtimeOldEnough(Path.Combine(progress, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(progress, "task.json"));
        WriteJob(TaskStates.FailedPickup, "RUN-102", fixture: true);
        var failedPickup = Path.Combine(_watchPath, TaskStates.FailedPickup, "RUN-102");

        var (archiver, _) = Build();
        var staleDecisions = await archiver.SweepAsync();
        var drainDecisions = await archiver.DrainFailedPickupLaneAsync();

        Assert.Empty(staleDecisions);
        Assert.Empty(drainDecisions);
        Assert.True(Directory.Exists(progress));
        Assert.True(Directory.Exists(failedPickup));
    }

    [Fact]
    public async Task Sweep_ZeroWindow_DisablesPass()
    {
        WriteJob(TaskStates.Progress, "would-be-orphan");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "would-be-orphan");
        WriteCliLog(folder, "no sentinel");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "task.json"));

        var (archiver, _) = Build(stuckResumeWindowMinutes: 0);
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Empty(decisions);
    }

    private (StaleProgressArchiver Archiver, TaskScannerService Scanner) Build(int stuckResumeWindowMinutes = 60)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Supervisor:StuckResumeWindowMinutes"] = stuckResumeWindowMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        // Empty service provider: tests use StatusProviderOverride to drive the
        // active-job guard, so the runner doesn't need to be instantiated.
        var sp = new ServiceCollection().BuildServiceProvider();

        var archiver = new StaleProgressArchiver(
            scanner, states, transitions, chatLog, sp, config, taskAccess,
            NullLogger<StaleProgressArchiver>.Instance);
        return (archiver, scanner);
    }

    private void WriteJob(string state, string slug, bool fixture = false)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"fixture\":{fixture.ToString().ToLowerInvariant()}}}");
    }

    private void WriteJobWithEnteredLaneAt(string state, string slug, DateTime enteredLaneAt)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var stamp = enteredLaneAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"enteredLaneAt\":\"{stamp}\"}}");
    }

    private static void WriteCliLog(string folder, string body)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cli-output.log"),
            $"[12:00:00.000] [stdout] {body}{Environment.NewLine}");
    }

    private static void WriteCliLogWithSentinel(string folder, string sentinel)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        for (int i = 0; i < 10; i++) lines.Add($"[12:0{i}:00.000] [stdout] working line {i}");
        lines.Add($"[12:30:00.000] [stdout] {sentinel}");
        File.WriteAllText(Path.Combine(dir, "cli-output.log"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void SetMtimeOldEnough(string path)
    {
        // Three hours back keeps us well past the 60-minute default window.
        var stale = DateTime.UtcNow - TimeSpan.FromHours(3);
        File.SetLastWriteTimeUtc(path, stale);
    }
}
