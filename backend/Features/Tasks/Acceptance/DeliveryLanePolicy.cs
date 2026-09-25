namespace AgentStudio.Tasks;

/// <summary>Pure lane admission rule for the guarded delivery chain.</summary>
public static class DeliveryLanePolicy
{
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
