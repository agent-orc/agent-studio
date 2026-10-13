using System.Collections.Immutable;
using System.Globalization;

using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskLiveStatusProjectionTests
{
    private static readonly DateTime Now = new(2026, 7, 24, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_UsesCurrentAttemptRunningStepAndItsRecordedPromptProvenance()
    {
        var pipeline = Pipeline();
        var execution = new PipelineExecutionRecord
        {
            Attempt = 3,
            StartedAt = Now.AddMinutes(-2),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-2), Now.AddMinutes(-1)),
                Step("aspect-tests", PipelineStepStatus.Running, Now.AddSeconds(-40)),
                Step("grade", PipelineStepStatus.Pending),
                Step("gate", PipelineStepStatus.Pending),
                Step("merge", PipelineStepStatus.Pending),
            ],
        };
        var prompts = new[]
        {
            new StepPromptEntry
            {
                At = Now.AddSeconds(-40),
                StepId = "aspect-tests",
                Cli = "codex",
                Model = "gpt-5.4-mini",
            },
        };

        var result = TaskLiveStatusProjection.Build(
            Job(), pipeline, execution, new ProjectSettings(), () => prompts, queue: null);

        Assert.Equal(3, result.Attempt);
        Assert.Equal("aspect-tests", result.ActiveStep?.StepId);
        Assert.Equal("Tests and evidence", result.ActiveStep?.DisplayName);
        Assert.Equal("codex", result.ActiveStep?.CliType);
        Assert.Equal("gpt-5.4-mini", result.ActiveStep?.Model);
        Assert.Equal(new[] { "Grade", "Gate", "Merge" }, result.NextSteps.Select(step => step.DisplayName));
    }

    [Fact]
    public void Build_NeverPromotesRunningStepFromPreviousAttemptIntoLiveStatus()
    {
        var previous = new PipelineExecutionRecord
        {
            Attempt = 6,
            StartedAt = Now.AddHours(-1),
            Steps = [Step("aspect-tests", PipelineStepStatus.Running, Now.AddHours(-1))],
        };
        var current = new PipelineExecutionRecord
        {
            Attempt = 7,
            StartedAt = Now.AddMinutes(-12),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-12), Now.AddMinutes(-11)),
                Step("aspect-tests", PipelineStepStatus.Pending),
                Step("grade", PipelineStepStatus.Pending),
            ],
            PreviousAttempts = [previous],
        };

        var result = TaskLiveStatusProjection.Build(
            Job(), Pipeline(), current, new ProjectSettings(), () => [], queue: null);

        Assert.Equal(7, result.Attempt);
        Assert.Null(result.ActiveStep);
        Assert.Equal("Tests and evidence", result.NextSteps[0].DisplayName);
    }

    [Fact]
    public void Build_ReadyTaskTreatsExistingRootAsPreviousAndPreviewsFreshChain()
    {
        var completed = new PipelineExecutionRecord
        {
            Attempt = 4,
            StartedAt = Now.AddMinutes(-8),
            CompletedAt = Now.AddMinutes(-1),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-8), Now.AddMinutes(-6)),
                Step("aspect-tests", PipelineStepStatus.Passed, Now.AddMinutes(-6), Now.AddMinutes(-5)),
                Step("grade", PipelineStepStatus.Passed, Now.AddMinutes(-5), Now.AddMinutes(-4)),
            ],
        };
        var ready = Job() with { State = TaskStates.Ready };

        var result = TaskLiveStatusProjection.Build(
            ready,
            Pipeline(),
            completed,
            new ProjectSettings(),
            () => [],
            new TaskLiveQueue { Kind = "runner", Position = 2 });

        Assert.Equal(5, result.Attempt);
        Assert.Null(result.ActiveStep);
        Assert.Equal("runner", result.Queue?.Kind);
        Assert.Equal(new[] { "Agent execution", "Tests and evidence", "Grade" },
            result.NextSteps.Select(step => step.DisplayName));
    }

    [Fact]
    public void Queue_ReportsAndClearsExistingReviewSlotPosition()
    {
        var queue = new AgentStudio.Runner.AutoReviewPostProcessingQueue();
        var first = Request("one", Now);
        var second = Request("two", Now.AddSeconds(1));

        Assert.True(queue.Enqueue(first));
        Assert.True(queue.Enqueue(second));
        Assert.Equal(1, queue.PositionOf("demo", "one"));
        Assert.Equal(2, queue.PositionOf("demo", "two"));

        queue.MarkStarted(first);

        Assert.Null(queue.PositionOf("demo", "one"));
        Assert.Equal(1, queue.PositionOf("demo", "two"));
    }

    // AGT-3094: the registry names its first active executor; the lease names
    // the one actually reviewing. The card must say the latter.
    [Fact]
    public void ReviewQueue_LeasedReview_NamesTheLeaseHolder_NotTheRegistryDetail()
    {
        var acquired = Now.AddMinutes(-10);
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Leased, Now.AddMinutes(1), AttemptId: "review_eb2a0d3e",
            CreatedAt: Now.AddMinutes(-20), ExecutorId: "kori-review", AcquiredAt: acquired);

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", null), null,
            Wait(PostProcessingCardResult.AwaitingCanonicalReviewVerdict,
                "agent-runner-01-review is registered and active"),
            Now)!;

        Assert.Equal(TaskLiveQueueStates.Reviewing, queue.State);
        Assert.Equal("kori-review", queue.ExecutorId);
        Assert.Equal(acquired, queue.Since);
        Assert.Null(queue.Position);
        Assert.Equal($"reviewing on kori-review since {Utc(acquired)}", queue.Reason);
    }

    [Fact]
    public void ReviewQueue_PendingReview_ShowsTheAuthorityPosition()
    {
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Pending, null, "urgent", Now.AddMinutes(-11), "review_b5277308");

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("urgent", 1), null,
            Wait(PostProcessingCardResult.AwaitingCanonicalReviewVerdict,
                "agent-runner-01-review is registered and active"),
            Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal(1, queue.Position);
        Assert.Null(queue.Reason);
        Assert.Null(queue.ExecutorId);
    }

    [Fact]
    public void ReviewQueue_PendingReviewWithoutAnyRegisteredExecutor_KeepsTheRegistrationWait()
    {
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Pending, null, CreatedAt: Now.AddMinutes(-5), AttemptId: "review_1");

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", 4), null,
            Wait(PostProcessingCardResult.AwaitingReviewExecutorRegistration, "no review executor is registered"),
            Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal(4, queue.Position);
        Assert.Equal("waiting for review executor: no review executor is registered", queue.Reason);
    }

    [Fact]
    public void ReviewQueue_LeaseThatRanOut_IsQueuedAgain()
    {
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Leased, Now.AddSeconds(-1), CreatedAt: Now.AddMinutes(-30),
            AttemptId: "review_1", ExecutorId: "kori-review", AcquiredAt: Now.AddMinutes(-20));

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", 2), null, null, Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal(2, queue.Position);
        Assert.Null(queue.ExecutorId);
        Assert.Null(queue.Reason);
    }

    [Fact]
    public void ReviewQueue_InfrastructureFailureWithAScheduledRetry_NamesTheRetry()
    {
        var due = Now.AddMinutes(3);
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Failed, null, CreatedAt: Now.AddMinutes(-10), AttemptId: "review_1",
            Outcome: ReviewTerminalOutcome.InfrastructureFailure, PendingInfrastructureRetryAt: due);

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", null), null,
            Wait(PostProcessingCardResult.AwaitingCanonicalReviewVerdict, "agent-runner-01-review is registered and active"),
            Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal("infrastructureFailure", queue.Outcome);
        Assert.Equal($"review ended (infrastructureFailure); retry scheduled for {Utc(due)}", queue.Reason);
    }

    // QS-123: the fourth infrastructure failure, retry budget spent, nothing
    // pending. That is not a wait for an executor.
    [Fact]
    public void ReviewQueue_InfrastructureFailureWithoutASuccessor_IsStalled()
    {
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Failed, null, CreatedAt: Now.AddDays(-1), AttemptId: "review_bf8f3b3d",
            Outcome: ReviewTerminalOutcome.InfrastructureFailure);

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", null), null,
            Wait(PostProcessingCardResult.AwaitingCanonicalReviewVerdict, "agent-runner-01-review is registered and active"),
            Now)!;

        Assert.Equal(TaskLiveQueueStates.Stalled, queue.State);
        Assert.Equal("infrastructureFailure", queue.Outcome);
        Assert.Equal(
            "review ended (infrastructureFailure); no new review attempt; operator decision needed",
            queue.Reason);
    }

    [Fact]
    public void ReviewQueue_NoCurrentReviewAttempt_IsStalled()
    {
        // A priority-only snapshot entry reads Completed but carries no attempt.
        var priorityOnly = new BoardReviewActivity(AttemptLifecycleState.Completed, null, "urgent");
        const string expected = "no review attempt for this delivery; operator decision needed";

        var missing = TaskLiveStatusProjection.ReviewQueueFor(true, null, null, null, null, Now)!;
        var withoutAttempt = TaskLiveStatusProjection.ReviewQueueFor(
            true, priorityOnly, new ReviewQueueEntry("urgent", null), null, null, Now)!;

        Assert.Equal(TaskLiveQueueStates.Stalled, missing.State);
        Assert.Equal(expected, missing.Reason);
        Assert.Equal(TaskLiveQueueStates.Stalled, withoutAttempt.State);
        Assert.Equal(expected, withoutAttempt.Reason);
    }

    [Fact]
    public void ReviewQueue_PassedReview_KeepsTheIntegrationWait()
    {
        var current = new BoardReviewActivity(
            AttemptLifecycleState.Completed, null, CreatedAt: Now.AddMinutes(-10), AttemptId: "review_1",
            Outcome: ReviewTerminalOutcome.Pass);

        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            true, current, new ReviewQueueEntry("normal", null), null,
            Wait(PostProcessingCardResult.AwaitingDeliveryIntegration, null), Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal("waiting for delivery integration", queue.Reason);
    }

    [Fact]
    public void ReviewQueue_LegacyCard_FallsBackToTheLocalWaitState()
    {
        var queue = TaskLiveStatusProjection.ReviewQueueFor(
            false, null, null, null,
            Wait(PostProcessingCardResult.AwaitingCanonicalReviewVerdict, "agent-runner-01-review is registered and active"),
            Now)!;
        var slotted = TaskLiveStatusProjection.ReviewQueueFor(false, null, null, 2, null, Now)!;

        Assert.Equal(TaskLiveQueueStates.Queued, queue.State);
        Assert.Equal("waiting for review executor: agent-runner-01-review is registered and active", queue.Reason);
        Assert.Equal(2, slotted.Position);
        Assert.Null(TaskLiveStatusProjection.ReviewQueueFor(false, null, null, null, null, Now));
    }

    [Fact]
    public void CanonicalAuthorityKey_UsesTheCardKey_AndSkipsReportOnlyModes()
    {
        var known = new BoardAuthoritySnapshot(
            ImmutableDictionary.Create<string, BoardReviewActivity>(StringComparer.OrdinalIgnoreCase),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "AGT-3039"));
        var keyed = Job() with { Key = "AGT-3039", TaskKey = "/workspace/demo::remote-review" };

        Assert.Equal("AGT-3039", TaskLiveStatusProjection.CanonicalAuthorityKey(keyed, known));
        Assert.Null(TaskLiveStatusProjection.CanonicalAuthorityKey(keyed with { Mode = TaskModes.Planning }, known));
        Assert.Null(TaskLiveStatusProjection.CanonicalAuthorityKey(keyed with { Key = "AGT-OTHER" }, known));
        Assert.Null(TaskLiveStatusProjection.CanonicalAuthorityKey(keyed, null));
    }

    private static AutoReviewQueueWaitState Wait(string reason, string? detail)
        => new(reason, detail, 3, Now.AddMinutes(5));

    private static string Utc(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static TaskInfo Job() => new()
    {
        Id = "AGT-2315",
        TaskKey = "demo::AGT-2315",
        ProjectName = "demo",
        State = TaskStates.AutoReview,
        LastActivity = Now.AddSeconds(-20),
        CliType = "codex",
    };

    private static TaskPipeline Pipeline() => new()
    {
        Id = "test",
        DisplayName = "Test",
        Core =
        [
            Definition("core", "Agent execution", StepKind.Core),
        ],
        Post =
        [
            Definition("aspect-tests", "Tests and evidence", StepKind.Aspect),
            Definition("grade", "Grade", StepKind.Orchestrator),
            Definition("gate", "Gate", StepKind.Tool),
            Definition("merge", "Merge", StepKind.Tool),
        ],
    };

    private static PipelineStep Definition(string id, string name, StepKind kind) => new()
    {
        Id = id,
        DisplayName = name,
        Kind = kind,
        DefaultEnabled = true,
    };

    private static PipelineStepExecution Step(
        string id,
        PipelineStepStatus status,
        DateTime? started = null,
        DateTime? completed = null) => new()
    {
        StepId = id,
        Kind = id == "core" ? StepKind.Core : StepKind.Aspect,
        Status = status,
        StartedAt = started,
        CompletedAt = completed,
    };

    private static AgentStudio.Runner.AutoReviewPostProcessingRequest Request(string id, DateTime at) => new(
        ProjectName: "demo",
        JobId: id,
        WatchPath: "/workspace",
        EnqueuedAtUtc: at,
        Source: "test");
}
