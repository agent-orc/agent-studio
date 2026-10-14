namespace AgentStudio.TaskServer.Contracts.BatchGate;

/// <summary>
/// Decides whether an in-flight review may count as an upcoming batch member.
/// Batch membership requires a build/test deferred to the batch and the
/// card's current run, so any other review can never join a batch and must
/// not hold waiting cards on the look-ahead window.
/// </summary>
public static class BatchPlanUpcomingEligibility
{
    public static bool IsCandidate(ReviewPlanDto? plan, bool sourceRunIsCurrent,
        bool continuationPending, string? reviewSubjectSha, string? currentDeliverySha)
        => plan?.BuildTestDeferredToBatch == true && sourceRunIsCurrent
            && DeliveryExclusion(continuationPending, reviewSubjectSha, currentDeliverySha) is null;

    /// <summary>Shared by waiting deliveries and upcoming reviews, before Git reads.</summary>
    public static string? DeliveryExclusion(bool continuationPending,
        string? reviewSubjectSha, string? currentDeliverySha)
    {
        if (continuationPending) return "pending-continue-intent";
        if (string.IsNullOrWhiteSpace(reviewSubjectSha)
            || string.IsNullOrWhiteSpace(currentDeliverySha)
            || !string.Equals(reviewSubjectSha, currentDeliverySha, StringComparison.OrdinalIgnoreCase))
            return "review-subject-mismatch";
        return null;
    }
}
