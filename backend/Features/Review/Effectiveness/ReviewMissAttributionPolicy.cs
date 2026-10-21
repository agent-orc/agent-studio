namespace AgentStudio.Review;

/// <summary>
/// Pure rule for which review approvals a piece of defect evidence
/// contradicts (AGT-3269). Evidence only ever counts against the delivery it
/// names; a reissue or a fix round is a different delivery.
/// <list type="bullet">
///   <item>A gate failure, an operator send-back, a production incident or a
///   rollback contradicts every approval of the delivery, at every level.</item>
///   <item>A blocking review contradicts approvals of the other levels only.
///   Two aspects disagreeing inside one plan is coverage, not a miss.</item>
///   <item>Ordering inside one delivery does not matter: a grade approving
///   a delivery that a deep review blocks earlier or later approved the
///   same defect.</item>
/// </list>
/// The result is the full set of pairs; the caller stores the new ones, so a
/// replay never counts a miss twice.
/// </summary>
public static class ReviewMissAttributionPolicy
{
    public static IReadOnlyList<ReviewMiss> Attribute(
        IReadOnlyList<ReviewObservation> observations,
        IReadOnlyList<ReviewDefectEvidence> evidence,
        DateTime recordedAtUtc)
    {
        var misses = new List<ReviewMiss>();
        foreach (var item in evidence)
        {
            foreach (var observation in observations)
            {
                if (!Contradicts(item, observation)) continue;
                misses.Add(new ReviewMiss(observation.Id, item.Id, observation.Level, observation.Model,
                    observation.ReasoningLevel, item.Source, recordedAtUtc));
            }
        }
        return misses;
    }

    public static bool Contradicts(ReviewDefectEvidence evidence, ReviewObservation observation)
    {
        if (!observation.Approved) return false;
        if (evidence.Delivery != observation.Delivery) return false;
        if (string.Equals(evidence.FinderObservationId, observation.Id, StringComparison.Ordinal)) return false;
        if (evidence.Source == ReviewDefectSources.LaterReview)
            return evidence.FinderLevel is not null
                   && !string.Equals(evidence.FinderLevel, observation.Level, StringComparison.Ordinal);
        return true;
    }

    /// <summary>The defect evidence a review itself produces, or null when it does not find a defect.</summary>
    public static ReviewDefectEvidence? EvidenceFrom(ReviewObservation observation)
        => observation.FindsDefect
            ? new ReviewDefectEvidence(
                $"review:{observation.Id}",
                observation.Delivery,
                ReviewDefectSources.LaterReview,
                $"{observation.Level} review {observation.Subject} returned {observation.Verdict}.",
                observation.ObservedAtUtc,
                observation.Origin,
                FinderLevel: observation.Level,
                FinderObservationId: observation.Id,
                Severity: ReviewFindingSeverities.Blocker,
                Reference: observation.EvidenceRef)
            : null;
}
