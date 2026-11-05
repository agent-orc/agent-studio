namespace AgentStudio.OrchestrationDecisions;

public enum AutoReviewStrandingAction { Leave, StartStep, ReenterBatch, Escalate }

public sealed record AutoReviewStrandingFacts(
    TimeSpan InLane, TimeSpan Threshold, bool RunningStep, bool ActiveReview,
    bool BatchMember, bool QueuedRound, bool QueuedStep, bool ReportOnly,
    bool HasCompletedRun, bool HasReview, bool PassedReview);

/// <summary>Per-card liveness decision for Auto Review. All ownership facts are read before acting.</summary>
public static class AutoReviewStrandingPolicy
{
    public static AutoReviewStrandingAction Decide(AutoReviewStrandingFacts facts)
    {
        if (facts.InLane <= facts.Threshold || facts.RunningStep || facts.ActiveReview
            || facts.BatchMember || facts.QueuedRound || facts.QueuedStep)
            return AutoReviewStrandingAction.Leave;
        if (facts.ReportOnly || facts.HasCompletedRun && !facts.HasReview)
            return AutoReviewStrandingAction.StartStep;
        if (facts.PassedReview) return AutoReviewStrandingAction.ReenterBatch;
        return AutoReviewStrandingAction.Escalate;
    }
}
