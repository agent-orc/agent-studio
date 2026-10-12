namespace AgentStudio.Tokens;

/// <summary>One identity for pricing, persisted usage, and display projections.</summary>
public sealed record ResolvedModelIdentity(string ModelId, string? Cli, string DisplayLabel);

public static class ModelIdentity
{
    // Receipt storage preserves observed generations. Only spelling variants and
    // historical labels of the same registry entry become its id.
    public static string? StoredId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return SameModelEntry(value)?.Id ?? value;
    }

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
