namespace AgentStudio.Review;

/// <summary>
/// One aspect execution for the fixed-share regression: the prompt tokens the
/// model read and the review material lines it was given. Either may be
/// unknown; such a sample is counted, never fitted.
/// </summary>
public sealed record FixedShareSample(string Aspect, long? PromptTokens, int? MaterialLines);

/// <summary>
/// Least-squares fit of prompt tokens against material lines for one aspect.
/// <see cref="InterceptTokens"/> is the part of the prompt that does not grow
/// with the diff (instructions, Dossier, shared context). <see cref="FixedShare"/>
/// is f of the Dossier: the intercept as a share of the mean prompt, clamped
/// to [0, 1]. Null when fewer than <see cref="ReviewFixedShareEstimator.MinimumSamples"/>
/// known samples exist or every sample has the same material size.
/// </summary>
public sealed record FixedShareEstimate(
    string Aspect, string Tier, int Samples, int UnknownSamples,
    double? InterceptTokens, double? TokensPerLine, double? MeanPromptTokens, double? MeanMaterialLines,
    double? FixedShare, double? RSquared);

/// <summary>Pure fixed-share estimate per aspect (AGT-3400 S2, deliverable 2).</summary>
public static class ReviewFixedShareEstimator
{
    public const int MinimumSamples = 3;

    public static IReadOnlyList<FixedShareEstimate> Estimate(IReadOnlyList<FixedShareSample> samples)
        => samples.GroupBy(sample => sample.Aspect, StringComparer.Ordinal)
            .Select(group => Fit(group.Key, group.ToList()))
            .OrderBy(estimate => estimate.Tier == ReviewSteps.PackageTier ? 0 : 1)
            .ThenBy(estimate => estimate.Aspect, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The package-tier f weighted by each aspect's prompt volume, so a large
    /// aspect counts for its share of the tokens. Null without a fitted aspect.
    /// </summary>
    public static double? PackageTierShare(IReadOnlyList<FixedShareEstimate> estimates)
    {
        var fitted = estimates.Where(estimate => estimate.Tier == ReviewSteps.PackageTier
            && estimate.FixedShare is not null && estimate.MeanPromptTokens is > 0).ToList();
        var volume = fitted.Sum(estimate => estimate.MeanPromptTokens!.Value * estimate.Samples);
        return volume <= 0 ? null
            : fitted.Sum(estimate => estimate.FixedShare!.Value * estimate.MeanPromptTokens!.Value * estimate.Samples) / volume;
    }

    internal static FixedShareEstimate Fit(string aspect, IReadOnlyList<FixedShareSample> samples)
    {
        var known = samples.Where(sample => sample.PromptTokens is not null && sample.MaterialLines is not null)
            .Select(sample => (X: (double)sample.MaterialLines!.Value, Y: (double)sample.PromptTokens!.Value))
            .ToList();
        var tier = ReviewSteps.Tier(aspect);
        var unknown = samples.Count - known.Count;
        if (known.Count == 0)
            return new FixedShareEstimate(aspect, tier, 0, unknown, null, null, null, null, null, null);
        var meanX = known.Average(point => point.X);
        var meanY = known.Average(point => point.Y);
        var sxx = known.Sum(point => (point.X - meanX) * (point.X - meanX));
        if (known.Count < MinimumSamples || sxx <= 0)
            return new FixedShareEstimate(aspect, tier, known.Count, unknown, null, null, meanY, meanX, null, null);
        var slope = known.Sum(point => (point.X - meanX) * (point.Y - meanY)) / sxx;
        var intercept = meanY - slope * meanX;
        var total = known.Sum(point => (point.Y - meanY) * (point.Y - meanY));
        var residual = known.Sum(point => Math.Pow(point.Y - (intercept + slope * point.X), 2));
        return new FixedShareEstimate(aspect, tier, known.Count, unknown, intercept, slope, meanY, meanX,
            meanY <= 0 ? null : Math.Clamp(intercept / meanY, 0, 1),
            total <= 0 ? null : 1 - residual / total);
    }
}
