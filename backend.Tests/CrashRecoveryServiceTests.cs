using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Crash-recovery doctrine (ADR-0020). Two simulations:
///
/// <list type="number">
///   <item><b>Surviving completion marker.</b> A job sits in <c>3-progress</c>
///   with a <c>completion-marker.json</c> next to it - the proxy for a runner
///   that crashed between deciding to transition and actually moving the
///   folder. After <see cref="CrashRecoveryService.RecoverAsync"/>, the job
///   must be in <c>4-review</c> and the marker gone.</item>
///   <item><b>Orphan working-tree changes.</b> The repo has uncommitted files
///   and a recently-active job in <c>3-progress</c>. After recovery, the
///   working tree is clean and the resulting commit carries the fixed
///   <c>crash-recovery</c> author tag so it's findable in <c>git log</c>.</item>
/// </list>
/// </summary>
public sealed class CrashRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _logDir;
    private const string ProjectName = "demo";

    public CrashRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-crash-recovery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        _logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_logDir);

        RunGit(_repoRoot, "init -q -b main");
        RunGit(_repoRoot, "config user.email test@example.com");
        RunGit(_repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed");
        RunGit(_repoRoot, "add -A");
        RunGit(_repoRoot, "commit -q -m seed");
    }

    // AGT-2858: TryDelete already clears the read-only attribute Git puts on
    // .git/objects and retries a handle Windows has not released yet, so the
    // fixture no longer has to walk the tree itself.
    public void Dispose() => TestTempRoot.TryDelete(_tempDir);

    [Fact]
    public async Task RecoverAsync_SurvivingCompletionMarker_FinishesProgressToReviewTransition()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "demo-task");

        // Drop a marker as if the runner had matched [[TASK_DONE]] but
        // crashed before MoveAsync ran.
        CompletionMarker.Write(jobFolder, new CompletionMarker
        {
            TargetState = TaskStates.AutoReview,
            ExecutionStatus = "completed",
            AgentOutcome = "Done"
        });

        var (recovery, scanner) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.False(Directory.Exists(jobFolder), "job folder must move out of 3-progress");
        var newFolder = Path.Combine(_watchPath, TaskStates.AutoReview, "demo-task");
        Assert.True(Directory.Exists(newFolder), "job folder must land in 4-review");
        Assert.False(File.Exists(CompletionMarker.PathFor(newFolder)),
            "completion-marker.json must be cleared after a successful recovery move");

        var transition = Assert.Single(decisions);
        Assert.Equal(RecoveryDecisionKinds.TransitionCompleted, transition.Kind);
        Assert.Equal("demo-task", transition.JobId);
        Assert.Equal(TaskStates.AutoReview, transition.TargetState);

        // recovery.jsonl: one structured row per decision so an external
        // operator (or Layer 3 review) can parse it without tailing logs.
        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("transition-completed", jsonl);
        Assert.Contains("\"jobId\":\"demo-task\"", jsonl);
    }

    [Fact]
    public async Task RecoverAsync_OrphanWorkingTreeChanges_AreQueuedUntilConfirmed()
    {
        WriteJob(TaskStates.Progress, "active-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "active-task");
        // lastProgressAt makes "active-task" the attribution target.
        StampLastProgressAt(jobFolder, DateTime.UtcNow);

        // Simulate orphan changes: a tracked file modification + a brand
        // new untracked file. Both must end up in the recovery commit.
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "modified post-crash");
        File.WriteAllText(Path.Combine(_repoRoot, "new-file.txt"), "agent left this behind");

        var (recovery, scanner) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        var pending = Assert.Single(recovery.GetPendingOrphanRecoveries());
        Assert.Equal(ProjectName, pending.ProjectName);
        Assert.Equal("active-task", pending.JobId);
        Assert.Contains("README.md", pending.Files);
        Assert.Contains("new-file.txt", pending.Files);

        var queuedDecision = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanPending);
        Assert.Equal("active-task", queuedDecision.JobId);
        Assert.Contains(pending.Id, queuedDecision.Reason);

        // The boot sweep must not commit autonomously.
        var dirtyBeforeConfirmation = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.False(string.IsNullOrWhiteSpace(dirtyBeforeConfirmation),
            "working tree must remain dirty until the operator confirms recovery");

        var result = recovery.CommitPendingOrphanRecovery(pending.Id);
        Assert.Equal(CrashRecoveryActionStatuses.Committed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));

        // Working tree must be clean after confirmation.
        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.True(string.IsNullOrWhiteSpace(status),
            $"working tree must be clean after orphan recovery; got: {status}");

        // The new commit must carry the crash-recovery author tag.
        var lastAuthor = RunGitCapture(_repoRoot, "log -1 --format=%ae");
        Assert.Contains("crash-recovery", lastAuthor, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(recovery.GetPendingOrphanRecoveries());

        var gateEvidence = Path.Combine(jobFolder, "results", "commit-candidate-gate.json");
        Assert.True(File.Exists(gateEvidence));
        var evidenceText = File.ReadAllText(gateEvidence);
        Assert.Contains("crash-recovery", evidenceText);
        Assert.Contains("active-task", evidenceText);

        // Decision is mirrored both in recovery.jsonl and in the daily backend log.
        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("orphan-pending-confirmation", jsonl);
        Assert.Contains("orphan-committed", jsonl);

        var dailyLog = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(dailyLog), "daily backend log must exist after recovery sweep");
        Assert.Contains("Backend.CrashRecovery", File.ReadAllText(dailyLog));
    }

    [Fact]
    public async Task RecoverAsync_SameFindingAfterBackendRestart_KeepsPendingId()
    {
        WriteJob(TaskStates.Progress, "restart-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "restart-task");
        var firstSessionAt = new DateTime(2026, 8, 3, 21, 15, 0, DateTimeKind.Utc);
        var firstFindingAt = firstSessionAt.AddMinutes(1);
        StampLastProgressAt(jobFolder, firstSessionAt.AddMinutes(5));
        AppendSessionEvent(jobFolder, firstSessionAt);

        var dirtyPath = Path.Combine(_repoRoot, "restart-recovery.txt");
        File.WriteAllText(dirtyPath, "survived backend restart\n");
        File.SetLastWriteTimeUtc(dirtyPath, firstFindingAt);

        var (beforeRestart, _) = BuildRecovery();
        await beforeRestart.RecoverAsync();
        var original = Assert.Single(beforeRestart.GetPendingOrphanRecoveries());

        // A new service instance represents a fresh backend boot. The pending
        // list itself is in memory, so stable identity must come from the
        // finding rather than from service-instance state.
        var (afterRestart, _) = BuildRecovery();
        await afterRestart.RecoverAsync();
        var recovered = Assert.Single(afterRestart.GetPendingOrphanRecoveries());

        Assert.Equal(firstFindingAt, original.CreatedAt);
        Assert.Equal(original.CreatedAt, recovered.CreatedAt);
        Assert.Equal(original.Id, recovered.Id);
        Assert.NotEqual(original.BootId, recovered.BootId);
        Assert.NotEqual(Guid.Empty, Guid.ParseExact(original.BootId, "N"));
        Assert.True(original.DetectedAt >= firstFindingAt);
        Assert.True(recovered.DetectedAt >= original.DetectedAt);
        Assert.Equal(64, recovered.Id.Length);
        Assert.All(recovered.Id, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public async Task DismissPendingOrphanRecovery_StaleId_RemainsNotFound()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "stale-id.txt"), "pending\n");
        var (recovery, _) = BuildRecovery();
        await recovery.RecoverAsync();
        var current = Assert.Single(recovery.GetPendingOrphanRecoveries());

        var result = recovery.DismissPendingOrphanRecovery("stale-" + current.Id);

        Assert.Equal(CrashRecoveryActionStatuses.NotFound, result.Status);
        Assert.Equal("Pending crash recovery item not found.", result.Error);
        Assert.Equal(current.Id, Assert.Single(recovery.GetPendingOrphanRecoveries()).Id);
    }

    [Fact]
    public void PendingId_UsesProjectWorktreeClassificationAndFirstTimestamp()
    {
        var firstObservedAt = new DateTime(2026, 8, 3, 21, 15, 0, DateTimeKind.Utc);
        var baseline = CrashRecoveryPendingId.Create(
            ProjectName, _repoRoot, CrashRecoveryClassifications.ReviewRequired, firstObservedAt);

        Assert.Equal(
            baseline,
            CrashRecoveryPendingId.Create(
                " DEMO ",
                _repoRoot + Path.DirectorySeparatorChar,
                "REVIEW-REQUIRED",
                firstObservedAt));
        Assert.NotEqual(
            baseline,
            CrashRecoveryPendingId.Create(
                "another-project", _repoRoot, CrashRecoveryClassifications.ReviewRequired, firstObservedAt));
        Assert.NotEqual(
            baseline,
            CrashRecoveryPendingId.Create(
                ProjectName, _watchPath, CrashRecoveryClassifications.ReviewRequired, firstObservedAt));
        Assert.NotEqual(
            baseline,
            CrashRecoveryPendingId.Create(
                ProjectName, _repoRoot, CrashRecoveryClassifications.Trivial, firstObservedAt));
        Assert.NotEqual(
            baseline,
            CrashRecoveryPendingId.Create(
                ProjectName, _repoRoot, CrashRecoveryClassifications.ReviewRequired, firstObservedAt.AddTicks(1)));
    }

    [Fact]
    public async Task RecoverAsync_OrphanWorkingTreeChanges_ScopesCommitToActiveTaskFiles()
    {
        WriteJob(TaskStates.Progress, "active-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "active-task");
        StampLastProgressAt(jobFolder, DateTime.UtcNow);

        // Foreign dirty changes existed before this run started. They must
        // remain dirty, not ride along in the active task's recovery commit.
        var oldDirtyAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "foreign edit");
        File.SetLastWriteTimeUtc(Path.Combine(_repoRoot, "README.md"), oldDirtyAt);
        File.WriteAllText(Path.Combine(_repoRoot, "foreign.txt"), "foreign new file");
        File.SetLastWriteTimeUtc(Path.Combine(_repoRoot, "foreign.txt"), oldDirtyAt);

        var runStartedAt = DateTime.UtcNow;
        AppendSessionEvent(jobFolder, runStartedAt);

        File.WriteAllText(Path.Combine(_repoRoot, "alpha.txt"), "agent alpha");
        File.SetLastWriteTimeUtc(Path.Combine(_repoRoot, "alpha.txt"), runStartedAt.AddSeconds(30));
        File.WriteAllText(Path.Combine(_repoRoot, "beta.txt"), "agent beta");
        File.SetLastWriteTimeUtc(Path.Combine(_repoRoot, "beta.txt"), runStartedAt.AddSeconds(30));

        var (recovery, scanner) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        var pending = Assert.Single(recovery.GetPendingOrphanRecoveries());
        Assert.Equal("active-task", pending.JobId);
        Assert.Contains("alpha.txt", pending.Files);
        Assert.Contains("beta.txt", pending.Files);
        Assert.DoesNotContain("README.md", pending.Files);
        Assert.DoesNotContain("foreign.txt", pending.Files);
        var queuedDecision = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanPending);
        Assert.Equal("active-task", queuedDecision.JobId);

        var result = recovery.CommitPendingOrphanRecovery(pending.Id);
        Assert.Equal(CrashRecoveryActionStatuses.Committed, result.Status);

        var committed = RunGitCapture(_repoRoot, "show --name-only --pretty=format: HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Equal(2, committed.Count);
        Assert.Contains("alpha.txt", committed);
        Assert.Contains("beta.txt", committed);
        Assert.DoesNotContain("README.md", committed);
        Assert.DoesNotContain("foreign.txt", committed);

        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.Contains("README.md", status);
        Assert.Contains("foreign.txt", status);

        var moved = scanner.FindJob("active-task", _watchPath);
        Assert.NotNull(moved);
        Assert.Equal(2, moved!.Commit?.FilesChanged);
        Assert.Contains("alpha.txt", moved.Commit!.Files);
        Assert.Contains("beta.txt", moved.Commit!.Files);
    }

    [Fact]
    public async Task RecoverAsync_OrphanChangesWithNoActiveJob_AreQueuedForOperatorDecision()
    {
        // C1 (2026-05-22): uncommitted changes WITHOUT an active 3-progress
        // job are usually a human editor session, not a crashed agent run,
        // so the recovery sweep queues a confirmation item instead of
        // committing those changes blindly.
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "edited by a human");
        File.WriteAllText(Path.Combine(_repoRoot, "new-file.txt"), "from a Claude Code session");

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        // Working tree must stay dirty — the operator's edits are preserved.
        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.False(string.IsNullOrWhiteSpace(status),
            "uncommitted edits must be preserved when there is no active job to attribute them to");

        var pending = Assert.Single(recovery.GetPendingOrphanRecoveries());
        Assert.Null(pending.JobId);
        Assert.Contains("README.md", pending.Files);
        Assert.Contains("new-file.txt", pending.Files);

        var queued = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanPending);
        Assert.Null(queued.JobId);
        Assert.Contains(pending.Id, queued.Reason);
        Assert.DoesNotContain(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanCommitted);

        var dismiss = recovery.DismissPendingOrphanRecovery(pending.Id);
        Assert.Equal(CrashRecoveryActionStatuses.Dismissed, dismiss.Status);
        Assert.Empty(recovery.GetPendingOrphanRecoveries());

        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("orphan-pending-confirmation", jsonl);
        Assert.Contains("operator dismissed pending orphan recovery", jsonl);
    }

    [Theory]
    [InlineData(null, new[] { "docs/runner.md.meta.json", "docs/chat.md.meta.json" }, CrashRecoveryClassifications.Trivial)]
    [InlineData("", new[] { "DOCS/README.MD.META.JSON" }, CrashRecoveryClassifications.Trivial)]
    [InlineData("AGT-2435", new[] { "docs/runner.md.meta.json" }, CrashRecoveryClassifications.ReviewRequired)]
    [InlineData(null, new[] { "docs/runner.md.meta.json", "frontend/src/app.ts" }, CrashRecoveryClassifications.ReviewRequired)]
    [InlineData(null, new string[0], CrashRecoveryClassifications.ReviewRequired)]
    public void PendingRecovery_Classification_RequiresOnlyUnattributedMetadataSidecars(
        string? jobId,
        string[] files,
        string expected)
    {
        Assert.Equal(expected, CrashRecoveryClassifications.Classify(jobId, files));
    }

    [Fact]
    public async Task RecoverAsync_UnknownDirectCheckout_CannotWidenAfterOperatorReview()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "reviewed.txt"), "reviewed before confirmation\n");

        var (recovery, _) = BuildRecovery();
        await recovery.RecoverAsync();
        var pending = Assert.Single(recovery.GetPendingOrphanRecoveries());
        Assert.Equal(["reviewed.txt"], pending.Pathspecs);

        File.WriteAllText(Path.Combine(_repoRoot, "concurrent.txt"), "appeared after review\n");
        var result = recovery.CommitPendingOrphanRecovery(pending.Id);

        Assert.Equal(CrashRecoveryActionStatuses.Committed, result.Status);
        Assert.Equal("reviewed.txt", RunGitCapture(_repoRoot, "show --name-only --pretty=format: HEAD"));
        Assert.Contains("?? concurrent.txt", RunGitCapture(_repoRoot, "status --short"));
    }

    [Fact]
    public async Task RecoverAsync_InterruptedRun_RequeuesToReadyAndClearsStaleLock()
    {
        // Simulate a run that crashed after the runner stamped the pickup
        // lock but before its finally-block released it: a 3-progress job with
        // a .pickup-lock.json whose owning pid is dead on this host.
        WriteJob(TaskStates.Progress, "interrupted-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "interrupted-task");
        WriteRawLock(jobFolder, new PickupLockInfo
        {
            Pid = 0x7FFFFFFE, // obviously-dead pid (same convention as PickupLockFileTests)
            Hostname = Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            AcquiredAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        // Job must move back to 2-ready so the next pickup tick starts it clean.
        Assert.False(Directory.Exists(jobFolder), "job must leave 3-progress");
        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "interrupted-task");
        Assert.True(Directory.Exists(readyFolder), "job must land in 2-ready");

        // No stale .pickup-lock.json after recovery by any path.
        Assert.False(File.Exists(Path.Combine(readyFolder, PickupLockFile.LockFileName)),
            "stale pickup lock must be cleared");

        // Mandatory diagnostic: the requeue is never silent. The 2026-05-30
        // incident had an empty logs/ dir and no cli-output.log at all.
        var cliLog = Path.Combine(readyFolder, TaskPaths.LogsDirName, TaskPaths.CliOutputLogFileName);
        Assert.True(File.Exists(cliLog), "interrupted-run diagnostic must be written to cli-output.log");
        // One compact recovery line on the [orchestrator] stream so the chat
        // renders it as a calm [recovery] notice (not a fat lock-detail block).
        var cliText = File.ReadAllText(cliLog);
        Assert.Contains($"[{RecoveryChatLine.RecoveryTag}] {RecoveryChatLine.ReasonCrash}", cliText);
        Assert.Contains($"requeued to {TaskStates.Ready}", cliText);
        // The long form (which backend / pid held the stale lock) stays out of chat.
        Assert.DoesNotContain("pid=", cliText);

        var decision = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.RunInterruptedRequeued);
        Assert.Equal("interrupted-task", decision.JobId);
        Assert.Equal(TaskStates.Ready, decision.TargetState);

        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("run-interrupted-requeued", jsonl);
    }

    [Fact]
    public async Task RecoverAsync_ValidSteerPendingMarker_OwnsStaleLockRecovery()
    {
        WriteJob(TaskStates.Progress, "steer-wait");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "steer-wait");
        SteerPendingMarker.Write(jobFolder, new SteerPendingRecord
        {
            WaitStartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(5),
            Kind = SteerPendingKinds.Steer,
            Ask = "ist iframe schon implementiert?"
        });
        WriteRawLock(jobFolder, new PickupLockInfo
        {
            Pid = 0x7FFFFFFE,
            Hostname = Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            AcquiredAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.True(Directory.Exists(jobFolder));
        Assert.True(SteerPendingMarker.Exists(jobFolder));
        Assert.True(File.Exists(Path.Combine(jobFolder, PickupLockFile.LockFileName)));
        Assert.DoesNotContain(decisions, d => d.Kind == RecoveryDecisionKinds.RunInterruptedRequeued);
    }

    [Fact]
    public async Task RecoverAsync_LiveForeignLock_LeavesRunInProgress()
    {
        // A live foreign owner (e.g. the other backend on the same workspace)
        // still holds the lock. That run is not ours to requeue - leave the
        // job and its lock exactly where they are.
        WriteJob(TaskStates.Progress, "foreign-active");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "foreign-active");
        WriteRawLock(jobFolder, new PickupLockInfo
        {
            Pid = Environment.ProcessId, // guaranteed-alive pid
            Hostname = Environment.MachineName,
            Role = RunnerRoles.TestSubject,
            BackendName = "dev",
            AcquiredAt = DateTime.UtcNow
        });

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.True(Directory.Exists(jobFolder), "job must stay in 3-progress while a live owner holds the lock");
        Assert.True(File.Exists(Path.Combine(jobFolder, PickupLockFile.LockFileName)),
            "a live foreign lock must be left untouched");
        Assert.DoesNotContain(decisions, d => d.Kind == RecoveryDecisionKinds.RunInterruptedRequeued);
        Assert.DoesNotContain(decisions, d => d.Kind == RecoveryDecisionKinds.StalePickupLockCleared);
    }

    [Fact]
    public async Task RecoverAsync_NoMarkersAndCleanTree_IsANoOp()
    {
        WriteJob(TaskStates.Progress, "untouched");

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.Empty(decisions);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "untouched")));
    }

    [Fact]
    public async Task RecoverAsync_CrashRecoveryDisabled_SkipsProjectSweep()
    {
        WriteJob(TaskStates.Progress, "active-task");
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "active-task");
        StampLastProgressAt(jobFolder, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "dirty but disabled");

        var (recovery, _) = BuildRecovery(settings => settings.SetCrashRecoveryEnabled(ProjectName, false));
        var decisions = await recovery.RecoverAsync();

        Assert.Empty(decisions);
        Assert.Empty(recovery.GetPendingOrphanRecoveries());
        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.Contains("README.md", status);
    }

    private (CrashRecoveryService Recovery, TaskScannerService Scanner) BuildRecovery(
        Action<ProjectSettingsService>? configureSettings = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repoRoot,
            ["WatchPaths:0:RepositoryPath"] = _repoRoot,
            ["TaskRepository"] = _tempDir,
            ["Logging:BackendFile:LogDirectory"] = _logDir
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        configureSettings?.Invoke(settings);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);

        var logOptions = new BackendFileLoggerOptions { LogDirectory = _logDir, RetentionDays = 14 };
        var sink = new BackendFileLogSink(logOptions);
        var recovery = new CrashRecoveryService(
            scanner, transitions, mutations, git, settings,
            sink, Options.Create(logOptions),
            NullLogger<CrashRecoveryService>.Instance);
        return (recovery, scanner);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static void WriteRawLock(string jobFolder, PickupLockInfo info)
    {
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(jobFolder, PickupLockFile.LockFileName), json);
    }

    private static void StampLastProgressAt(string jobFolder, DateTime utc)
    {
        var jsonPath = Path.Combine(jobFolder, "task.json");
        var json = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        var newDict = new Dictionary<string, object>();
        foreach (var kv in dict) newDict[kv.Key] = kv.Value;
        newDict["lastProgressAt"] = utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(newDict, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AppendSessionEvent(string jobFolder, DateTime utc)
    {
        var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
        Directory.CreateDirectory(logsDir);
        var line = JsonSerializer.Serialize(new SessionEvent
        {
            Ts = utc,
            Kind = "start",
            Cli = "copilot"
        }) + Environment.NewLine;
        File.AppendAllText(Path.Combine(logsDir, TaskPaths.SessionEventsLogFileName), line);
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private static string RunGitCapture(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output.Trim();
    }
}
