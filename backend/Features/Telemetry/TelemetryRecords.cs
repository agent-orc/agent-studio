using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Telemetry;

/// <summary>One project environment; the unit of authentication, storage, and retention.</summary>
public sealed record TelemetryEnvironmentKey(string Project, string Environment)
{
    public override string ToString() => $"{Project}/{Environment}";
}

public enum MetricKind { Gauge, Sum, Histogram, ExponentialHistogram, Summary }

/// <summary>OTLP AggregationTemporality; the numeric values are the wire values.</summary>
public enum MetricTemporality { Unspecified = 0, Delta = 1, Cumulative = 2 }

/// <summary>
/// Attribute set with string values, sorted by key. OTLP values of other types
/// are rendered as text so that series identity and attribute filters compare
/// one representation. The set is an immutable array of key-value references:
/// a record without attributes of its own shares the set of its resource and
/// scope (<see cref="IsShared"/>), and a record with its own attributes refers
/// to the same strings. Only shared sets cache their JSON text, so a queued
/// batch never holds one JSON text per record.
/// </summary>
public sealed class TelemetryAttributes
{
    public static readonly TelemetryAttributes Empty = new([], shared: true);

    private readonly KeyValuePair<string, string>[] _items;
    private string? _json;

    private TelemetryAttributes(KeyValuePair<string, string>[] items, bool shared)
    {
        _items = items;
        IsShared = shared;
    }

    /// <summary>True for the resource and scope set that many records refer to.</summary>
    public bool IsShared { get; }

    public int Count => _items.Length;

    /// <summary>Entries in ordinal key order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    /// <summary>A sorted copy for queries and tests; the set itself keeps no dictionary.</summary>
    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in _items) values[key] = value;
            return values;
        }
    }

    /// <summary>Canonical JSON object; equal sets have equal text.</summary>
    public string Json => IsShared ? _json ??= Render() : Render();

    public static TelemetryAttributes From(IEnumerable<KeyValuePair<string, string>> values, bool shared = false)
    {
        var unique = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values) unique[key] = value;
        var items = unique.ToArray();
        Array.Sort(items, (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new(items, shared);
    }

    public static TelemetryAttributes ParseJson(string json)
        => From(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);

    /// <summary>
    /// Applies <paramref name="map"/> to every value, for masking before storage.
    /// A set whose values do not change is returned as it is, so sharing survives.
    /// </summary>
    public TelemetryAttributes MapValues(Func<string, string> map)
    {
        KeyValuePair<string, string>[]? mapped = null;
        for (var index = 0; index < _items.Length; index++)
        {
            var (key, value) = _items[index];
            var result = map(value);
            if (mapped is null && string.Equals(result, value, StringComparison.Ordinal)) continue;
            mapped ??= (KeyValuePair<string, string>[])_items.Clone();
            mapped[index] = KeyValuePair.Create(key, result);
        }
        return mapped is null ? this : new(mapped, IsShared);
    }

    private string Render()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in _items) writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// One OTLP data point. Gauge and sum points carry <see cref="Value"/> with a
/// count of one; histogram and summary points carry their sum in
/// <see cref="Value"/> and their observation count in <see cref="Count"/>.
/// </summary>
public sealed record MetricSample(
    string Name, string Unit, MetricKind Kind, MetricTemporality Temporality,
    TelemetryAttributes Attributes, long TimeUnixNano, long StartTimeUnixNano,
    double Value, double Count, double? Min, double? Max);

public sealed record TelemetryLogRecord(
    long TimeUnixNano, int SeverityNumber, string SeverityText, string Body,
    TelemetryAttributes Attributes, string? TraceId, string? SpanId, string? EventName);

/// <summary>
/// What one OTLP request produced: accepted items, a count of rejected ones,
/// and the estimated memory the items hold (see <see cref="OtlpRecordFactory"/>).
/// </summary>
public sealed record OtlpDecodeResult<T>(IReadOnlyList<T> Items, long Rejected, string? RejectionMessage, long EstimatedBytes = 0);

/// <summary>One accepted request, waiting for the background writer.</summary>
public sealed record TelemetryIngestBatch(
    TelemetryEnvironmentKey Environment, IReadOnlyList<MetricSample> Metrics, IReadOnlyList<TelemetryLogRecord> Logs,
    long EstimatedBytes)
{
    public int RecordCount => Metrics.Count + Logs.Count;
}

/// <summary>OTLP severity numbers 1 to 24 grouped in six ranges of four.</summary>
public static class TelemetrySeverity
{
    private static readonly string[] Names = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];

    public static string Name(int severityNumber)
        => severityNumber is >= 1 and <= 24 ? Names[(severityNumber - 1) / 4] : "UNSPECIFIED";

    /// <summary>Lowest severity number of a named range, or a number 0 to 24 given as text.</summary>
    public static int? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number is >= 0 and <= 24 ? number : null;
        var upper = text.ToUpperInvariant() switch
        {
            "WARNING" => "WARN",
            "CRITICAL" or "PANIC" => "FATAL",
            "ERR" => "ERROR",
            "INFORMATION" => "INFO",
            var other => other
        };
        var index = Array.IndexOf(Names, upper);
        return index < 0 ? null : index * 4 + 1;
    }
}
