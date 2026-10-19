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
/// one representation.
/// </summary>
public sealed class TelemetryAttributes
{
    public static readonly TelemetryAttributes Empty = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, string> _values;
    private string? _json;

    private TelemetryAttributes(SortedDictionary<string, string> values) => _values = values;

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Canonical JSON object; equal sets have equal text.</summary>
    public string Json => _json ??= JsonSerializer.Serialize(_values);

    public static TelemetryAttributes From(IEnumerable<KeyValuePair<string, string>> values)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values) sorted[key] = value;
        return new(sorted);
    }

    public static TelemetryAttributes ParseJson(string json)
        => From(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);

    /// <summary>Applies <paramref name="map"/> to every value, for masking before storage.</summary>
    public TelemetryAttributes MapValues(Func<string, string> map)
        => From(_values.Select(item => KeyValuePair.Create(item.Key, map(item.Value))));
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

/// <summary>What one OTLP request produced: accepted items plus a count of rejected ones.</summary>
public sealed record OtlpDecodeResult<T>(IReadOnlyList<T> Items, long Rejected, string? RejectionMessage);

/// <summary>One accepted request, waiting for the background writer.</summary>
public sealed record TelemetryIngestBatch(
    TelemetryEnvironmentKey Environment, IReadOnlyList<MetricSample> Metrics, IReadOnlyList<TelemetryLogRecord> Logs)
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
