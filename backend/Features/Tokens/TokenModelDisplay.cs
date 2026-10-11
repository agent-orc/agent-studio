namespace AgentStudio.Tokens;

internal static class TokenModelDisplay
{
    /// <summary>
    /// Storage form of a usage model value. The ledger stores the observed
    /// model id. Only a historical display label (AGT-2740) or a pure
    /// spelling variant of a registry id (dot versus dash, dated snapshot
    /// suffix) folds to the registry id. Any other value, including an
    /// equivalence alias that names another model generation, is kept as
    /// recorded so a new model id is never rewritten into an older one.
    /// </summary>
    public static string? StoredId(string? model)
        => ModelIdentity.StoredId(model);

    public static string? Label(string? modelId)
        => ModelIdentity.Label(modelId);

    public static bool IsAgentParticipant(string? participantId)
        => HasPrefix(participantId, "agent:");

    public static bool IsSupportingParticipant(string? participantId)
        => HasPrefix(participantId, "support:");

    public static bool IsOrchestratorParticipant(string? participantId)
        => HasPrefix(participantId, "orchestrator:");

    private static bool HasPrefix(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
