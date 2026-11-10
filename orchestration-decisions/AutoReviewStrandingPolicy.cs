namespace AgentStudio.OrchestrationDecisions;

public enum AutoReviewStrandingAction { Leave, StartStep, ReenterBatch, Escalate }

/// <param name="ScheduledRetryOverdue">
/// Time since the scheduled review infrastructure retry of the current review
/// came due, negative while its backoff runs, null when none is scheduled.
/// </param>
/// <param name="CauseBreakerWait">
/// An open cause breaker holds the card for its release or runs it as the probe.
/// </param>
public sealed record AutoReviewStrandingFacts(
    TimeSpan WithoutOwner, TimeSpan Threshold, bool RunningStep, bool ActiveReview,
    bool BatchMember, bool QueuedRound, bool QueuedStep, bool ReportOnly,
    bool HasCompletedRun, bool HasReview, bool PassedReview,
    TimeSpan? SinceLastIntervention = null, bool StartedStepBefore = false,
    bool ReenteredBatchBefore = false, TimeSpan? ScheduledRetryOverdue = null,
    bool CauseBreakerWait = false, bool InfrastructureFailure = false,
    bool InfrastructureRetryAvailable = false, bool InfrastructureRetryScheduled = false);

/// <summary>Per-card liveness decision for Auto Review. All ownership facts are read before acting.</summary>
public static class AutoReviewStrandingPolicy
{
    public static AutoReviewStrandingAction Decide(AutoReviewStrandingFacts facts)
    {
        if (facts.ScheduledRetryOverdue is { } overdue && overdue > facts.Threshold)
            return AutoReviewStrandingAction.Escalate;
        if (facts.WithoutOwner <= facts.Threshold
            || facts.SinceLastIntervention is { } elapsed && elapsed <= facts.Threshold
            || facts.RunningStep || facts.ActiveReview
            || facts.BatchMember || facts.QueuedRound || facts.QueuedStep
            || OwnedByScheduledRetry(facts) || facts.CauseBreakerWait
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

    /// <summary>
    /// The retry scheduler owns the card while the backoff runs and for one
    /// threshold after the retry came due. A retry that no tick fires within
    /// that time (scheduler disabled or failing) leaves the card ownerless.
    /// </summary>
    private static bool OwnedByScheduledRetry(AutoReviewStrandingFacts facts)
        => facts.ScheduledRetryOverdue is { } overdue && overdue <= facts.Threshold;
}
