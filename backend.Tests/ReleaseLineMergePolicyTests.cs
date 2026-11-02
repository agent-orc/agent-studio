using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3435: the decision after the pre-main full suite ran on the merge result
/// of a delivery that was behind the release line.
/// </summary>
public sealed class ReleaseLineMergePolicyTests
{
    [Theory]
    [InlineData(true, false, 1, ReleaseLineMergeAction.Publish)]
    [InlineData(true, false, 3, ReleaseLineMergeAction.Publish)]
    [InlineData(true, true, 1, ReleaseLineMergeAction.Retry)]
    [InlineData(true, true, 2, ReleaseLineMergeAction.Retry)]
    [InlineData(true, true, 3, ReleaseLineMergeAction.StopMoving)]
    [InlineData(false, false, 1, ReleaseLineMergeAction.Refuse)]
    [InlineData(false, true, 1, ReleaseLineMergeAction.Refuse)]
    [InlineData(false, true, 3, ReleaseLineMergeAction.Refuse)]
    public void Decide_Matrix(bool gateGreen, bool releaseLineMoved, int attempt, ReleaseLineMergeAction expected)
    {
        var decision = ReleaseLineMergePolicy.Decide(gateGreen, releaseLineMoved, attempt);

        Assert.Equal(expected, decision.Action);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_StopReasonNamesTheBound()
    {
        var decision = ReleaseLineMergePolicy.Decide(gateGreen: true, releaseLineMoved: true, attempt: 2, maxAttempts: 2);

        Assert.Equal(ReleaseLineMergeAction.StopMoving, decision.Action);
        Assert.Contains("stopped after 2/2 attempts", decision.Reason);
    }

    [Fact]
    public void Decide_NonPositiveBound_StillAllowsOneAttempt()
    {
        Assert.Equal(
            ReleaseLineMergeAction.StopMoving,
            ReleaseLineMergePolicy.Decide(gateGreen: true, releaseLineMoved: true, attempt: 1, maxAttempts: 0).Action);
        Assert.Equal(
            ReleaseLineMergeAction.Publish,
            ReleaseLineMergePolicy.Decide(gateGreen: true, releaseLineMoved: false, attempt: 1, maxAttempts: 0).Action);
    }
}
