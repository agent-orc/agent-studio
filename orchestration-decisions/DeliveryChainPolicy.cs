namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Facts of one integration-required card in Auto Review, Human Review or
/// Delivered that the delivery-chain reconciler asks about.
/// </summary>
/// <param name="State">The card's lane.</param>
/// <param name="Integrating">The card is in the integration phase of Auto Review.</param>
/// <param name="Integrated">Git reports the current delivery as integrated.</param>
/// <param name="RequiresEscalation">The integration failed in a way a person must resolve (conflict, no branch, typed failure).</param>
/// <param name="AcceptedDeliveryStale">The acceptance of a Delivered card no longer matches its current delivery.</param>
/// <param name="CompletedReopenLimitReached">More Delivered cards would reopen in this pass than the per-pass bound allows.</param>
public sealed record DeliveryChainOperatorFacts(
    string State,
    bool Integrating,
    bool Integrated,
    bool RequiresEscalation,
    bool AcceptedDeliveryStale,
    bool CompletedReopenLimitReached);

public enum DeliveryChainAction
{
    Leave,

    /// <summary>The integrated delivery is ready for the human verdict.</summary>
    PromoteToHumanReview,

    /// <summary>Integrate the current delivery and repeat review.</summary>
    ReturnToAutoReview,

    /// <summary>The integration failed; a person recovers the delivery.</summary>
    Escalate,
}

public sealed record DeliveryChainVerdict(DeliveryChainAction Action, string ReasonCode, string Detail);

public static class DeliveryChainReasons
{
    public const string Integrated = "delivery-integrated";
    public const string IntegrationFailed = "integration-failed";
    public const string IntegrationPending = "integration-pending";
    public const string VerdictStale = "delivery-verdict-stale";
    public const string AcceptedDeliveryStale = "accepted-delivery-stale";
    public const string AcceptedDeliveryCurrent = "accepted-delivery-current";
    public const string CompletedReopenLimit = "completed-reopen-limit";
    public const string OutsideChain = "outside-delivery-chain";
}

/// <summary>
/// Pure lane rule of the guarded delivery chain (formerly
/// <c>DeliveryLanePolicy.ReconciliationTarget</c> in the monolith). An
/// integrated delivery in the integration phase advances to Human Review, a
/// Human Review card whose delivery is no longer integrated returns to Auto
/// Review, a stale Delivered acceptance reopens, and a fatal integration
/// failure escalates. Delivered reopens stop for the whole pass once the
/// per-pass bound is crossed, so a fleet-wide fault cannot drain the lane.
/// </summary>
public static class DeliveryChainPolicy
{
    public static DeliveryChainVerdict Decide(DeliveryChainOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.State == TaskStates.AutoReview && facts.Integrating)
        {
            if (facts.Integrated)
                return new(DeliveryChainAction.PromoteToHumanReview, DeliveryChainReasons.Integrated,
                    "The integrated delivery waits for the human verdict.");
            return facts.RequiresEscalation
                ? Escalate()
                : new(DeliveryChainAction.Leave, DeliveryChainReasons.IntegrationPending,
                    "The integration is still running.");
        }
        if (facts.State == TaskStates.HumanReview && !facts.Integrated)
            return facts.RequiresEscalation
                ? Escalate()
                : new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.VerdictStale,
                    "Integrate the current delivery and repeat review.");
        if (facts.State != TaskStates.Completed)
            return new(DeliveryChainAction.Leave, DeliveryChainReasons.OutsideChain,
                "The card is outside the reconciled delivery lanes.");
        if (!facts.AcceptedDeliveryStale)
            return new(DeliveryChainAction.Leave, DeliveryChainReasons.AcceptedDeliveryCurrent,
                "The acceptance matches the current delivery.");
        return facts.CompletedReopenLimitReached
            ? new(DeliveryChainAction.Leave, DeliveryChainReasons.CompletedReopenLimit,
                "Too many Delivered cards would reopen in one pass; the whole batch stops.")
            : new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.AcceptedDeliveryStale,
                "Integrate the current delivery and repeat review.");
    }

    private static DeliveryChainVerdict Escalate()
        => new(DeliveryChainAction.Escalate, DeliveryChainReasons.IntegrationFailed,
            "Recover the delivery, resolve the integration failure and rerun the gate.");
}
