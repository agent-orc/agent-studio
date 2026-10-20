using System.Text;

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

/// <summary>The request is larger than the intake accepts; decoding stopped at the limit.</summary>
public sealed class OtlpLimitException(string message) : Exception(message);

/// <summary>
/// Turns decoded fields into stored records and counts what it cannot use.
/// Shared by the protobuf and JSON decoders so both encodings accept and reject
/// the same data.
/// <para>It also bounds the memory of one request while the request is decoded:
/// every resource and scope gets one shared attribute set, a record without
/// attributes of its own refers to that set, and each record adds an estimate of
/// the memory it holds. Past <see cref="OtlpLimits.MaxRecords"/> records or
/// <see cref="OtlpLimits.MaxDecodedBytes"/> estimated bytes it throws
/// <see cref="OtlpLimitException"/>, so decoding stops before the rest of the
/// body is turned into records.</para>
/// <para>It bounds what the writer stores for one request as well. A shared
/// set costs its memory once, but the writer renders its JSON for every point
/// and writes it into every log row. So each record charges the bytes the
/// writer stores and renders for it (attribute JSON, measured once per shared
/// set and once per merged set, plus name, unit, body, severity text, event
/// name, and ids) to <see cref="OtlpLimits.MaxStoredBytes"/>, and decoding
/// stops with <see cref="OtlpLimitException"/> past it.</para>
/// <para>It bounds the CPU time of one request as well. The decoders read at
/// most <see cref="OtlpLimits.MaxAttributes"/> entries of every attribute list
/// (<see cref="TakesAttribute"/>) and skip the rest without creating strings.
/// Each resource set is built once and every scope starts from it, resources
/// and scopes count toward <see cref="OtlpLimits.MaxScopes"/>, and every unit
/// of work is charged to <see cref="Work"/>, which checks the cancellation
/// token (request aborted or intake deadline passed) every few thousand units.</para>
/// </summary>
internal sealed class OtlpRecordFactory(OtlpLimits limits, long receivedUnixNano, CancellationToken cancellationToken = default)
{
    /// <summary>OTLP DataPointFlags.FLAG_NO_RECORDED_VALUE: a staleness marker, not a value.</summary>
    private const uint NoRecordedValue = 1;
    private const int MaxUnitChars = 63;
    /// <summary>Object header, fields, and list slot of one record; strings and attribute sets come on top.</summary>
    private const int RecordBytes = 160;
    /// <summary>The minute and the hour bucket row one data point updates.</summary>
    private const int StoredPointBytes = 192;
    private const int StringHeaderBytes = 24;
    private const int ReferenceBytes = 16;
    private const int CancellationCheckInterval = 4096;

    private readonly List<MetricSample> _metrics = [];
    private readonly List<TelemetryLogRecord> _logs = [];
    private long _rejected;
    private string? _message;
    private long _records;
    private int _scopes;
    private long _estimatedBytes;
    private long _storedBytes;
    private long _work;
    private long _nextCancellationCheck = CancellationCheckInterval;

    public OtlpLimits Limits => limits;

    /// <summary>Attribute entries read or skipped, attribute entries copied, metrics, resources, scopes, and records so far.</summary>
    public long Work => _work;

    /// <summary>Counts decoding work; throws <see cref="OperationCanceledException"/> once the token is cancelled.</summary>
    public void Charge(long units = 1)
    {
        _work += units;
        if (_work < _nextCancellationCheck) return;
        _nextCancellationCheck = _work + CancellationCheckInterval;
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Whether the next entry of an attribute list that already holds
    /// <paramref name="read"/> entries is decoded; the decoder skips it otherwise.
    /// </summary>
    public bool TakesAttribute(int read)
    {
        Charge();
        return read < limits.MaxAttributes;
    }

    public void Reject(string reason, long count = 1)
    {
        if (count <= 0) return;
        _rejected += count;
        _message ??= reason;
    }

    /// <summary>The capped attribute set of one resource, built once; every scope of the resource starts from it.</summary>
    public TelemetryAttributes Resource(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        CountScope();
        Charge(attributes.Count);
        var shared = new OtlpAttributeBuilder(limits).AddRange(attributes).Build(shared: true);
        // The strings, the reference array, and the JSON text the store caches for a shared set.
        long bytes = RecordBytes + ReferenceBytes * shared.Count;
        foreach (var (key, value) in shared.Items) bytes += 2 * StringHeaderBytes + 4L * (key.Length + value.Length + 6);
        Account(bytes);
        return shared;
    }

    /// <summary>
    /// The attribute set every record of one resource and scope refers to; the
    /// scope name becomes <c>otel.scope.name</c>. A scope without a name, or
    /// one whose name does not fit, shares the resource set itself.
    /// </summary>
    public TelemetryAttributes Scope(TelemetryAttributes resource, string scopeName)
    {
        CountScope();
        if (scopeName.Length == 0) return resource;
        Charge(resource.Count + 1);
        var builder = new OtlpAttributeBuilder(limits, resource);
        builder.Add("otel.scope.name", scopeName);
        if (!builder.Changed) return resource;
        var shared = builder.Build(shared: true);
        // References to the resource strings, the scope name, and the JSON text the store caches for a shared set.
        long bytes = RecordBytes + ReferenceBytes * shared.Count + StringHeaderBytes
            + 2L * Math.Min(scopeName.Length, limits.MaxAttributeValueChars + 1);
        foreach (var (key, value) in shared.Items) bytes += 2L * (key.Length + value.Length + 6);
        Account(bytes);
        return shared;
    }

    public void AddPoint(string name, string unit, MetricKind kind, MetricTemporality temporality,
        TelemetryAttributes scope, OtlpPointFields point, bool isMonotonic = false)
    {
        CountRecord();
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
        var attributes = Merge(scope, point.Attributes);
        unit = OtlpAttributeBuilder.Truncate(unit, MaxUnitChars);
        Account(RecordBytes + StringBytes(name) + StringBytes(unit) + OwnBytes(attributes, point.Attributes));
        Store(StoredPointBytes + Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(unit) + attributes.JsonBytes);
        _metrics.Add(new MetricSample(name, unit, kind, temporality, attributes,
            point.TimeUnixNano == 0 ? receivedUnixNano : point.TimeUnixNano, point.StartTimeUnixNano,
            value, count, Finite(point.Min), Finite(point.Max), isMonotonic));
    }

    public void AddLog(TelemetryAttributes scope, OtlpLogFields log)
    {
        CountRecord();
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
        var attributes = Merge(scope, log.Attributes);
        var body = OtlpAttributeBuilder.Truncate(log.Body, limits.MaxBodyChars);
        var eventName = string.IsNullOrEmpty(log.EventName) ? null : OtlpAttributeBuilder.Truncate(log.EventName, 255);
        Account(RecordBytes + StringBytes(body) + StringBytes(severityText) + StringBytes(eventName)
            + StringBytes(log.TraceId) + StringBytes(log.SpanId) + OwnBytes(attributes, log.Attributes));
        var record = new TelemetryLogRecord(time, severity, severityText, body, attributes, log.TraceId, log.SpanId, eventName);
        Store(record.StoredBytes(attributes.JsonBytes));
        _logs.Add(record);
    }

    public OtlpDecodeResult<MetricSample> Metrics() => new(_metrics, _rejected, _message, _estimatedBytes, _storedBytes);

    public OtlpDecodeResult<TelemetryLogRecord> Logs() => new(_logs, _rejected, _message, _estimatedBytes, _storedBytes);

    /// <summary>Lowercase hex of a trace or span id; all-zero and wrong-length ids are absent.</summary>
    public static string? Identifier(ReadOnlySpan<byte> bytes, int expectedLength)
        => bytes.Length != expectedLength || !bytes.ContainsAnyExcept((byte)0) ? null : Convert.ToHexStringLower(bytes);

    /// <summary>
    /// The shared scope set itself when the record adds nothing to it (no
    /// attributes, or none that fit); otherwise a merged copy of references.
    /// </summary>
    private TelemetryAttributes Merge(TelemetryAttributes scope, List<KeyValuePair<string, string>> own)
    {
        if (own.Count == 0) return scope;
        Charge(scope.Count + own.Count);
        var builder = new OtlpAttributeBuilder(limits, scope).AddRange(own);
        return builder.Changed ? builder.Build() : scope;
    }

    /// <summary>A record's own set: the reference array plus the strings decoded for this record only.</summary>
    private static long OwnBytes(TelemetryAttributes attributes, List<KeyValuePair<string, string>> own)
    {
        if (attributes.IsShared) return 0;
        long bytes = RecordBytes + ReferenceBytes * attributes.Count;
        foreach (var (key, value) in own) bytes += StringBytes(key) + StringBytes(value);
        return bytes;
    }

    private static long StringBytes(string? value) => value is null ? 0 : StringHeaderBytes + 2L * value.Length;

    private void CountRecord()
    {
        Charge();
        if (++_records > limits.MaxRecords)
            throw new OtlpLimitException($"The request has more than {limits.MaxRecords} data points or log records.");
    }

    private void CountScope()
    {
        Charge();
        if (++_scopes > limits.MaxScopes)
            throw new OtlpLimitException($"The request has more than {limits.MaxScopes} resources and instrumentation scopes.");
    }

    private void Account(long bytes)
    {
        _estimatedBytes += bytes;
        if (_estimatedBytes > limits.MaxDecodedBytes)
            throw new OtlpLimitException($"The decoded request needs more than {limits.MaxDecodedBytes} bytes of memory.");
    }

    private void Store(long bytes)
    {
        _storedBytes += bytes;
        if (_storedBytes > limits.MaxStoredBytes)
            throw new OtlpLimitException($"The request would store more than {limits.MaxStoredBytes} bytes.");
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
