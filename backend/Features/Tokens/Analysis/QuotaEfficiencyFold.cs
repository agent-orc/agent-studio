namespace AgentStudio.Tokens;

/// <summary>One token observation of one CLI for the quota fold.</summary>
public readonly record struct QuotaUsageSample(DateTime At, string Cli, long Tokens, long UncachedTokens);

/// <summary>
/// Pure quota context of the token analysis (AGT-3267). Splits each recorded
/// quota window series (<c>.runtime/quota-history</c>) into resets and
/// divides the CLI's workspace tokens inside a window by the percentage
/// points it consumed. A falling tokens-per-point ratio for the same work
/// means the effective plan limit shrank.
/// </summary>
public static class QuotaEfficiencyFold
{
    /// <summary>A drop of more than this many points between readings is a reset.</summary>
    public const double ResetDropPct = 0.5;

    /// <summary>A reset time that moves by more than this is a new window.</summary>
    public static readonly TimeSpan ResetShift = TimeSpan.FromHours(1);

    /// <summary>Below one consumed point the ratio is noise and stays null.</summary>
    public const double MinDeltaPct = 1.0;

    public static IReadOnlyList<TokenAnalysisQuotaWindow> Fold(
        string cli,
        IReadOnlyList<QuotaHistoryPoint> history,
        IReadOnlyList<QuotaUsageSample> usage,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var samples = usage
            .Where(sample => string.Equals(sample.Cli, cli, StringComparison.OrdinalIgnoreCase))
            .OrderBy(sample => sample.At)
            .ToList();
        var windows = new List<TokenAnalysisQuotaWindow>();
        foreach (var series in history
                     .Where(point => point.At >= fromUtc && point.At < toUtc)
                     .GroupBy(point => point.Window, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var points = series.OrderBy(point => point.At).ToList();
            if (points.Count == 0) continue;
            windows.Add(new TokenAnalysisQuotaWindow(
                cli, series.Key, Hourly(points), Segments(points, samples)));
        }
        return windows;
    }

    internal static IReadOnlyList<TokenAnalysisQuotaSegment> Segments(
        IReadOnlyList<QuotaHistoryPoint> points, IReadOnlyList<QuotaUsageSample> samples)
    {
        var segments = new List<TokenAnalysisQuotaSegment>();
        var start = 0;
        for (var i = 1; i <= points.Count; i++)
        {
            var closes = i == points.Count || IsReset(points[i - 1], points[i]);
            if (!closes) continue;
            var first = points[start];
            var last = points[i - 1];
            var delta = last.UsedPct - first.UsedPct;
            long tokens = 0, uncached = 0;
            foreach (var sample in samples)
            {
                if (sample.At <= first.At || sample.At > last.At) continue;
                tokens += sample.Tokens;
                uncached += sample.UncachedTokens;
            }
            segments.Add(new TokenAnalysisQuotaSegment(
                first.At, last.At, first.UsedPct, last.UsedPct, tokens, uncached,
                delta >= MinDeltaPct ? Math.Round(tokens / delta, 1) : null,
                delta >= MinDeltaPct ? Math.Round(uncached / delta, 1) : null,
                Open: i == points.Count));
            start = i;
        }
        return segments;
    }

    private static bool IsReset(QuotaHistoryPoint previous, QuotaHistoryPoint next)
    {
        if (next.UsedPct < previous.UsedPct - ResetDropPct) return true;
        return previous.ResetAt is { } a && next.ResetAt is { } b && (b - a).Duration() > ResetShift;
    }

    /// <summary>Last reading per hour, enough for a chart overlay.</summary>
    private static IReadOnlyList<TokenAnalysisQuotaPoint> Hourly(IReadOnlyList<QuotaHistoryPoint> points)
        => points
            .GroupBy(point => new DateTime(point.At.Year, point.At.Month, point.At.Day, point.At.Hour, 0, 0, DateTimeKind.Utc))
            .Select(group => group.Last())
            .Select(point => new TokenAnalysisQuotaPoint(point.At, point.UsedPct))
            .ToList();
}
