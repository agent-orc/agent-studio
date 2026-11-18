using System.Globalization;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Telemetry;

/// <summary>
/// Reads the Operations view (monitoring, load and log search) from the built-in
/// OTLP store (M5). It understands the OpenTelemetry semantic conventions the
/// project templates emit: the HTTP server duration histogram for requests,
/// errors and endpoints, <c>process.cpu.time</c> for CPU and
/// <c>process.memory.usage</c> for memory. An environment without a store has
/// no telemetry (null); a measurement without data stays null.
/// <para>The store keeps count, sum, min and max per bucket, not histogram
/// buckets, so response percentiles stay null. Sessions, active users,
/// restarts, disk and network have no source metric yet and stay null too.</para>
/// </summary>
public sealed class TelemetryOperationsReader(TelemetryStore store, TelemetryOptions options, TimeProvider clock)
    : IOperationsTelemetryReader
{
    /// <summary>Stable HTTP semantic conventions first (seconds), then the older names (milliseconds).</summary>
    private static readonly (string Name, string StatusAttribute)[] HttpDurations =
    [
        ("http.server.request.duration", "http.response.status_code"),
        ("http.server.duration", "http.status_code"),
    ];
    public const string CpuTimeMetric = "process.cpu.time";
    public const string MemoryMetric = "process.memory.usage";
    public const string RouteAttribute = "http.route";
    private const long StepSeconds = 3600;
    private const int TopEndpointCount = 10;

    public OperationsMetrics? ReadMetrics(string project, string environmentId, DateTimeOffset since, DateTimeOffset until)
    {
        if (Key(project, environmentId) is not { } key) return null;
        var plan = MetricBucketPolicy.Plan(clock.GetUtcNow(), since, until, StepSeconds, options, out _);
        if (plan is null) return null;

        var requests = new SortedDictionary<long, (double Requests, double Errors)>();
        var routes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, statusAttribute) in HttpDurations)
        {
            var http = store.QueryMetrics(key, new MetricQuery(name, Empty, plan, MetricAggregation.Sum, TelemetryQueryParser.MaxSeriesLimit));
            if (http.Series.Count == 0) continue;
            foreach (var series in http.Series)
            {
                var failed = series.Attributes.TryGetValue(statusAttribute, out var status)
                    && int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code >= 500;
                var route = series.Attributes.GetValueOrDefault(RouteAttribute);
                foreach (var point in series.Points)
                {
                    var at = point.Time.ToUnixTimeSeconds();
                    var (total, errors) = requests.GetValueOrDefault(at);
                    requests[at] = (total + point.Count, errors + (failed ? point.Count : 0));
                    if (!string.IsNullOrEmpty(route)) routes[route] = routes.GetValueOrDefault(route) + point.Count;
                }
            }
            // One naming convention per environment; the stable one wins when both exist.
            break;
        }

        var cpu = new SortedDictionary<long, double>();
        foreach (var series in store.QueryMetrics(key, new MetricQuery(CpuTimeMetric, Empty, plan, MetricAggregation.Sum,
                     TelemetryQueryParser.MaxSeriesLimit)).Series)
            foreach (var point in series.Points)
                cpu[point.Time.ToUnixTimeSeconds()] = cpu.GetValueOrDefault(point.Time.ToUnixTimeSeconds()) + point.Value / plan.StepSeconds;

        // Several processes report one series each; the environment uses their sum.
        var memory = new SortedDictionary<long, double>();
        foreach (var series in store.QueryMetrics(key, new MetricQuery(MemoryMetric, Empty, plan, MetricAggregation.Avg,
                     TelemetryQueryParser.MaxSeriesLimit)).Series)
            foreach (var point in series.Points)
                memory[point.Time.ToUnixTimeSeconds()] = memory.GetValueOrDefault(point.Time.ToUnixTimeSeconds()) + point.Value;

        var times = requests.Keys.Concat(cpu.Keys).Concat(memory.Keys).Distinct().Order().ToArray();
        var samples = times.Select(at =>
        {
            var hasRequests = requests.TryGetValue(at, out var bucket);
            return new OperationsMetricSample(DateTimeOffset.FromUnixTimeSeconds(at), null,
                hasRequests && bucket.Requests > 0 ? bucket.Errors / bucket.Requests : null,
                hasRequests ? (long)Math.Round(bucket.Requests) : null,
                cpu.TryGetValue(at, out var cores) ? cores : null,
                memory.TryGetValue(at, out var bytes) ? (long)Math.Round(bytes) : null, null);
        }).ToArray();

        var totalRequests = requests.Count == 0 ? (double?)null : requests.Values.Sum(item => item.Requests);
        var totalErrors = requests.Values.Sum(item => item.Errors);
        // Load is the latest hour that reported it, not an average over the month.
        return new OperationsMetrics(null, null,
            totalRequests > 0 ? totalErrors / totalRequests : null, null,
            totalRequests is { } count ? (long)Math.Round(count) : null, null, null,
            cpu.Count == 0 ? null : cpu.Last().Value, null,
            memory.Count == 0 ? null : (long)Math.Round(memory.Last().Value), null, null, null, null,
            routes.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(TopEndpointCount).Select(item => new OperationsEndpointCount(item.Key, (long)Math.Round(item.Value))).ToArray(),
            [], samples);
    }

    public IReadOnlyList<OperationsLogLine>? SearchLogs(OperationsLogQuery query)
    {
        if (Key(query.Project, query.EnvironmentId) is not { } key) return null;
        int? severity = null;
        if (query.Level is not null && (severity = TelemetrySeverity.Parse(query.Level)) is null) return [];
        var attributes = query.ReleaseId is null ? Empty
            : new Dictionary<string, string>(StringComparer.Ordinal) { [DeployTelemetryVocabulary.ReleaseAttribute] = query.ReleaseId };
        var page = store.QueryLogs(key, new LogQuery(TelemetryAdmissionPolicy.UnixNanos(query.Since),
            TelemetryAdmissionPolicy.UnixNanos(query.Until), severity, query.Text, attributes, query.Limit, null));
        return page.Records.Select(record => new OperationsLogLine(record.Time,
            (record.SeverityNumber is >= 1 and <= 24 ? TelemetrySeverity.Name(record.SeverityNumber) : record.Severity).ToLowerInvariant(),
            record.Body, record.Attributes.GetValueOrDefault(DeployTelemetryVocabulary.ReleaseAttribute))).ToArray();
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private TelemetryEnvironmentKey? Key(string project, string environmentId)
    {
        if (!TelemetryStore.IsValidEnvironmentId(environmentId)) return null;
        var key = new TelemetryEnvironmentKey(project, environmentId);
        return store.HasStore(key) ? key : null;
    }
}
