namespace AgentStudio.Telemetry;

/// <summary>
/// Intake limits and retention of the built-in telemetry store, read from the
/// <c>Telemetry</c> configuration section. The defaults follow Dossier
/// integrierte-plattform section 8.3: metrics at one-minute resolution for 14
/// days and hourly for 90 days, logs for 7 days with a size cap per environment.
/// </summary>
public sealed record TelemetryOptions
{
    public string? StoreRoot { get; init; }
    public int MetricMinuteRetentionDays { get; init; } = 14;
    public int MetricHourRetentionDays { get; init; } = 90;
    public int LogRetentionDays { get; init; } = 7;
    public long LogMaxBytesPerEnvironment { get; init; } = 256L * 1024 * 1024;
    public int MaxSeriesPerEnvironment { get; init; } = 10_000;
    /// <summary>Names and attribute JSON of the series of one environment, kept in the writer's cache and the series table.</summary>
    public long MaxSeriesBytesPerEnvironment { get; init; } = 32L * 1024 * 1024;
    /// <summary>Estimated disk size of the minute buckets of all environments together, enforced by the retention sweep.</summary>
    public long MaxMinuteBucketBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxRequestBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxRecordsPerRequest { get; init; } = 20_000;
    /// <summary>Estimated memory of the records decoded from one request; decoding stops past it.</summary>
    public long MaxDecodedBytesPerRequest { get; init; } = 32L * 1024 * 1024;
    public int MaxConcurrentRequests { get; init; } = 4;
    public int MaxQueuedRecords { get; init; } = 200_000;
    public long MaxQueuedBytes { get; init; } = 128L * 1024 * 1024;
    public int MaxFutureSkewMinutes { get; init; } = 10;
    public int MaxLogBodyChars { get; init; } = 16 * 1024;
    public int MaxAttributeValueChars { get; init; } = 1024;
    public int MaxAttributesPerRecord { get; init; } = 128;
    /// <summary>UTF-8 bytes of the attribute keys and values of one record, resource attributes included; caps the series key.</summary>
    public int MaxAttributeBytesPerRecord { get; init; } = 16 * 1024;
    public int RetentionSweepMinutes { get; init; } = 10;

    public TimeSpan MetricMinuteRetention => TimeSpan.FromDays(MetricMinuteRetentionDays);
    public TimeSpan MetricHourRetention => TimeSpan.FromDays(MetricHourRetentionDays);
    public TimeSpan LogRetention => TimeSpan.FromDays(LogRetentionDays);
    public TimeSpan MaxFutureSkew => TimeSpan.FromMinutes(MaxFutureSkewMinutes);

    public static TelemetryOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (options.MetricMinuteRetentionDays < 1 || options.MetricHourRetentionDays < options.MetricMinuteRetentionDays
            || options.LogRetentionDays < 1 || options.LogMaxBytesPerEnvironment < 1024 * 1024
            || options.MaxSeriesPerEnvironment < 1 || options.MaxRequestBytes < 1024 || options.MaxQueuedRecords < 1
            || options.MaxLogBodyChars < 64 || options.MaxAttributeValueChars < 16 || options.MaxAttributesPerRecord < 1
            || options.RetentionSweepMinutes < 1 || options.MaxFutureSkewMinutes < 0
            || options.MaxSeriesBytesPerEnvironment < 64 * 1024 || options.MaxMinuteBucketBytes < 1024 * 1024
            || options.MaxRecordsPerRequest < 1 || options.MaxRecordsPerRequest > options.MaxQueuedRecords
            || options.MaxDecodedBytesPerRequest < 1024 * 1024 || options.MaxDecodedBytesPerRequest > options.MaxQueuedBytes
            || options.MaxConcurrentRequests < 1 || options.MaxAttributeBytesPerRecord < 256)
            throw new InvalidOperationException("Telemetry configuration has an out-of-range limit or retention value.");
        return options with
        {
            StoreRoot = string.IsNullOrWhiteSpace(options.StoreRoot)
                ? string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null : Path.Combine(configuration["TaskRepository"]!, "telemetry")
                : options.StoreRoot
        };
    }
}
