namespace AgentStudio.Tasks;

/// <summary>Pure lane admission rule for the guarded delivery chain.</summary>
public static class DeliveryLanePolicy
{
    /// <summary>Shared acceptance freshness check for reconciliation and archive admission.</summary>
    public static bool IsAcceptedDeliveryStale(TaskInfo card, TaskIntegrationStatus? status)
    {
        var claim = card.CompletionClaim;
        if (claim is null) return true;
        var subject = TaskIntegrationStatusService.CurrentReviewSubject(card);
        var latestCommit = card.Commits.Where(TaskCommitSupersession.IsEffectiveDelivery)
            .OrderBy(commit => commit.DeliveryGeneration ?? 0)
            .ThenBy(commit => commit.At)
            .LastOrDefault();
        var currentResult = subject?.ResultSha ?? latestCommit?.ResultSha ?? latestCommit?.Sha;
        var currentEpoch = subject?.RunAttemptId ?? latestCommit?.RunAttemptId
            ?? latestCommit?.DeliveryGeneration?.ToString() ?? latestCommit?.Sha;
        // A target HEAD is a cache key for the Git reachability calculation,
        // not part of the accepted delivery. Legacy claims have no result or
        // epoch, so Git's current integrated verdict is their available proof.
        // Operator overrides are an explicit acceptance ground; they can also
        // cover a delivery that was not integrated when the operator accepted it.
        if (claim.Basis is not (CompletionClaimBases.IntegratedDelivery
            or CompletionClaimBases.OperatorOverride)) return true;
        if (claim.Basis == CompletionClaimBases.IntegratedDelivery
            && status?.Status != IntegrationStatuses.Integrated) return true;
        var resultChanged = !string.IsNullOrWhiteSpace(claim.ResultSha)
            && !string.IsNullOrWhiteSpace(currentResult)
            && !string.Equals(claim.ResultSha, currentResult, StringComparison.OrdinalIgnoreCase);
        var epochChanged = !string.IsNullOrWhiteSpace(claim.DeliveryEpoch)
            && !string.IsNullOrWhiteSpace(currentEpoch)
            && !string.Equals(claim.DeliveryEpoch, currentEpoch, StringComparison.Ordinal);
        return resultChanged || epochChanged;
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
