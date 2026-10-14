using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public sealed record ReviewBudgetFacts(string Outcome, IReadOnlyList<ReviewFollowUpFinding> Findings,
    int ConcernRoundsUsed, int MaximumConcernRounds, CardRoundBudgetState CardBudget,
    ReviewRoundBudgetLedger DeliveredRounds, string AttemptId, int MaximumDeliveredRounds,
    int ConsecutiveBlockRounds, int PriorReissues, int MaximumReissues, bool AcceptedReplay = false);

public sealed record ReviewBudgetDecision(ReviewFollowUpDecision FollowUp, ReviewRoundBudgetDecision RoundBudget,
    CardRoundBudgetAction CardAction, IReadOnlyList<string> BlockingAspects, bool ShouldDegrade, bool Escalate);

public static class ReviewBudgetPolicy
{
    public static ReviewBudgetDecision Decide(ReviewBudgetFacts facts)
    {
        var followUp = ReviewFollowUpPolicy.Decide(facts.Findings, facts.ConcernRoundsUsed, facts.MaximumConcernRounds);
        var blocked = facts.Findings.Where(item => ReviewGradingPolicy.IsBlockingToken(item.Status))
            .Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var round = ReviewRoundBudgetPolicy.Decide(facts.DeliveredRounds, facts.AttemptId, blocked,
            facts.MaximumDeliveredRounds, facts.ConsecutiveBlockRounds, facts.PriorReissues, facts.MaximumReissues);
        var concern = facts.Outcome == ReviewOutcomeKinds.Pass && followUp.Action == ReviewFollowUpAction.ReviewConcernRound;
        var card = CardRoundBudgetPolicy.DecideForReview(facts.CardBudget, blocked, concern);
        if (!facts.AcceptedReplay && card == CardRoundBudgetAction.DegradeToFollowUp
            && (facts.Outcome == ReviewOutcomeKinds.ProductFailure || concern))
            round = new(round.RoundNumber, round.MaximumRounds,
                concern ? facts.Findings.Where(item => !string.Equals(item.Status, "pass", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : blocked,
                "card-round-budget");
        var degrade = !facts.AcceptedReplay && ((facts.Outcome == ReviewOutcomeKinds.ProductFailure && round.Degrade)
            || (concern && card == CardRoundBudgetAction.DegradeToFollowUp));
        if (degrade)
        {
            var adjusted = facts.Findings.Select(item => round.DegradedAspects.Contains(item.Aspect,
                StringComparer.OrdinalIgnoreCase) && ReviewGradingPolicy.IsBlockingToken(item.Status)
                ? item with { Status = "concerns" } : item).ToArray();
            followUp = ReviewFollowUpPolicy.Decide(adjusted, facts.ConcernRoundsUsed, facts.MaximumConcernRounds);
            if (!adjusted.Any(item => ReviewGradingPolicy.IsBlockingToken(item.Status)))
                followUp = ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, round);
        }
        return new(followUp, round, card, blocked, degrade,
            !facts.AcceptedReplay && facts.Outcome == ReviewOutcomeKinds.ProductFailure && card == CardRoundBudgetAction.Escalate);
    }
}
