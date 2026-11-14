namespace AgentStudio.OrchestrationDecisions;

public static class GateCancellationReasons
{
    public const string Superseded = "superseded";
    public const string Shutdown = "shutdown";
    public const string Operator = "operator";
}

/// <summary>Cancellation is a lifecycle event; subject ownership wins over its source.</summary>
public static class GateCancellationPolicy
{
    public static bool OwnsResult(string? expectedSha, string? currentSha,
        string? expectedAttempt, string? currentAttempt,
        string? expectedLane, string? currentLane, bool continuePending)
        => string.Equals(expectedSha, currentSha, StringComparison.OrdinalIgnoreCase)
           && string.Equals(expectedAttempt, currentAttempt, StringComparison.Ordinal)
           && string.Equals(expectedLane, currentLane, StringComparison.OrdinalIgnoreCase)
           && !continuePending;

    public static string Decide(string? expectedSha, string? currentSha,
        string? expectedLane, string? currentLane, bool continuePending,
        bool shuttingDown, string? reportedReason = null)
    {
        if (!string.Equals(expectedSha, currentSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedLane, currentLane, StringComparison.OrdinalIgnoreCase)
            || continuePending)
            return GateCancellationReasons.Superseded;
        // Completion or integration cancelled the gate through its registry.
        if (reportedReason == GateCancellationReasons.Superseded)
            return GateCancellationReasons.Superseded;
        if (reportedReason == GateCancellationReasons.Operator)
            return GateCancellationReasons.Operator;
        return shuttingDown || reportedReason == GateCancellationReasons.Shutdown
            ? GateCancellationReasons.Shutdown : GateCancellationReasons.Operator;
    }

    /// <summary>
    /// Interrupted starts a per-task gate keeps after a cancellation. Every start
    /// is charged before the gate runs, so a crash that never comes back to this
    /// rule keeps its charge and the budget still ends a gate that never returns.
    /// The service's own stop says nothing about the gate: it refunds the start,
    /// so planned restarts never exhaust the budget (2 November 2026). A
    /// shutdown reported by a remote gate host while this service keeps running
    /// stays charged.
    /// </summary>
    /// <summary>
    /// Charged starts once a later service instance finds the gate it lost
    /// (AGT-3647). The start in flight was charged by <paramref name="runByInstance"/>;
    /// when that is another instance, the service was replaced under the gate,
    /// by a deploy, a restart or a kill past its stop timeout, and the gate
    /// never had a verdict. That start is refunded, so an API restart never
    /// escalates the card. A gate that throws keeps its charge under the
    /// instance that ran it, so the budget still ends a gate that keeps failing.
    /// </summary>
    public static int ChargedStartsAfterServiceRestart(int chargedStarts, string? runByInstance,
        string currentInstance)
        => runByInstance is not null && !string.Equals(runByInstance, currentInstance, StringComparison.Ordinal)
            ? Math.Max(0, chargedStarts - 1)
            : Math.Max(0, chargedStarts);

    public static int ChargedStartsAfterCancellation(int startsBeforeThisRun, string? reason,
        bool serviceStopping)
        => reason == GateCancellationReasons.Shutdown && serviceStopping
            ? Math.Max(0, startsBeforeThisRun)
            : Math.Max(0, startsBeforeThisRun) + 1;
}
