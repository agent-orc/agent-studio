namespace AgentStudio.Tokens;

/// <summary>
/// Executing-host attribution for token ledger rows (AGT-2986). New rows carry
/// the host explicitly: the remote runner id for remote completions, review
/// attempts, and remote chat turns, or <see cref="Local"/> for the workstation
/// runner. Rows written before the field existed resolve from their
/// participant id so the workspace breakdown never drops them.
/// </summary>
public static class TokenUsageHost
{
    /// <summary>The workstation runner and in-process Studio calls.</summary>
    public const string Local = "local";

    /// <summary>A remote run whose receipt predates host attribution.</summary>
    public const string UnrecordedRemote = "remote-unrecorded";

    /// <summary>Participant prefix of remote coding-run receipts.</summary>
    public const string RemoteRunnerParticipantPrefix = "agent:remote-runner:";

    public static string Resolve(string? host, string? participantId)
    {
        if (!string.IsNullOrWhiteSpace(host)) return host.Trim();
        return !string.IsNullOrWhiteSpace(participantId)
               && participantId.Trim().StartsWith(RemoteRunnerParticipantPrefix, StringComparison.OrdinalIgnoreCase)
            ? UnrecordedRemote
            : Local;
    }
}
