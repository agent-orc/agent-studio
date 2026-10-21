namespace AgentStudio.Review;

public enum ReviewDepthAction
{
    /// <summary>Too few approvals on the current route to judge it; keep it.</summary>
    InsufficientEvidence,
    /// <summary>The current route performs within thresholds and nothing cheaper matches it.</summary>
    KeepRoute,
    /// <summary>The same model at another reasoning level misses no more and costs less per review.</summary>
    PreferCheaperReasoning,
    /// <summary>The current route misses too often; follow it with a deep review.</summary>
    AddDeepReview,
}

/// <summary>Thresholds for <see cref="ReviewDepthAdvisor"/>. Defaults favour keeping a route until evidence is clear.</summary>
public sealed record ReviewDepthThresholds(
    int MinimumApprovals = 20,
    double DeepReviewMissRate = 0.15,
    double EquivalentMissRateTolerance = 0.02);

public sealed record ReviewDepthAdvice(
    ReviewDepthAction Action,
    string Level,
    string Model,
    string ReasoningLevel,
    string? SuggestedReasoningLevel,
    string Reason,
    ReviewDepthThresholds Thresholds,
    ReviewEffectivenessRow? Current,
    ReviewEffectivenessRow? Alternative);

/// <summary>
/// Pure review-depth recommendation from recorded effectiveness (AGT-3269).
/// It reads one review level only, because miss rates of different levels
/// answer different questions. Quality is checked before cost: a route that
/// misses too often gets a deep review even when a cheaper level exists. A
/// cheaper reasoning level is offered only when it has enough approvals of its
/// own, a priced cost per review, and a miss rate within the tolerance of the
/// current route. The advice is evidence for the orchestrator; it never
/// overrides a routing correctness floor.
/// </summary>
public static class ReviewDepthAdvisor
{
    public static ReviewDepthAdvice Advise(
        string level,
        string model,
        string reasoningLevel,
        IReadOnlyList<ReviewEffectivenessRow> rows,
        ReviewDepthThresholds? thresholds = null)
    {
        var limits = thresholds ?? new ReviewDepthThresholds();
        var sameLevel = rows.Where(row => row.Level == level).ToArray();
        var current = sameLevel.FirstOrDefault(row => SameRoute(row, model, reasoningLevel));
        if (current is null || current.Approvals < limits.MinimumApprovals || current.MissRate is null)
            return new(ReviewDepthAction.InsufficientEvidence, level, model, reasoningLevel, null,
                FormattableString.Invariant($"{current?.Approvals ?? 0} recorded approvals on this route; at least {limits.MinimumApprovals} are needed."),
                limits, current, null);

        if (current.MissRate.Value >= limits.DeepReviewMissRate)
            return new(ReviewDepthAction.AddDeepReview, level, model, reasoningLevel, null,
                FormattableString.Invariant($"Miss rate {current.MissRate.Value:P1} over {current.Approvals} approvals reaches the deep-review threshold {limits.DeepReviewMissRate:P0}."),
                limits, current, null);

        var cheaper = current.CostPerReviewUsd is null
            ? null
            : sameLevel
                .Where(row => string.Equals(row.Model, current.Model, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(row.ReasoningLevel, current.ReasoningLevel, StringComparison.OrdinalIgnoreCase)
                              && row.Approvals >= limits.MinimumApprovals
                              && row.MissRate is not null
                              && row.MissRate.Value <= current.MissRate.Value + limits.EquivalentMissRateTolerance
                              && row.CostPerReviewUsd is not null
                              && row.CostPerReviewUsd.Value < current.CostPerReviewUsd.Value)
                .OrderBy(row => row.CostPerReviewUsd)
                .ThenBy(row => row.MissRate)
                .FirstOrDefault();
        if (cheaper is not null)
            return new(ReviewDepthAction.PreferCheaperReasoning, level, model, reasoningLevel, cheaper.ReasoningLevel,
                FormattableString.Invariant($"Reasoning level {cheaper.ReasoningLevel} misses {cheaper.MissRate!.Value:P1} against {current.MissRate.Value:P1} at ")
                + FormattableString.Invariant($"{cheaper.CostPerReviewUsd!.Value:0.####} USD per review instead of {current.CostPerReviewUsd!.Value:0.####} USD."),
                limits, current, cheaper);

        return new(ReviewDepthAction.KeepRoute, level, model, reasoningLevel, null,
            FormattableString.Invariant($"Miss rate {current.MissRate.Value:P1} over {current.Approvals} approvals is below the deep-review threshold, and no cheaper reasoning level matches it."),
            limits, current, null);
    }

    private static bool SameRoute(ReviewEffectivenessRow row, string model, string reasoningLevel)
        => string.Equals(row.Model, model, StringComparison.OrdinalIgnoreCase)
           && string.Equals(row.ReasoningLevel, reasoningLevel, StringComparison.OrdinalIgnoreCase);
}
