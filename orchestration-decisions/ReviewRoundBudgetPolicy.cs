namespace AgentStudio.OrchestrationDecisions;

public sealed record DeliveredReviewRound(
    string AttemptId,
    IReadOnlyList<string> BlockingAspects,
    IReadOnlyList<string> DegradedAspects,
    string? FollowUpTaskKey = null,
    string? SpentBy = null);

public sealed record ReviewRoundBudgetLedger(IReadOnlyList<DeliveredReviewRound> Rounds)
{
    public static ReviewRoundBudgetLedger Empty { get; } = new([]);
    public int Delivered => Rounds.Count;
    public string? SpentBy => Rounds.LastOrDefault(round => round.SpentBy is not null)?.SpentBy;
}

public sealed record ReviewRoundBudgetDecision(
    int RoundNumber,
    int MaximumRounds,
    IReadOnlyList<string> DegradedAspects,
    string? SpentBy)
{
    public bool Degrade => DegradedAspects.Count > 0;
}

/// <summary>Pure lifetime review budget. An operator epoch changes evidence, never this history.</summary>
public static class ReviewRoundBudgetPolicy
{
    public const int DefaultMaximumRounds = 4;
    public const int DefaultConsecutiveBlockRounds = 2;

    /// <summary>
    /// A report with one of these outcomes delivered a review round. The report
    /// endpoint accepts outcome tokens case-insensitively, so the ledger must too.
    /// </summary>
    public static bool IsDeliveredOutcome(string? outcome)
        => string.Equals(outcome, ReviewOutcomeKinds.Pass, StringComparison.OrdinalIgnoreCase)
           || string.Equals(outcome, ReviewOutcomeKinds.ProductFailure, StringComparison.OrdinalIgnoreCase)
           || string.Equals(outcome, ReviewOutcomeKinds.IntegrationBranchDefect, StringComparison.OrdinalIgnoreCase);

    /// <summary>An uncited block is not an actionable finding for any transition or budget decision.</summary>
    public static bool IsUncitedBlock(string? classification)
        => string.Equals(classification, AgentStudio.TaskServer.Contracts.ReviewVerdictCitationPolicy.BlockWithoutCitation,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The blocking aspects one delivered round charges. TransitionDecisionPolicy
    /// and every ledger write count them here, so the stored block history
    /// matches the decision made for that round.
    /// </summary>
    public static IReadOnlyList<string> CountedBlockingAspects(
        IEnumerable<AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding> findings)
        => findings.Where(item => !IsUncitedBlock(item.Classification)
                && AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(item.Status))
            .Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> CountedBlockingAspects(
        IEnumerable<AgentStudio.TaskServer.Contracts.ReviewVerdictDto> verdicts)
        => CountedBlockingAspects(verdicts.Select(verdict => new AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding(
            verdict.Aspect, verdict.Status, verdict.Summary, Classification: verdict.Classification)));

    public static AgentStudio.TaskServer.Contracts.ReviewReportRequest ApplyRemoteReport(
        AgentStudio.TaskServer.Contracts.ReviewReportRequest request,
        ReviewRoundBudgetDecision decision)
    {
        if (!decision.Degrade
            || !string.Equals(request.Outcome, ReviewOutcomeKinds.ProductFailure, StringComparison.OrdinalIgnoreCase))
            return request;
        var verdicts = request.Verdicts.Select(verdict =>
            decision.DegradedAspects.Contains(verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            && AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(verdict.Status)
                ? verdict with { Status = "concerns" }
                : verdict).ToArray();
        var remainingBlock = verdicts.Any(verdict =>
            AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(verdict.Status));
        return request with
        {
            Outcome = remainingBlock ? request.Outcome : ReviewOutcomeKinds.Pass,
            FailureClassification = remainingBlock ? request.FailureClassification : null,
            Summary = $"Review round {decision.RoundNumber} of {decision.MaximumRounds}: "
                      + $"{decision.SpentBy} spent the review budget; open findings moved to a linked follow-up. "
                      + request.Summary,
            Verdicts = verdicts,
        };
    }

    /// <summary>
    /// A spent budget moves the degraded findings to the linked follow-up card,
    /// so the delivery must not start another automatic finding or concern round
    /// for them. Shared by local aspect review and Remote Review.
    /// </summary>
    public static AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision ApplyFollowUp(
        AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision followUp,
        ReviewRoundBudgetDecision decision)
    {
        if (!decision.Degrade || !followUp.StartsCodingRound) return followUp;
        return followUp with
        {
            Action = AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.Accept,
            Reason = "The lifetime review budget moved these findings to a linked follow-up.",
        };
    }

    public static ReviewRoundBudgetDecision Decide(
        ReviewRoundBudgetLedger ledger,
        string attemptId,
        IEnumerable<string> blockedAspects,
        int maximumRounds,
        int consecutiveBlockRounds,
        int? priorAutomaticReissues = null,
        int? maximumAutomaticReissues = null)
    {
        var prior = ledger.Rounds.Where(round => round.AttemptId != attemptId).ToArray();
        var blocked = blockedAspects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roundNumber = prior.Length + 1;
        maximumRounds = Math.Clamp(maximumRounds, 1, 20);
        consecutiveBlockRounds = Math.Clamp(consecutiveBlockRounds, 1, 20);
        // The deterministic build/test gate owns its failure. Never turn that
        // failure, or a mixed review containing it, into an acceptance.
        if (blocked.Contains("build-tests", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("review-verification", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("tests-and-evidence", StringComparer.OrdinalIgnoreCase))
            return new(roundNumber, maximumRounds, [],
                blocked.Contains("build-tests", StringComparer.OrdinalIgnoreCase)
                    ? "build-tests"
                    : blocked.Contains("review-verification", StringComparer.OrdinalIgnoreCase)
                        ? "review-verification" : "tests-and-evidence");

        string? recurrent = null;
        foreach (var aspect in blocked)
        {
            var streak = 0;
            foreach (var previous in prior.Reverse())
            {
                if (!previous.BlockingAspects.Contains(aspect, StringComparer.OrdinalIgnoreCase)) break;
                streak++;
            }
            if (streak >= consecutiveBlockRounds)
            {
                recurrent = aspect;
                break;
            }
        }
        var reissuesSpent = priorAutomaticReissues is not null
                            && maximumAutomaticReissues is not null
                            && priorAutomaticReissues > 0
                            && priorAutomaticReissues >= maximumAutomaticReissues;
        var lifetimeSpent = roundNumber >= maximumRounds || reissuesSpent;
        var spentBy = recurrent ?? (lifetimeSpent ? blocked.FirstOrDefault() : null);
        return new(roundNumber, maximumRounds,
            lifetimeSpent ? blocked : recurrent is null ? [] : [recurrent], spentBy);
    }
}
