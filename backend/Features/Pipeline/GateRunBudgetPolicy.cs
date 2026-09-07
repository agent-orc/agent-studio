namespace AgentStudio.Pipeline;

/// <summary>
/// Pure sizing policy for the pre-develop gate-run budget.
/// <para>
/// The budget used to be a fixed 30 minutes. On 2026-09-06 two cards
/// (AGT-2707, AGT-2710) were parked in Human Review because the suite consumed
/// 1800488 ms against a 1800000 ms limit: 488 milliseconds over, on an
/// overloaded host. A budget is a guard against a hung run, not a verdict about
/// the change, so it is sized from what the project's runs actually cost.
/// </para>
/// </summary>
public static class GateRunBudgetPolicy
{
    /// <summary>Default when a project declares nothing and has no history.</summary>
    public const int DefaultMinutes = 60;

    public const int MinimumMinutes = 5;
    public const int MaximumMinutes = 240;

    /// <summary>Number of recent gate runs the p95 is measured over.</summary>
    public const int HistoryWindow = 20;

    /// <summary>Headroom added on top of the measured p95.</summary>
    public const double HeadroomFactor = 1.5;

    public static TimeSpan Default => TimeSpan.FromMinutes(DefaultMinutes);

    /// <summary>
    /// Resolves the budget for one project. An explicit project setting wins.
    /// Otherwise the p95 of the last <see cref="HistoryWindow"/> completed runs
    /// plus 50 percent is used when that history exists, and
    /// <see cref="DefaultMinutes"/> when it does not. The result is always
    /// clamped and never below the default when history is absent.
    /// </summary>
    /// <param name="configuredMinutes">Per-project override, or null.</param>
    /// <param name="recentRunDurations">
    /// Completed gate-run durations, newest first or oldest first; order does
    /// not matter. Non-positive entries are ignored.
    /// </param>
    public static TimeSpan Resolve(
        int? configuredMinutes,
        IEnumerable<TimeSpan>? recentRunDurations = null)
    {
        if (configuredMinutes is { } configured)
            return TimeSpan.FromMinutes(Math.Clamp(configured, MinimumMinutes, MaximumMinutes));

        var history = (recentRunDurations ?? [])
            .Where(duration => duration > TimeSpan.Zero)
            .OrderByDescending(duration => duration)
            .Take(HistoryWindow)
            .OrderBy(duration => duration)
            .ToArray();
        if (history.Length == 0) return Default;

        var derived = Percentile95(history) * HeadroomFactor;
        // History only ever raises the budget. A project that happens to have had
        // a fast week must not get a budget so tight that its next slow run is
        // reported as a failure.
        return derived <= Default
            ? Default
            : TimeSpan.FromMinutes(Math.Clamp(derived.TotalMinutes, MinimumMinutes, MaximumMinutes));
    }

    /// <summary>
    /// Nearest-rank p95 over an ascending sample. With fewer than 20 samples
    /// this is the slowest observed run, which is the honest answer: nothing in
    /// the sample says a slower run is impossible.
    /// </summary>
    private static TimeSpan Percentile95(IReadOnlyList<TimeSpan> ascending)
    {
        var rank = (int)Math.Ceiling(0.95 * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }
}
