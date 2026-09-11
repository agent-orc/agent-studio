namespace AgentStudio.Pipeline;

/// <summary>
/// Sizes the build/test gate-run budget (AGT-2749). Before this, the platform
/// default was 300 seconds; the operator host ran with a 1800-second override
/// that was still too short during the 2026-09-06 overload night ("dotnet
/// test ... violated gate-run budget (limit=1800000ms, consumed=1800488ms)").
/// </summary>
public static class GateRunBudgetDefaults
{
    /// <summary>Platform default absent a project override or run history: 60 minutes.</summary>
    public const int DefaultSeconds = 3600;

    /// <summary>Minimum recent-run sample count before history overrides the default.</summary>
    public const int MinimumHistorySamples = 20;

    /// <summary>Headroom applied to the measured p95 so a normal run never brushes the ceiling.</summary>
    public const double HistoryMargin = 1.5;
}

/// <summary>
/// Pure sizing policy: a per-project override wins outright; absent that, a
/// long-enough run history sizes the budget from its own p95; absent both,
/// the platform default applies.
/// </summary>
public static class GateRunBudgetPolicy
{
    /// <param name="projectOverrideSeconds">
    /// Explicit per-project budget (<c>ProjectSettings.BuildTestGateTimeoutSeconds</c>). Wins outright.
    /// </param>
    /// <param name="recentRunSeconds">
    /// Duration of the most recent gate runs for this project, newest-first or
    /// unordered - only the distribution matters. Needs at least
    /// <see cref="GateRunBudgetDefaults.MinimumHistorySamples"/> entries to be used.
    /// </param>
    public static int ResolveSeconds(
        int? projectOverrideSeconds,
        IReadOnlyList<double>? recentRunSeconds = null)
    {
        if (projectOverrideSeconds is { } configured) return Math.Max(1, configured);

        if (recentRunSeconds is { Count: >= GateRunBudgetDefaults.MinimumHistorySamples })
        {
            var p95 = Percentile95(recentRunSeconds);
            return Math.Max(1, (int)Math.Ceiling(p95 * GateRunBudgetDefaults.HistoryMargin));
        }

        return GateRunBudgetDefaults.DefaultSeconds;
    }

    /// <summary>
    /// Nearest-rank p95: sorts the sample and takes the value at the ceiling of
    /// <c>0.95 * n</c> (1-indexed), clamped to the last element.
    /// </summary>
    private static double Percentile95(IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
