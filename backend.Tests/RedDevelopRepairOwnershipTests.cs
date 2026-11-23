using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>RT-1 (AGT-3988): every lane, before and after the two-hour owner question.</summary>
public sealed class RedDevelopRepairOwnershipTests
{
    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void Review_gate_integration_and_settled_deliveries_are_held(string state)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(1, true, false, TimeSpan.FromMinutes(119), state));
        Assert.Equal(RedDevelopRepairAction.Hold, decision.Action);
        Assert.Null(decision.Question);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.FailedPickup)]
    [InlineData(TaskStates.CodeNotComplete)]
    [InlineData(TaskStates.Escalated)]
    public void Repairs_outside_delivery_keep_the_repair_route_before_two_hours(string? state)
    {
        Assert.Equal(RedDevelopRepairAction.Revert,
            RedDevelopRepairPolicy.Decide(new(1, true, false, TimeSpan.FromMinutes(119), state)).Action);
        Assert.Equal(RedDevelopRepairAction.FixForward,
            RedDevelopRepairPolicy.Decide(new(0, false, false, TimeSpan.FromMinutes(119), state)).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.FailedPickup)]
    [InlineData(TaskStates.CodeNotComplete)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.Escalated)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void At_two_hours_every_lane_asks_the_owner_and_never_moves_a_card(string? state)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(0, false, false, TimeSpan.FromHours(2), state));
        Assert.Equal(RedDevelopRepairAction.AskOwner, decision.Action);
        Assert.NotNull(decision.Question);
    }
}
