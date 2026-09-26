using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateEnvironmentContinuationPolicyTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    public void StartsOnlyOneAutomaticRoundForTheCard(
        bool automaticEnabled, int priorRounds, bool expected)
    {
        Assert.Equal(expected, GateEnvironmentContinuationPolicy.ShouldStart(
            automaticEnabled, priorRounds));
    }
}
