namespace AgentStudio.OrchestrationDecisions;

public enum AutoReviewStrandingAction { Leave, StartStep, ReenterBatch, Escalate }

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
