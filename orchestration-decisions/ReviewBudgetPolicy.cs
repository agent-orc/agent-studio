using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public sealed record ReviewBudgetFacts(string Outcome, IReadOnlyList<ReviewFollowUpFinding> Findings,
    int ConcernRoundsUsed, int MaximumConcernRounds, CardRoundBudgetState CardBudget,
    ReviewRoundBudgetLedger DeliveredRounds, string AttemptId, int MaximumDeliveredRounds,
    int ConsecutiveBlockRounds, int PriorReissues, int MaximumReissues, bool AcceptedReplay = false);

public sealed record ReviewBudgetDecision(ReviewFollowUpDecision FollowUp, ReviewRoundBudgetDecision RoundBudget,
    CardRoundBudgetAction CardAction, IReadOnlyList<string> BlockingAspects, bool ShouldDegrade, bool Escalate);

/// <summary>A frozen content decision. A transfer is admitted only after its durable follow-up exists.</summary>
public sealed record ReviewContentAdmission(string Aspect, string OriginalStatus, string Disposition,
    string Reason, bool RequiresFollowUp = false)
{
    public bool Admitted(string? followUpKey)
        => (Disposition == "passed"
                ? string.Equals(OriginalStatus, "pass", StringComparison.OrdinalIgnoreCase)
                : Disposition is "advisory" or "concern-budget-accepted" or "transferred"
                  && ReviewGradingPolicy.IsConcernToken(OriginalStatus))
           && !string.IsNullOrWhiteSpace(Reason)
           && (!(RequiresFollowUp || Disposition == "transferred") || !string.IsNullOrWhiteSpace(followUpKey));
}

public static class ReviewBudgetPolicy
{
    public static ReviewBudgetDecision Decide(ReviewBudgetFacts facts)
    {
        var findings = ReviewFindingMergePolicy.Merge(facts.Findings);
        var followUp = ReviewFollowUpPolicy.Decide(findings, facts.ConcernRoundsUsed, facts.MaximumConcernRounds);
        var blocked = ReviewRoundBudgetPolicy.CountedBlockingAspects(findings);
        var previous = facts.DeliveredRounds.Rounds.Where(round => round.AttemptId != facts.AttemptId)
            .TakeLast(2).ToArray();
        var repeatedDeepBlock = previous.Length == 2 && findings.Where(item =>
                item.Aspect.Equals("deep-review", StringComparison.OrdinalIgnoreCase)
                && ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewRoundBudgetPolicy.IsUncitedBlock(item.Classification))
            .Select(ReviewFindingMergePolicy.Fingerprint)
            .Any(key => previous.All(round => round.BlockingFindingKeys?.Contains(
                key, StringComparer.Ordinal) == true));
        var round = ReviewRoundBudgetPolicy.Decide(facts.DeliveredRounds, facts.AttemptId, blocked,
            facts.MaximumDeliveredRounds, facts.ConsecutiveBlockRounds, facts.PriorReissues, facts.MaximumReissues);
        var concern = facts.Outcome == ReviewOutcomeKinds.Pass && followUp.Action == ReviewFollowUpAction.ReviewConcernRound;
        var card = CardRoundBudgetPolicy.DecideForReview(facts.CardBudget, blocked, concern);
        var contentBlock = blocked.Any(aspect => aspect.Equals("requirement-fit", StringComparison.OrdinalIgnoreCase)
            || aspect.Equals("concept-fit", StringComparison.OrdinalIgnoreCase));
        if (contentBlock && facts.CardBudget.Exhausted) card = CardRoundBudgetAction.Escalate;
        if (!facts.AcceptedReplay && concern && round.RoundNumber >= round.MaximumRounds)
            round = new(round.RoundNumber, round.MaximumRounds,
                followUp.Findings.Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                "lifetime-review-round-budget");
        if (!facts.AcceptedReplay && card == CardRoundBudgetAction.DegradeToFollowUp
            && (facts.Outcome == ReviewOutcomeKinds.ProductFailure || concern))
            round = new(round.RoundNumber, round.MaximumRounds,
                concern ? findings.Where(item => !string.Equals(item.Status, "pass", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : blocked,
                "card-round-budget");
        // A budget can transfer quality work, never an unmet requirement.
        round = round with { DegradedAspects = round.DegradedAspects.Where(aspect =>
            !blocked.Contains(aspect, StringComparer.OrdinalIgnoreCase)
            || !aspect.Equals("requirement-fit", StringComparison.OrdinalIgnoreCase)
            && !aspect.Equals("concept-fit", StringComparison.OrdinalIgnoreCase)).ToArray() };
        var degrade = !repeatedDeepBlock && !facts.AcceptedReplay
            && ((facts.Outcome == ReviewOutcomeKinds.ProductFailure && round.Degrade)
            || (concern && (card == CardRoundBudgetAction.DegradeToFollowUp || round.Degrade)));
        if (degrade)
        {
            var adjusted = findings.Select(item => round.DegradedAspects.Contains(item.Aspect,
                StringComparer.OrdinalIgnoreCase) && ReviewGradingPolicy.IsBlockingToken(item.Status)
                ? item with { Status = "concerns" } : item).ToArray();
            followUp = ReviewFollowUpPolicy.Decide(adjusted, facts.ConcernRoundsUsed, facts.MaximumConcernRounds);
            if (!adjusted.Any(item => ReviewGradingPolicy.IsBlockingToken(item.Status)))
                followUp = ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, round);
        }
        round = round with { ContentAdmissions = findings.Where(item =>
                item.Aspect.Equals("requirement-fit", StringComparison.OrdinalIgnoreCase)
                || item.Aspect.Equals("concept-fit", StringComparison.OrdinalIgnoreCase))
            .Select(item => ContentAdmission(item, followUp, degrade)).ToArray() };
        return new(followUp, round, card, blocked, degrade,
            !facts.AcceptedReplay && facts.Outcome == ReviewOutcomeKinds.ProductFailure
            && (card == CardRoundBudgetAction.Escalate || repeatedDeepBlock
                || contentBlock && round.RoundNumber >= round.MaximumRounds));
    }

    private static ReviewContentAdmission ContentAdmission(ReviewFollowUpFinding finding,
        ReviewFollowUpDecision followUp, bool transfer)
    {
        if (ReviewGradingPolicy.IsBlockingToken(finding.Status))
            return new(finding.Aspect, finding.Status!, "refused", "A blocking content finding requires a repaired delivery and review.");
        if (string.Equals(finding.Status, "pass", StringComparison.OrdinalIgnoreCase))
            return new(finding.Aspect, finding.Status!, "passed", "The settled content review passed for this delivery.", transfer);
        if (!ReviewGradingPolicy.IsConcernToken(finding.Status) || followUp.Action != ReviewFollowUpAction.Accept)
            return new(finding.Aspect, finding.Status ?? "missing", "refused", followUp.Reason);
        return new(finding.Aspect, finding.Status!, transfer ? "transferred"
                : ReviewFollowUpPolicy.IsActionableConcern(finding) ? "concern-budget-accepted" : "advisory",
            followUp.Reason, transfer);
    }
}
