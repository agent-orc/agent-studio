namespace AgentStudio.Tasks;

/// <summary>Pure lane admission rule for the guarded delivery chain.</summary>
public static class DeliveryLanePolicy
{
    public static string? ReconciliationTarget(TaskInfo card, TaskIntegrationStatus? status)
    {
        var integrated = status?.Status == IntegrationStatuses.Integrated;
        var fatal = RequiresEscalation(status);
        if (card.State == TaskStates.AutoReview && card.Phase == LifecyclePhases.Integrating)
            return integrated ? TaskStates.HumanReview : fatal ? TaskStates.Escalated : null;
        if (card.State == TaskStates.HumanReview && !integrated)
            return fatal ? TaskStates.Escalated : TaskStates.AutoReview;
        if (card.State != TaskStates.Completed) return null;

        var claim = card.CompletionClaim;
        var subject = TaskIntegrationStatusService.CurrentReviewSubject(card);
        var stale = !integrated || claim?.Basis != CompletionClaimBases.IntegratedDelivery
            || !string.Equals(claim.IntegrationBranch,
                status?.IntegrationBranch, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(claim.TargetRefFingerprint)
            || !string.Equals(claim.TargetRefFingerprint,
                status?.TargetRefFingerprint, StringComparison.Ordinal)
            || (subject is not null
                && (!string.Equals(claim.ResultSha, subject.ResultSha,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(claim.DeliveryEpoch, subject.RunAttemptId,
                        StringComparison.Ordinal)));
        return stale ? TaskStates.AutoReview : null;
    }

    public static bool RequiresEscalation(TaskIntegrationStatus? status)
        => status?.Status != IntegrationStatuses.Integrated
           && (status?.Status is IntegrationStatuses.ConflictSkipped or IntegrationStatuses.NoBranch
               || status?.Failure is not null);

    public static DeliveryLaneDecision Decide(
        string source, string target, bool requiresIntegration, string? status,
        bool archiveOverride = false)
    {
        if (target is not (TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive))
            return new(true);
        if (!requiresIntegration)
            return new(true);
        if (status == IntegrationStatuses.Integrated)
            return new(true);
        if (target == TaskStates.Archive && archiveOverride)
            return new(true);
        var category = status switch
        {
            IntegrationStatuses.NoBranch => "missing-delivery",
            IntegrationStatuses.ConflictSkipped => "integration-conflict",
            _ => "integration-pending",
        };
        return new(false, category,
            target == TaskStates.Archive
                ? "Integrate the delivery or request an owner archive override for this card."
                : category == "missing-delivery"
                    ? "Recover or attribute the expected code delivery, then integrate it."
                    : category == "integration-conflict"
                        ? "Resolve the integration failure and rerun its gate."
                        : "Complete integration and publish the target branch before review.");
    }
}

public sealed record DeliveryLaneDecision(bool Allowed, string? Category = null, string? RecoveryAction = null);
