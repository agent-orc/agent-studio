namespace AgentStudio.Review;

/// <summary>
/// Effectiveness of one review route: a level, a model and a reasoning level.
/// Rates are null without a denominator, and cost figures are null when no
/// review of the route was priced; unknown is never reported as zero.
/// </summary>
public sealed record ReviewEffectivenessRow(
    string Level,
    string Model,
    string ReasoningLevel,
    int Reviews,
    int Approvals,
    int Blocks,
    double? BlockerRate,
    int MissedApprovals,
    double? MissRate,
    IReadOnlyDictionary<string, int> MissesBySource,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    int PricedReviews,
    decimal? TotalCostUsd,
    decimal? CostPerReviewUsd,
    ReviewFindingCounts Findings,
    int RealFindings,
    decimal? CostPerRealFindingUsd,
    long? AverageDurationMs);

public sealed record ReviewEffectivenessCoverage(
    int Reviews,
    int PricedReviews,
    int ReviewsWithTokens,
    int ReviewsWithModel,
    int Evidence,
    int Misses);

public static class ReviewEffectivenessAggregation
{
    public const string Unknown = "unknown";

    /// <summary>
    /// Groups observations by level, model and reasoning level. Miss rate is
    /// the share of approvals that at least one piece of evidence
    /// contradicted; blocker rate is the share of reviews that blocked. Cost
    /// per real finding divides the priced cost by the real findings of the
    /// priced reviews.
    /// </summary>
    public static IReadOnlyList<ReviewEffectivenessRow> Aggregate(
        IReadOnlyList<ReviewObservation> observations,
        IReadOnlyList<ReviewMiss> misses)
    {
        var missesByObservation = misses
            .GroupBy(miss => miss.ObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return observations
            .GroupBy(item => (item.Level, Model: Label(item.Model), Reasoning: Label(item.ReasoningLevel)))
            .Select(group => Row(group.Key.Level, group.Key.Model, group.Key.Reasoning, group.ToArray(), missesByObservation))
            .OrderBy(row => Array.IndexOf(ReviewLevels.All.ToArray(), row.Level))
            .ThenBy(row => row.Model, StringComparer.Ordinal)
            .ThenBy(row => row.ReasoningLevel, StringComparer.Ordinal)
            .ToArray();
    }

    public static ReviewEffectivenessCoverage Coverage(
        IReadOnlyList<ReviewObservation> observations, int evidence, IReadOnlyList<ReviewMiss> misses)
        => new(
            observations.Count,
            observations.Count(item => item.CostUsd is not null),
            observations.Count(item => item.InputTokens + item.OutputTokens + item.CacheReadTokens + item.CacheCreationTokens > 0),
            observations.Count(item => !string.IsNullOrWhiteSpace(item.Model)),
            evidence,
            misses.Count);

    private static ReviewEffectivenessRow Row(
        string level, string model, string reasoning, IReadOnlyList<ReviewObservation> items,
        IReadOnlyDictionary<string, ReviewMiss[]> missesByObservation)
    {
        var approvals = items.Where(item => item.Approved).ToArray();
        var missed = approvals.Where(item => missesByObservation.ContainsKey(item.Id)).ToArray();
        var bySource = missed
            .SelectMany(item => missesByObservation[item.Id])
            .GroupBy(miss => miss.Source, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var blocks = items.Count(item => item.Outcome == ReviewOutcomes.Block);
        var priced = items.Where(item => item.CostUsd is not null).ToArray();
        decimal? total = priced.Length == 0 ? null : priced.Sum(item => item.CostUsd!.Value);
        var findings = items.Aggregate(ReviewFindingCounts.None, (sum, item) => sum.Add(item.Findings));
        var pricedRealFindings = priced.Sum(item => item.Findings.Real);
        var timed = items.Where(item => item.DurationMs > 0).ToArray();
        return new ReviewEffectivenessRow(
            level, model, reasoning,
            items.Count, approvals.Length, blocks,
            Rate(blocks, items.Count),
            missed.Length,
            Rate(missed.Length, approvals.Length),
            bySource,
            items.Sum(item => item.InputTokens),
            items.Sum(item => item.OutputTokens),
            items.Sum(item => item.CacheReadTokens),
            items.Sum(item => item.CacheCreationTokens),
            priced.Length,
            total,
            total is null ? null : Math.Round(total.Value / priced.Length, 6),
            findings,
            findings.Real,
            total is null || pricedRealFindings == 0 ? null : Math.Round(total.Value / pricedRealFindings, 6),
            timed.Length == 0 ? null : (long)Math.Round(timed.Average(item => (double)item.DurationMs)));
    }

    private static double? Rate(int numerator, int denominator)
        => denominator == 0 ? null : Math.Round((double)numerator / denominator, 4);

    private static string Label(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();
}
