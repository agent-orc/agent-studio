using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture breaker for loop-inventory entry
/// <c>integration.attribution-agent-round</c>. An ambiguous mechanical rebase
/// may open two automatic steer rounds, but a repeat after that budget for the
/// same fenced delivery terminates in Human Review.
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

    [Fact]
    public void Budget_TwoReviewsOfOneDeliveryShareTheSameRounds()
    {
        var subject = Subject(new string('a', 40), "refs/heads/results/run-1/fence-1");
        var chainId = IntegrationRecoveryBudget.DeliveryChainId(subject);
        var usage = IntegrationRecoveryBudget.Count(
            [AutomaticRound(chainId, attemptEpoch: 3), AutomaticRound(chainId, attemptEpoch: 4)],
            subject);

        Assert.Equal(2, usage.Used);
        Assert.Equal(0, usage.LegacyRounds);
        Assert.Equal(
            RemoteIntegrationContinuationAction.LeaveForHumanReview,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                usage.Used));
    }

    [Fact]
    public void Budget_NewDeliveryStartsFreshDespiteOlderAutomaticRounds()
    {
        var oldSubject = Subject(new string('a', 40), "refs/heads/results/run-1/fence-1");
        var newSubject = Subject(new string('b', 40), "refs/heads/results/run-2/fence-1");
        var oldChainId = IntegrationRecoveryBudget.DeliveryChainId(oldSubject);
        var usage = IntegrationRecoveryBudget.Count(
            [AutomaticRound(oldChainId, attemptEpoch: 3), AutomaticRound(oldChainId, attemptEpoch: 4)],
            newSubject);

        Assert.Equal(0, usage.Used);
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                usage.Used));
    }

    [Fact]
    public void Budget_LegacyRoundsCountConservativelyAndAreNamedInParkReason()
    {
        var subject = Subject(new string('c', 40), "refs/heads/results/run-3/fence-1");
        var usage = IntegrationRecoveryBudget.Count(
            [AutomaticRound(deliveryChainId: null, attemptEpoch: 1),
             AutomaticRound(deliveryChainId: null, attemptEpoch: 7)],
            subject);

        Assert.Equal(2, usage.Used);
        Assert.Equal(2, usage.LegacyRounds);
        Assert.Equal(
            "automatic recovery budget used: 2/2 for delivery cccccccccccc (includes 2 legacy automatic recovery rounds without a delivery identifier)",
            usage.ExhaustedReason(maximumRounds: 2));
    }

    private static ReviewSubjectRecord Subject(string sha, string deliveryRef)
        => new()
        {
            ResultSha = sha,
            ResultRef = deliveryRef,
            ImmutableResultRef = deliveryRef,
        };

    private static TimelineEvent AutomaticRound(string? deliveryChainId, int attemptEpoch)
    {
        var details = new Dictionary<string, string>
        {
            ["automatic"] = "true",
            ["attemptEpoch"] = attemptEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (deliveryChainId is not null)
            details[IntegrationRecoveryBudget.DeliveryChainIdKey] = deliveryChainId;
        return new TimelineEvent
        {
            Kind = TimelineEventKinds.IntegrationRecoveryQueued,
            Details = details,
        };
    }
}
