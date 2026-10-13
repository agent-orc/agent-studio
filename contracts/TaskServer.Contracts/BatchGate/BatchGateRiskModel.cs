namespace AgentStudio.TaskServer.Contracts.BatchGate;

/// <summary>
/// Pure per-card risk for the look-ahead planner. A relative scale tuned to
/// the measured delivery shapes of 2026-10-12, not a calibrated red probability.
/// </summary>
public static class BatchGateRiskModel
{
    /// <summary>
    /// Risk of a card whose own diff cannot be computed. It equals the
    /// balanced high-risk limit, so the card goes per-task under
    /// <c>balanced</c> and <c>conservative</c>.
    /// </summary>
    public const double UnknownDiff = .55;

    private static readonly string[] InfrastructurePrefixes =
    [
        "backend/Features/Pipeline/", "backend/Features/Git/", "contracts/", "deploy/",
    ];

    /// <summary>
    /// Scores a card from its own changed paths and changed lines (both
    /// measured from the merge base with the integration line) and its
    /// batch history. Size terms rise smoothly without a hard cap.
    /// </summary>
    public static double Score(IReadOnlyList<string> paths, int changedLines,
        bool priorRed, bool flaky, bool areaRed)
    {
        var pathCount = paths.Count;
        var lines = Math.Max(0, changedLines);
        var infrastructure = paths.Count(IsInfrastructure);
        return Math.Clamp(.08
            + .20 * pathCount / (pathCount + 30d)
            + .15 * lines / (lines + 1500d)
            + (infrastructure > 0 ? .06 + .02 * Math.Min(infrastructure, 5) : 0)
            + History(priorRed, flaky, areaRed), 0, .95);
    }

    /// <summary>Risk of a card without a usable diff; history still adds on top.</summary>
    public static double Unknown(bool priorRed, bool flaky, bool areaRed)
        => Math.Clamp(UnknownDiff + History(priorRed, flaky, areaRed), 0, .95);

    public static bool IsInfrastructure(string path)
        => InfrastructurePrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static double History(bool priorRed, bool flaky, bool areaRed)
        => (priorRed ? .2 : 0) + (flaky ? .1 : 0) + (areaRed ? .1 : 0);
}
