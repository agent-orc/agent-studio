namespace AgentStudio.Security;

/// <summary>
/// The pure decisions of the operator kill switch (security model SM-2): when
/// a drill passes, when the next one is due, and when a failed Operations
/// Server revocation is tried again.
/// </summary>
public static class OperatorKillSwitchPolicy
{
    /// <summary>The acceptance target: every operator call refused within this time after the switch.</summary>
    public static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>null</c> when a drill passed, else the first reason it failed. A drill
    /// passes when Studio refused the test session within the budget, the
    /// Operations Server revocation (when configured) completed within it, and
    /// no other operator session was touched.
    /// </summary>
    public static string? DrillFailure(TimeSpan? studioRefusedAfter, string operationsServerState, TimeSpan? operationsServerAfter,
        bool otherSessionsKept)
    {
        if (studioRefusedAfter is null) return "studio-not-refused";
        if (studioRefusedAfter > RefusalBudget) return "studio-too-slow";
        if (operationsServerState is not (OperatorKillPropagationStates.Revoked or OperatorKillPropagationStates.NotConfigured))
            return "operations-server-unreachable";
        if (operationsServerState == OperatorKillPropagationStates.Revoked && operationsServerAfter > RefusalBudget)
            return "operations-server-too-slow";
        if (!otherSessionsKept) return "other-sessions-affected";
        return null;
    }

    /// <summary>A drill is due once its scheduled time passed; an interval of zero disables the schedule.</summary>
    public static bool DrillDue(DateTime? nextDrillAt, DateTime now, TimeSpan interval)
        => interval > TimeSpan.Zero && nextDrillAt is { } next && next <= now;

    /// <summary>The first drill of an installation runs one interval after the schedule starts, never at startup.</summary>
    public static DateTime? NextDrillAt(DateTime? scheduled, DateTime now, TimeSpan interval)
        => interval <= TimeSpan.Zero ? null : scheduled ?? now + interval;

    /// <summary>Exponential backoff from two seconds, capped at thirty, so a pending revocation is retried well inside the budget.</summary>
    public static TimeSpan RetryDelay(int attempts)
        => TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, Math.Pow(2, Math.Clamp(attempts, 1, 5))));

    public static bool RetryDue(OperatorKillPropagation propagation, DateTime now)
        => propagation.State == OperatorKillPropagationStates.Pending
           && (propagation.LastAttemptAt is not { } last || now - last >= RetryDelay(propagation.Attempts));
}
