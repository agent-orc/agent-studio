namespace AgentStudio.Telemetry;

/// <summary>Fields of one OTLP data point, whichever encoding they came from.</summary>
internal sealed class OtlpPointFields
{
    public List<KeyValuePair<string, string>> Attributes { get; } = [];
    public long StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public double? Number { get; set; }
    public double? Count { get; set; }
    public double? Sum { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public uint Flags { get; set; }
}

/// <summary>Fields of one OTLP log record, whichever encoding they came from.</summary>
internal sealed class OtlpLogFields
{
    public List<KeyValuePair<string, string>> Attributes { get; } = [];
    public long TimeUnixNano { get; set; }
    public long ObservedTimeUnixNano { get; set; }
    public int SeverityNumber { get; set; }
    public string SeverityText { get; set; } = "";
    public string Body { get; set; } = "";
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? EventName { get; set; }
}

/// <summary>
/// Turns decoded fields into stored records and counts what it cannot use.
/// Shared by the protobuf and JSON decoders so both encodings accept and reject
/// the same data.
/// </summary>
internal sealed class OtlpRecordFactory(OtlpLimits limits, long receivedUnixNano)
{
    /// <summary>OTLP DataPointFlags.FLAG_NO_RECORDED_VALUE: a staleness marker, not a value.</summary>
    private const uint NoRecordedValue = 1;

    private readonly List<MetricSample> _metrics = [];
    private readonly List<TelemetryLogRecord> _logs = [];
    private long _rejected;
    private string? _message;

    public OtlpLimits Limits => limits;

    public void Reject(string reason, long count = 1)
    {
        if (count <= 0) return;
        _rejected += count;
        _message ??= reason;
    }

    public void AddPoint(string name, string unit, MetricKind kind, MetricTemporality temporality,
        IReadOnlyList<KeyValuePair<string, string>> resource, OtlpPointFields point)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
        {
            Reject("Metric name is missing or longer than 255 characters.");
            return;
        }
        if ((point.Flags & NoRecordedValue) != 0) return;
        if (point.TimeUnixNano < 0 || point.StartTimeUnixNano < 0)
        {
            Reject("Data point timestamp is out of range.");
            return;
        }
        double value, count;
        if (kind is MetricKind.Gauge or MetricKind.Sum)
        {
            if (point.Number is not { } number || !double.IsFinite(number))
            {
                Reject("Number data point has no finite value.");
                return;
            }
            value = number;
            count = 1;
        }
        else
        {
            // Without a sum the stored mean would be wrong, so such a point is rejected.
            count = point.Count ?? 0;
            value = point.Sum ?? (count == 0 ? 0 : double.NaN);
            if (!double.IsFinite(value) || !double.IsFinite(count) || count < 0)
            {
                Reject("Distribution data point has no finite count and sum.");
                return;
            }
        }
        var attributes = new OtlpAttributeBuilder(limits).AddRange(resource).AddRange(point.Attributes).Build();
        _metrics.Add(new MetricSample(name, unit, kind, temporality, attributes,
            point.TimeUnixNano == 0 ? receivedUnixNano : point.TimeUnixNano, point.StartTimeUnixNano,
            value, count, Finite(point.Min), Finite(point.Max)));
    }

    public void AddLog(IReadOnlyList<KeyValuePair<string, string>> resource, OtlpLogFields log)
    {
        if (log.TimeUnixNano < 0 || log.ObservedTimeUnixNano < 0)
        {
            Reject("Log record timestamp is out of range.");
            return;
        }
        var severity = log.SeverityNumber is >= 1 and <= 24 ? log.SeverityNumber
            : TelemetrySeverity.Parse(log.SeverityText) ?? 0;
        var severityText = string.IsNullOrWhiteSpace(log.SeverityText)
            ? severity == 0 ? "" : TelemetrySeverity.Name(severity)
            : OtlpAttributeBuilder.Truncate(log.SeverityText.Trim(), 32);
        var time = log.TimeUnixNano != 0 ? log.TimeUnixNano
            : log.ObservedTimeUnixNano != 0 ? log.ObservedTimeUnixNano : receivedUnixNano;
        var attributes = new OtlpAttributeBuilder(limits).AddRange(resource).AddRange(log.Attributes).Build();
        _logs.Add(new TelemetryLogRecord(time, severity, severityText,
            OtlpAttributeBuilder.Truncate(log.Body, limits.MaxBodyChars), attributes,
            log.TraceId, log.SpanId,
            string.IsNullOrEmpty(log.EventName) ? null : OtlpAttributeBuilder.Truncate(log.EventName, 255)));
    }

    public OtlpDecodeResult<MetricSample> Metrics() => new(_metrics, _rejected, _message);

    public OtlpDecodeResult<TelemetryLogRecord> Logs() => new(_logs, _rejected, _message);

    /// <summary>Lowercase hex of a trace or span id; all-zero and wrong-length ids are absent.</summary>
    public static string? Identifier(ReadOnlySpan<byte> bytes, int expectedLength)
        => bytes.Length != expectedLength || !bytes.ContainsAnyExcept((byte)0) ? null : Convert.ToHexStringLower(bytes);

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
