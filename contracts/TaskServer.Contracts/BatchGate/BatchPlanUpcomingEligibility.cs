namespace AgentStudio.TaskServer.Contracts.BatchGate;

/// <summary>
/// Decides whether an in-flight review may count as an upcoming batch member.
/// Batch membership requires a build/test deferred to the batch and the
/// card's current run, so any other review can never join a batch and must
/// not hold waiting cards on the look-ahead window.
/// </summary>
public static class BatchPlanUpcomingEligibility
{
    public static bool IsCandidate(ReviewPlanDto? plan, bool sourceRunIsCurrent)
        => plan?.BuildTestDeferredToBatch == true && sourceRunIsCurrent;
}
