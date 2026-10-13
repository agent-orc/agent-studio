namespace AgentStudio.Pipeline;

public static class GateCancellationReasons
{
    public const string Superseded = "superseded";
    public const string Shutdown = "shutdown";
    public const string Operator = "operator";
}

/// <summary>Cancellation is a lifecycle event; subject ownership wins over its source.</summary>
public static class GateCancellationPolicy
{
    public static string Decide(string? expectedSha, string? currentSha,
        string? expectedLane, string? currentLane, bool continuePending,
        bool shuttingDown, string? reportedReason = null)
    {
        if (!string.Equals(expectedSha, currentSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedLane, currentLane, StringComparison.OrdinalIgnoreCase)
            || continuePending)
            return GateCancellationReasons.Superseded;
        if (reportedReason == GateCancellationReasons.Operator)
            return GateCancellationReasons.Operator;
        return shuttingDown || reportedReason == GateCancellationReasons.Shutdown
            ? GateCancellationReasons.Shutdown : GateCancellationReasons.Operator;
    }

    public static BuildTestGateResult Apply(BuildTestGateResult result, string reason)
        => result with { CancellationReason = reason, Reason = $"Gate cancelled: {reason}." };
}
