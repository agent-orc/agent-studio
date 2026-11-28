namespace AgentStudio.TaskServer.Contracts;

/// <summary>Studio's Codex route and the GPT-6.2 provider ladder.</summary>
public static class CodexRoutingPolicy
{
    public const string StandardModel = CodexCliRelease.Gpt62Sol;
    public const string StrongModel = CodexCliRelease.Gpt62Astra;
    public const string StandardThinkingLevel = "medium";
    public const string HighThinkingLevel = "high";
    public const string ProviderDefaultThinkingLevel = "low";

    public static IReadOnlyList<string> ThinkingLevels { get; } =
        Array.AsReadOnly<string>(["low", "medium", "high", "xhigh", "max", "ultra"]);

    public static bool IsGpt62(string? model)
        => string.Equals(model, StandardModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, StrongModel, StringComparison.OrdinalIgnoreCase);

    public static bool IsSupersededSolPreference(string? model)
        => string.Equals(model, "gpt-6-sol", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, CodexCliRelease.Gpt61Sol, StringComparison.OrdinalIgnoreCase);

    public static string ResolveThinkingLevel(string? level, string defaultLevel = StandardThinkingLevel)
    {
        if (string.IsNullOrWhiteSpace(level)) return defaultLevel;
        var normalized = level.Trim().ToLowerInvariant();
        return ThinkingLevels.Contains(normalized) ? normalized : ProviderDefaultThinkingLevel;
    }

    /// <summary>Apply the owner-approved upgrade on a fresh coding selection.</summary>
    public static SelectionPins UpgradePreference(SelectionPins pins)
        => pins.Mode != SelectionPinMode.Hard
            && string.Equals(pins.CliType, "codex", StringComparison.OrdinalIgnoreCase)
            && IsSupersededSolPreference(pins.Model?.Trim())
            ? pins with { Model = StandardModel, ThinkingLevel = ResolveThinkingLevel(pins.ThinkingLevel) }
            : pins;
}
