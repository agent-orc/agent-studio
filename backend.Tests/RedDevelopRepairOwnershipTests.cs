using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RedDevelopRepairOwnershipTests
{
    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void Review_gate_and_settled_deliveries_keep_their_owner_even_after_two_hours(string state)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(1, true, false, TimeSpan.FromHours(3), state));
        Assert.Equal(RedDevelopRepairAction.Hold, decision.Action);
    }

    [Theory]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public void The_age_rule_still_escalates_repairs_outside_delivery(string state)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(0, false, false, TimeSpan.FromHours(2), state));
        Assert.Equal(RedDevelopRepairAction.Escalate, decision.Action);
    }
}
