using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the strict-iteration progress-first pickup contract (routing per
/// ADR-0051 failed-pickup elimination, supersedes ADR-0028/0029):
///
/// <list type="number">
///   <item>The pickup tick prefers ANY 3-progress folder over 2-ready -
///   even one with no <c>cli-output.log</c> and no captured session id.
///   The "no log" case is the most-restartable case (CLI never streamed
///   anything), not the most-skippable.</item>
///   <item>Iteration order is deterministic: oldest-first by mtime
///   (cli-output.log when present, else task.json, else folder).</item>
///   <item>A 3-progress folder past the retry budget is no longer
///   dead-lettered. It routes by cause: a spawn failure (CLI never started)
///   returns the task to <c>2-ready</c> and pauses the runner; a task-shaped
///   silence (CLI ran but stayed quiet) or a session-less zombie escalates to
///   <c>5-human-review</c> and the picker continues. A no-<c>task.json</c>
///   orphan with no downstream twin is archived to <c>7-archive</c> as debris.
///   Every routing appends a row to
///   <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>.</item>
/// </list>
/// </summary>
public sealed class PickupLoopStrictIterationTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public PickupLoopStrictIterationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-strict-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ===== Pure helpers =====

    [Fact]
    public void OrderProgressByMtime_OldestFirst_TieBreaksOnSlug()
    {
        var t = DateTime.UtcNow;
        var a = new ProgressPickupCandidate("p/a", "alpha", null, t.AddMinutes(-30));
        var b = new ProgressPickupCandidate("p/b", "bravo", null, t.AddMinutes(-10));
        var c = new ProgressPickupCandidate("p/c", "charlie", null, t.AddMinutes(-30));

        var ordered = ProjectRunner.OrderProgressByMtime(new[] { b, a, c });

        // alpha and charlie share the same mtime; tie-broken by slug ascending.
        Assert.Equal(new[] { "alpha", "charlie", "bravo" }, ordered.Select(o => o.Slug).ToArray());
    }

    [Fact]
    public void MeasureProgressFolderMtime_PrefersCliLog_FallsBackToJobJson_FallsBackToFolder()
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "demo-task");
        Directory.CreateDirectory(folder);

        // Just folder: returns folder mtime (well after epoch).
        var folderOnly = ProjectRunner.MeasureProgressFolderMtime(folder);
        Assert.True(folderOnly > DateTime.UtcNow.AddDays(-1));

        // Add task.json with stamped mtime: returns that.
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var jobStamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "task.json"), jobStamp);
        Assert.Equal(jobStamp, ProjectRunner.MeasureProgressFolderMtime(folder));

        // Add cli-output.log with newer mtime: takes precedence.
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "logs", "cli-output.log"), "stream");
        var logStamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "logs", "cli-output.log"), logStamp);
        Assert.Equal(logStamp, ProjectRunner.MeasureProgressFolderMtime(folder));
    }

    [Fact]
    public void MeasureProgressFolderMtime_EmptyFolderWithoutFiles_ReturnsFolderMtime()
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "empty");
        Directory.CreateDirectory(folder);
        var stamp = new DateTime(2024, 12, 1, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(folder, stamp);

        Assert.Equal(stamp, ProjectRunner.MeasureProgressFolderMtime(folder));
    }

    [Fact]
    public void BuildArchiveSlug_DisambiguatesOnCollision()
    {
        var d = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        // No collisions.
        Assert.Equal("foo-pickup-failed-2026-05-06",
            PickupFailureLog.BuildArchiveSlug("foo", d, _ => false));

        // First two collide.
        var taken = new HashSet<string> { "foo-pickup-failed-2026-05-06", "foo-pickup-failed-2026-05-06-2" };
        Assert.Equal("foo-pickup-failed-2026-05-06-3",
            PickupFailureLog.BuildArchiveSlug("foo", d, taken.Contains));
    }

    // ===== Scenario 1: progress-first wins over ready (even with no log) =====

    /// <summary>
    /// Production observation that drove this work: a 3-progress folder
    /// without a cli-output.log was previously skipped because the older
    /// "GetNextResumableProgressJob" filter required a captured session id.
    /// The pickup then started a fresh 2-ready job. Strict-iteration says:
    /// take the 3-progress folder anyway. The "no log" case means the CLI
    /// never streamed anything, which is the most-restartable case.
    /// </summary>
    [Fact]
    public void ListProgressFoldersOldestFirst_WithOneNoLogProgressAndOneReady_PicksProgress()
    {
        WriteJob(TaskStates.Progress, "silent-progress");
        // Deliberately no cli-output.log and no session id.
        WriteJob(TaskStates.Ready, "fresh-ready");

        var runner = BuildRunner();

        var folders = runner.ListProgressFoldersOldestFirst();
        var only = Assert.Single(folders);
        Assert.Equal("silent-progress", only.Slug);

        // The picker would route this to RunCliAsync; we verify it returns the
        // progress slug without taking the ready job by exercising the same
        // method shape via the public iteration helper above. The 2-ready
        // folder is on disk and would be picked only after 3-progress drains.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "fresh-ready")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "silent-progress")));
    }

    // ===== Scenario 2: oldest 3-progress runs first =====

    [Fact]
    public void ListProgressFoldersOldestFirst_WithThreeProgressFolders_OrdersByMtime()
    {
        // Three progress folders in non-mtime order, plus several ready jobs.
        WriteJob(TaskStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "newest", "task.json"), TimeSpan.FromMinutes(-5));

        WriteJob(TaskStates.Progress, "oldest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "oldest", "task.json"), TimeSpan.FromMinutes(-90));

        WriteJob(TaskStates.Progress, "middle");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "middle", "task.json"), TimeSpan.FromMinutes(-30));

        WriteJob(TaskStates.Ready, "ready-1");
        WriteJob(TaskStates.Ready, "ready-2");

        var runner = BuildRunner();

        var ordered = runner.ListProgressFoldersOldestFirst();
        Assert.Equal(new[] { "oldest", "middle", "newest" }, ordered.Select(c => c.Slug).ToArray());
    }

    // ===== Scenario 3: over-budget routing (no dead-letter lane) =====

    /// <summary>
    /// Three 3-progress folders, all with the per-slug attempt counter primed
    /// at the failure threshold and the attempt history task-shaped (the CLI
    /// did spawn but produced no output). ADR-0051: a task-shaped over-budget
    /// folder is escalated to 5-human-review, not dead-lettered. The picker
    /// keeps going (no pause) and returns null so TickAsync falls through to
    /// 2-ready. Nothing lands in 3a-failed-pickup.
    /// </summary>
    [Fact]
    public void StrictIteration_AllProgressFoldersExhausted_EscalateToHumanReviewAndFallThrough()
    {
        // Three folders that ran the CLI but never produced a CLI output line.
        WriteJob(TaskStates.Progress, "stuck-a");
        WriteJob(TaskStates.Progress, "stuck-b");
        WriteJob(TaskStates.Progress, "stuck-c");

        // The "next pickup" semantics: a fresh ready job stays untouched until
        // 3-progress drains.
        WriteJob(TaskStates.Ready, "ready-after-drain");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // No executionStatus override -> task-shaped (CLI spawned, stayed silent).
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold);

        var picked = InvokePickerLoop(runner);

        // Task-shaped escalation does not pause the runner: the folder leaves
        // 3-progress so there is no spin, and pausing would stall the queue.
        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);

        // Each folder is now under 5-human-review under its ORIGINAL slug; the
        // 3a-failed-pickup lane is never touched.
        foreach (var slug in new[] { "stuck-a", "stuck-b", "stuck-c" })
        {
            Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, slug)),
                $"{slug} must have been moved out of 3-progress");
            Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, slug)),
                $"{slug} must have been escalated to 5e-escalated under its original slug");
        }
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup))
            && Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.FailedPickup)).Any(),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        // pickup-failures.jsonl carries one row per escalation.
        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.Contains("\"kind\":\"escalated-human-review\"", row);
            Assert.Contains("\"projectName\":\"demo\"", row);
            Assert.Contains("\"threshold\":3", row);
            Assert.Contains("\"outputDeadlineSeconds\":60", row);
            // The folder keeps its original slug as it moves to 5-human-review.
            Assert.DoesNotContain("-pickup-failed-", row);
        }

        // 2-ready folder is untouched - the runner reaches it only on the
        // next pickup tick now that 3-progress has drained.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-drain")));
    }

    /// <summary>
    /// One folder past the threshold (task-shaped), two below. The over-budget
    /// folder is escalated to 5-human-review, and the next-iteration call
    /// returns one of the remaining folders to resume.
    /// </summary>
    [Fact]
    public void StrictIteration_OneExhaustedTwoFresh_EscalatesExhaustedAndPicksNext()
    {
        WriteJob(TaskStates.Progress, "exhausted");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "exhausted", "task.json"), TimeSpan.FromMinutes(-90));
        WriteJob(TaskStates.Progress, "second-oldest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "second-oldest", "task.json"), TimeSpan.FromMinutes(-30));
        WriteJob(TaskStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "newest", "task.json"), TimeSpan.FromMinutes(-5));

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("exhausted", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        // 'exhausted' escalated to 5-human-review; 'second-oldest' and 'newest'
        // remain in 3-progress because a single tick starts ONE job (ADR-0001).
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "exhausted")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "exhausted")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "second-oldest")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "newest")));

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Single(rows);
        Assert.Contains("\"slug\":\"exhausted\"", rows[0]);
        Assert.Contains("\"kind\":\"escalated-human-review\"", rows[0]);
    }

    /// <summary>
    /// A spawn failure (every recorded attempt shows the CLI never started)
    /// is infrastructure, not a task fault. ADR-0051 cause #6: the task is
    /// returned to 2-ready unchanged and the runner pauses so it does not spin
    /// against an unavailable CLI. Nothing lands in 3a-failed-pickup; the row
    /// is kind 'requeued-ready'.
    /// </summary>
    [Fact]
    public void StrictIteration_SpawnFailureOverBudget_RequeuesToReadyAndPausesRunner()
    {
        WriteJob(TaskStates.Progress, "cli-down");
        WriteJob(TaskStates.Ready, "waiting-behind");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // Every attempt shows the CLI process never spawned.
        runner.SetPickupAttemptsForTest("cli-down", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);

        var picked = InvokePickerLoop(runner);

        // Spawn failure pauses the runner so it does not loop against a dead CLI.
        Assert.Null(picked);
        Assert.Equal("manual", runner.GetStatus().Mode);

        // The task is returned to 2-ready UNCHANGED (original slug), not
        // dead-lettered and not escalated.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "cli-down")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "cli-down")),
            "spawn-failure task must wait in 2-ready under its original slug");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "cli-down")));

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var row = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);
        Assert.Contains("\"kind\":\"requeued-ready\"", row);
        Assert.Contains("\"slug\":\"cli-down\"", row);
        Assert.Contains("\"destinationSlug\":\"cli-down\"", row);
    }

    [Fact]
    public void StrictIteration_BusyOrphanAtFiveAttempts_EscalatesWithPathAndStopsRetrying()
    {
        const string slug = "busy-orphan";
        var busyPath = Path.Combine(Path.GetTempPath(), "ass-worktrees", "demo", slug);
        WriteJob(TaskStates.Progress, slug);

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        // Attempts 1-4 remain retryable: the strict picker returns the same
        // progress candidate and leaves it in place. The fifth identical busy
        // preparation failure is the bounded terminal below.
        runner.SetPickupAttemptsForTest(
            slug,
            ProjectRunner.WorktreeBlockedFailureThreshold - 1,
            ProjectRunner.WorktreeBlockedExecutionStatus,
            $"Orphan worktree dir busy at {busyPath}; deferring task {slug}.");

        var retryable = InvokePickerLoop(runner);

        Assert.NotNull(retryable);
        Assert.Equal(slug, retryable!.Id);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, slug)));

        runner.SetPickupAttemptsForTest(
            slug,
            ProjectRunner.WorktreeBlockedFailureThreshold,
            ProjectRunner.WorktreeBlockedExecutionStatus,
            $"Orphan worktree dir busy at {busyPath}; deferring task {slug}.");
        Assert.Equal(ProjectRunner.WorktreeBlockedFailureThreshold, runner.GetPickupFailureThreshold(slug));

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, slug)));
        var escalated = Path.Combine(_watchPath, TaskStates.Escalated, slug);
        Assert.True(Directory.Exists(escalated));

        var status = File.ReadAllText(Path.Combine(escalated, "status.md"));
        Assert.Contains("worktree-blocked", status);
        Assert.Contains(busyPath, status);
        Assert.Contains("after 5 attempts", status);

        var followUp = File.ReadAllText(Path.Combine(escalated, "orchestrator-follow-up.md"));
        Assert.Contains("- [ ] worktree-blocked:", followUp);
        Assert.Contains(busyPath, followUp);
        Assert.Contains("after 5 attempts", followUp);

        var row = File.ReadAllLines(Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl"))
            .Single(line => line.Length > 0);
        Assert.Contains("\"threshold\":5", row);
        Assert.Contains("\"executionStatus\":\"worktree-blocked\"", row);
        Assert.Contains(busyPath.Replace("\\", "\\\\"), row);
    }

    [Fact]
    public void WorktreePreparationFailure_SurfacesCodeGitMessageAndPathOnCardAndTimeline()
    {
        const string slug = "prepare-visible";
        var worktreePath = Path.Combine(Path.GetTempPath(), "ass-worktrees", "demo", slug);
        const string gitMessage = "fatal: path is not a working tree";
        WriteJob(TaskStates.Progress, slug);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        var info = new TaskInfo
        {
            Id = slug,
            Title = "Visible preparation failure",
            State = TaskStates.Progress,
            FolderPath = folder,
            WatchPath = _watchPath,
            ProjectName = ProjectName,
        };
        var runner = BuildRunner();

        runner.RecordWorktreePreparationFailureForTest(info, gitMessage, worktreePath);

        var log = File.ReadAllText(TaskPaths.CliOutputLog(folder));
        Assert.Contains("[worktree-preparation-failed]", log);
        Assert.Contains(gitMessage, log);
        Assert.Contains(worktreePath, log);

        var timeline = File.ReadAllText(TaskPaths.TimelineLog(folder));
        Assert.Contains("\"kind\":\"worktree_preparation_failed\"", timeline);
        Assert.Contains("\"failureCode\":\"worktree-preparation-failed\"", timeline);
        Assert.Contains(gitMessage, timeline);
        Assert.Contains(worktreePath.Replace("\\", "\\\\"), timeline);

        var scanned = BuildScanner().FindJob(slug, _watchPath);
        Assert.NotNull(scanned?.OutcomeIssue);
        Assert.Equal("worktree-preparation-failed", scanned!.OutcomeIssue!.Kind);
        Assert.Equal("worktree-preparation-failed", scanned.OutcomeIssue.Label);
        Assert.Contains(gitMessage, scanned.OutcomeIssue.TechnicalDetails);
        Assert.Contains(worktreePath, scanned.OutcomeIssue.TechnicalDetails);
    }

    [Fact]
    public void LocalRunAdmission_LogsConfiguredProjectUrlPortOccupantWithPid()
    {
        var logger = new RecordingLogger();
        var runner = BuildRunner(
            logger,
            [new ProjectUrlRecord
            {
                Id = "url-1",
                Label = "Dev server",
                Url = "http://127.0.0.1:4184",
                StartRule = new ProjectUrlStartRule { Command = "npm start", Port = 4184 },
            }],
            new FixedPortInspector(4184, 43210, "python"));
        var info = new TaskInfo { Id = "web-19", ProjectName = ProjectName };

        runner.WarnWhenProjectUrlPortIsAlreadyOccupiedForTest(info);

        var warning = Assert.Single(
            logger.Messages,
            message => message.Contains("run-port-collision-warning", StringComparison.Ordinal));
        Assert.Contains("port=4184", warning);
        Assert.Contains("occupantPid=43210", warning);
        Assert.Contains("occupantProcess=python", warning);
        Assert.Contains("job=web-19", warning);
    }

    [Fact]
    public void RepeatedPickReverts_SuppressSameCardForTenMinutes_ThenEmitCountAndReset()
    {
        var runner = BuildRunner();
        var start = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal((true, 0), runner.TakeRevertLogDecisionForTest("busy-orphan", start));
        Assert.Equal((false, 1), runner.TakeRevertLogDecisionForTest("busy-orphan", start.AddMinutes(1)));
        Assert.Equal((false, 2), runner.TakeRevertLogDecisionForTest("busy-orphan", start.AddMinutes(5)));
        Assert.Equal((false, 3), runner.TakeRevertLogDecisionForTest("busy-orphan", start.AddMinutes(9).AddSeconds(59)));
        Assert.Equal((true, 3), runner.TakeRevertLogDecisionForTest("busy-orphan", start.AddMinutes(10)));
        Assert.Equal((false, 1), runner.TakeRevertLogDecisionForTest("busy-orphan", start.AddMinutes(11)));
        Assert.Equal((true, 0), runner.TakeRevertLogDecisionForTest("another-task", start.AddMinutes(1)));
    }

    [Fact]
    public void StrictIteration_PostMoveSkeleton_TwinInHumanReview_IsSilentlyDeleted()
    {
        // Setup mirrors the Windows file-handle race that produces the
        // skeleton in production: a job runs in 3-progress, the move to
        // 5-human-review succeeds for most of the tree, but a logs/* file
        // stays locked by an in-process writer and leaves an empty shell
        // behind in 3-progress while the canonical folder (with task.json)
        // lives in the downstream lane.
        WriteOrphanProgressFolder("duplicate-later-lane");
        WriteJob(TaskStates.HumanReview, "duplicate-later-lane");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        // Returns null so TickAsync falls through to GetNextReadyJob on the
        // same tick (the 2026-05-11 root cause was the picker handing the
        // orphan back as if it were runnable). Mode stays auto-continuous.
        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);

        // The shell folder is gone, the downstream twin is untouched, and
        // the ready job is still in line.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-orphan")));

        // No orphan entry is written: this was cleanup debris, not a
        // pickup failure. 3a-failed-pickup stays clean.
        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var entries = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("duplicate-later-lane", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(entries);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "post-move skeletons are not infra failures");
    }

    /// <summary>
    /// Same shape as the human-review variant but with the downstream twin
    /// in 4-auto-review. The picker must treat every post-progress lane
    /// identically: a slug match anywhere downstream is the signal that the
    /// 3-progress remnant is cleanup debris and should be deleted silently.
    /// </summary>
    [Fact]
    public void StrictIteration_PostMoveSkeleton_TwinInAutoReview_IsSilentlyDeleted()
    {
        WriteOrphanProgressFolder("canonical-in-auto-review");
        WriteJob(TaskStates.AutoReview, "canonical-in-auto-review");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-orphan")));

        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var entries = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("canonical-in-auto-review", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(entries);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "post-move skeletons are not infra failures");
    }

    /// <summary>
    /// Genuine orphan: a 3-progress folder with no task.json AND no twin in
    /// any post-progress lane. A manual filesystem intervention or a hard
    /// backend crash before the move leaves this behind. ADR-0051 cause #5:
    /// a folder with no task.json is not a runnable task, it is debris. It is
    /// archived to 7-archive with its evidence (logs, status.md) intact,
    /// never parked in a dead-end failure lane the operator must triage.
    /// </summary>
    [Fact]
    public void StrictIteration_GenuineOrphan_NoTwin_IsArchivedAsDebris()
    {
        WriteOrphanProgressFolder("genuine-orphan-no-twin");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "genuine-orphan-no-twin")));

        // Debris lands in 7-archive with its evidence intact, not in failed-pickup.
        var archiveRoot = Path.Combine(_watchPath, TaskStates.Archive);
        var archived = Directory.EnumerateDirectories(archiveRoot)
            .Where(d => Path.GetFileName(d).StartsWith("orphan-genuine-orphan-no-twin-", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(archived);
        Assert.True(File.Exists(Path.Combine(only, "logs", "cli-output.log")), "evidence must travel to the archive");
        Assert.True(File.Exists(Path.Combine(only, "status.md")), "evidence must travel to the archive");

        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var inFailedPickup = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("genuine-orphan-no-twin", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(inFailedPickup);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "stale metadata orphans are queue-hygiene issues, not CLI infra failures");
    }

    [Fact]
    public void OverBudgetRow_IncludesAttemptHistoryWhenAvailable()
    {
        WriteJob(TaskStates.Progress, "history-task");
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // Task-shaped (no spawn-failed status) so it escalates to 5-human-review.
        runner.SetPickupAttemptsForTest("history-task", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var line = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);

        // Assert the row shape against the schema's required fields. ADR-0051:
        // the task keeps its original slug as it moves to 5-human-review.
        Assert.Contains("\"at\":\"", line);
        Assert.Contains("\"kind\":\"escalated-human-review\"", line);
        Assert.Contains("\"projectName\":\"demo\"", line);
        Assert.Contains("\"slug\":\"history-task\"", line);
        Assert.Contains("\"jobId\":\"history-task\"", line);
        Assert.Contains("\"destinationSlug\":\"history-task\"", line);
        Assert.Contains("\"attempts\":3", line);
        Assert.Contains("\"threshold\":3", line);
        Assert.Contains("\"outputDeadlineSeconds\":60", line);
        Assert.Contains("\"attemptHistory\":[", line);
        Assert.Contains("\"reason\":\"", line);

        // Surface a sample JSONL row to the task job folder so the task report
        // can quote a real wire-format line. Best-effort: never fails the test.
        var sampleSink = Environment.GetEnvironmentVariable("PICKUP_FAILURE_SAMPLE_PATH");
        if (!string.IsNullOrWhiteSpace(sampleSink))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sampleSink)!);
                File.WriteAllText(sampleSink, line + Environment.NewLine);
            }
            catch { /* sample capture is informational only */ }
        }
    }

    // ===== Cross-slug infra circuit breaker (loop-inventory:
    // pickup.cross-slug-infra-circuit-breaker) =====

    /// <summary>
    /// Integration scenario for the cross-slug infra breaker under ADR-0051.
    /// A spawn failure pauses the runner on the FIRST over-budget folder
    /// (per-task pause), so a single tick can never reach a second slug; the
    /// breaker's distinct-slug cascade therefore unfolds across ticks. Tick 1:
    /// stuck-a spawn-fails, requeues to 2-ready, mode flips to manual, the
    /// breaker records its 1st distinct slug (no audit row yet). The operator
    /// resumes (mode back to auto). Tick 2: stuck-b spawn-fails, the breaker
    /// records its 2nd distinct slug and trips, writing one
    /// <c>cross-slug-spawn-failed-cascade</c> row to infra-halts.jsonl. stuck-c
    /// is never touched.
    /// </summary>
    [Fact]
    public void CrossSlug_SpawnFailuresAcrossTicks_TripBreakerOnSecondDistinctSlug()
    {
        WriteJob(TaskStates.Progress, "stuck-a");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-a", "task.json"), TimeSpan.FromMinutes(-90));
        WriteJob(TaskStates.Progress, "stuck-b");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-b", "task.json"), TimeSpan.FromMinutes(-60));
        WriteJob(TaskStates.Progress, "stuck-c");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-c", "task.json"), TimeSpan.FromMinutes(-30));

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);

        // Tick 1: oldest (stuck-a) spawn-fails, requeues to 2-ready, pauses.
        InvokePickerLoop(runner);
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "stuck-a")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "the first distinct slug does not trip the breaker");

        // Operator fixes the CLI and resumes.
        runner.SetMode("auto-continuous");

        // Tick 2: stuck-b spawn-fails, the breaker's 2nd distinct slug trips it.
        InvokePickerLoop(runner);
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "stuck-b")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "stuck-c")),
            "third folder must NOT have been touched after the cross-slug breaker tripped");

        // Both spawn failures requeued to 2-ready; nothing dead-lettered.
        var pickupJsonl = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(pickupJsonl));
        var pickupRows = File.ReadAllLines(pickupJsonl).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, pickupRows.Count);
        Assert.All(pickupRows, r => Assert.Contains("\"kind\":\"requeued-ready\"", r));

        // infra-halts.jsonl carries exactly one cross-slug cascade row.
        var infraJsonl = Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl");
        Assert.True(File.Exists(infraJsonl));
        var infraRows = File.ReadAllLines(infraJsonl).Where(l => l.Length > 0).ToList();
        Assert.Single(infraRows);
        Assert.Contains("\"kind\":\"cross-slug-spawn-failed-cascade\"", infraRows[0]);
        Assert.Contains("\"projectName\":\"demo\"", infraRows[0]);
        Assert.Contains("\"cliType\":\"copilot\"", infraRows[0]);
        Assert.Contains("\"slugs\":[\"stuck-a\",\"stuck-b\"]", infraRows[0]);
    }

    // ===== Zombie-resume wiring (this bug: failed pickup leaves a queue-
    // jumping zombie in 3-progress) =====

    /// <summary>
    /// The increment wire that was missing in production. A session-less
    /// 3-progress folder whose auto-pickup run finished without reaching
    /// review is left stranded in 3-progress; the progress-first picker would
    /// resume it again next tick forever. <see cref="ProjectRunner.AccountZombieResumeOutcome"/>
    /// must count each such failed resume so the picker can eventually give up,
    /// and must reset the counter the moment a folder gains a resumable session
    /// (real progress). Before the fix the counter never moved, so the picker's
    /// zombie guard never tripped and the zombie kept getting picked.
    /// </summary>
    [Fact]
    public void AccountZombieResumeOutcome_IncrementsPerFailedResume_ResetsWhenSessionCaptured()
    {
        WriteJob(TaskStates.Progress, "zombie-x");                 // session-less
        WriteResumableProgressJob("resumable-y", "sess-abc123");   // carries a session id

        var runner = BuildRunner();

        // Session-less folder: every failed resume counts.
        Assert.Equal(0, runner.GetZombieResumeFailures("zombie-x"));
        runner.AccountZombieResumeOutcome("zombie-x");
        Assert.Equal(1, runner.GetZombieResumeFailures("zombie-x"));
        runner.AccountZombieResumeOutcome("zombie-x");
        Assert.Equal(2, runner.GetZombieResumeFailures("zombie-x"));

        // A folder that now carries a resumable session made real progress:
        // any accumulated streak is cleared instead of incremented.
        runner.SetZombieResumeFailuresForTest("resumable-y", 1);
        runner.AccountZombieResumeOutcome("resumable-y");
        Assert.Equal(0, runner.GetZombieResumeFailures("resumable-y"));
    }

    /// <summary>
    /// End-to-end of the reported bug: a session-less zombie in 3-progress that
    /// has burned its resume budget must be dead-lettered (escalated out of
    /// 3-progress) so the due 2-ready task becomes the next pickup, instead of
    /// the zombie jumping the queue every tick. Drives the counter through the
    /// real <see cref="ProjectRunner.AccountZombieResumeOutcome"/> wire (not just
    /// the test seam) to prove the full chain: failed resumes increment ->
    /// picker gives up -> ready job is next.
    /// </summary>
    [Fact]
    public void ZombieResumesPastBudget_PickerEscalatesAndDueReadyBecomesNext()
    {
        WriteJob(TaskStates.Progress, "zombie-a"); // session-less, stranded
        WriteJob(TaskStates.Ready, "due-b");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        // Simulate the real failed resumes that production was never counting.
        for (var i = 0; i < ProjectRunner.ZombieResumeFailureThreshold; i++)
            runner.AccountZombieResumeOutcome("zombie-a");
        Assert.Equal(ProjectRunner.ZombieResumeFailureThreshold, runner.GetZombieResumeFailures("zombie-a"));

        var picked = InvokePickerLoop(runner);

        // The picker no longer hands back the zombie; 3-progress drained so
        // TickAsync falls through to 2-ready. The zombie is escalated to
        // 5-human-review (task-shaped terminal route), not dead-ended in
        // 3a-failed-pickup.
        Assert.Null(picked);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "zombie-a")),
            "the session-less zombie must leave 3-progress once its resume budget is spent");
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "zombie-a")),
            "the zombie escalates to 5e-escalated under its original slug");

        // The due ready task is untouched and is what runs next.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "due-b")));
        var next = runner.GetNextReadyJob();
        Assert.NotNull(next);
        Assert.Equal("due-b", next!.Id);

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var row = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);
        Assert.Contains("\"slug\":\"zombie-a\"", row);
        Assert.Contains("\"kind\":\"escalated-human-review\"", row);
    }

    // ===== Helpers =====

    /// <summary>
    /// Drives the picker by reflecting into the private
    /// <c>TryPickProgressJobOrDeadLetter</c> method. Picker is the unit-of-
    /// behavior we want to test; we don't want to spin up a real CLI to
    /// observe its decisions. Fragile against rename, but the rename will
    /// fail this test at the same time as the production change.
    ///
    /// Returns the picker's verdict (the <see cref="TaskInfo"/> it would
    /// hand to <c>RunCliAsync</c>, or <c>null</c> if 3-progress drained
    /// and <c>TickAsync</c> will fall through to <see cref="TaskInfo"/> from
    /// 2-ready next).
    /// </summary>
    private static TaskInfo? InvokePickerLoop(ProjectRunner runner)
    {
        var method = typeof(ProjectRunner).GetMethod("TryPickProgressJobOrDeadLetter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        // One call: the picker walks the full 3-progress lane, dead-letters
        // every folder past the threshold, and stops at the first folder
        // still under the threshold (returning it) or returns null when
        // all folders were exhausted. That single-call shape matches what
        // TickAsync invokes; tests that need multiple ticks can call this
        // helper repeatedly.
        return method!.Invoke(runner, null) as TaskInfo;
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        // Pre-stamp ownerClientId so the scanner's owner-id migration sweep
        // does not rewrite task.json on first scan (which would clobber the
        // mtime values the ordering tests rely on).
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"cliType\":\"copilot\",\"ownerClientId\":\"local-default\"}}");
    }

    private void WriteResumableProgressJob(string slug, string sessionName)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Progress}\",\"order\":1,\"agent\":\"copilot\",\"cliType\":\"copilot\",\"sessionName\":\"{sessionName}\",\"ownerClientId\":\"local-default\"}}");
    }

    private void WriteOrphanProgressFolder(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), "orphan log");
        File.WriteAllText(Path.Combine(dir, "status.md"), "orphan status");
    }

    private static void SetMtime(string path, TimeSpan offset)
    {
        var stamp = DateTime.UtcNow + offset;
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        return scanner;
    }

    private ProjectRunner BuildRunner(
        ILogger? logger = null,
        IReadOnlyList<ProjectUrlRecord>? projectUrls = null,
        AgentStudio.Registry.IProjectUrlPortInspector? projectUrlPortInspector = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        return new ProjectRunner(
            ProjectName, entry,
            logger ?? NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null,
            timeline: new TimelineLog(NullLogger<TimelineLog>.Instance),
            projectUrls: projectUrls,
            projectUrlPortInspector: projectUrlPortInspector);
    }

    private sealed class FixedPortInspector(int expectedPort, int pid, string processName)
        : AgentStudio.Registry.IProjectUrlPortInspector
    {
        public ProjectUrlPortOccupant? FindListener(int port)
            => port == expectedPort ? new ProjectUrlPortOccupant(pid, processName) : null;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
