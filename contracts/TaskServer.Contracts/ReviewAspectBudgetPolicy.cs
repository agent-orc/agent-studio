namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One derived wall-clock budget for a single review aspect call, plus the
/// sentence that explains where the number came from. The derivation travels
/// with the budget so a violation can name the model and the limit instead of
/// leaving an operator to guess which constant cut the call short.
/// </summary>
public sealed record ReviewAspectBudget(
    int Seconds,
    string CliType,
    string Model,
    string Derivation)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Seconds);

    public long LimitMilliseconds => Seconds * 1000L;

    /// <summary>Operator sentence for a budget violation: which model, which limit, why that limit.</summary>
    public string Describe()
        => $"model={Model} cli={CliType} limit={LimitMilliseconds}ms derived={Derivation}";
}

/// <summary>
/// Reference points for <see cref="ReviewAspectBudgetPolicy"/>. The per-toolchain
/// base is the budget a small, fast model needs for an aspect prompt with no
/// appended review material; everything above it is derived.
/// </summary>
public static class ReviewAspectBudgetDefaults
{
    public const int FloorSeconds = 60;
    public const int CeilingSeconds = 7200;

    // Per-toolchain base, measured against the cheapest model each CLI runs.
    public const int CodexBaseSeconds = 120;
    public const int ClaudeBaseSeconds = 240;
    public const int GeminiBaseSeconds = 180;
    public const int FallbackBaseSeconds = 120;

    /// <summary>Seconds granted per 1000 characters of prompt and appended review material.</summary>
    public const double SecondsPerThousandCharacters = 0.5;

    /// <summary>Hard cap on the material term so one enormous diff cannot buy an unbounded budget.</summary>
    public const int MaximumMaterialSeconds = 600;

    // Reasoning weight per model class. A flagship reasoning model spends far
    // longer on the same prompt than the support model these budgets were
    // originally sized for; that is the whole point of routing a card to it.
    public const double FlagshipWeight = 2.0;
    public const double MidWeight = 1.25;
    public const double EconomyWeight = 1.0;

    /// <summary>
    /// Weight for a model this table does not recognize. Deliberately above
    /// <see cref="MidWeight"/>: an unknown model is more likely to be a new
    /// flagship than a new economy model, and a budget that is too short
    /// produces a false infrastructure verdict while one that is too long only
    /// costs time the stall watchdog already bounds.
    /// </summary>
    public const double UnknownWeight = 1.5;
}

/// <summary>
/// Pure derivation of one review aspect's wall-clock budget from the toolchain,
/// the model, the thinking level, and the size of the material the call has to
/// read.
/// <para>
/// Written after 2026-09-14/15, when ten cards routed to <c>claude-opus-5</c>
/// had every review fail. The budget was a constant sized for a small model:
/// <c>aspect-code-quality</c> consumed 122202 ms against a 120000 ms limit and
/// was reported as a broken toolchain, while <c>aspect-requirement-fit</c> on
/// the same model and the same attempt finished in 78020 ms and passed. Whether
/// the reviewer could keep up was luck, and an operator routing cards to a
/// strong model had no way to know the reviewer could not.
/// </para>
/// <para>
/// The shape is <c>base(cli) * weight(model) * weight(thinking) + material</c>.
/// Each term answers one question an operator can check: which toolchain, how
/// much thinking the model does, and how much there is to read.
/// </para>
/// </summary>
public static class ReviewAspectBudgetPolicy
{
    public static ReviewAspectBudget Derive(
        string? cliType,
        string? model,
        string? thinkingLevel,
        int materialCharacters,
        int? configuredBaseSeconds = null)
    {
        var cli = Normalize(cliType);
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? "unspecified" : model.Trim();
        var baseSeconds = configuredBaseSeconds is { } configured and > 0
            ? configured
            : BaseSecondsFor(cli);
        var modelWeight = ModelWeight(resolvedModel);
        var thinkingWeight = ThinkingWeight(thinkingLevel);
        var materialSeconds = MaterialSeconds(materialCharacters);
        var derived = (int)Math.Round(baseSeconds * modelWeight * thinkingWeight) + materialSeconds;
        var seconds = Math.Clamp(
            derived,
            ReviewAspectBudgetDefaults.FloorSeconds,
            ReviewAspectBudgetDefaults.CeilingSeconds);
        var derivation =
            $"base={baseSeconds}s x model={modelWeight:0.##} x thinking={thinkingWeight:0.##} " +
            $"+ material={materialSeconds}s ({materialCharacters} chars)";
        return new ReviewAspectBudget(seconds, cli, resolvedModel, derivation);
    }

    /// <summary>
    /// Material characters, counted the same way at plan time and at execution
    /// time so a budget frozen into the plan and a budget recomputed after the
    /// executor appended the diff are comparable numbers.
    /// </summary>
    public static int MaterialCharacters(string? prompt, string? appendedMaterial = null)
        => Math.Max(0, (prompt?.Length ?? 0) + (appendedMaterial?.Length ?? 0));

    private static int MaterialSeconds(int materialCharacters)
    {
        if (materialCharacters <= 0) return 0;
        var seconds = (int)Math.Round(
            materialCharacters / 1000.0 * ReviewAspectBudgetDefaults.SecondsPerThousandCharacters);
        return Math.Clamp(seconds, 0, ReviewAspectBudgetDefaults.MaximumMaterialSeconds);
    }

    private static int BaseSecondsFor(string cli) => cli switch
    {
        "codex" => ReviewAspectBudgetDefaults.CodexBaseSeconds,
        "claude" => ReviewAspectBudgetDefaults.ClaudeBaseSeconds,
        "gemini" => ReviewAspectBudgetDefaults.GeminiBaseSeconds,
        _ => ReviewAspectBudgetDefaults.FallbackBaseSeconds,
    };

    /// <summary>
    /// Model classes, not model generations. A new Opus or a new flagship Codex
    /// route inherits the flagship weight from its family prefix instead of
    /// needing a new table row on every release.
    /// </summary>
    private static double ModelWeight(string model)
    {
        var id = model.ToLowerInvariant();
        if (id.Contains("haiku", StringComparison.Ordinal)
            || id.Contains("mini", StringComparison.Ordinal)
            || id.Contains("luna", StringComparison.Ordinal))
            return ReviewAspectBudgetDefaults.EconomyWeight;
        if (id.Contains("opus", StringComparison.Ordinal)
            || id.Contains("sol", StringComparison.Ordinal)
            || id.Contains("astra", StringComparison.Ordinal)
            || id.Contains("codex", StringComparison.Ordinal)
            || id.StartsWith("gpt-5.5", StringComparison.Ordinal)
            || id.StartsWith("gpt-6", StringComparison.Ordinal))
            return ReviewAspectBudgetDefaults.FlagshipWeight;
        if (id.Contains("sonnet", StringComparison.Ordinal)
            || id.Contains("fable", StringComparison.Ordinal)
            || id.Contains("terra", StringComparison.Ordinal))
            return ReviewAspectBudgetDefaults.MidWeight;
        return ReviewAspectBudgetDefaults.UnknownWeight;
    }

    private static double ThinkingWeight(string? thinkingLevel) =>
        (thinkingLevel ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "max" => 2.0,
            "xhigh" => 1.7,
            "high" => 1.4,
            "medium" => 1.2,
            "low" => 1.0,
            _ => 1.0,
        };

    private static string Normalize(string? cliType)
    {
        var value = (cliType ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 ? "codex" : value;
    }
}
