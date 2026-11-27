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
    bool CompletedReopenLimitReached,
    bool RecoverableConflict = false,
    bool ReviewVerdictStale = false,
    StaleVerdictRecoveryFacts? Recovery = null);

public enum DeliveryChainAction
{
    Leave,
    RequestFreshReview,
    RouteGateFailure,
    WaitCapacity,

    /// <summary>The integrated delivery is ready for the human verdict.</summary>
    PromoteToHumanReview,

    /// <summary>Integrate the current delivery and repeat review.</summary>
    ReturnToAutoReview,

    /// <summary>Queue a bounded agent round to reconcile the delivery with develop.</summary>
    QueueMergeRound,

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
    public const string MergeRoundQueued = "delivery-merge-round";
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
        if (facts.Recovery is { } recovery)
        {
            if (recovery.Recalling)
                return facts.State == TaskStates.Escalated
                    && StaleVerdictRecoveryPolicy.Recall(recovery.SystemOwned, recovery.StalePark,
                    recovery.IntegratedVerified, recovery.RetryOwnerAvailable)
                    ? new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.VerdictStale,
                        "The obsolete machine stale-verdict park returns to automatic recovery.")
                    : new(DeliveryChainAction.Leave, DeliveryChainReasons.IntegrationPending,
                        "The current park remains in force.");
            if (facts.State != TaskStates.AutoReview)
                return new(DeliveryChainAction.Leave, DeliveryChainReasons.OutsideChain,
                    "The card left stale-verdict recovery.");
            return StaleVerdictRecoveryPolicy.AfterIntegration(recovery.IntegratedVerified,
                recovery.Successful, recovery.Conflict, recovery.GateOwned, recovery.Capacity) switch
            {
                StaleVerdictRecoveryAction.Review => new(DeliveryChainAction.RequestFreshReview,
                    DeliveryChainReasons.VerdictStale, "Request fresh review of the integrated delivery."),
                StaleVerdictRecoveryAction.RouteGateFailure => new(DeliveryChainAction.RouteGateFailure,
                    DeliveryChainReasons.IntegrationPending, "The gate failure router owns recovery."),
                StaleVerdictRecoveryAction.WaitCapacity => new(DeliveryChainAction.WaitCapacity,
                    DeliveryChainReasons.IntegrationPending, "Wait for integration capacity."),
                StaleVerdictRecoveryAction.MergeRound => MergeRound(),
                _ => Escalate(),
            };
        }
        if (facts.State == TaskStates.AutoReview && facts.Integrating)
        {
            if (facts.Integrated)
                return new(DeliveryChainAction.PromoteToHumanReview, DeliveryChainReasons.Integrated,
                    "The integrated delivery waits for the human verdict.");
            if (facts.RecoverableConflict) return MergeRound();
            return facts.RequiresEscalation
                ? Escalate()
                : new(DeliveryChainAction.Leave, DeliveryChainReasons.IntegrationPending,
                    "The integration is still running.");
        }
        if (facts.State == TaskStates.HumanReview && facts.Integrated && facts.ReviewVerdictStale)
            return new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.VerdictStale,
                "Request fresh review of the integrated current delivery.");
        if (facts.State == TaskStates.HumanReview && !facts.Integrated)
            return facts.RecoverableConflict
                ? MergeRound()
                : facts.RequiresEscalation
                ? Escalate()
                : new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.VerdictStale,
                    "Integrate the current delivery and repeat review.");
        if (facts.State != TaskStates.Completed)
            return new(DeliveryChainAction.Leave, DeliveryChainReasons.OutsideChain,
                "The card is outside the reconciled delivery lanes.");
        if (!facts.AcceptedDeliveryStale)
            return new(DeliveryChainAction.Leave, DeliveryChainReasons.AcceptedDeliveryCurrent,
                "The acceptance matches the current delivery.");
        if (facts.CompletedReopenLimitReached)
            return new(DeliveryChainAction.Leave, DeliveryChainReasons.CompletedReopenLimit,
                "Too many Delivered cards would reopen in one pass; the whole batch stops.");
        return facts.RecoverableConflict
            ? MergeRound()
            : new(DeliveryChainAction.ReturnToAutoReview, DeliveryChainReasons.AcceptedDeliveryStale,
                "Integrate the current delivery and repeat review.");
    }

    private static DeliveryChainVerdict Escalate()
        => new(DeliveryChainAction.Escalate, DeliveryChainReasons.IntegrationFailed,
            "Recover the delivery, resolve the integration failure and rerun the gate.");

    private static DeliveryChainVerdict MergeRound()
        => new(DeliveryChainAction.QueueMergeRound, DeliveryChainReasons.MergeRoundQueued,
            "Queue a merge round against develop, then review the new delivery.");
}

/// <summary>Recovery ownership after one stale delivery integration attempt.</summary>
public enum StaleVerdictRecoveryAction { Review, RouteGateFailure, WaitCapacity, MergeRound, Escalate }

public static class StaleVerdictRecoveryPolicy
{
    public static StaleVerdictRecoveryAction AfterIntegration(bool integratedVerified,
        bool successful, bool conflict, bool gateOwned, bool capacity)
        => integratedVerified ? StaleVerdictRecoveryAction.Review
            : conflict ? StaleVerdictRecoveryAction.MergeRound
            : capacity ? StaleVerdictRecoveryAction.WaitCapacity
            : gateOwned ? StaleVerdictRecoveryAction.RouteGateFailure
            : successful ? StaleVerdictRecoveryAction.Review
            : StaleVerdictRecoveryAction.Escalate;

    public static bool Recall(bool systemOwned, bool staleRecoveryPark, bool integratedVerified,
        bool retryOwnerAvailable = false)
        => systemOwned && staleRecoveryPark && (integratedVerified || retryOwnerAvailable);
}

/// <summary>Frozen recovery result, or the ownership proof for recalling a machine park.</summary>
public sealed record StaleVerdictRecoveryFacts(bool IntegratedVerified, bool Successful = false,
    bool Conflict = false, bool GateOwned = false, bool Capacity = false,
    bool Recalling = false, bool SystemOwned = false, bool StalePark = false,
    bool RetryOwnerAvailable = false, string? DeliveryIdentity = null,
    string? VerdictIdentity = null, long LaneEnteredTicks = 0);
