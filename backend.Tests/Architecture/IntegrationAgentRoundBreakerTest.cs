using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture breaker for loop-inventory entry
/// <c>integration.attribution-agent-round</c>. An ambiguous mechanical rebase
/// may open one automatic steer round, but a repeat after that budget for the
/// same card and integration target terminates in Human Review. AGT-3968: a
/// new delivery does not restart the budget; every round publishes one.
/// </summary>
public sealed class IntegrationAgentRoundBreakerTest
{
    [Fact]
    public void Budget_AllowsTwoAutomaticRoundsAndEscalatesTheThird()
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
        Assert.Equal(
            RemoteIntegrationContinuationAction.LeaveForHumanReview,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.Conflict,
                automaticAgentRoundsUsed: 2,
                maximumAgentRounds: 3));
    }

    [Fact]
    public void OtherIntegrationOutcomes_NeverOpenThisLoop()
    {
        foreach (var outcome in Enum.GetValues<MergeIntoIntegrationOutcome>()
                     .Where(outcome => outcome is not (MergeIntoIntegrationOutcome.AgentRoundRequired
                         or MergeIntoIntegrationOutcome.Conflict)))
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

    /// <summary>
    /// AGT-3968: AGT-3897 took eight automatic rounds under a limit of one,
    /// because each round's new delivery SHA started a fresh budget.
    /// </summary>
    [Fact]
    public void Budget_NewDeliveryKeepsTheCardsSpentRounds()
    {
        var oldSubject = Subject(new string('a', 40), "refs/heads/results/run-1/fence-1");
        var newSubject = Subject(new string('b', 40), "refs/heads/results/run-2/fence-1");
        var oldChainId = IntegrationRecoveryBudget.DeliveryChainId(oldSubject);
        var usage = IntegrationRecoveryBudget.Count(
            [AutomaticRound(oldChainId, attemptEpoch: 3)],
            newSubject,
            "refs/heads/develop");

        Assert.Equal(1, usage.Used);
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.Conflict,
                usage.Used));
        Assert.Equal("automatic recovery budget used: 1/1 for this card on develop",
            usage.ExhaustedReason(maximumRounds: 1));
    }

    [Fact]
    public void Budget_RoundsForAnotherIntegrationTargetDoNotCount()
    {
        var subject = Subject(new string('b', 40), "refs/heads/results/run-2/fence-1");
        var usage = IntegrationRecoveryBudget.Count(
            [AutomaticRound("x@1", attemptEpoch: 3, integrationBranch: "release/1.0"),
             AutomaticRound("x@2", attemptEpoch: 4, integrationBranch: "refs/heads/develop")],
            subject,
            "develop");

        Assert.Equal(1, usage.Used);
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
            "automatic recovery budget used: 1/1 for this card (includes 2 legacy automatic recovery rounds without a delivery identifier)",
            usage.ExhaustedReason(maximumRounds: 1));
    }

    private static ReviewSubjectRecord Subject(string sha, string deliveryRef)
        => new()
        {
            ResultSha = sha,
            ResultRef = deliveryRef,
            ImmutableResultRef = deliveryRef,
        };

    private static TimelineEvent AutomaticRound(string? deliveryChainId, int attemptEpoch,
        string? integrationBranch = null)
    {
        var details = new Dictionary<string, string>
        {
            ["automatic"] = "true",
            ["attemptEpoch"] = attemptEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (deliveryChainId is not null)
            details[IntegrationRecoveryBudget.DeliveryChainIdKey] = deliveryChainId;
        if (integrationBranch is not null)
            details[IntegrationRecoveryBudget.IntegrationBranchKey] = integrationBranch;
        return new TimelineEvent
        {
            Kind = TimelineEventKinds.IntegrationRecoveryQueued,
            Details = details,
        };
    }
}
