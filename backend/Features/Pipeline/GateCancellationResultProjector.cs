namespace AgentStudio.Pipeline;

/// <summary>Applies a decided cancellation reason to the host gate result.</summary>
public static class GateCancellationResultProjector
{
    public static BuildTestGateResult Apply(BuildTestGateResult result, string reason)
        => result with { CancellationReason = reason, Reason = $"Gate cancelled: {reason}." };
}
