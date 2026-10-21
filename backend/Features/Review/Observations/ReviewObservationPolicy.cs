namespace AgentStudio.Review.Observations;

/// <summary>Pure attribution and aggregation rules for observed reviews.</summary>
public static class ReviewObservationPolicy
{
    public static bool Approved(ReviewObservation review)
        => string.Equals(review.Verdict, "pass", StringComparison.OrdinalIgnoreCase)
           || string.Equals(review.Verdict, "A", StringComparison.OrdinalIgnoreCase)
           || string.Equals(review.Verdict, "B", StringComparison.OrdinalIgnoreCase);

    public static bool IsMiss(ReviewObservation review, ReviewLaterEvidence evidence)
        => Approved(review)
           && string.Equals(review.Id, evidence.ReviewId, StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(review.ResultSha)
           && string.Equals(review.ResultSha, evidence.ResultSha, StringComparison.OrdinalIgnoreCase)
           && evidence.FoundAt > review.ReviewedAt
           && !string.IsNullOrWhiteSpace(evidence.EvidenceRef);

    public static IReadOnlyList<ReviewObservationAggregate> Aggregate(
        IReadOnlyList<ReviewObservation> reviews, IReadOnlyList<ReviewLaterEvidence> evidence)
        => reviews.GroupBy(r => (Model: r.Model ?? "unknown", Reasoning: r.ReasoningLevel ?? "unknown"))
            .Select(group =>
            {
                var rows = group.ToArray();
                var approved = rows.Count(Approved);
                var missEligible = rows.Count(row => Approved(row) && !string.IsNullOrWhiteSpace(row.ResultSha));
                var missed = rows.Count(row => evidence.Any(item => IsMiss(row, item)));
                var blockers = rows.Count(row => row.Findings.Blocker > 0
                    || string.Equals(row.Verdict, "block", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Verdict, "D", StringComparison.OrdinalIgnoreCase));
                var confirmedKnown = rows.All(row => row.Findings.Total == 0 || row.ConfirmedFindings.HasValue);
                int? findings = confirmedKnown ? rows.Sum(row => row.ConfirmedFindings ?? 0) : null;
                var allPriced = rows.All(row => row.CostUsd.HasValue);
                decimal? cost = allPriced ? rows.Sum(row => row.CostUsd!.Value) : null;
                return new ReviewObservationAggregate(
                    group.Key.Model, group.Key.Reasoning, rows.Length, blockers, approved,
                    missEligible,
                    missed, findings, cost, cost / rows.Length,
                    (double)blockers / rows.Length,
                    missEligible == 0 ? null : (double)missed / missEligible,
                    findings is null or 0 ? null : cost / findings);
            })
            .OrderBy(row => row.Model, StringComparer.Ordinal)
            .ThenBy(row => row.ReasoningLevel, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Keep full review after repeated measured misses; otherwise leave existing scope policy intact.</summary>
    public static bool RequireFullReview(IReadOnlyList<ReviewObservationAggregate> aggregates)
        => aggregates.Any(row => row.MissEligibleApprovals >= 5 && row.Misses >= 2 && row.MissRate >= 0.2);
}
