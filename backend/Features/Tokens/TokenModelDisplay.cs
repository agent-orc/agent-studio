
namespace AgentStudio.Tokens;

internal static class TokenModelDisplay
{
    public static string? Label(string? modelId)
    {
        var id = TokenPricing.CanonicalModelId(modelId);
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (IsPlaceholder(id)) return null;
        return TokenPricing.ModelDisplayName(id);
    }

    public static bool IsAgentParticipant(string? participantId)
        => HasPrefix(participantId, "agent:");

    public static bool IsSupportingParticipant(string? participantId)
        => HasPrefix(participantId, "support:");

    public static bool IsOrchestratorParticipant(string? participantId)
        => HasPrefix(participantId, "orchestrator:");

    private static bool HasPrefix(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaceholder(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "(unknown)", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "?", StringComparison.Ordinal);
    }
}
