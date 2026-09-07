namespace AgentStudio.Shared;

/// <summary>
/// Stable model-family references used by runtime defaults. A family is not a
/// model id and must be resolved against the current CLI catalog immediately
/// before it is used.
/// </summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string GptMini = "gpt-mini";
    public const string GptFlagship = "gpt-flagship";

    public static readonly IReadOnlyList<string> All =
    [
        ClaudeHaiku,
        ClaudeSonnet,
        ClaudeOpus,
        GptMini,
        GptFlagship,
    ];

    public static bool IsKnown(string? family)
        => All.Contains(family?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string? CliTypeFor(string? family)
        => family?.Trim().ToLowerInvariant() switch
        {
            ClaudeHaiku or ClaudeSonnet or ClaudeOpus => CliTypes.Claude,
            GptMini or GptFlagship => CliTypes.Codex,
            _ => null,
        };

    /// <summary>Infer the stable family for a canonical or newly discovered model id.</summary>
    public static string? InferFromModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var id = modelId.Trim().ToLowerInvariant();
        if (id.StartsWith("claude-haiku-", StringComparison.Ordinal)) return ClaudeHaiku;
        if (id.StartsWith("claude-sonnet-", StringComparison.Ordinal)) return ClaudeSonnet;
        if (id.StartsWith("claude-opus-", StringComparison.Ordinal)) return ClaudeOpus;
        if (id.StartsWith("gpt-", StringComparison.Ordinal))
            return id.Contains("-mini", StringComparison.Ordinal) ? GptMini : GptFlagship;
        return null;
    }
}
