using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture breaker for loop-inventory entry
/// <c>integration.attribution-agent-round</c>. An ambiguous mechanical rebase
/// may open two automatic steer rounds, but a repeat after that bounded budget
/// terminates in Human Review.
/// </summary>
public sealed class IntegrationAgentRoundBreakerTest
{
    [Fact]
    public void Budget_AllowsExactlyTwoAutomaticRounds()
    {
        Assert.Equal(2, RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds);
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                automaticAgentRoundsUsed: 0));
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                automaticAgentRoundsUsed: 1));
        Assert.Equal(
            RemoteIntegrationContinuationAction.LeaveForHumanReview,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                automaticAgentRoundsUsed: 2));
    }

    [Fact]
    public void OtherIntegrationOutcomes_NeverOpenThisLoop()
    {
        foreach (var outcome in Enum.GetValues<MergeIntoIntegrationOutcome>()
                     .Where(outcome => outcome != MergeIntoIntegrationOutcome.AgentRoundRequired))
        {
            Assert.Equal(
                RemoteIntegrationContinuationAction.None,
                RemoteIntegrationContinuationPolicy.Decide(outcome, automaticAgentRoundsUsed: 0));
        }
    }
}
