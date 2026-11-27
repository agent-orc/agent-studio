using AgentStudio.Shared;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Projects;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: each fixture instant is supplied to the pure aggregation as its clock.
public sealed class EnvironmentFlowServiceTests
{
    [Fact]
    public void Aggregate_UsesInjectedInstant_ForTodayAndRollingLaneWindow()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var task = Task("one", TaskStates.Completed, now.AddHours(-2));
        var older = Task("old", TaskStates.Ready, now.AddDays(-3));
        var history = new Dictionary<string, IReadOnlyList<TimelineEvent>>
        {
            ["one"] = [
                Move(now.AddHours(-26), TaskStates.Preparation, TaskStates.Ready),
                Move(now.AddHours(-4), TaskStates.Ready, TaskStates.Progress),
                Move(now.AddHours(-3), TaskStates.Progress, TaskStates.AutoReview),
                new TimelineEvent { Ts = now.AddHours(-2.7), Kind = TimelineEventKinds.PostStepFinished,
                    Details = new Dictionary<string, string> { ["pipelineStepId"] = PipelineCatalogue.BuildTestGateStepId } },
                Move(now.AddHours(-2.5), TaskStates.AutoReview, TaskStates.HumanReview),
                new TimelineEvent { Ts = now.AddHours(-2.2), Kind = TimelineEventKinds.IntegrationSucceeded },
                Move(now.AddHours(-2), TaskStates.HumanReview, TaskStates.Completed),
            ],
            ["old"] = [],
        };

        var result = EnvironmentFlowService.Aggregate([task, older], t => history[t.Id], now);

        Assert.Equal(now, result.CapturedAt);
        Assert.Equal(1, result.Today.Created);
        Assert.Equal(1, result.Today.Delivered);
        Assert.Equal(1, result.Today.Reviewed);
        Assert.Equal(1, result.Today.Integrated);
        Assert.Equal(1, result.Today.Completed);
        Assert.Equal(1428, result.Today.ReadyToIntegrationMedianMinutes);
        Assert.Equal(1, result.Lanes.Single(l => l.Lane == TaskStates.Ready).Left);
        Assert.Equal(0, result.Lanes.Single(l => l.Lane == TaskStates.Ready).Entered);
        Assert.Equal(22 * 60, result.Lanes.Single(l => l.Lane == TaskStates.Ready).MedianMinutes);
        Assert.Equal(1, result.Queues.Single(q => q.Queue == "Coding").Waiting);
        Assert.Null(result.Queues.Single(q => q.Queue == "Gate").Waiting);
        Assert.Equal(1d / 24d, result.Queues.Single(q => q.Queue == "Gate").ThroughputPerHour);
    }

    [Fact]
    public void Aggregate_ExcludesArchivedHistoricalParent_FromAllWorkMetrics()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var work = Task("work", TaskStates.Completed, now.AddHours(-3));
        var parent = Task("parent", TaskStates.Archive, now.AddHours(-2)) with { Kind = "epic" };
        var events = new[] {
            Move(now.AddHours(-2), TaskStates.HumanReview, TaskStates.Completed),
        };

        var result = EnvironmentFlowService.Aggregate([work, parent], _ => events, now);
        var control = EnvironmentFlowService.Aggregate([work], _ => events, now);

        Assert.Equal(control.Today, result.Today);
        Assert.Equal(control.Lanes, result.Lanes);
        Assert.Equal(control.Queues, result.Queues);
        Assert.Equal(control.WaitingReasons, result.WaitingReasons);
    }

    [Fact]
    public void Aggregate_CountsReadyCardsUnderTheirPickupHoldMechanism()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        var held = Task("held", TaskStates.Ready, now.AddDays(-2)) with
        {
            PickupHold = new PickupHoldStatus { Mechanism = PickupHoldMechanisms.ExecutionPlacement },
        };
        var ready = Task("ready", TaskStates.Ready, now.AddHours(-1));

        var result = EnvironmentFlowService.Aggregate([held, ready], _ => [], now);

        Assert.Contains(result.WaitingReasons, reason => reason.Reason == "Execution placement" && reason.Count == 1);
        Assert.Contains(result.WaitingReasons, reason => reason.Reason == "Waiting for coding slot" && reason.Count == 1);
    }


    [Fact]
    public void Aggregate_IntegrationUsesPublishedQueueAndSuccessEvents_AcceptanceIsSeparate()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var board = new DeliveryIntegrationQueueBoard();
        board.Publish("project", [
            new("compile", DeliveryIntegrationQueueBoard.CompilingTrain(3), new DateTimeOffset(now.AddMinutes(-55))),
            new("one", DeliveryIntegrationQueueBoard.Waiting, new DateTimeOffset(now.AddMinutes(-40))),
            new("two", DeliveryIntegrationQueueBoard.Waiting, new DateTimeOffset(now.AddMinutes(-20))),
        ]);
        var tasks = new[] {
            Task("compile", TaskStates.AutoReview, now.AddHours(-5)),
            Task("one", TaskStates.AutoReview, now.AddHours(-4)),
            Task("two", TaskStates.AutoReview, now.AddHours(-3)),
            Task("integrated", TaskStates.HumanReview, now.AddHours(-2)),
            Task("accepted", TaskStates.Completed, now.AddHours(-1)),
            Task("auto-completed", TaskStates.Completed, now.AddHours(-1)),
        }.Select(t => t with { ProjectName = "project" }).ToArray();
        IReadOnlyList<TimelineEvent> Events(TaskInfo task) => task.Id switch
        {
            "integrated" => [new() { Ts = now.AddHours(-2), Kind = TimelineEventKinds.IntegrationSucceeded }],
            "auto-completed" => [Move(now.AddHours(-1), TaskStates.AutoReview, TaskStates.Completed)],
            "accepted" => [
                new() { Ts = now.AddHours(-23), Kind = TimelineEventKinds.IntegrationSucceeded },
                Move(now.AddHours(-1), TaskStates.HumanReview, TaskStates.Completed),
            ],
            _ => [],
        };

        var result = EnvironmentFlowService.Aggregate(tasks, Events, now, integrationQueues: board.Read());
        var integration = result.Queues.Single(q => q.Queue == "Integration");
        Assert.Equal(3, integration.Waiting);
        Assert.Equal(55, integration.OldestWaitMinutes);
        Assert.Equal(2d / 24, integration.ThroughputPerHour);
        var acceptance = result.Queues.Single(q => q.Queue == "Acceptance");
        Assert.Equal(1, acceptance.Waiting);
        Assert.Equal(120, acceptance.OldestWaitMinutes);
        Assert.Equal(1d / 24, acceptance.ThroughputPerHour);
        Assert.Contains(result.WaitingReasons, r => r.Reason == "Waiting for integration" && r.Count == 2);
        Assert.Contains(result.WaitingReasons, r => r.Reason == "Integration: compiling the combined tree of 3 cards");
        Assert.Contains(result.WaitingReasons, r => r.Reason == "Waiting for acceptance" && r.Count == 1);

        // Accepting another card cannot change integration output or queue occupancy.
        var accepted = tasks.Select(t => t.Id == "integrated" ? t with { State = TaskStates.Completed } : t).ToArray();
        var afterAcceptance = EnvironmentFlowService.Aggregate(accepted, t => t.Id == "integrated"
            ? [.. Events(t), Move(now, TaskStates.HumanReview, TaskStates.Completed)] : Events(t),
            now, integrationQueues: board.Read());
        Assert.Equal(integration, afterAcceptance.Queues.Single(q => q.Queue == "Integration"));
    }

    [Fact]
    public void Aggregate_EmptyPublishedQueueIsAuthoritative_AndSnapshotIsDetached()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var board = new DeliveryIntegrationQueueBoard();
        var rows = new List<DeliveryIntegrationQueueRow> { new("one", "waiting", null) };
        board.Publish("project", rows);
        rows.Clear();
        var snapshot = board.Read();
        board.Clear("project");
        Assert.Single(snapshot["project"]);
        var result = EnvironmentFlowService.Aggregate([Task("human", TaskStates.HumanReview, now.AddHours(-1))],
            _ => [], now, integrationQueues: board.Read());
        Assert.Equal(new EnvironmentQueueFlow("Integration", 0, null, 0),
            result.Queues.Single(q => q.Queue == "Integration"));
        Assert.Equal(1, result.Queues.Single(q => q.Queue == "Acceptance").Waiting);
    }

    [Fact]
    public void Aggregate_LocalFallbackIsLabelled_AndNeverUsesAcceptanceAsIntegrationOutput()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var result = EnvironmentFlowService.Aggregate([Task("human", TaskStates.HumanReview, now.AddHours(-1))],
            _ => [Move(now.AddMinutes(-5), TaskStates.HumanReview, TaskStates.Completed)], now);
        Assert.Equal(new EnvironmentQueueFlow("Integration (local lane fallback)", 1, 60, 0),
            result.Queues.Single(q => q.Queue.StartsWith("Integration", StringComparison.Ordinal)));
    }

    [Fact]
    public void Aggregate_MixedFleetKeepsLocalLaneFallbackSeparateFromRemoteQueue()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var tasks = new[] {
            Task("local", TaskStates.HumanReview, now.AddHours(-2)) with { ProjectName = "local" },
            Task("remote", TaskStates.AutoReview, now.AddHours(-3)) with { ProjectName = "remote" },
        };
        var board = new DeliveryIntegrationQueueBoard();
        board.Publish("remote", [new("remote", DeliveryIntegrationQueueBoard.Waiting, new DateTimeOffset(now.AddMinutes(-10)))]);
        var result = EnvironmentFlowService.Aggregate(tasks, _ => [], now, integrationQueues: board.Read(),
            localIntegrationProjects: new HashSet<string> { "local" });
        Assert.Equal(new EnvironmentQueueFlow("Integration", 1, 10, 0), result.Queues.Single(q => q.Queue == "Integration"));
        Assert.Equal(new EnvironmentQueueFlow("Integration (local lane fallback)", 1, 120, 0),
            result.Queues.Single(q => q.Queue == "Integration (local lane fallback)"));
    }

    [Fact]
    public void Aggregate_CurrentGateActivityWinsOverReviewFallback_ButPreservesHolds()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var gate = Task("gate", TaskStates.AutoReview, now.AddHours(-1));
        var held = gate with { Id = "held", PickupHold = new() { Mechanism = PickupHoldMechanisms.DependencyGate } };
        var activities = new[] { new AutoReviewActivityView("", "gate", "gate:1/2", now),
            new AutoReviewActivityView("", "held", AutoReviewActivitySteps.Gate, now) };
        var result = EnvironmentFlowService.Aggregate([gate, held], _ => [], now, reviewActivities: activities);
        Assert.Contains(result.WaitingReasons, r => r.Reason == "Build/test gate" && r.Count == 1);
        Assert.Contains(result.WaitingReasons, r => r.Reason == "Dependency gate" && r.Count == 1);
        Assert.DoesNotContain(result.WaitingReasons, r => r.Reason == "Waiting for review");
    }

    private static TaskInfo Task(string id, string lane, DateTime at) => new()
    {
        Id = id, Kind = TaskKinds.Task, State = lane, CreatedAt = at,
        EnteredLaneAt = at, LastActivity = at,
    };

    private static TimelineEvent Move(DateTime at, string from, string to) => new()
    {
        Ts = at, Kind = TimelineEventKinds.LaneChanged,
        Details = new Dictionary<string, string> { ["from"] = from, ["to"] = to },
    };
}
