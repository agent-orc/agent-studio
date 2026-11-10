namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Recovery for an ownerless Auto Review card. <c>IntegratePerTask</c> (AGT-3740):
/// the passed review ran its own build and tests instead of deferring them to the
/// batch gate, so batch admission refuses it. The card integrates through the
/// per-task path with that review evidence, or, when no recorded verdict can admit
/// it, gets a re-planned review.
/// </summary>
public enum AutoReviewStrandingAction { Leave, StartStep, ReenterBatch, Escalate, IntegratePerTask }

public sealed record AutoReviewStrandingFacts(
    TimeSpan InLane, TimeSpan Threshold, bool RunningStep, bool ActiveReview,
    bool BatchMember, bool QueuedRound, bool QueuedStep, bool ReportOnly,
    bool HasCompletedRun, bool HasReview, bool PassedReview,
    // AGT-3740: BuildTestDeferredToBatch is false when the passed review's frozen
    // plan ran build and tests itself; such a card has no batch route.
    // IntegratedPerTaskBefore: an earlier intervention in this lane visit chose the
    // per-task route.
    bool BuildTestDeferredToBatch = true, bool IntegratedPerTaskBefore = false,
    TimeSpan? SinceLastIntervention = null, bool StartedStepBefore = false,
    bool ReenteredBatchBefore = false);

/// <summary>Per-card liveness decision for Auto Review. All ownership facts are read before acting.</summary>
public static class AutoReviewStrandingPolicy
{
    public static AutoReviewStrandingAction Decide(AutoReviewStrandingFacts facts)
    {
        if (facts.InLane <= facts.Threshold
            || facts.SinceLastIntervention is { } elapsed && elapsed <= facts.Threshold
            || facts.RunningStep || facts.ActiveReview
            || facts.BatchMember || facts.QueuedRound || facts.QueuedStep)
            return AutoReviewStrandingAction.Leave;
        if (facts.ReportOnly || facts.HasCompletedRun && !facts.HasReview)
            return facts.StartedStepBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.StartStep;
        // AGT-3740: batch admission refuses a review that ran its own build and
        // tests (BuildTestNotDeferred). Re-entering the batch was a dead end
        // that escalated the card on every lane visit.
        if (facts.PassedReview && !facts.BuildTestDeferredToBatch)
            return facts.IntegratedPerTaskBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.IntegratePerTask;
        if (facts.PassedReview)
            return facts.ReenteredBatchBefore ? AutoReviewStrandingAction.Escalate : AutoReviewStrandingAction.ReenterBatch;
        return AutoReviewStrandingAction.Escalate;
    }
}
