using AgentStudio.Tokens;

namespace AgentStudio.Review;

/// <summary>
/// One measured quantity of a review step, per round and per card. Medians,
/// p90 and the total use known values only; <see cref="UnknownRounds"/> and
/// <see cref="UnknownCards"/> count the rounds and cards with a missing value,
/// so a legacy row never pulls a median towards zero.
/// </summary>
public sealed record ReviewQuantity(
    double? PerRoundMedian, double? PerRoundP90, double? PerCardMedian, double? PerCardP90,
    double Total, int KnownRounds, int UnknownRounds, int KnownCards, int UnknownCards);

/// <summary>Minutes and tokens of one review step over the window.</summary>
public sealed record ReviewStepBaselineRow(
    string Step, string Tier, int Cards, int Rounds, int Executions,
    ReviewQuantity WallMinutes, ReviewQuantity InputTokens, ReviewQuantity OutputTokens,
    ReviewQuantity CacheReadTokens, ReviewQuantity CacheCreationTokens);

/// <summary>Pure per-step fold of review step samples (AGT-3400 S2, deliverable 1).</summary>
public static class ReviewStepBaselineFold
{
    public static IReadOnlyList<ReviewStepBaselineRow> Build(IReadOnlyList<ReviewStepSample> samples)
        => samples.GroupBy(sample => sample.Step, StringComparer.Ordinal)
            .Select(step =>
            {
                var rounds = step.GroupBy(sample => (sample.JobId, sample.RoundId))
                    .Select(round => (round.Key.JobId, Samples: round.ToList())).ToList();
                ReviewQuantity Quantity(Func<ReviewStepSample, double?> value)
                    => Measure(rounds, value);
                return new ReviewStepBaselineRow(step.Key, ReviewSteps.Tier(step.Key),
                    rounds.Select(round => round.JobId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    rounds.Count, step.Count(),
                    Quantity(sample => sample.WallMinutes),
                    Quantity(sample => sample.Tokens?.Input),
                    Quantity(sample => sample.Tokens?.Output),
                    Quantity(sample => sample.Tokens?.CacheRead),
                    Quantity(sample => sample.Tokens?.CacheCreation));
            })
            .OrderBy(row => TierOrder(row.Tier)).ThenByDescending(row => row.WallMinutes.Total)
            .ThenBy(row => row.Step, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A round sums its executions of the step and is unknown when one of them
    /// is; a card sums its rounds and is unknown when one of them is.
    /// </summary>
    internal static ReviewQuantity Measure(
        IReadOnlyList<(string JobId, List<ReviewStepSample> Samples)> rounds, Func<ReviewStepSample, double?> value)
    {
        var perRound = rounds.Select(round => (round.JobId,
            Value: round.Samples.All(sample => value(sample) is not null)
                ? round.Samples.Sum(sample => value(sample)!.Value) : (double?)null)).ToList();
        var perCard = perRound.GroupBy(round => round.JobId, StringComparer.OrdinalIgnoreCase)
            .Select(card => card.All(round => round.Value is not null)
                ? card.Sum(round => round.Value!.Value) : (double?)null).ToList();
        var knownRounds = perRound.Where(round => round.Value is not null).Select(round => round.Value!.Value).ToList();
        var knownCards = perCard.Where(card => card is not null).Select(card => card!.Value).ToList();
        return new ReviewQuantity(
            ReviewStatisticsFold.Quantile(knownRounds, 0.5), ReviewStatisticsFold.Quantile(knownRounds, 0.9),
            ReviewStatisticsFold.Quantile(knownCards, 0.5), ReviewStatisticsFold.Quantile(knownCards, 0.9),
            knownRounds.Sum(), knownRounds.Count, perRound.Count - knownRounds.Count,
            knownCards.Count, perCard.Count - knownCards.Count);
    }

    private static int TierOrder(string tier) => tier switch
    {
        ReviewSteps.CardCheckTier => 0,
        ReviewSteps.PackageTier => 1,
        ReviewSteps.MemberTier => 2,
        _ => 3,
    };
}
