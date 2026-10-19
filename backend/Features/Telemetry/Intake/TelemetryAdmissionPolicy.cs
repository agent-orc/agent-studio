namespace AgentStudio.Telemetry;

/// <summary>
/// Pure time-window admission. A metric point older than the hourly retention,
/// a log record older than the log retention, or anything further in the
/// future than the allowed clock skew is rejected at intake, so the OTLP
/// partial-success count tells the sender what was not stored.
/// </summary>
public static class TelemetryAdmissionPolicy
{
    public const string OutsideWindowMessage = "Timestamp is outside the retention window or too far in the future.";

    public static OtlpDecodeResult<MetricSample> AdmitMetrics(OtlpDecodeResult<MetricSample> decoded, DateTimeOffset now, TelemetryOptions options)
        => Admit(decoded, item => item.TimeUnixNano, now - options.MetricHourRetention, now + options.MaxFutureSkew);

    public static OtlpDecodeResult<TelemetryLogRecord> AdmitLogs(OtlpDecodeResult<TelemetryLogRecord> decoded, DateTimeOffset now, TelemetryOptions options)
        => Admit(decoded, item => item.TimeUnixNano, now - options.LogRetention, now + options.MaxFutureSkew);

    public static long UnixNanos(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;

    public static DateTimeOffset FromUnixNanos(long value) => DateTimeOffset.UnixEpoch.AddTicks(value / 100);

    private static OtlpDecodeResult<T> Admit<T>(OtlpDecodeResult<T> decoded, Func<T, long> time, DateTimeOffset oldest, DateTimeOffset newest)
    {
        var lower = UnixNanos(oldest);
        var upper = UnixNanos(newest);
        var admitted = decoded.Items.Where(item => time(item) >= lower && time(item) <= upper).ToList();
        var outside = decoded.Items.Count - admitted.Count;
        return outside == 0 ? decoded
            : decoded with
            {
                Items = admitted, Rejected = decoded.Rejected + outside,
                RejectionMessage = decoded.RejectionMessage ?? OutsideWindowMessage
            };
    }
}
