using System.Text.Json;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CardWaitingReasonTests
{
    private static TaskInfo Ready => new() { Id = "consumer", State = TaskStates.Ready };

    [Theory]
    [InlineData(TaskStates.Progress, "in progress")]
    [InlineData(TaskStates.Escalated, "escalated")]
    [InlineData(TaskStates.HumanReview, "human review")]
    public void DependencyOutranksDispatchAndRunner(string lane, string label)
    {
        var task = Ready with
        {
            WaitsOn = new() { Blocked = true, Items = [new() { Key = "AGT-3997", Resolved = true, TargetState = lane }] },
            PickupHold = new() { Mechanism = "dispatch-plan", Reason = "overlap" },
            LiveStatus = new() { Queue = new() { Kind = "runner", ReasonCode = "slots-full" } },
        };
        Assert.Equal($"waits for AGT-3997 ({label})", CardWaitingReason.Select(task));
    }

    [Theory]
    [InlineData("slots-full", "waits for a free slot")]
    [InlineData("quota-defer", "quota")]
    [InlineData("provider-limit", "quota")]
    [InlineData("paused", "Runner is paused")]
    public void RecordedRunnerWait(string code, string expected)
    {
        var task = Ready with { LiveStatus = new() { Queue = new() { Kind = "runner", ReasonCode = code, Reason = "Runner is paused" } } };
        Assert.Equal(expected, CardWaitingReason.Select(task));
    }

    [Fact]
    public void DispatchOutranksRunner_AndKeepsRecordedRejection()
    {
        var task = Ready with
        {
            RemoteDispatchRejection = new() { Reason = "host lacks model" },
            PickupHold = new() { Mechanism = "dispatch-rejection", Reason = "long refusal" },
            LiveStatus = new() { Queue = new() { Kind = "runner", ReasonCode = "quota-defer" } },
        };
        Assert.Equal("dispatch rule: host lacks model", CardWaitingReason.Select(task));
    }

    [Fact]
    public void NoRecordedHoldOrNonReady_LeavesNoLine()
    {
        Assert.Null(CardWaitingReason.Select(Ready));
        Assert.Null(CardWaitingReason.Select(Ready with { State = TaskStates.Progress,
            PickupHold = new() { Reason = "stale", Mechanism = "dispatch-rejection" } }));
        using var doc = JsonDocument.Parse(BoardCardSummary.Serialize(CardWaitingReason.Attach(Ready)));
        Assert.False(doc.RootElement.TryGetProperty("waitingReason", out _));
    }

    [Fact]
    public void CardPayloadIncludesComputedLine()
    {
        var task = Ready with { LiveStatus = new() { Queue = new() { Kind = "runner", ReasonCode = "slots-full" } } };
        using var doc = JsonDocument.Parse(BoardCardSummary.Serialize(CardWaitingReason.Attach(task)));
        Assert.Equal("waits for a free slot", doc.RootElement.GetProperty("waitingReason").GetString());
    }
}
