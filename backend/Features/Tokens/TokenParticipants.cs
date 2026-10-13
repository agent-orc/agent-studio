namespace AgentStudio.Tokens;

/// <summary>
/// Participant kind of a usage ledger row, read from the participant id
/// prefix: <c>agent:*</c> coding runs, <c>support:*</c> supporting loops,
/// and <c>orchestrator:*</c> project meta-turns.
/// </summary>
public static class TokenParticipants
{
    public static bool IsAgent(string? participantId)
        => HasPrefix(participantId, "agent:");

    public static bool IsSupporting(string? participantId)
        => HasPrefix(participantId, "support:");

    public static bool IsOrchestrator(string? participantId)
        => HasPrefix(participantId, "orchestrator:");

    private static bool HasPrefix(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
