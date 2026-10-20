using System.Globalization;
using System.Text;

namespace AgentStudio.Telemetry;

public enum MetricAggregation { Avg, Sum, Min, Max, Count, Last }

public sealed record MetricQuery(string Name, IReadOnlyDictionary<string, string> Attributes, MetricQueryPlan Plan,
    MetricAggregation Aggregation, int MaxSeries);

public sealed record LogQuery(long FromUnixNano, long ToUnixNano, int? MinSeverity, string? Text,
    IReadOnlyDictionary<string, string> Attributes, int Limit, LogCursor? Cursor);

/// <summary>Position after the last returned record; pages run from newest to oldest.</summary>
public sealed record LogCursor(long TimeUnixNano, long Id)
{
    public string Encode()
        => Convert.ToBase64String(Encoding.ASCII.GetBytes(FormattableString.Invariant($"{TimeUnixNano}:{Id}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static LogCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            return parts.Length == 2
                   && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var time)
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? new LogCursor(time, id) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record MetricPointResponse(DateTimeOffset Time, double Value, double Count);

public sealed record MetricSeriesResponse(IReadOnlyDictionary<string, string> Attributes, string Unit, string Kind,
    IReadOnlyList<MetricPointResponse> Points);

public sealed record MetricQueryResponse(string Name, DateTimeOffset From, DateTimeOffset To, long StepSeconds,
    string Resolution, string Aggregation, IReadOnlyList<MetricSeriesResponse> Series, bool Truncated);

public sealed record MetricNameResponse(string Name, string Kind, string Unit, int SeriesCount);

public sealed record LogRecordResponse(DateTimeOffset Time, string TimeUnixNano, int SeverityNumber, string Severity,
    string Body, IReadOnlyDictionary<string, string> Attributes, string? TraceId, string? SpanId, string? EventName);

public sealed record LogPageResponse(IReadOnlyList<LogRecordResponse> Records, string? NextCursor);

/// <summary>
/// Boundary parsing of the query API. Times are ISO 8601; the range defaults
/// to the last hour. Attribute filters are <c>attr.&lt;key&gt;=&lt;value&gt;</c>
/// pairs and must all match.
/// </summary>
public static class TelemetryQueryParser
{
    public const int DefaultMaxSeries = 100;
    public const int MaxSeriesLimit = 500;
    public const int DefaultLogLimit = 100;
    public const int MaxLogLimit = 1000;
    private const string AttributePrefix = "attr.";

    public static (DateTimeOffset From, DateTimeOffset To)? Range(string? from, string? to, DateTimeOffset now, out string? error)
    {
        error = null;
        var end = now;
        if (!string.IsNullOrWhiteSpace(to) && !TryTime(to, out end))
        {
            error = "'to' must be an ISO 8601 timestamp.";
            return null;
        }
        var start = end - TimeSpan.FromHours(1);
        if (!string.IsNullOrWhiteSpace(from) && !TryTime(from, out start))
        {
            error = "'from' must be an ISO 8601 timestamp.";
            return null;
        }
        if (end <= start)
        {
            error = "'to' must be after 'from'.";
            return null;
        }
        return (start, end);
    }

    public static IReadOnlyDictionary<string, string>? Attributes(IEnumerable<KeyValuePair<string, string?>> query, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in query)
        {
            if (!name.StartsWith(AttributePrefix, StringComparison.Ordinal)) continue;
            var key = name[AttributePrefix.Length..];
            if (key.Length is 0 or > 256 || key.Any(ch => ch is '"' or '\\' || char.IsControl(ch)))
            {
                error = $"Attribute filter '{name}' has an invalid key.";
                return null;
            }
            if (result.Count >= 16)
            {
                error = "At most 16 attribute filters are allowed.";
                return null;
            }
            result[key] = value ?? "";
        }
        return result;
    }

    public static MetricAggregation? Aggregation(string? value)
        => string.IsNullOrWhiteSpace(value) ? MetricAggregation.Avg
            : Enum.TryParse<MetricAggregation>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    public static int? Limit(string? value, int fallback, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 && parsed <= max
            ? parsed : null;
    }

    private static bool TryTime(string value, out DateTimeOffset time)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
}
