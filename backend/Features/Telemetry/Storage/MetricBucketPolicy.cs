namespace AgentStudio.Telemetry;

public enum MetricResolution { Minute, Hour }

/// <summary>What one data point adds to a minute or hour bucket.</summary>
public readonly record struct BucketContribution(double Count, double Sum, double Min, double Max, double Last, long LastTimeUnixNano);

/// <summary>Last cumulative count and sum seen for a histogram or summary series.</summary>
public sealed record CumulativeState(long StartTimeUnixNano, double Count, double Sum, long TimeUnixNano);

/// <summary>Resolution, step, and bucket range chosen for one series query.</summary>
public sealed record MetricQueryPlan(MetricResolution Resolution, long StepSeconds, long FromSeconds, long ToSeconds)
{
    public long NativeSeconds => MetricBucketPolicy.NativeSeconds(Resolution);
}

/// <summary>
/// Pure bucket arithmetic. Gauges and delta sums add their value as one observation.
/// Cumulative sums preserve their monotonic flag and are converted to deltas.
/// Delta histograms add count and sum. Cumulative histograms and summaries are
/// turned into deltas against the previous point of the same series: a new
/// start time or a falling count is a reset, and the first point of a series
/// only sets the baseline unless its start time lies within the last minute.
/// </summary>
public static class MetricBucketPolicy
{
    public const long NanosPerSecond = 1_000_000_000;
    public const int MaxPointsPerSeries = 1440;
    private const int AutomaticPoints = 240;
    private static readonly long FreshStartNanos = 60 * NanosPerSecond;

    public static long NativeSeconds(MetricResolution resolution) => resolution == MetricResolution.Minute ? 60 : 3600;

    public static long BucketStart(long unixNano, MetricResolution resolution)
    {
        var native = NativeSeconds(resolution);
        return unixNano / NanosPerSecond / native * native;
    }

    public static (BucketContribution? Contribution, CumulativeState? State) Contribute(MetricSample sample, CumulativeState? prior)
    {
        if (sample.Kind == MetricKind.Gauge || sample.Kind == MetricKind.Sum && sample.Temporality != MetricTemporality.Cumulative)
            return (new BucketContribution(1, sample.Value, sample.Value, sample.Value, sample.Value, sample.TimeUnixNano), prior);

        var cumulative = sample.Kind == MetricKind.Summary || sample.Temporality == MetricTemporality.Cumulative;
        if (!cumulative)
            return (Whole(sample), prior);

        var current = new CumulativeState(sample.StartTimeUnixNano, sample.Count, sample.Value, sample.TimeUnixNano);
        if (sample.Kind == MetricKind.Sum)
        {
            if (prior is null)
            {
                var fresh = sample.StartTimeUnixNano > 0 && sample.TimeUnixNano - sample.StartTimeUnixNano <= FreshStartNanos;
                return (fresh ? SumValue(sample.Value, sample.TimeUnixNano) : null, current);
            }
            if (sample.TimeUnixNano <= prior.TimeUnixNano) return (null, prior);
            var sumReset = sample.StartTimeUnixNano != prior.StartTimeUnixNano
                || sample.IsMonotonic && sample.Value < prior.Sum;
            return (SumValue(sumReset ? sample.Value : sample.Value - prior.Sum, sample.TimeUnixNano), current);
        }
        if (prior is null)
        {
            var fresh = sample.StartTimeUnixNano > 0 && sample.TimeUnixNano - sample.StartTimeUnixNano <= FreshStartNanos;
            return (fresh ? Whole(sample) : null, current);
        }
        if (sample.TimeUnixNano <= prior.TimeUnixNano) return (null, prior);
        var reset = sample.StartTimeUnixNano != prior.StartTimeUnixNano || sample.Count < prior.Count;
        if (reset) return (Whole(sample), current);
        var count = sample.Count - prior.Count;
        if (count <= 0) return (null, current);
        var mean = (sample.Value - prior.Sum) / count;
        return (new BucketContribution(count, sample.Value - prior.Sum, mean, mean, mean, sample.TimeUnixNano), current);
    }

    /// <summary>
    /// Minute buckets serve ranges inside the minute retention with steps below
    /// one hour; anything else reads hourly aggregates. Without a requested
    /// step the plan targets about 240 points. A requested step is rounded up
    /// to the native bucket width.
    /// </summary>
    public static MetricQueryPlan? Plan(DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, long? requestedStepSeconds,
        TelemetryOptions options, out string? error)
    {
        error = null;
        if (to <= from)
        {
            error = "'to' must be after 'from'.";
            return null;
        }
        if (requestedStepSeconds is < 1)
        {
            error = "'step' must be a positive number of seconds.";
            return null;
        }
        var fromSeconds = from.ToUnixTimeSeconds();
        var toSeconds = (long)Math.Ceiling((to - DateTimeOffset.UnixEpoch).TotalSeconds);
        var range = toSeconds - fromSeconds;
        var resolution = requestedStepSeconds >= 3600 || from < now - options.MetricMinuteRetention
            ? MetricResolution.Hour : MetricResolution.Minute;
        var native = NativeSeconds(resolution);
        var step = requestedStepSeconds ?? (range + AutomaticPoints - 1) / AutomaticPoints;
        step = Math.Max(native, (step + native - 1) / native * native);
        if ((range + step - 1) / step > MaxPointsPerSeries)
        {
            error = $"The range needs more than {MaxPointsPerSeries} points at this step; choose a larger step.";
            return null;
        }
        return new MetricQueryPlan(resolution, step, fromSeconds / native * native, toSeconds);
    }

    private static BucketContribution SumValue(double value, long time)
        => new(1, value, value, value, value, time);

    private static BucketContribution? Whole(MetricSample sample)
    {
        if (sample.Count <= 0) return null;
        var mean = sample.Value / sample.Count;
        return new BucketContribution(sample.Count, sample.Value, sample.Min ?? mean, sample.Max ?? mean, mean, sample.TimeUnixNano);
    }
}
