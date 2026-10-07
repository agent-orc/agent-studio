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
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var trimmed = model.Trim();
        return SameModelEntry(trimmed)?.Id ?? trimmed;
    }

    /// <summary>
    /// Render-time label for a stored model id. Resolves through the registry
    /// only when the id names the same model as the registry entry; an
    /// unknown id renders as itself, never as another model's label.
    /// </summary>
    public static string? Label(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var trimmed = modelId.Trim();
        if (IsPlaceholder(trimmed)) return null;
        return SameModelEntry(trimmed)?.Label ?? trimmed;
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

    // Registry aliases serve two purposes: spelling variants of one model and
    // routing equivalences between generations (claude-opus-5-5 is accepted
    // for a claude-opus-5 pin, AGT-2893). Only the first is an identity, so
    // only the first may borrow the entry's id or label.
    private static ModelMetadata? SameModelEntry(string value)
    {
        var byLabel = ModelMetadataRegistry.FindByLabel(value);
        if (byLabel is not null) return byLabel;
        var entry = ModelMetadataRegistry.Find(value);
        if (entry is null) return null;
        return string.Equals(SpellingKey(value), SpellingKey(entry.Id), StringComparison.Ordinal)
            ? entry
            : null;
    }

    private static string SpellingKey(string id)
    {
        var key = id.Trim().ToLowerInvariant().Replace('.', '-');
        var separator = key.LastIndexOf('-');
        if (separator > 0)
        {
            var suffix = key[(separator + 1)..];
            if (suffix.Length == 8 && suffix.All(char.IsDigit))
                key = key[..separator];
        }
        return key;
    }
}
