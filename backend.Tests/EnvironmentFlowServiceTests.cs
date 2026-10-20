using AgentStudio.Shared;
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
