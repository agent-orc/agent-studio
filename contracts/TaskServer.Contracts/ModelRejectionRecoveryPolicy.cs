namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Decides when a launch-time model rejection on a host ends. Servers never
/// compare the runner clock with their own for this. A runner process numbers
/// its model verdicts, including its own record of the rejection, so a ready
/// verdict proves a newer probe only when its sequence is larger than the
/// sequence of that rejection verdict from the same process.
/// </summary>
public static class ModelRejectionRecoveryPolicy
{
    /// <param name="InstanceId">Runner process the hold is bound to.</param>
    /// <param name="ConfirmedSequence">
    /// Sequence of that process's own unavailable verdict. Until it is known,
    /// no ready verdict can be ordered after the rejection.
    /// </param>
    public sealed record Hold(string? InstanceId, long? ConfirmedSequence);

    /// <summary>Returns the updated hold, or null when the advertisement ends it.</summary>
    public static Hold? Observe(Hold hold, string instanceId, string advertisedStatus, long? sequence)
    {
        // A new process never saw the rejection. The first verdict this server
        // receives from it is the baseline; only a later probe counts.
        if (!string.Equals(hold.InstanceId, instanceId, StringComparison.Ordinal))
            return new Hold(instanceId, sequence ?? 0);
        var ready = string.Equals(advertisedStatus, "ready", StringComparison.OrdinalIgnoreCase);
        if (ready)
            return hold.ConfirmedSequence is { } confirmed && sequence > confirmed ? null : hold;
        if (string.Equals(advertisedStatus, "unavailable", StringComparison.OrdinalIgnoreCase)
            && sequence is { } refused)
            return hold with { ConfirmedSequence = Math.Max(hold.ConfirmedSequence ?? refused, refused) };
        return hold;
    }
}
