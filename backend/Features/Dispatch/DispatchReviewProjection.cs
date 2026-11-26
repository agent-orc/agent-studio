using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Dispatch;

public sealed record DispatchWeightRevision(string Id, string Author, DateTimeOffset At, string? Reason,
    IReadOnlyDictionary<string, int> Weights, double Gate)
{
    public const string Setting = "dispatch.review.weights";
    public static DispatchWeightRevision Default => new("w1", "platform", DateTimeOffset.UnixEpoch,
        "DS-D7 B optimistic defaults", new Dictionary<string, int>
        {
            ["conflicts"] = 30, ["tokens"] = 25, ["rework"] = 10, ["forecast"] = 10,
            ["parallelism"] = 10, ["lead"] = 5, ["ownCost"] = 5, ["explanation"] = 5,
        }, 0.8);

    public static void Validate(IReadOnlyDictionary<string, int>? weights, double gate)
    {
        if (weights is null || weights.Count != Default.Weights.Count
            || Default.Weights.Keys.Any(key => !weights.ContainsKey(key))
            || weights.Values.Any(value => value is < 0 or > 100) || weights.Values.Sum() != 100
            || !double.IsFinite(gate) || gate is < 0.5 or > 1)
            throw new ArgumentException("Use all eight non-negative weights adding to 100 and a gate from 0.5 to 1.");
    }
}

/// <summary>Stored observations, independent of any weight revision. Null means no evidence.</summary>
public sealed record DispatchReviewRaw(string Persona, string Strategy, string Mode, int IntegratedCards,
    IReadOnlyList<string> Cards, IReadOnlyDictionary<string, double?> Criteria, double? FileF1,
    bool TokensEstimated, IReadOnlyList<string> Warnings)
{
    public int AcceptedCards { get; init; }
    public double? DeterministicExplanationPassRate { get; init; }
    public double? OwnerExplanationRating { get; init; }
}
public sealed record DispatchReviewWindow(string Project, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<DispatchReviewRaw> Raw)
{
    public IReadOnlyList<DispatchReviewRaw> ObservedPersonas { get; init; } = [];
}
public sealed record DispatchReviewRow(DispatchReviewRaw Raw, IReadOnlyDictionary<string, double?> Scores,
    double? PlainValue, double? Value, double? GateMultiplier, string Status);
public sealed record DispatchReviewProjection(string Project, DateTimeOffset From, DateTimeOffset To,
    DispatchWeightRevision Weights, IReadOnlyList<DispatchReviewRow> Personas,
    IReadOnlyList<DispatchWeightRevision> WeightRevisions)
{
    public DispatchLeaseV1? Lease { get; init; }
    public IReadOnlyList<DispatchReviewRaw> ObservedPersonas { get; init; } = [];
    public IReadOnlyList<DispatchReviewCompetition> Competition { get; init; } = [];
}

public sealed record DispatchReviewCompetition(string Champion, string Challenger,
    DispatchCompetitionFacts Facts, DispatchCompetitionEvaluation Evaluation);

/// <summary>Wave/guard observations supplied by DS-11, separate from the weight-dependent scores.</summary>
public sealed record DispatchCompetitionEvidence(string Challenger, int ShadowIntegratedCards, int ShadowWaves, int AlternatingLiveWaves,
    bool AheadInEveryLiveWave, int GuardCards, double? LiveConflictsPer100,
    double? BaselineShadowConflictsPer100, bool SameGuardCards);

public static class DispatchReviewScoring
{
    public static DispatchReviewProjection Score(DispatchReviewWindow window, DispatchWeightRevision revision,
        IReadOnlyList<DispatchWeightRevision> revisions)
    {
        DispatchWeightRevision.Validate(revision.Weights, revision.Gate);
        if (!window.Raw.Any(row => row.Strategy == DispatchStrategies.LaneEntry))
            throw new ArgumentException("The comparison must include the Queue keeper baseline.");
        var cohort = window.Raw[0].Cards.Order(StringComparer.Ordinal).ToArray();
        if (window.Raw.Any(row => !row.Cards.Order(StringComparer.Ordinal).SequenceEqual(cohort)))
            throw new ArgumentException("Compare personas on the same cards.");
        var rows = window.Raw.Select(raw =>
        {
            var scores = new Dictionary<string, double?>();
            foreach (var key in revision.Weights.Keys)
            {
                var value = raw.Criteria.GetValueOrDefault(key);
                if (value is null) { scores[key] = null; continue; }
                if (key is "forecast" or "parallelism") scores[key] = 100 * Math.Clamp(value.Value, 0, 1);
                else
                {
                    var set = window.Raw.Select(row => row.Criteria.GetValueOrDefault(key)).OfType<double>().ToArray();
                    var low = set.Min();
                    var high = set.Max();
                    scores[key] = high == low ? 100 : key == "explanation"
                        ? 100 * (value - low) / (high - low) : 100 * (high - value) / (high - low);
                }
            }
            // A missing criterion is not a zero-cost observation. Exclude it for every persona
            // so each value in the comparison has the same denominator.
            var measured = revision.Weights.Keys.Where(key => window.Raw.All(row => row.Criteria.GetValueOrDefault(key).HasValue)).ToArray();
            var denominator = measured.Sum(key => revision.Weights[key]);
            var gate = raw.Criteria.GetValueOrDefault("parallelism") is { } parallelism ? Math.Min(1, parallelism / revision.Gate) : (double?)null;
            var plain = raw.IntegratedCards >= 40 && denominator > 0
                ? measured.Sum(key => scores[key]!.Value * revision.Weights[key]) / denominator : (double?)null;
            return new DispatchReviewRow(raw, scores, plain, plain * gate, gate,
                raw.IntegratedCards < 40 ? "too few cards" : gate is null ? "missing parallelism evidence" : denominator == 0 ? "unmeasured" : "scored");
        }).OrderByDescending(row => row.Value).ThenBy(row => row.Raw.Persona, StringComparer.Ordinal).ToArray();
        return new(window.Project, window.From, window.To, revision, rows, revisions) { ObservedPersonas = window.ObservedPersonas };
    }
}
