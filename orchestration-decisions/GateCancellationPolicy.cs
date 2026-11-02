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
    public static int ChargedStartsAfterCancellation(int startsBeforeThisRun, string? reason,
        bool serviceStopping)
        => reason == GateCancellationReasons.Shutdown && serviceStopping
            ? Math.Max(0, startsBeforeThisRun)
            : Math.Max(0, startsBeforeThisRun) + 1;
}
