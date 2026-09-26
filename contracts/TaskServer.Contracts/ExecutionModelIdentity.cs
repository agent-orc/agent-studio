namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Shared model identity rules for the separately deployed runner and backend.
/// The backend registry uses these aliases for pricing and usage attribution.
/// </summary>
public static class ExecutionModelIdentity
{
    private static readonly IReadOnlyDictionary<string, string[]> AliasesByCanonical =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = ["claude-opus-5-5"],
            ["claude-fable-5-1"] = ["claude-fable-5.1"],
            ["claude-opus-4-8"] = ["claude-opus-4.8"],
            ["claude-opus-4-7"] = ["claude-opus-4.7"],
            ["claude-opus-4-6"] = ["claude-opus-4.6"],
            ["claude-opus-4-5"] = ["claude-opus-4.5"],
            ["claude-sonnet-4-6"] = ["claude-sonnet-4.6"],
            ["claude-sonnet-4-5"] = ["claude-sonnet-4.5"],
            ["claude-haiku-4-5"] = ["claude-haiku-4.5", "claude-haiku-4-5-20251001"],
        };

    public static string[] AliasesFor(string canonical)
        => AliasesByCanonical.TryGetValue(canonical, out var aliases) ? aliases : [];

    public static bool Equivalent(string pinned, string observed)
        => string.Equals(Normalize(pinned), Normalize(observed), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string model)
    {
        var normalized = model.Trim();
        var separator = normalized.LastIndexOf('-');
        if (separator > 0)
        {
            var suffix = normalized[(separator + 1)..];
            if (suffix.Length == 8 && suffix.All(char.IsDigit))
                normalized = normalized[..separator];
        }

        foreach (var (canonical, aliases) in AliasesByCanonical)
        {
            if (string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase)
                || aliases.Any(alias => string.Equals(normalized, alias, StringComparison.OrdinalIgnoreCase)))
                return canonical;
        }

        return normalized;
    }
}
