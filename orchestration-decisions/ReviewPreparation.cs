using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestration.Decisions;

public sealed record PreparedReviewDecision(
    ReviewRoundBudgetDecision Budget,
    CardRoundBudgetAction CardAction,
    bool ShouldDegrade,
    ReviewFollowUpDecision FollowUp,
    ReviewReportRequest Report,
    DeliveredReviewRound? DeliveredRound,
    IReadOnlyList<string> BlockingAspects);

public static class ReviewPreparationPolicy
{
    public static ReviewRoundBudgetDecision ApplyCardBudget(ReviewRoundBudgetDecision budget,
        CardRoundBudgetAction action, IReadOnlyList<string> blockedAspects, IReadOnlyList<string> concernAspects)
        => action == CardRoundBudgetAction.DegradeToFollowUp
            ? new(budget.RoundNumber, budget.MaximumRounds,
                blockedAspects.Count > 0 ? blockedAspects : concernAspects, "card-round-budget")
            : budget;

    public static PreparedReviewDecision Decide(
        ReviewReportRequest report, ReviewRoundBudgetLedger seed, CardRoundBudgetState cardBudget,
        string attemptId, int priorReissues, int concernRoundsUsed,
        ProjectDecisionSettings settings, bool acceptedReplay)
    {
        var original = report.Verdicts;
        var blocks = original.Where(v => ReviewGradingPolicy.IsBlockingToken(v.Status))
            .Select(v => v.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var budget = ReviewRoundBudgetPolicy.Decide(seed, attemptId, blocks,
            settings.MaximumDeliveredRounds, settings.ConsecutiveBlockRounds,
            priorReissues, settings.MaximumAutomaticReissues);
        ReviewFollowUpDecision FollowUp(ReviewReportRequest value)
            => ReviewFollowUpPolicy.Decide(value.Verdicts.Select(v => new ReviewFollowUpFinding(
                    v.Aspect, v.Status, v.Summary, v.EvidenceChecked, v.Missing, v.Classification)),
                concernRoundsUsed, settings.MaximumConcernRounds);
        var opensConcern = string.Equals(report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
                           && FollowUp(report).Action == ReviewFollowUpAction.ReviewConcernRound;
        var cardAction = CardRoundBudgetPolicy.DecideForReview(cardBudget, blocks, opensConcern);
        if (!acceptedReplay && string.Equals(report.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase)
            && cardAction == CardRoundBudgetAction.DegradeToFollowUp)
            budget = new(budget.RoundNumber, budget.MaximumRounds, blocks, "card-round-budget");
        var spentConcern = !acceptedReplay && opensConcern && cardAction == CardRoundBudgetAction.DegradeToFollowUp;
        if (spentConcern)
            budget = new(budget.RoundNumber, budget.MaximumRounds,
                original.Where(v => !string.Equals(v.Status, "pass", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "card-round-budget");
        var degrade = (!acceptedReplay && string.Equals(report.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase) && budget.Degrade) || spentConcern;
        if (degrade) report = ReviewRoundBudgetPolicy.ApplyRemoteReport(report, budget);
        var followUp = FollowUp(report);
        if (degrade && string.Equals(report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)) followUp = ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, budget);
        var delivered = TransitionDecisionPolicy.IsDeliveredOutcome(report.Outcome)
            ? new DeliveredReviewRound(attemptId, blocks, degrade ? budget.DegradedAspects : [], SpentBy: budget.SpentBy)
            : null;
        return new(budget, cardAction, degrade, followUp, report, delivered, blocks);
    }
}
