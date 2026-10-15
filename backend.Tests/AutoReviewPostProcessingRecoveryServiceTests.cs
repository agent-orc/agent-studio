using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the startup-recovery scan that re-drives 4-auto-review cards whose
/// volatile post-processing enqueue was lost on a backend restart
/// (<see cref="AgentStudio.Runner.AutoReviewPostProcessingRecoveryService"/>).
/// </summary>
public sealed class AutoReviewPostProcessingRecoveryServiceTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace;
    private readonly string _watchPath;

    public AutoReviewPostProcessingRecoveryServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "auto-review-recovery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // ---- End-to-end scan against a real workspace + scanner + queue -------------

    [Fact]
    public void RunRecoveryScan_WaitsForEngineAndReEnqueuesAfterRuntimeRollback()
    {
        var clock = new FakeTimeProvider();
        SeedAutoReviewJob("rollback-task", clock.GetUtcNow().UtcDateTime.AddMinutes(-30),
            writeEntryMarker: false, writeDecision: false);
        var deps = BuildDeps();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["Orchestration:Transitions:CodingCompletion"] = "Engine" }).Build();

        var engine = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, null, NullLogger.Instance, configuration: configuration);
        Assert.Equal(0, engine.ReEnqueued);
        Assert.Empty(Drain(deps.Queue));

        configuration["Orchestration:Transitions:CodingCompletion"] = "Monolith";
        var rollback = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, null, NullLogger.Instance, configuration: configuration);
        Assert.Equal(1, rollback.ReEnqueued);
        Assert.Single(Drain(deps.Queue));
    }

    [Fact]
    public void RunRecoveryScan_ReEnqueuesAutoReviewCardWithNoFreshOutcome()
    {
        var enteredLaneAt = DateTime.UtcNow.AddMinutes(-30);
        // Card entered post-processing (entry marker written) but never reached a
        // decision - exactly the lost-queue-entry hang the scan repairs.
        SeedAutoReviewJob("stuck-task", enteredLaneAt, writeEntryMarker: true, writeDecision: false);
        // A second card with no outcome log at all (crash before the first append).
        SeedAutoReviewJob("stuck-bare", enteredLaneAt, writeEntryMarker: false, writeDecision: false);

        var deps = BuildDeps();
        var summary = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, NullLogger.Instance);

        var enqueued = Drain(deps.Queue);
        Assert.Equal(2, summary.ReEnqueued);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(
            new[] { "stuck-bare", "stuck-task" },
            enqueued.Select(r => r.JobId).OrderBy(x => x).ToArray());
        Assert.All(enqueued, r => Assert.Equal("startup-recovery", r.Source));
    }

    [Fact]
    public void RunRecoveryScan_SkipsAutoReviewCardWithCompletedOutcome()
    {
        var enteredLaneAt = DateTime.UtcNow.AddMinutes(-30);
        // Post-processing already produced a decision after the card entered the
        // lane: it must not be re-enqueued (no double processing).
        SeedAutoReviewJob("done-task", enteredLaneAt, writeEntryMarker: true, writeDecision: true);
        WriteRunningLifecycle("done-task", enteredLaneAt);

        var deps = BuildDeps();
        var summary = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, NullLogger.Instance);

        var enqueued = Drain(deps.Queue);
        Assert.Empty(enqueued);
        Assert.Equal(0, summary.ReEnqueued);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Scanned);
        var lifecycle = ReadLifecycle("done-task");
        var check = Assert.Single(lifecycle.PostProcessingChecks);
        Assert.Equal("completed", check.Status);
        Assert.Equal(enteredLaneAt.AddSeconds(45), check.FinishedAt);
        Assert.Equal(LifecyclePhases.AwaitingReview, lifecycle.Phase);
    }

    [Fact]
    public void RunRecoveryScan_NeverTouchesHumanReviewLane()
    {
        var enteredLaneAt = DateTime.UtcNow.AddMinutes(-30);
        // A human-review card with no decision outcome would look "unfinished" by
        // the same heuristic, but the scan must only ever act on 4-auto-review.
        SeedJobInState(TaskStates.HumanReview, "human-card", enteredLaneAt, writeEntryMarker: false, writeDecision: false);
        SeedJobInState(TaskStates.Escalated, "escalated-card", enteredLaneAt, writeEntryMarker: false, writeDecision: false);

        var deps = BuildDeps();
        var summary = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, NullLogger.Instance);

        Assert.Empty(Drain(deps.Queue));
        Assert.Equal(0, summary.Scanned);
        Assert.Equal(0, summary.ReEnqueued);
    }

    [Fact]
    public void RunRecoveryScan_ExcludesFixturesAndReplacesChecksOlderThanRecovery()
    {
        var enteredLaneAt = new DateTime(2026, 08, 02, 08, 00, 00, DateTimeKind.Utc);
        var recoveryStartedAt = enteredLaneAt.AddHours(2);
        SeedAutoReviewJob("real-recovery", enteredLaneAt, writeEntryMarker: true, writeDecision: false);
        SeedAutoReviewJob("RUN-101", enteredLaneAt, writeEntryMarker: true, writeDecision: false, fixture: true);
        WriteRunningLifecycle("real-recovery", enteredLaneAt);
        WriteRunningLifecycle("RUN-101", enteredLaneAt);

        var deps = BuildDeps();
        var summary = AgentStudio.Runner.AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            deps.Scanner, deps.Transitions, NullLogger.Instance, recoveryStartedAt);

        var request = Assert.Single(Drain(deps.Queue));
        Assert.Equal("real-recovery", request.JobId);
        Assert.Equal(1, summary.Scanned);
        Assert.Equal(1, summary.ReEnqueued);

        var recovered = ReadLifecycle("real-recovery");
        Assert.Equal(LifecyclePhases.PostProcessingRunning, recovered.Phase);
        Assert.NotEmpty(recovered.PostProcessingChecks);
        Assert.All(recovered.PostProcessingChecks, check =>
        {
            Assert.Equal("running", check.Status);
            Assert.NotNull(check.StartedAt);
            Assert.True(check.StartedAt >= recoveryStartedAt,
                $"Recovery kept stale check {check.Name} from {check.StartedAt:o} before recovery {recoveryStartedAt:o}.");
        });
        Assert.Equal(recoveryStartedAt, recovered.PhaseEnteredAt);

        var fixture = ReadLifecycle("RUN-101");
        Assert.Equal(enteredLaneAt, fixture.PhaseEnteredAt);
        Assert.Single(fixture.PostProcessingChecks);
        Assert.Equal("stale-check", fixture.PostProcessingChecks[0].Name);
    }

    [Fact]
    public void ConfirmedExecutionStart_ClearsPriorPostProcessingAttempt()
    {
        var previousAttempt = new DateTime(2026, 08, 02, 08, 00, 00, DateTimeKind.Utc);
        var restartedAt = previousAttempt.AddHours(1);
        SeedAutoReviewJob("execution-retry", previousAttempt, writeEntryMarker: true, writeDecision: false);
        WriteRunningLifecycle("execution-retry", previousAttempt);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, "execution-retry");

        var updated = PostProcessingLifecycleStore.ResetForExecution(
            folder, restartedAt, NullLogger.Instance);

        Assert.True(updated);
        var lifecycle = ReadLifecycle("execution-retry");
        Assert.Equal(LifecyclePhases.ExecutionRunning, lifecycle.Phase);
        Assert.Equal(restartedAt, lifecycle.PhaseEnteredAt);
        Assert.Empty(lifecycle.PostProcessingChecks);
    }

    [Theory]
    [InlineData(TaskStates.HumanReview, "completed")]
    [InlineData(TaskStates.Ready, "failed")]
    public async Task LeavingAutoReview_TerminalizesRunningChecks(string targetState, string expectedStatus)
    {
        var startedAt = DateTime.UtcNow.AddHours(-1);
        SeedAutoReviewJob("terminal-check", startedAt, writeEntryMarker: true, writeDecision: false);
        WriteRunningLifecycle("terminal-check", startedAt);
        var deps = BuildDeps();

        var move = await deps.Transitions.MoveAsync(
            "terminal-check", targetState, _watchPath, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Success, move.Status);
        var lifecycle = ReadLifecycleAt(move.NewFolderPath!);
        var check = Assert.Single(lifecycle.PostProcessingChecks);
        Assert.Equal(expectedStatus, check.Status);
        Assert.NotNull(check.FinishedAt);
        Assert.NotEqual(LifecyclePhases.PostProcessingRunning, lifecycle.Phase);
    }

    // ---- Pure heuristic boundary tests -----------------------------------------

    [Fact]
    public void NeedsPostProcessingRecovery_FreshDecision_IsComplete()
    {
        var t0 = new DateTime(2026, 07, 20, 12, 00, 00, DateTimeKind.Utc);
        var job = AutoReviewInfo(t0);
        var outcomes = new List<PostProcessingOutcomeRecord>
        {
            EntryMarker(t0),
            Decision(t0.AddSeconds(45)),
        };
        Assert.False(AgentStudio.Runner.AutoReviewPostProcessingRecoveryService
            .NeedsPostProcessingRecovery(job, outcomes));
    }

    [Fact]
    public void NeedsPostProcessingRecovery_StaleDecisionFromEarlierOccupancy_NeedsRecovery()
    {
        var t0 = new DateTime(2026, 07, 20, 12, 00, 00, DateTimeKind.Utc);
        var job = AutoReviewInfo(t0);
        // Decision predates the latest entry into the lane (card was reissued
        // 4 -> 3 -> 4 after this decision): does not count as fresh.
        var outcomes = new List<PostProcessingOutcomeRecord>
        {
            Decision(t0.AddMinutes(-10)),
            EntryMarker(t0),
        };
        Assert.True(AgentStudio.Runner.AutoReviewPostProcessingRecoveryService
            .NeedsPostProcessingRecovery(job, outcomes));
    }

    [Fact]
    public void NeedsPostProcessingRecovery_OnlyEntryMarker_NeedsRecovery()
    {
        var t0 = new DateTime(2026, 07, 20, 12, 00, 00, DateTimeKind.Utc);
        var job = AutoReviewInfo(t0);
        var outcomes = new List<PostProcessingOutcomeRecord> { EntryMarker(t0) };
        Assert.True(AgentStudio.Runner.AutoReviewPostProcessingRecoveryService
            .NeedsPostProcessingRecovery(job, outcomes));
    }

    [Fact]
    public void NeedsPostProcessingRecovery_NonAutoReviewState_IsFalse()
    {
        var t0 = new DateTime(2026, 07, 20, 12, 00, 00, DateTimeKind.Utc);
        var job = AutoReviewInfo(t0) with { State = TaskStates.HumanReview };
        Assert.False(AgentStudio.Runner.AutoReviewPostProcessingRecoveryService
            .NeedsPostProcessingRecovery(job, new List<PostProcessingOutcomeRecord>()));
    }

    // ---- Fixture helpers -------------------------------------------------------

    private static TaskInfo AutoReviewInfo(DateTime enteredLaneAt) => new()
    {
        Id = "job",
        State = TaskStates.AutoReview,
        ProjectName = Project,
        WatchPath = "watch",
        FolderPath = "folder",
        EnteredLaneAt = enteredLaneAt,
    };

    private static PostProcessingOutcomeRecord EntryMarker(DateTime at) => new()
    {
        At = at,
        JobId = "job",
        Project = Project,
        Outcome = PostProcessingOutcomes.FindingsAdded,
        Performer = PostProcessingPerformers.Orchestrator,
        StepId = PipelineCatalogue.GitCommitAttributionStepId,
        Summary = "Entered orchestrator post-processing after task execution.",
    };

    private static PostProcessingOutcomeRecord Decision(DateTime at) => new()
    {
        At = at,
        JobId = "job",
        Project = Project,
        Outcome = PostProcessingOutcomes.PassToHumanReview,
        Performer = PostProcessingPerformers.Orchestrator,
        StepId = PipelineCatalogue.OrchestratorDecisionStepId,
        Summary = "Auto-review decision complete.",
    };

    private static List<AgentStudio.Runner.AutoReviewPostProcessingRequest> Drain(
        AgentStudio.Runner.AutoReviewPostProcessingQueue queue)
    {
        var items = new List<AgentStudio.Runner.AutoReviewPostProcessingRequest>();
        while (queue.Reader.TryRead(out var request)) items.Add(request);
        return items;
    }

    private void SeedAutoReviewJob(
        string slug,
        DateTime enteredLaneAt,
        bool writeEntryMarker,
        bool writeDecision,
        bool fixture = false)
        => SeedJobInState(TaskStates.AutoReview, slug, enteredLaneAt, writeEntryMarker, writeDecision, fixture);

    private void SeedJobInState(
        string state,
        string slug,
        DateTime enteredLaneAt,
        bool writeEntryMarker,
        bool writeDecision,
        bool fixture = false)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"enteredLaneAt\":\"{enteredLaneAt:o}\",\"fixture\":{fixture.ToString().ToLowerInvariant()}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nWork body.\n");

        if (writeEntryMarker)
        {
            PostProcessingOutcomeLog.Append(dir, new PostProcessingOutcomeRecord
            {
                At = enteredLaneAt,
                JobId = slug,
                Project = Project,
                Outcome = PostProcessingOutcomes.FindingsAdded,
                Performer = PostProcessingPerformers.Orchestrator,
                StepId = PipelineCatalogue.GitCommitAttributionStepId,
                Summary = "Entered orchestrator post-processing after task execution.",
            }, NullLogger.Instance);
        }

        if (writeDecision)
        {
            PostProcessingOutcomeLog.Append(dir, new PostProcessingOutcomeRecord
            {
                At = enteredLaneAt.AddSeconds(45),
                JobId = slug,
                Project = Project,
                Outcome = PostProcessingOutcomes.PassToHumanReview,
                Performer = PostProcessingPerformers.Orchestrator,
                StepId = PipelineCatalogue.OrchestratorDecisionStepId,
                Summary = "Auto-review decision complete.",
            }, NullLogger.Instance);
        }
    }

    private void WriteRunningLifecycle(string slug, DateTime startedAt)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        var snapshot = new LifecycleSnapshot
        {
            Phase = LifecyclePhases.PostProcessingRunning,
            PhaseEnteredAt = startedAt,
            PostProcessingChecks =
            [
                new LifecycleCheck
                {
                    Name = "stale-check",
                    Status = "running",
                    StartedAt = startedAt,
                },
            ],
        };
        File.WriteAllText(
            Path.Combine(dir, "lifecycle.json"),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
    }

    private LifecycleSnapshot ReadLifecycle(string slug)
        => ReadLifecycleAt(Path.Combine(_watchPath, TaskStates.AutoReview, slug));

    private static LifecycleSnapshot ReadLifecycleAt(string folderPath)
        => JsonSerializer.Deserialize<LifecycleSnapshot>(
            File.ReadAllText(Path.Combine(folderPath, "lifecycle.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private Deps BuildDeps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var queue = new AgentStudio.Runner.AutoReviewPostProcessingQueue();
        var transitions = new TaskTransitionService(
            scanner,
            stateMachine,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            autoReviewQueue: queue);

        return new Deps(scanner, transitions, queue);
    }

    private sealed record Deps(
        TaskScannerService Scanner,
        TaskTransitionService Transitions,
        AgentStudio.Runner.AutoReviewPostProcessingQueue Queue);
}
