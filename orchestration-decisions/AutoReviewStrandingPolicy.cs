namespace AgentStudio.OrchestrationDecisions;

public enum AutoReviewStrandingAction
{
    Leave,
    StartStep,
    ReenterBatch,
    Escalate,

    /// <summary>
    /// AGT-3740: the passed review ran its own build and tests instead of
    /// deferring them to the batch gate, so batch admission refuses it. The
    /// card integrates through the per-task path with that review evidence,
    /// or, when no recorded verdict can admit it, gets a re-planned review.
    /// </summary>
    IntegratePerTask,
}

public sealed record AutoReviewStrandingFacts(
    TimeSpan WithoutOwner, TimeSpan Threshold, bool RunningStep, bool ActiveReview,
    bool BatchMember, bool QueuedRound, bool QueuedStep, bool ReportOnly,
    bool HasCompletedRun, bool HasReview, bool PassedReview,
    TimeSpan? SinceLastIntervention = null, bool StartedStepBefore = false,
    bool ReenteredBatchBefore = false, bool InfrastructureFailure = false,
    bool InfrastructureRetryAvailable = false, bool InfrastructureRetryScheduled = false);

/// <summary>Per-card liveness decision for Auto Review. All ownership facts are read before acting.</summary>
public static class AutoReviewStrandingPolicy
{
    /// <summary>
    /// AGT-3740: the batch route of a passed review, applied to the action
    /// <see cref="Decide"/> returned. Batch admission refuses a review that ran
    /// its own build and tests (<c>BuildTestNotDeferred</c>), so re-entering the
    /// batch was a dead end that escalated the card on every lane visit. Such a
    /// card takes the per-task route instead, once per lane visit, even after an
    /// earlier batch re-entry of the same visit.
    /// </summary>
    public static AutoReviewStrandingAction RouteBatch(AutoReviewStrandingAction decided,
        AutoReviewStrandingFacts facts, AutoReviewBatchRouteFacts route)
        => decided is AutoReviewStrandingAction.ReenterBatch or AutoReviewStrandingAction.Escalate
            && facts is { PassedReview: true, HasReview: true, ReportOnly: false }
            && !route.BuildTestDeferredToBatch
                ? route.IntegratedPerTaskBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.IntegratePerTask
                : decided;

    public static AutoReviewStrandingAction Decide(AutoReviewStrandingFacts facts)
    {
        if (facts.WithoutOwner <= facts.Threshold
            || facts.SinceLastIntervention is { } elapsed && elapsed <= facts.Threshold
            || facts.RunningStep || facts.ActiveReview
            || facts.BatchMember || facts.QueuedRound || facts.QueuedStep
            || facts.InfrastructureRetryScheduled)
            return AutoReviewStrandingAction.Leave;
        if (facts.InfrastructureFailure)
            return facts.InfrastructureRetryAvailable
                ? AutoReviewStrandingAction.StartStep : AutoReviewStrandingAction.Escalate;
        if (facts.ReportOnly || facts.HasCompletedRun && !facts.HasReview)
            return facts.StartedStepBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.StartStep;
        if (facts.PassedReview)
            return facts.ReenteredBatchBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.ReenterBatch;
        return AutoReviewStrandingAction.Escalate;
    }
}

/// <param name="BuildTestDeferredToBatch">
/// The passed review's frozen plan deferred build and tests to the batch gate.
/// False when the review ran them itself; such a card has no batch route.
/// </param>
/// <param name="IntegratedPerTaskBefore">An earlier intervention in this lane visit chose the per-task route.</param>
public sealed record AutoReviewBatchRouteFacts(bool BuildTestDeferredToBatch, bool IntegratedPerTaskBefore = false);
