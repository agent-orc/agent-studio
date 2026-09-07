namespace AgentStudio.Pipeline;

/// <summary>
/// Canonical xUnit category exclusion for generated gate test commands.
/// <para>
/// <c>MachineBound</c> covers tests that depend on host timing, real processes,
/// real ports, or performance thresholds. <c>LiveCli</c> covers tests that call
/// a real provider CLI: they consume account quota and fail for reasons that
/// have nothing to do with the reviewed change. On 2026-09-06 a live Codex
/// review test failed because the weekly quota was exhausted and the card was
/// parked as a product regression (AGT-2749, QS-89).
/// </para>
/// <para>
/// The expression is single-quoted because <c>&amp;</c> is a shell control
/// operator: gate commands run through <c>sh -lc</c>, and an unquoted
/// <c>Category!=MachineBound&amp;Category!=LiveCli</c> would background the test
/// run instead of filtering it.
/// </para>
/// </summary>
public static class GateTestCategoryFilter
{
    public const string MachineBoundCategory = "MachineBound";
    public const string LiveCliCategory = "LiveCli";

    /// <summary>The xUnit filter expression, without shell quoting.</summary>
    public const string Expression =
        $"Category!={MachineBoundCategory}&Category!={LiveCliCategory}";

    /// <summary>Shell-safe <c>dotnet test</c> suffix, including the leading space.</summary>
    public const string DotNetSuffix = $" --filter '{Expression}'";

    /// <summary>Operator-facing reason recorded against the excluded families.</summary>
    public const string SkipReason =
        "Excluded from gate runs by default: " +
        $"{MachineBoundCategory} depends on host timing and real processes, " +
        $"{LiveCliCategory} calls a real provider CLI and consumes account quota. " +
        "Run them separately in an appropriate environment.";
}
