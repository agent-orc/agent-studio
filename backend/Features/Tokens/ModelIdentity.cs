namespace AgentStudio.Tokens;

/// <summary>One identity for pricing, persisted usage, and display projections.</summary>
public sealed record ResolvedModelIdentity(string ModelId, string? Cli, string DisplayLabel);

/// <summary>
/// The only owner of model identity in the token usage and pricing area.
/// Every recorded model value (catalog id, dated snapshot, display label,
/// bare <c>sonnet</c> / <c>opus</c> alias, or an unknown id) is resolved
/// here; readers, pricing, folds, and projections never normalize model
/// strings themselves.
/// </summary>
public static class ModelIdentity
{
    /// <summary>
    /// Storage form of a recorded model value. The ledger stores the observed
    /// id; only a historical display label (AGT-2740) or a pure spelling
    /// variant of a registry id (dot versus dash, dated snapshot suffix)
    /// folds to the registry id, so a new model generation is never
    /// rewritten into an older one.
    /// </summary>
    public static string? StoredId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return SameModelEntry(value)?.Id ?? value;
    }

    /// <summary>Display label for a recorded model value; null for blank or unknown markers.</summary>
    public static string? Label(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value is "?" || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || value.Equals("(unknown)", StringComparison.OrdinalIgnoreCase)) return null;
        return SameModelEntry(value)?.Label ?? value;
    }

    private static ModelMetadata? SameModelEntry(string value)
    {
        var byLabel = ModelMetadataRegistry.FindByLabel(value);
        if (byLabel is not null) return byLabel;
        var entry = ModelMetadataRegistry.Find(value);
        return entry is not null && string.Equals(SpellingKey(value), SpellingKey(entry.Id), StringComparison.Ordinal)
            ? entry : null;
    }

    private static string SpellingKey(string id)
    {
        var key = id.Trim().ToLowerInvariant().Replace('.', '-');
        var separator = key.LastIndexOf('-');
        if (separator > 0)
        {
            var suffix = key[(separator + 1)..];
            if (suffix.Length == 8 && suffix.All(char.IsDigit)) key = key[..separator];
        }
        return key;
    }

    /// <summary>
    /// Registry-normalized id used as a comparison key (duplicate
    /// fingerprints, expected-versus-actual model checks, cost rows).
    /// </summary>
    public static string RegistryId(string? raw)
        => ModelMetadataRegistry.NormalizeId(raw);

    /// <summary>
    /// Pricing identity: the catalog id (aliases, display names, and bare
    /// aliases resolve to it), the CLI that runs the model, and its display
    /// label. Unknown values keep their trimmed spelling as the id.
    /// </summary>
    public static ResolvedModelIdentity? Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        var bareAlias = value.ToLowerInvariant() switch
        {
            "sonnet" => ModelIds.ClaudeSonnet5,
            "opus" => ModelIds.ClaudeOpus5,
            _ => value,
        };
        var listing = TokenEconomy.ModelPriceCatalog.Default.Find(bareAlias)
            ?? TokenEconomy.ModelPriceCatalog.Default.Listings.FirstOrDefault(entry =>
                string.Equals(entry.DisplayName, bareAlias, StringComparison.OrdinalIgnoreCase));
        var modelId = listing?.ModelId ?? ModelMetadataRegistry.FindByLabelOrAlias(bareAlias)?.Id ?? bareAlias;
        var metadata = ModelMetadataRegistry.Find(modelId);
        var cli = metadata?.Vendor switch
        {
            "anthropic" => CliTypes.Claude,
            "openai" => CliTypes.Codex,
            "google" => "gemini",
            _ when modelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) => CliTypes.Claude,
            _ when modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) => CliTypes.Codex,
            _ when modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) => "gemini",
            _ => null,
        };
        return new ResolvedModelIdentity(modelId, cli, metadata?.Label ?? listing?.DisplayName ?? value);
    }
}
