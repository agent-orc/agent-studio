namespace AgentStudio.Projects;

/// <summary>One probe result. <see cref="Error"/> is a bounded code, never remote output.</summary>
public sealed record HealthSample(DateTimeOffset At, bool Ok, int? LatencyMs, int? Status, string? Error,
    DateTimeOffset? CertificateNotAfter = null);

/// <summary>One hour of samples after raw retention has passed.</summary>
public sealed record HealthHourlyAggregate(DateTimeOffset Hour, int Total, int Failures, int? P50Ms, int? P95Ms,
    int? MaxMs);

public sealed record HealthLatency(int? P50Ms, int? P95Ms, int? P99Ms);

/// <summary>Pure time-series math: uptime, percentiles and hourly aggregation.</summary>
public static class HealthSeries
{
    public static readonly TimeSpan RawRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan HourlyRetention = TimeSpan.FromDays(400);

    /// <returns>Share of successful samples in the window, or null without samples.</returns>
    public static double? Uptime(IEnumerable<HealthSample> samples, DateTimeOffset now, TimeSpan window)
    {
        var total = 0;
        var ok = 0;
        foreach (var sample in samples)
        {
            if (sample.At <= now - window || sample.At > now) continue;
            total++;
            if (sample.Ok) ok++;
        }
        return total == 0 ? null : (double)ok / total;
    }

    public static HealthLatency Latency(IEnumerable<HealthSample> samples, DateTimeOffset now, TimeSpan window)
    {
        var values = samples.Where(sample => sample.Ok && sample.LatencyMs is not null
                && sample.At > now - window && sample.At <= now)
            .Select(sample => sample.LatencyMs!.Value).Order().ToArray();
        return new(Percentile(values, 0.50), Percentile(values, 0.95), Percentile(values, 0.99));
    }

    /// <summary>Nearest-rank percentile over sorted values.</summary>
    public static int? Percentile(IReadOnlyList<int> sorted, double quantile)
    {
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    public static IReadOnlyList<HealthHourlyAggregate> Hourly(IEnumerable<HealthSample> samples)
        => samples.GroupBy(sample => new DateTimeOffset(sample.At.UtcDateTime.Year, sample.At.UtcDateTime.Month,
                sample.At.UtcDateTime.Day, sample.At.UtcDateTime.Hour, 0, 0, TimeSpan.Zero))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var latencies = group.Where(sample => sample.Ok && sample.LatencyMs is not null)
                    .Select(sample => sample.LatencyMs!.Value).Order().ToArray();
                return new HealthHourlyAggregate(group.Key, group.Count(), group.Count(sample => !sample.Ok),
                    Percentile(latencies, 0.50), Percentile(latencies, 0.95),
                    latencies.Length == 0 ? null : latencies[^1]);
            }).ToArray();

    /// <summary>
    /// Raw day files (UTC dates) whose whole day lies before the raw retention window and
    /// must be folded into hourly aggregates.
    /// </summary>
    public static IReadOnlyList<DateOnly> DaysToAggregate(IEnumerable<DateOnly> rawDays, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime - RawRetention;
        return rawDays.Where(day => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1) <= cutoff)
            .Order().ToArray();
    }
}
