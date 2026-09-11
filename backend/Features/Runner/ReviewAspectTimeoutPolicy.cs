namespace AgentStudio.Runner;

/// <summary>
/// Per-toolchain defaults for one review aspect call (AGT-2749 addendum,
/// 2026-09-07 19:20). Before this, every aspect call shared one flat
/// <c>ReviewDecisionOrchestrator:AspectTimeoutSeconds</c> budget of 60 seconds.
/// Claude aspect calls regularly need longer, so every card whose cliType
/// fell back to claude (a quota fallback, see AGT-2751) was killed on its
/// budget and classified <c>ToolUnavailable</c> - 18 cards on 2026-09-07.
/// </summary>
public static class ReviewAspectTimeoutDefaults
{
    public const int CodexSeconds = 120;
    public const int ClaudeSeconds = 240;
    public const int GeminiSeconds = 180;

    /// <summary>Used for a cliType this table does not name.</summary>
    public const int FallbackSeconds = 120;
}

/// <summary>
/// Resolves the aspect-call timeout for one cliType. Each toolchain has its
/// own configuration key so an operator can raise one CLI's budget without
/// silently narrowing every other CLI's budget back to a shared minimum.
/// </summary>
public static class ReviewAspectTimeoutPolicy
{
    public static TimeSpan For(string? cliType, IConfiguration configuration)
        => TimeSpan.FromSeconds(SecondsFor(cliType, configuration));

    public static int SecondsFor(string? cliType, IConfiguration configuration)
    {
        var normalized = ReviewDecisionOrchestrator.NormalizeReviewCliType(cliType);
        var configured = configuration.GetValue<int?>(
            $"ReviewDecisionOrchestrator:AspectTimeoutSeconds:{normalized}");
        var value = configured ?? DefaultSecondsFor(normalized);
        return Math.Clamp(value, 1, 7200);
    }

    private static int DefaultSecondsFor(string normalizedCliType)
    {
        if (normalizedCliType == CliTypes.Codex) return ReviewAspectTimeoutDefaults.CodexSeconds;
        if (normalizedCliType == CliTypes.Claude) return ReviewAspectTimeoutDefaults.ClaudeSeconds;
        if (normalizedCliType == CliTypes.Gemini) return ReviewAspectTimeoutDefaults.GeminiSeconds;
        return ReviewAspectTimeoutDefaults.FallbackSeconds;
    }
}
