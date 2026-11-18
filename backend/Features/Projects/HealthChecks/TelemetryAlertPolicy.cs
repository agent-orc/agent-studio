using System.Globalization;
using System.Text.Json;
using AgentStudio.Telemetry;

namespace AgentStudio.Projects;

/// <summary>Golden-signal rules for a production environment. Values use shares, seconds and counts.</summary>
public sealed record TelemetryAlertDefinition(string Id, double Threshold, int WindowMinutes, int RecoveryEvaluations = 2);

public sealed record TelemetryAlertDeclarations(IReadOnlyList<TelemetryAlertDefinition> Rules, long? StorageReservationBytes)
{
    public static readonly IReadOnlyList<TelemetryAlertDefinition> Defaults =
    [
        new("5xx-rate", 0.05, 2), new("latency-p95", 2, 2),
        new("storage-usage", 0.9, 2), new("restarts", 1, 5),
    ];

    public static TelemetryAlertDeclarations Parse(JsonElement declaration, string file)
    {
        if (!declaration.TryGetProperty("telemetryAlerts", out var alerts)) return new(Defaults, null);
        if (alerts.ValueKind != JsonValueKind.Object || alerts.EnumerateObject().Any(item =>
                item.Name is not ("5xxRate" or "latencyP95" or "storageUsage" or "restarts" or "storageReservationBytes")))
            throw new JsonException($"Environment {file} telemetryAlerts must contain known rule settings.");
        long? reservation = null;
        if (alerts.TryGetProperty("storageReservationBytes", out var capacity))
        {
            if (!capacity.TryGetInt64(out var bytes) || bytes <= 0)
                throw new JsonException($"Environment {file} storageReservationBytes must be positive.");
            reservation = bytes;
        }
        var rules = new List<TelemetryAlertDefinition>();
        foreach (var rule in Defaults)
        {
            var name = rule.Id switch
            {
                "5xx-rate" => "5xxRate", "latency-p95" => "latencyP95",
                "storage-usage" => "storageUsage", _ => "restarts",
            };
            if (!alerts.TryGetProperty(name, out var value)) { rules.Add(rule); continue; }
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(item =>
                    item.Name is not ("threshold" or "windowMinutes" or "recoveryEvaluations")))
                throw new JsonException($"Environment {file} telemetryAlerts.{name} is invalid.");
            var threshold = value.TryGetProperty("threshold", out var limit) && limit.TryGetDouble(out var parsed)
                ? parsed : rule.Threshold;
            var minutes = value.TryGetProperty("windowMinutes", out var window) && window.TryGetInt32(out var m)
                ? m : rule.WindowMinutes;
            var recovery = value.TryGetProperty("recoveryEvaluations", out var count) && count.TryGetInt32(out var n)
                ? n : rule.RecoveryEvaluations;
            if (!double.IsFinite(threshold) || threshold <= 0 || threshold > (rule.Id is "5xx-rate" or "storage-usage" ? 1 : 1_000_000)
                || minutes is < 1 or > 60 || recovery is < 1 or > 20)
                throw new JsonException($"Environment {file} telemetryAlerts.{name} has an invalid threshold or window.");
            rules.Add(new(rule.Id, threshold, minutes, recovery));
        }
        return new(rules, reservation);
    }
}

public sealed record TelemetryAlertObservation(double? Value, string Detail);

public static class TelemetryAlertPolicy
{
    public static bool Firing(TelemetryAlertDefinition rule, double value) => value >= rule.Threshold;

    public static HealthIncidentStep Next(HealthCheckStreak current, bool? firing, int recoveryEvaluations)
        => firing is null ? new(current, HealthIncidentAction.None)
            : HealthIncidentPolicy.Next(current, !firing.Value, 1, recoveryEvaluations);

    public static string Describe(TelemetryAlertDefinition rule, TelemetryAlertObservation observation)
        => $"{rule.Id}: {observation.Detail} over {rule.WindowMinutes}m (threshold {rule.Threshold.ToString("G", CultureInfo.InvariantCulture)})";
}

/// <summary>Reads bounded telemetry windows. A missing series is an unknown observation, never a zero.</summary>
public sealed class TelemetryAlertReader(TelemetryStore store)
{
    public TelemetryAlertObservation Read(string project, string environment, TelemetryAlertDefinition rule,
        long? reservationBytes, DateTimeOffset now)
    {
        var key = new TelemetryEnvironmentKey(project, environment);
        var from = now.AddMinutes(-rule.WindowMinutes);
        MetricQueryResponse Query(string name, MetricAggregation aggregation)
            => store.QueryMetrics(key, new MetricQuery(name, new Dictionary<string, string>(),
                new MetricQueryPlan(MetricResolution.Minute, 60, from.ToUnixTimeSeconds(), now.ToUnixTimeSeconds() + 60),
                aggregation, 500));
        static IEnumerable<(MetricSeriesResponse Series, MetricPointResponse Point)> Points(MetricQueryResponse response)
            => response.Series.SelectMany(series => series.Points.Select(point => (series, point)));
        static double? Latest(MetricQueryResponse response)
            => Points(response).OrderByDescending(item => item.Point.Time).Select(item => (double?)item.Point.Value).FirstOrDefault();
        switch (rule.Id)
        {
            case "5xx-rate":
            {
                var response = Query("http.server.request.duration", MetricAggregation.Count);
                var points = Points(response).Where(item => Status(item.Series.Attributes) is not null).ToArray();
                var total = points.Sum(item => item.Point.Value);
                if (total <= 0 || response.Truncated) return new(null, "not observed");
                var failed = points.Where(item => Status(item.Series.Attributes) is >= 500 and <= 599)
                    .Sum(item => item.Point.Value);
                var share = failed / total;
                return new(share, $"5xx share {share:P1} ({failed:G}/{total:G} requests)");
            }
            case "latency-p95":
            {
                var response = Query("http.server.request.duration.p95", MetricAggregation.Max);
                var values = Points(response).Select(item => Seconds(item.Point.Value, item.Series.Unit)).ToArray();
                // Exporters without a percentile series still supply histogram maxima. This is a
                // conservative high-water signal until an exact percentile is exported.
                if (values.Length == 0)
                    values = Points(Query("http.server.request.duration", MetricAggregation.Max))
                        .Select(item => Seconds(item.Point.Value, item.Series.Unit)).ToArray();
                if (values.Length == 0) return new(null, "not observed");
                var p95 = values.Max();
                return new(p95, $"latency p95/high-water {p95:G3}s");
            }
            case "storage-usage":
            {
                var usage = Query("system.filesystem.usage", MetricAggregation.Last);
                var capacity = Latest(Query("system.filesystem.capacity", MetricAggregation.Last));
                var volumes = usage.Series.Select(series =>
                    (Series: series, Point: series.Points.MaxBy(point => point.Time)))
                    .Where(item => item.Point is not null)
                    .GroupBy(item => item.Series.Attributes.GetValueOrDefault("mountpoint")
                        ?? item.Series.Attributes.GetValueOrDefault("mount_point")
                        ?? item.Series.Attributes.GetValueOrDefault("device") ?? "default", StringComparer.Ordinal);
                var shares = new List<(double Share, double Used, double Reserved)>();
                foreach (var volume in volumes)
                {
                    if (!volume.Any(item => item.Series.Attributes.GetValueOrDefault("state") is "used" or null))
                        continue;
                    var used = volume.Where(item => item.Series.Attributes.GetValueOrDefault("state") is "used" or null)
                        .Sum(item => item.Point!.Value);
                    var available = volume.Where(item => item.Series.Attributes.GetValueOrDefault("state") is "free" or "reserved")
                        .Sum(item => item.Point!.Value);
                    var reserved = reservationBytes is > 0 ? (double)reservationBytes.Value
                        : available > 0 ? used + available : capacity;
                    if (used >= 0 && reserved is > 0) shares.Add((used / reserved.Value, used, reserved.Value));
                }
                if (shares.Count == 0)
                {
                    var used = Latest(Query("container.filesystem.usage", MetricAggregation.Last));
                    var reserved = reservationBytes is > 0 ? (double)reservationBytes.Value : capacity;
                    if (used is null || reserved is null or <= 0) return new(null, "not observed");
                    shares.Add((used.Value / reserved.Value, used.Value, reserved.Value));
                }
                var worst = shares.MaxBy(item => item.Share);
                return new(worst.Share, $"storage usage {worst.Share:P1} ({worst.Used:G} of {worst.Reserved:G} bytes)");
            }
            case "restarts":
            {
                var response = Query("process.restart.count", MetricAggregation.Sum);
                var points = Points(response).ToArray();
                if (points.Length == 0) return new(null, "not observed");
                var count = points.Sum(item => item.Point.Value);
                return new(count, $"restart count {count:G}");
            }
            default: return new(null, "not observed");
        }
    }

    private static int? Status(IReadOnlyDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue("http.response.status_code", out var value)
            && !attributes.TryGetValue("http.status_code", out value)) return null;
        return int.TryParse(value, out var status) ? status : null;
    }

    private static double Seconds(double value, string unit)
        => unit is "ms" or "millisecond" ? value / 1000 : value;
}
