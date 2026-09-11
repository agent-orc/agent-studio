using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression cover for the 2026-05-26 bug where the Task-commit panel
/// surfaced an unrelated SHA on a task. The agent's CLI run produced
/// nothing, but the project's working tree was carrying leftover dirty
/// changes from an earlier context. The progress->auto-review auto-commit
/// swept those changes into a brand-new commit and stamped its SHA onto
/// the job, producing a commit whose subject described work that was
/// nothing to do with that task.
///
/// <para>
/// The fix - the per-file mtime guard in
/// <see cref="TaskTransitionService.IsWorkingTreeAttributableToTask"/> -
/// refuses to bundle dirty paths whose last-write times all predate the
/// task's first session event. This file pins both branches: the unrelated
/// dirty change is skipped, the agent-authored dirty change is committed
/// and stamped on the job.
/// </para>
/// </summary>
public sealed class TaskTransitionAutoCommitAttributionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private const string ProjectName = "demo";

    public TaskTransitionAutoCommitAttributionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-attribution-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repoRoot);

        RunGit(_repoRoot, "init", "-q", "-b", "main");
        RunGit(_repoRoot, "config", "user.email", "test@example.com");
        RunGit(_repoRoot, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "seed line\n");
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", "seed");
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
    public async Task MoveProgressToAutoReview_PreExistingDirtyChange_DoesNotStampCommit()
    {
        // 1) An external party (operator, earlier task) leaves AGENTS.md dirty
        //    BEFORE this task ever starts. mtime is anchored well in the past.
        var preExisting = Path.Combine(_repoRoot, "AGENTS.md");
        File.WriteAllText(preExisting, "seed line\nexternal edit\n");
        File.SetLastWriteTimeUtc(preExisting, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // 2) Two adjacent progress jobs A and B; A is about to transition.
        WriteJob(TaskStates.Progress, "task-a");
        WriteJob(TaskStates.Progress, "task-b");

        // 3) Task A's CLI run starts AFTER the dirty change already exists.
        AppendSessionEvent("task-a", DateTime.UtcNow);

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // Working tree changes predate the run -> guard refuses the auto-commit.
        // No SHA stamped on A. No SHA stamped on B (it never transitioned).
        var movedA = ReadJob(TaskStates.AutoReview, "task-a");
        var movedB = ReadJob(TaskStates.Progress, "task-b");
        Assert.Null(movedA?.Commit);
        Assert.Empty(movedA?.Commits ?? new List<TaskCommitInfo>());
        Assert.Null(movedB?.Commit);
        Assert.Empty(movedB?.Commits ?? new List<TaskCommitInfo>());
    }

    [Fact]
    public async Task MoveProgressToAutoReview_AgentEditDuringRun_StampsCommit()
    {
        // 1) Task A's CLI starts cleanly (no prior dirty state).
        WriteJob(TaskStates.Progress, "task-a");
        WriteJob(TaskStates.Progress, "task-b");
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("task-a", firstActivity);

        // 2) Agent dirties a file DURING the run (mtime > first activity).
        var edited = Path.Combine(_repoRoot, "work.txt");
        File.WriteAllText(edited, "agent change\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        // 3) Transition to auto-review.
        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // The agent edit DOES qualify for attribution -> auto-commit fires
        // and stamps a real SHA on A. B is still untouched.
        var movedA = ReadJob(TaskStates.AutoReview, "task-a");
        var movedB = ReadJob(TaskStates.Progress, "task-b");
        Assert.NotNull(movedA?.Commit);
        Assert.False(string.IsNullOrWhiteSpace(movedA!.Commit!.Sha));
        Assert.Null(movedB?.Commit);
    }

    [Fact]
    public async Task MoveProgressToAutoReview_ScopesCommitToTaskFiles_IgnoresForeignDirtyChanges()
    {
        // The mega-blob / mis-attribution bug: at maxParallelism==1 the agent
        // works in the SHARED main checkout, so foreign dirty changes (operator
        // edits, an earlier task that never committed) sit alongside this task's
        // edits. A blanket `git add -A` would sweep them all into this task's
        // commit. The scoped commit must stage ONLY the task's own files.
        WriteJob(TaskStates.Progress, "task-a");

        // Foreign dirty changes left in the checkout BEFORE the task ran.
        var foreign1 = Path.Combine(_repoRoot, "AGENTS.md");
        File.WriteAllText(foreign1, "seed line\nforeign edit\n");
        File.SetLastWriteTimeUtc(foreign1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var foreign2 = Path.Combine(_repoRoot, "foreign.txt");
        File.WriteAllText(foreign2, "foreign new file\n");
        File.SetLastWriteTimeUtc(foreign2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Task A's run starts AFTER the foreign changes already exist.
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("task-a", firstActivity);

        // The agent edits exactly TWO files during the run.
        var edit1 = Path.Combine(_repoRoot, "alpha.txt");
        File.WriteAllText(edit1, "agent alpha\n");
        File.SetLastWriteTimeUtc(edit1, firstActivity.AddSeconds(30));
        var edit2 = Path.Combine(_repoRoot, "beta.txt");
        File.WriteAllText(edit2, "agent beta\n");
        File.SetLastWriteTimeUtc(edit2, firstActivity.AddSeconds(30));

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // A real SHA is stamped and the commit records exactly the two files.
        var movedA = ReadJob(TaskStates.AutoReview, "task-a");
        Assert.NotNull(movedA?.Commit);
        Assert.Equal(2, movedA!.Commit!.FilesChanged);

        // Inspect the actual HEAD commit: it must contain ONLY the task's two
        // files, never the foreign dirty ones swept in by a whole-tree add -A.
        var committed = RunGitCapture(_repoRoot, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Equal(2, committed.Count);
        Assert.Contains("alpha.txt", committed);
        Assert.Contains("beta.txt", committed);
        Assert.DoesNotContain("AGENTS.md", committed);
        Assert.DoesNotContain("foreign.txt", committed);

        // The foreign dirty changes are STILL uncommitted in the working tree.
        var status = deps.Git.GetStatus("task-a", _watchPath);
        Assert.Contains(status.Files, f => f.Path.EndsWith("AGENTS.md", StringComparison.Ordinal));
        Assert.Contains(status.Files, f => f.Path.EndsWith("foreign.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveProgressToAutoReview_NoSessionEvents_RequiresExplicitPathScope()
    {
        // A legacy/direct-develop path has no trustworthy task window. The
        // platform must leave the interactive edit dirty instead of widening
        // to add-all and attributing it to this task.
        WriteJob(TaskStates.Progress, "legacy-task");
        var dirty = Path.Combine(_repoRoot, "legacy-change.txt");
        File.WriteAllText(dirty, "legacy edit\n");

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("legacy-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(TaskStates.AutoReview, "legacy-task");
        Assert.Null(moved?.Commit);
        Assert.Contains(deps.Git.GetStatus("legacy-task", _watchPath).Files,
            f => f.Path == "legacy-change.txt");
    }

    [Fact]
    public async Task MoveProgressToAutoReview_UsesLiveAutoCommitSetting()
    {
        WriteJob(TaskStates.Progress, "toggle-off-task");
        var dirty = Path.Combine(_repoRoot, "toggle-off-change.txt");
        File.WriteAllText(dirty, "operator chose no auto-commit\n");

        var deps = BuildDeps();
        deps.Settings.SetAutoCommit(ProjectName, false);

        var outcome = await deps.Transitions.MoveAsync("toggle-off-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(TaskStates.AutoReview, "toggle-off-task");
        Assert.Null(moved?.Commit);
        Assert.Empty(moved?.Commits ?? new List<TaskCommitInfo>());

        var status = deps.Git.GetStatus("toggle-off-task", _watchPath);
        Assert.True(status.IsRepo);
        Assert.Contains(status.Files, f => f.Path.EndsWith("toggle-off-change.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveProgressToAutoReview_ReadOnlyMode_SkipsAutoCommit_LeavesTreeDirty()
    {
        // Read-only-Pipeline fuer planning/research: a planning / research run
        // skips every git side effect on the transition. Even an agent edit that
        // WOULD qualify for attribution (mtime > first activity) must NOT be
        // auto-committed - the runner reports the dirty tree as a containment
        // violation instead. Contrast with
        // MoveProgressToAutoReview_AgentEditDuringRun_StampsCommit (coding mode).
        WriteJob(TaskStates.Progress, "plan-task", mode: TaskModes.Planning);
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("plan-task", firstActivity);

        var edited = Path.Combine(_repoRoot, "stray.txt");
        File.WriteAllText(edited, "agent wrote this in a read-only run\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("plan-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // No commit was stamped (auto-commit + attribution were both skipped).
        var moved = ReadJob(TaskStates.AutoReview, "plan-task");
        Assert.Null(moved?.Commit);
        Assert.Empty(moved?.Commits ?? new List<TaskCommitInfo>());

        // And the stray edit is still uncommitted in the working tree, so the
        // runner's containment check can see and report it.
        var status = deps.Git.GetStatus("plan-task", _watchPath);
        Assert.True(status.IsRepo);
        Assert.Contains(status.Files, f => f.Path.EndsWith("stray.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveProgressToAutoReview_EntersPostProcessingPhase_AndWritesEvidence()
    {
        WriteJob(TaskStates.Progress, "post-processing-task");

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("post-processing-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(TaskStates.AutoReview, "post-processing-task");
        Assert.NotNull(moved);
        Assert.Equal(LifecyclePhases.PostProcessingRunning, moved!.Phase);

        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, "post-processing-task");
        var lifecycle = File.ReadAllText(Path.Combine(folder, "lifecycle.json"));
        Assert.Contains("\"phase\": \"post-processing-running\"", lifecycle);
        Assert.Contains("orchestrator-post-processing", lifecycle);

        var outcomes = File.ReadAllText(Path.Combine(folder, PostProcessingOutcomeLog.FileName));
        Assert.Contains("\"outcome\":\"findings-added\"", outcomes);
        Assert.Contains("\"performer\":\"orchestrator\"", outcomes);
    }

    [Fact]
    public async Task MoveProgressToAutoReview_EnqueuesAutoReviewPostProcessing()
    {
        WriteJob(TaskStates.Progress, "queued-review-task");
        var queue = new RecordingAutoReviewQueue();

        var deps = BuildDeps(queue);
        var outcome = await deps.Transitions.MoveAsync("queued-review-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var request = Assert.Single(queue.Requests);
        Assert.Equal(ProjectName, request.ProjectName);
        Assert.Equal("queued-review-task", request.JobId);
        Assert.Equal(_watchPath, request.WatchPath);
        Assert.Equal("progress-to-auto-review", request.Source);
    }

    [Fact]
    public async Task MoveProgressToReady_DoesNotCommitOrStartPostProcessing()
    {
        // Steer-timeout auto-answer and Slice A process-loss recovery both
        // demote Progress -> Ready. That is a resume/retry transition, not a
        // completed core run, so it must not auto-commit, attribute commits, or
        // start/enqueue the auto-review post-processing bracket.
        WriteJob(TaskStates.Progress, "resume-task");
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("resume-task", firstActivity);
        var edited = Path.Combine(_repoRoot, "resume-work.txt");
        File.WriteAllText(edited, "work that the resumed run still owns\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));
        var queue = new RecordingAutoReviewQueue();

        var deps = BuildDeps(queue);
        var outcome = await deps.Transitions.MoveAsync("resume-task", TaskStates.Ready, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(TaskStates.Ready, "resume-task");
        Assert.NotNull(moved);
        Assert.Null(moved!.Commit);
        Assert.Empty(moved.Commits);
        Assert.Empty(queue.Requests);
        Assert.False(File.Exists(Path.Combine(moved.FolderPath, "lifecycle.json")));
        Assert.False(File.Exists(Path.Combine(moved.FolderPath, PostProcessingOutcomeLog.FileName)));

        var status = deps.Git.GetStatus("resume-task", _watchPath);
        Assert.Contains(status.Files, f => f.Path.EndsWith("resume-work.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("human:operator")]
    [InlineData("stale-progress-sweep")]
    [InlineData("run-liveness-detector")]
    public async Task ProgressRequeue_WithCompletedEnvelopedAttempt_RecoversOriginalDelivery(
        string trigger)
    {
        const string slug = "settled-run-recovery";
        const string resultSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        WriteJob(TaskStates.Progress, slug);
        var queue = new RecordingAutoReviewQueue();
        var deps = BuildDeps(queue);
        var task = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        var taskKey = !string.IsNullOrWhiteSpace(task.Key)
            ? task.Key
            : !string.IsNullOrWhiteSpace(task.TaskKey)
                ? task.TaskKey
                : task.Id;
        var acquired = deps.Authority.AcquireRun(
            taskKey,
            "demo-repository",
            sourceAttemptId: null,
            executorId: "runner-a",
            hostId: "host-a",
            requestedTtlSeconds: 120,
            idempotencyKey: "claim-1").RunAttempt!;
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "demo-repository",
            acquired.AttemptId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            resultSha,
            "refs/agent-studio/results/settled-run-recovery/attempt-1",
            null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        var settled = deps.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                acquired.AttemptId,
                acquired.LastFence,
                acquired.AuthorityEpoch,
                "completion-1"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);

        var outcome = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.Ready,
            _watchPath,
            cause: trigger,
            suppressProductExecution: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var recovered = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        Assert.Equal(TaskStates.AutoReview, recovered.State);
        Assert.Null(deps.Scanner.ScanAllJobs().SingleOrDefault(job =>
            string.Equals(job.Id, slug, StringComparison.OrdinalIgnoreCase)
            && string.Equals(job.State, TaskStates.Ready, StringComparison.Ordinal)));
        var projection = deps.Authority.GetTaskProjection(taskKey);
        Assert.Equal(acquired.AttemptId, projection.CurrentRunAttempt!.AttemptId);
        Assert.Equal(AttemptLifecycleState.Completed, projection.CurrentRunAttempt.State);
        Assert.Equal(acquired.AttemptId, projection.CurrentReviewAttempt!.SourceRunAttemptId);
        Assert.Single(projection.RunAttempts);
        Assert.Equal(resultSha, AgentStudio.Pipeline.ReviewSubjectStore.Read(recovered.FolderPath)!.ResultSha);
        Assert.Single(queue.Requests);
        Assert.Contains(
            new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(recovered.FolderPath),
            evt => evt.Kind == TimelineEventKinds.SettledRunRecovered
                   && evt.RunId == acquired.AttemptId
                   && evt.Details!["trigger"] == trigger);
    }

    [Fact]
    public async Task OperatorMoveFromEscalatedToAutoReview_OpensEpochRotatesVerdictAndQueuesFreshGate()
    {
        const string slug = "operator-requeue";
        WriteJob(TaskStates.Escalated, slug);
        var oldFolder = Path.Combine(_watchPath, TaskStates.Escalated, slug);
        File.WriteAllText(Path.Combine(oldFolder, "status.md"),
            "# Result\n\nResult: Escalated\n\nOld escalation must not drive the next verdict.");
        File.WriteAllText(Path.Combine(oldFolder, "aspect-code-quality.md"), "old BLOCK verdict");
        File.WriteAllText(
            Path.Combine(oldFolder, "code-review-2026-07-23T01-05-00Z.md"),
            "old grade D verdict");
        File.WriteAllText(
            Path.Combine(oldFolder, "post-abort-review-2026-07-23T01-00-00Z.md"),
            "old reviewer verdict");
        File.WriteAllText(
            Path.Combine(oldFolder, PipelineExecutionLog.FileName),
            "{\"runs\":[{\"verdict\":\"escalate\"}]}");
        File.WriteAllText(
            Path.Combine(oldFolder, PostProcessingOutcomeLog.FileName),
            "{\"outcome\":\"needs-human-input\"}\n");
        var contracts = Path.Combine(oldFolder, PostAbortReviewStepService.ContractsDirName);
        Directory.CreateDirectory(contracts);
        File.WriteAllText(
            Path.Combine(contracts, PostAbortReviewStepService.OutputContractName),
            "{\"action\":\"human-review\"}");
        File.WriteAllText(Path.Combine(oldFolder, "lifecycle.json"), "{\"phase\":\"escalated\"}");

        ReviewDecisionLog.Append(_watchPath, Decision(ReviewDecisionKind.Reissue));
        ReviewDecisionLog.Append(_watchPath, Decision(ReviewDecisionKind.Reissue));
        ReviewDecisionLog.Append(_watchPath, Decision(ReviewDecisionKind.Escalate));

        var queue = new RecordingAutoReviewQueue();
        var deps = BuildDeps(queue);
        var outcome = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.AutoReview,
            _watchPath,
            cause: TimelineActors.Human(""),
            reason: "Infrastructure repaired; reassess from fresh evidence.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        Assert.Equal(TaskStates.AutoReview, moved.State);
        Assert.Equal(1, OperatorReviewRequeueService.ReadEpoch(moved.FolderPath));
        Assert.True(OperatorReviewRequeueService.ReadCliLogLineBoundary(moved.FolderPath) >= 0);

        var decisions = ReviewDecisionLog.ReadAll(_watchPath, ProjectName)
            .Where(r => r.JobId == slug)
            .ToList();
        var boundary = Assert.Single(decisions, r => r.Kind == ReviewDecisionKind.OperatorRequeue);
        Assert.Equal(1, boundary.AttemptEpoch);
        Assert.Contains("Infrastructure repaired", boundary.Reason);
        Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(decisions, slug));
        Assert.True(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            boundary, 1));

        var freshStatus = File.ReadAllText(Path.Combine(moved.FolderPath, "status.md"));
        Assert.Contains("<!-- agent-studio:result-scaffold -->", freshStatus);
        Assert.DoesNotContain("Old escalation must not drive the next verdict", freshStatus);
        Assert.False(File.Exists(Path.Combine(moved.FolderPath, "aspect-code-quality.md")));
        Assert.False(File.Exists(Path.Combine(
            moved.FolderPath,
            PostAbortReviewStepService.ContractsDirName,
            PostAbortReviewStepService.OutputContractName)));
        Assert.Contains(
            Directory.EnumerateFiles(Path.Combine(moved.FolderPath, "results", "history"), "status.md", SearchOption.AllDirectories),
            _ => true);
        Assert.Contains(
            Directory.EnumerateFiles(Path.Combine(moved.FolderPath, "results", "history"), "aspect-code-quality.md", SearchOption.AllDirectories),
            _ => true);
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(moved.FolderPath, "results", "history"),
                "code-review-*.md",
                SearchOption.AllDirectories),
            _ => true);
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(moved.FolderPath, "results", "history"),
                "post-abort-review-*.md",
                SearchOption.AllDirectories),
            _ => true);
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(moved.FolderPath, "results", "history"),
                PostAbortReviewStepService.OutputContractName,
                SearchOption.AllDirectories),
            _ => true);
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(moved.FolderPath, "results", "history"),
                PipelineExecutionLog.FileName,
                SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("\"verdict\":\"escalate\"", StringComparison.Ordinal));
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(moved.FolderPath, "results", "history"),
                PostProcessingOutcomeLog.FileName,
                SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("needs-human-input", StringComparison.Ordinal));

        // EnterPostProcessingPhase recreated active lifecycle evidence after the
        // stale copy was rotated, and the full gate/aspect worker is queued.
        var lifecycle = File.ReadAllText(Path.Combine(moved.FolderPath, "lifecycle.json"));
        Assert.Contains("post-processing-running", lifecycle);
        Assert.Single(queue.Requests);

        var events = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(moved.FolderPath);
        var requeue = Assert.Single(events, e => e.Kind == TimelineEventKinds.OperatorRequeued);
        Assert.Equal("1", requeue.Details!["attemptEpoch"]);
        Assert.Equal("Infrastructure repaired; reassess from fresh evidence.", requeue.Details["reason"]);
        Assert.Equal("8", requeue.Details["rotatedArtifacts"]);
        Assert.StartsWith("results/history/review-epoch-0001/operator-requeue-", requeue.PayloadRef);
    }

    [Fact]
    public async Task Agt2707_OperatorMoveFromEscalatedToAutoReview_PreservesGeneratedResultByteForByte()
    {
        const string slug = "agt-2707-result-replay";
        WriteJob(TaskStates.Escalated, slug);
        var sourceFolder = Path.Combine(_watchPath, TaskStates.Escalated, slug);
        var generated = Encoding.UTF8.GetBytes(
            "# Status\n\n- Result: Success\n- Case: blocked\n- Duration: 2h 30m\n- Files: 12\n- Tests: 41 passed\n\n" +
            "## What Was Done\n\n- Preserved the generated run result.\n\n" +
            "## Open Items\n\n- Operator review remains.\n\n## Notes\n\n- Original bytes matter.\n");
        File.WriteAllBytes(Path.Combine(sourceFolder, "status.md"), generated);

        var queue = new RecordingAutoReviewQueue();
        var deps = BuildDeps(queue);
        var outcome = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.AutoReview,
            _watchPath,
            cause: TimelineActors.Human(""),
            reason: "Replay the 2026-09-11 AGT-2707 review requeue.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        Assert.Equal(TaskStates.AutoReview, moved.State);
        Assert.Equal(generated, File.ReadAllBytes(Path.Combine(moved.FolderPath, "status.md")));
        Assert.DoesNotContain(
            Directory.Exists(Path.Combine(moved.FolderPath, "results", "history"))
                ? Directory.EnumerateFiles(Path.Combine(moved.FolderPath, "results", "history"), "status.md", SearchOption.AllDirectories)
                : [],
            path => File.ReadAllText(path).Contains("Original bytes matter", StringComparison.Ordinal));
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task AutomaticMoves_DoNotChangeExistingOperatorEpoch()
    {
        const string slug = "automatic-recovery";
        WriteJob(TaskStates.Escalated, slug);
        var deps = BuildDeps();

        var operatorMove = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.Ready,
            _watchPath,
            cause: TimelineActors.Human(""),
            reason: "Open the first fresh review cycle.");

        Assert.Equal(MoveJobStatus.Success, operatorMove.Status);
        var requeued = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        Assert.Equal(1, OperatorReviewRequeueService.ReadEpoch(requeued.FolderPath));

        var automaticEscalation = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.Escalated,
            _watchPath,
            cause: TimelineActors.Orchestrator);
        Assert.Equal(MoveJobStatus.Success, automaticEscalation.Status);

        var automaticRecovery = await deps.Transitions.MoveAsync(
            slug,
            TaskStates.Ready,
            _watchPath,
            cause: TimelineActors.Orchestrator);

        Assert.Equal(MoveJobStatus.Success, automaticRecovery.Status);
        var moved = Assert.IsType<TaskInfo>(deps.Scanner.FindJob(slug, _watchPath));
        Assert.Equal(1, OperatorReviewRequeueService.ReadEpoch(moved.FolderPath));
        Assert.Single(
            ReviewDecisionLog.ReadAll(_watchPath, ProjectName),
            r => r.JobId == slug && r.Kind == ReviewDecisionKind.OperatorRequeue);
        Assert.Single(
            new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(moved.FolderPath),
            e => e.Kind == TimelineEventKinds.OperatorRequeued);
    }

    private Deps BuildDeps(IAutoReviewPostProcessingQueue? autoReviewQueue = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var operatorRequeue = new OperatorReviewRequeueService(
            config,
            NullLogger<OperatorReviewRequeueService>.Instance,
            timeline);
        var authority = new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance,
            sessions,
            autoReviewQueue: autoReviewQueue,
            timeline: timeline,
            operatorReviewRequeue: operatorRequeue,
            attemptAuthority: authority);
        return new Deps(scanner, transitions, git, settings, authority);
    }

    private static ReviewDecisionRecord Decision(ReviewDecisionKind kind)
        => new(
            CreatedAt: DateTime.UtcNow,
            JobId: "operator-requeue",
            Project: ProjectName,
            Kind: kind,
            Reason: kind.ToString(),
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty)
        {
            AttemptEpoch = 0,
        };

    private void WriteJob(string state, string slug, string? mode = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var modeField = mode == null ? "" : $",\"mode\":\"{mode}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"{modeField}}}");
    }

    private void AppendSessionEvent(string slug, DateTime ts)
    {
        var logsDir = Path.Combine(_watchPath, TaskStates.Progress, slug, "logs");
        Directory.CreateDirectory(logsDir);
        var line = JsonSerializer.Serialize(new SessionEvent
        {
            Ts = ts,
            Kind = "start",
            Cli = "copilot",
            HeadShaBefore = null,
            HeadShaAfter = null
        }) + Environment.NewLine;
        File.AppendAllText(Path.Combine(logsDir, "session-events.jsonl"), line, Encoding.UTF8);
    }

    private TaskInfo? ReadJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        if (!Directory.Exists(dir)) return null;
        // Force a fresh scan to pick up the post-transition stamp.
        var deps = BuildDeps();
        return deps.Scanner.FindJob(slug, _watchPath);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return so;
    }

    private sealed record Deps(
        TaskScannerService Scanner,
        TaskTransitionService Transitions,
        GitService Git,
        ProjectSettingsService Settings,
        AttemptAuthorityService Authority);

    private sealed class RecordingAutoReviewQueue : IAutoReviewPostProcessingQueue
    {
        public List<AutoReviewPostProcessingRequest> Requests { get; } = [];

        public bool Enqueue(AutoReviewPostProcessingRequest request)
        {
            Requests.Add(request);
            return true;
        }
    }
}
