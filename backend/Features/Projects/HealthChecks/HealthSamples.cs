using System.Text.Json.Serialization;

namespace AgentStudio.Projects;

/// <summary>One probe result. <see cref="Error"/> is a bounded code, never remote output.</summary>
public sealed record HealthSample(DateTimeOffset At, bool Ok, int? LatencyMs, int? Status, string? Error,
    DateTimeOffset? CertificateNotAfter = null)
{
    /// <summary>The target resolves only to refused addresses: a declaration problem, not an outage.</summary>
    public const string AddressBlocked = "address-blocked";
    /// <summary>The probe itself threw: a control-plane fault, not an outage.</summary>
    public const string ProbeFailed = "probe-failed";

    /// <summary>
    /// False for results that say nothing about the target's availability. They are shown on the
    /// check but neither stored in the series nor counted toward an incident.
    /// </summary>
    [JsonIgnore]
    public bool MeasuresAvailability => Error is not (AddressBlocked or ProbeFailed);
}

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

    /// <summary>
    /// Uptime over a long window from the hourly aggregates, completed by the raw samples after
    /// the last aggregated hour. The oldest edge has hour granularity.
    /// </summary>
    public static double? Uptime(IReadOnlyList<HealthHourlyAggregate> hourly, IEnumerable<HealthSample> raw,
        DateTimeOffset now, TimeSpan window)
    {
        var start = now - window;
        var total = 0;
        var failures = 0;
        var aggregatedUntil = AggregatedUntil(hourly) ?? DateTimeOffset.MinValue;
        foreach (var hour in hourly)
        {
            if (hour.Hour + TimeSpan.FromHours(1) <= start || hour.Hour > now) continue;
            total += hour.Total;
            failures += hour.Failures;
        }
        foreach (var sample in raw)
        {
            if (sample.At < aggregatedUntil || sample.At <= start || sample.At > now) continue;
            total++;
            if (!sample.Ok) failures++;
        }
        return total == 0 ? null : (double)(total - failures) / total;
    }

    /// <summary>The end of the last aggregated hour; raw samples from there on are not aggregated yet.</summary>
    public static DateTimeOffset? AggregatedUntil(IReadOnlyList<HealthHourlyAggregate> hourly)
        => hourly.Count == 0 ? null : hourly.Max(item => item.Hour) + TimeSpan.FromHours(1);

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
    /// Raw day files (UTC dates) that are complete and therefore folded into hourly aggregates,
    /// which serve the 7- and 30-day uptime.
    /// </summary>
    public static IReadOnlyList<DateOnly> DaysToAggregate(IEnumerable<DateOnly> rawDays, DateTimeOffset now)
        => DaysEndingBy(rawDays, now.UtcDateTime);

    /// <summary>Raw day files whose whole day lies before the raw retention window and are deleted.</summary>
    public static IReadOnlyList<DateOnly> DaysPastRawRetention(IEnumerable<DateOnly> rawDays, DateTimeOffset now)
        => DaysEndingBy(rawDays, now.UtcDateTime - RawRetention);

    private static DateOnly[] DaysEndingBy(IEnumerable<DateOnly> days, DateTime cutoff)
        => days.Where(day => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1) <= cutoff).Order().ToArray();
}
