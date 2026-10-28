namespace AgentRunner.WorkContainers;

/// <summary>p50 and p90 of one measured series, in milliseconds.</summary>
public sealed record LatencySummary(int Count, double? P50, double? P90)
{
    public static LatencySummary Of(IEnumerable<double?> values)
    {
        var sorted = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        return new LatencySummary(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.90));
    }

    // Nearest-rank percentile: always a measured value, never an interpolation.
    private static double? Percentile(double[] sorted, double quantile)
        => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];
}

public sealed record ExecutorMeasurement(
    LatencySummary GrantToFirstCommandWarm,
    LatencySummary ColdMaterialization,
    LatencySummary GateWall,
    LatencySummary AttemptWall,
    double WarmCacheHitRate);

public sealed record RolloutEvidence(
    ExecutorMeasurement Container,
    ExecutorMeasurement Process,
    double? ImagePullMs,
    bool ImagePullCold,
    int Attempts,
    int CleanupVerified,
    int LiveResidue,
    int OverBudgetLaunches,
    int OverBudgetDeniedBeforeRuntime,
    int Resurrections);

public sealed record RolloutCheck(string Name, string Threshold, string Measured, bool Passed);

public sealed record RolloutVerdict(bool Go, IReadOnlyList<RolloutCheck> Checks);

/// <summary>
/// DP-S3 go/no-go threshold for moving the container executor beyond canary.
/// The numbers are fixed here before broad rollout; a canary host that misses
/// any of them keeps the process executor (dossier D3 option B as rollback).
/// </summary>
public static class WorkContainerRolloutThreshold
{
    public const double MaxWarmFirstCommandP50OverheadMs = 3_000;
    public const double MaxWarmFirstCommandP90OverheadMs = 5_000;
    public const double MaxGateWallP50Ratio = 1.10;
    public const double MaxGateWallP90Ratio = 1.20;
    public const double MaxColdMaterializationP50Ratio = 1.50;
    public const double MaxImagePullMs = 180_000;
    public const double MinWarmCacheHitRate = 0.95;

    public static RolloutVerdict Evaluate(RolloutEvidence evidence)
    {
        var c = evidence.Container;
        var p = evidence.Process;
        var checks = new List<RolloutCheck>
        {
            new("verified cleanup", "100% of attempts, zero live container, secret mount or attempt volume",
                $"{evidence.CleanupVerified}/{evidence.Attempts}, residue {evidence.LiveResidue}",
                evidence.Attempts > 0 && evidence.CleanupVerified == evidence.Attempts && evidence.LiveResidue == 0),
            new("over-budget launches", "100% denied before any runtime mutation",
                $"{evidence.OverBudgetDeniedBeforeRuntime}/{evidence.OverBudgetLaunches}",
                evidence.OverBudgetLaunches > 0 && evidence.OverBudgetDeniedBeforeRuntime == evidence.OverBudgetLaunches),
            new("fenced attempt resurrection", "0", evidence.Resurrections.ToString(), evidence.Resurrections == 0),
            Overhead("warm grant-to-first-command p50", c.GrantToFirstCommandWarm.P50, p.GrantToFirstCommandWarm.P50, MaxWarmFirstCommandP50OverheadMs),
            Overhead("warm grant-to-first-command p90", c.GrantToFirstCommandWarm.P90, p.GrantToFirstCommandWarm.P90, MaxWarmFirstCommandP90OverheadMs),
            Ratio("gate wall p50", c.GateWall.P50, p.GateWall.P50, MaxGateWallP50Ratio),
            Ratio("gate wall p90", c.GateWall.P90, p.GateWall.P90, MaxGateWallP90Ratio),
            Ratio("cold repository materialization p50", c.ColdMaterialization.P50, p.ColdMaterialization.P50, MaxColdMaterializationP50Ratio),
            // Only a pull without local layers counts; a warm-layer pull proves nothing about a fresh host.
            new("cold image pull", $"<= {MaxImagePullMs / 1000:0} s per digest per host, measured without local layers",
                evidence.ImagePullMs is { } pull ? $"{pull / 1000:0.0} s{(evidence.ImagePullCold ? "" : " (layers already local, not cold)")}" : "not measured",
                evidence.ImagePullCold && evidence.ImagePullMs is { } measured && measured <= MaxImagePullMs),
            new("warm cache hit rate", $">= {MinWarmCacheHitRate:P0}", $"{c.WarmCacheHitRate:P1}", c.WarmCacheHitRate >= MinWarmCacheHitRate),
        };
        return new RolloutVerdict(checks.All(check => check.Passed), checks);
    }

    private static RolloutCheck Overhead(string name, double? container, double? process, double maxMs)
        => new(name, $"container <= process + {maxMs / 1000:0.#} s",
            $"container {Format(container)} vs process {Format(process)}",
            container is { } c && process is { } p && c - p <= maxMs);

    private static RolloutCheck Ratio(string name, double? container, double? process, double maxRatio)
        => new(name, $"container <= {maxRatio:0.00} x process",
            $"container {Format(container)} vs process {Format(process)}",
            container is { } c && process is { } p && p > 0 && c / p <= maxRatio);

    private static string Format(double? ms) => ms is { } value ? $"{value:0} ms" : "n/a";
}
