using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Per-toolchain base budget for one review aspect call (AGT-2749 addendum,
/// 2026-09-07 19:20). Before this, every aspect call shared one flat
/// <c>ReviewDecisionOrchestrator:AspectTimeoutSeconds</c> budget of 60 seconds.
/// Claude aspect calls regularly need longer, so every card whose cliType
/// fell back to claude (a quota fallback, see AGT-2751) was killed on its
/// budget and classified <c>ToolUnavailable</c> - 18 cards on 2026-09-07.
/// <para>
/// AGT-2820 (2026-09-15): these are now the <em>base</em> of a derivation, not
/// the budget itself. The model and the size of the material scale them - see
/// <see cref="Contract.ReviewAspectBudgetPolicy"/>.
/// </para>
/// </summary>
public static class ReviewAspectTimeoutDefaults
{
    public const int CodexSeconds = Contract.ReviewAspectBudgetDefaults.CodexBaseSeconds;
    public const int ClaudeSeconds = Contract.ReviewAspectBudgetDefaults.ClaudeBaseSeconds;
    public const int GeminiSeconds = Contract.ReviewAspectBudgetDefaults.GeminiBaseSeconds;

    /// <summary>Used for a cliType this table does not name.</summary>
    public const int FallbackSeconds = Contract.ReviewAspectBudgetDefaults.FallbackBaseSeconds;
}

/// <summary>
/// Resolves the aspect-call budget for one cliType, model, thinking level, and
/// prompt size. Each toolchain keeps its own configuration key so an operator
/// can raise one CLI's base without silently narrowing every other CLI's base
/// back to a shared minimum; the model and material terms are derived on top of
/// whichever base applies.
/// </summary>
public static class ReviewAspectTimeoutPolicy
{
    /// <summary>
    /// The full derivation. Prefer this over <see cref="SecondsFor(string?, IConfiguration)"/>
    /// wherever the model and the prompt are known: a budget that ignores them
    /// is the AGT-2820 defect.
    /// </summary>
    public static Contract.ReviewAspectBudget Derive(
        string? cliType,
        string? model,
        string? thinkingLevel,
        int materialCharacters,
        IConfiguration configuration)
    {
        var normalized = ReviewDecisionOrchestrator.NormalizeReviewCliType(cliType);
        return Contract.ReviewAspectBudgetPolicy.Derive(
            normalized,
            model,
            thinkingLevel,
            materialCharacters,
            ConfiguredBaseSeconds(normalized, configuration));
    }

    /// <summary>Base budget only - no model and no material. Used where neither is known yet.</summary>
    public static TimeSpan For(string? cliType, IConfiguration configuration)
        => TimeSpan.FromSeconds(SecondsFor(cliType, configuration));

    public static int SecondsFor(string? cliType, IConfiguration configuration)
    {
        var normalized = ReviewDecisionOrchestrator.NormalizeReviewCliType(cliType);
        var value = ConfiguredBaseSeconds(normalized, configuration) ?? DefaultSecondsFor(normalized);
        return Math.Clamp(
            value,
            1,
            Contract.ReviewAspectBudgetDefaults.CeilingSeconds);
    }

    private static int? ConfiguredBaseSeconds(string normalizedCliType, IConfiguration configuration)
        => configuration.GetValue<int?>(
            $"ReviewDecisionOrchestrator:AspectTimeoutSeconds:{normalizedCliType}");

    private static int DefaultSecondsFor(string normalizedCliType)
    {
        if (normalizedCliType == CliTypes.Codex) return ReviewAspectTimeoutDefaults.CodexSeconds;
        if (normalizedCliType == CliTypes.Claude) return ReviewAspectTimeoutDefaults.ClaudeSeconds;
        if (normalizedCliType == CliTypes.Gemini) return ReviewAspectTimeoutDefaults.GeminiSeconds;
        return ReviewAspectTimeoutDefaults.FallbackSeconds;
    }
}
