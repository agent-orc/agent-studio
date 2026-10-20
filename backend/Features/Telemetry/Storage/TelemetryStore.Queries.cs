using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Telemetry;

public sealed partial class TelemetryStore
{
    public IReadOnlyList<MetricNameResponse> MetricNames(TelemetryEnvironmentKey key)
    {
        using var connection = Open(DatabasePath(key), create: false);
        if (connection is null) return [];
        using var command = Command(connection, null,
            "SELECT name, MIN(kind), MIN(unit), COUNT(*) FROM series GROUP BY name ORDER BY name LIMIT 1000");
        using var reader = command.ExecuteReader();
        var names = new List<MetricNameResponse>();
        while (reader.Read()) names.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return names;
    }

    public MetricQueryResponse QueryMetrics(TelemetryEnvironmentKey key, MetricQuery query)
    {
        var plan = query.Plan;
        var empty = new MetricQueryResponse(query.Name, DateTimeOffset.FromUnixTimeSeconds(plan.FromSeconds),
            DateTimeOffset.FromUnixTimeSeconds(plan.ToSeconds), plan.StepSeconds, plan.Resolution.ToString().ToLowerInvariant(),
            query.Aggregation.ToString().ToLowerInvariant(), [], false);
        using var connection = Open(DatabasePath(key), create: false);
        if (connection is null) return empty;

        var series = new List<(long Id, string Attributes, string Unit, string Kind)>();
        using (var command = Command(connection, null, ""))
        {
            var filters = new List<string> { "name = $name" };
            command.Parameters.AddWithValue("$name", query.Name);
            var index = 0;
            foreach (var (attribute, value) in query.Attributes)
            {
                filters.Add($"json_extract(attributes, $path{index}) = $value{index}");
                command.Parameters.AddWithValue($"$path{index}", $"$.\"{attribute}\"");
                command.Parameters.AddWithValue($"$value{index}", value);
                index++;
            }
            command.Parameters.AddWithValue("$limit", query.MaxSeries + 1);
            command.CommandText = $"SELECT id, attributes, unit, kind FROM series WHERE {string.Join(" AND ", filters)} ORDER BY id LIMIT $limit";
            using var reader = command.ExecuteReader();
            while (reader.Read()) series.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        var truncated = series.Count > query.MaxSeries;
        if (truncated) series.RemoveAt(series.Count - 1);
        if (series.Count == 0) return empty;

        var buckets = new Dictionary<long, SortedDictionary<long, BucketAggregate>>();
        using (var command = Command(connection, null, ""))
        {
            var ids = string.Join(", ", series.Select((_, index) => "$id" + index.ToString(CultureInfo.InvariantCulture)));
            for (var index = 0; index < series.Count; index++) command.Parameters.AddWithValue("$id" + index.ToString(CultureInfo.InvariantCulture), series[index].Id);
            command.Parameters.AddWithValue("$from", plan.FromSeconds);
            command.Parameters.AddWithValue("$to", plan.ToSeconds);
            var table = plan.Resolution == MetricResolution.Minute ? "metric_minute" : "metric_hour";
            command.CommandText = $"""
                SELECT series_id, bucket, count_value, sum_value, min_value, max_value, last_value, last_time
                FROM {table} WHERE series_id IN ({ids}) AND bucket >= $from AND bucket < $to
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var group = reader.GetInt64(1) / plan.StepSeconds * plan.StepSeconds;
                if (!buckets.TryGetValue(reader.GetInt64(0), out var groups)) buckets[reader.GetInt64(0)] = groups = new();
                var row = new BucketAggregate(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetDouble(5), reader.GetDouble(6), reader.GetInt64(7));
                groups[group] = groups.TryGetValue(group, out var existing) ? existing.Merge(row) : row;
            }
        }

        return empty with
        {
            Truncated = truncated,
            Series = series.Select(item => new MetricSeriesResponse(
                TelemetryAttributes.ParseJson(item.Attributes).Values, item.Unit, item.Kind,
                buckets.TryGetValue(item.Id, out var groups)
                    ? groups.Select(pair => new MetricPointResponse(DateTimeOffset.FromUnixTimeSeconds(pair.Key),
                        pair.Value.Value(query.Aggregation), pair.Value.Count)).ToList()
                    : [])).ToList()
        };
    }

    public LogPageResponse QueryLogs(TelemetryEnvironmentKey key, LogQuery query)
    {
        using var connection = Open(DatabasePath(key), create: false);
        if (connection is null) return new([], null);
        using var command = Command(connection, null, "");
        var filters = new List<string> { "time >= $from", "time < $to" };
        command.Parameters.AddWithValue("$from", query.FromUnixNano);
        command.Parameters.AddWithValue("$to", query.ToUnixNano);
        if (query.MinSeverity is { } severity)
        {
            filters.Add("severity >= $severity");
            command.Parameters.AddWithValue("$severity", severity);
        }
        if (!string.IsNullOrEmpty(query.Text))
        {
            filters.Add("body LIKE $text ESCAPE '\\'");
            command.Parameters.AddWithValue("$text", "%" + query.Text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        var index = 0;
        foreach (var (attribute, value) in query.Attributes)
        {
            filters.Add($"json_extract(attributes, $path{index}) = $value{index}");
            command.Parameters.AddWithValue($"$path{index}", $"$.\"{attribute}\"");
            command.Parameters.AddWithValue($"$value{index}", value);
            index++;
        }
        if (query.Cursor is { } cursor)
        {
            filters.Add("(time, id) < ($cursorTime, $cursorId)");
            command.Parameters.AddWithValue("$cursorTime", cursor.TimeUnixNano);
            command.Parameters.AddWithValue("$cursorId", cursor.Id);
        }
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.CommandText = $"""
            SELECT id, time, severity, severity_text, body, attributes, trace_id, span_id, event_name
            FROM logs WHERE {string.Join(" AND ", filters)} ORDER BY time DESC, id DESC LIMIT $limit
            """;
        var records = new List<(long Id, long Time, LogRecordResponse Record)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var time = reader.GetInt64(1);
                records.Add((reader.GetInt64(0), time, new LogRecordResponse(TelemetryAdmissionPolicy.FromUnixNanos(time),
                    time.ToString(CultureInfo.InvariantCulture), reader.GetInt32(2),
                    reader.GetString(3).Length > 0 ? reader.GetString(3) : TelemetrySeverity.Name(reader.GetInt32(2)),
                    reader.GetString(4), TelemetryAttributes.ParseJson(reader.GetString(5)).Values,
                    NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8))));
            }
        }
        if (records.Count <= query.Limit) return new(records.Select(item => item.Record).ToList(), null);
        records.RemoveAt(records.Count - 1);
        var last = records[^1];
        return new(records.Select(item => item.Record).ToList(), new LogCursor(last.Time, last.Id).Encode());
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private readonly record struct BucketAggregate(double Count, double Sum, double Min, double Max, double Last, long LastTime)
    {
        public BucketAggregate Merge(BucketAggregate other)
            => new(Count + other.Count, Sum + other.Sum, Math.Min(Min, other.Min), Math.Max(Max, other.Max),
                other.LastTime >= LastTime ? other.Last : Last, Math.Max(LastTime, other.LastTime));

        public double Value(MetricAggregation aggregation) => aggregation switch
        {
            MetricAggregation.Sum => Sum,
            MetricAggregation.Min => Min,
            MetricAggregation.Max => Max,
            MetricAggregation.Count => Count,
            MetricAggregation.Last => Last,
            _ => Count == 0 ? 0 : Sum / Count
        };
    }
}
