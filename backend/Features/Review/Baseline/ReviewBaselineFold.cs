namespace AgentStudio.Review;

/// <summary>Review material size of one delivery: changed files and changed lines.</summary>
public sealed record ReviewMaterialSize(int Files, int Lines);

/// <summary>
/// The evidence of one card for the review baseline: every review step sample
/// of the card (all rounds, not only the window), its reviewOther seconds from
/// the cycle-time row when the card completed in the window, and the material
/// size per reviewed Result-SHA with a card-level fallback.
/// </summary>
public sealed record ReviewBaselineCard(
    string JobId, string TaskKey, string? DossierPath, IReadOnlyList<ReviewStepSample> Samples,
    double? ReviewOtherSeconds = null, ReviewMaterialSize? Material = null,
    IReadOnlyDictionary<string, ReviewMaterialSize>? MaterialBySha = null);

/// <summary>
/// a of the Dossier: catalogue-aspect wall time as a share of reviewOther,
/// pooled over cards and as a per-card median. Only cards that completed in
/// the window with known aspect minutes count.
/// </summary>
public sealed record AspectShare(double? Pooled, double? MedianPerCard, int Cards, int UnknownCards);

/// <summary>Review baseline of one project (Dossier AGT-3400, slice S2).</summary>
public sealed record ReviewBaselineReport(
    string Project, int Days, DateTime GeneratedAt, string Method,
    IReadOnlyList<ReviewStepBaselineRow> Steps, AspectShare AspectShareOfReviewOther,
    IReadOnlyList<FixedShareEstimate> FixedShare, double? PackageTierFixedShare, WaveCensus Waves);

/// <summary>Pure composition of the S2 review baseline from per-card evidence.</summary>
public static class ReviewBaselineFold
{
    public const string Method =
        "Review steps from pipeline-execution.json (every attempt and earlier run; carried-over verdicts excluded) "
        + "plus externally recorded review observations. Rows without token or duration fields count as unknown, "
        + "never zero. f: least squares of prompt tokens (input plus cache read plus cache creation) against changed "
        + "lines of the reviewed delivery, from published Git facts; intercept over mean prompt. a: catalogue-aspect "
        + "wall minutes over cycle-time reviewOther of cards completed in the window. k: PK5 A packages (one Dossier, "
        + "card check passed within 60 minutes of the first, at most 4 members, 200 files, 12,000 lines).";

    public static ReviewBaselineReport Build(string project, int days, DateTime nowUtc,
        IReadOnlyList<ReviewBaselineCard> cards)
    {
        var since = nowUtc.AddDays(-days);
        bool InWindow(ReviewStepSample sample) => sample.CompletedAt >= since && sample.CompletedAt <= nowUtc;
        var windowed = cards.Select(card => (Card: card, Samples: card.Samples.Where(InWindow).ToList()))
            .Where(entry => entry.Samples.Count > 0).ToList();
        var fixedShare = ReviewFixedShareEstimator.Estimate(windowed
            .SelectMany(entry => entry.Samples
                .Where(sample => sample.Step is not (ReviewSteps.ToolCommands or ReviewSteps.Decision))
                .Select(sample => new FixedShareSample(sample.Step, sample.Tokens?.Prompt,
                    MaterialOf(entry.Card, sample.ReviewedResultSha)?.Lines)))
            .ToList());
        return new ReviewBaselineReport(project, days, nowUtc, Method,
            ReviewStepBaselineFold.Build(windowed.SelectMany(entry => entry.Samples).ToList()),
            Share(cards), fixedShare, ReviewFixedShareEstimator.PackageTierShare(fixedShare),
            ReviewWaveCensus.Build(windowed.Select(entry => CensusCard(entry.Card, entry.Samples)).ToList()));
    }

    /// <summary>
    /// The card check passed in the latest round whose card-check samples all
    /// passed. Its time and that round's material size feed the census.
    /// </summary>
    internal static WaveCensusCard CensusCard(ReviewBaselineCard card, IReadOnlyList<ReviewStepSample> samples)
    {
        var passed = samples.Where(sample => ReviewSteps.Tier(sample.Step) == ReviewSteps.CardCheckTier)
            .GroupBy(sample => sample.RoundId, StringComparer.Ordinal)
            .Where(round => round.All(sample => sample.Passed))
            .Select(round => round.ToList())
            .MaxBy(round => round.Max(sample => sample.CompletedAt));
        var sha = passed?.Select(sample => sample.ReviewedResultSha).LastOrDefault(value => value is not null)
                  ?? samples.Select(sample => sample.ReviewedResultSha).LastOrDefault(value => value is not null);
        var material = MaterialOf(card, sha);
        // The reviewed file count is the merge-base material itself; the Git
        // facts count stands in for rows that predate it.
        var files = samples.Where(sample => sample.ReviewedResultSha == sha)
            .Select(sample => sample.FilesReviewed).LastOrDefault(value => value is not null) ?? material?.Files;
        return new WaveCensusCard(card.TaskKey, card.DossierPath,
            passed?.Max(sample => sample.CompletedAt), files, material?.Lines);
    }

    internal static AspectShare Share(IReadOnlyList<ReviewBaselineCard> cards)
    {
        var eligible = cards.Where(card => card.ReviewOtherSeconds is > 0).ToList();
        var known = new List<(double Aspect, double ReviewOther)>();
        foreach (var card in eligible)
        {
            var aspects = card.Samples.Where(sample => ReviewSteps.CatalogueAspects.Contains(sample.Step)).ToList();
            if (aspects.Count == 0 || aspects.Any(sample => sample.WallMinutes is null)) continue;
            known.Add((aspects.Sum(sample => sample.WallMinutes!.Value) * 60, card.ReviewOtherSeconds!.Value));
        }
        var reviewOther = known.Sum(card => card.ReviewOther);
        return new AspectShare(reviewOther <= 0 ? null : known.Sum(card => card.Aspect) / reviewOther,
            AgentStudio.Tokens.ReviewStatisticsFold.Quantile(known.Select(card => card.Aspect / card.ReviewOther), 0.5),
            known.Count, eligible.Count - known.Count);
    }

    private static ReviewMaterialSize? MaterialOf(ReviewBaselineCard card, string? resultSha)
        => resultSha is not null && card.MaterialBySha?.TryGetValue(resultSha, out var exact) == true
            ? exact : card.Material;
}
