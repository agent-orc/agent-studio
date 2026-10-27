using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RedDevelopRepairPolicyTests
{
    [Theory]
    [InlineData(1, true, false, 119, RedDevelopRepairAction.Revert)]
    [InlineData(0, true, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(2, true, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, false, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, true, true, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, true, false, 120, RedDevelopRepairAction.Escalate)]
    [InlineData(0, false, true, 121, RedDevelopRepairAction.Escalate)]
    public void D2DecisionTable(int suspects, bool clean, bool dependent, int redMinutes,
        RedDevelopRepairAction expected)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(suspects, clean, dependent,
            TimeSpan.FromMinutes(redMinutes)));
        Assert.Equal(expected, decision.Action);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void ContainmentHoldsOnlyOverlappingMergesAndEveryReleaseCandidate()
    {
        Assert.False(RedDevelopRepairPolicy.MayMerge(["backend/A.cs"], ["backend/A.cs"]));
        Assert.True(RedDevelopRepairPolicy.MayMerge(["frontend/B.ts"], ["backend/A.cs"]));
        Assert.False(RedDevelopRepairPolicy.MayCreateRelease(true));
        Assert.True(RedDevelopRepairPolicy.MayCreateRelease(false));
    }
}
