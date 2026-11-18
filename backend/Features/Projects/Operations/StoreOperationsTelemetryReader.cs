using System.Globalization;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Telemetry;

namespace AgentStudio.Projects;

/// <summary>Projects each environment's masked OTLP store into the Operations view.</summary>
public sealed class StoreOperationsTelemetryReader(TelemetryStore store, TelemetryOptions options) : IOperationsTelemetryReader
{
    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();
    private static readonly string[] DurationNames = ["http.server.request.duration", "http.server.duration"];
    private static readonly string[] RequestNames = ["http.server.request.count", "http.server.requests"];
    private static readonly string[] CpuNames = ["process.cpu.utilization", "container.cpu.utilization", "system.cpu.utilization", "process.cpu.time", "container.cpu.usage"];
    /// <summary>CPU conventions that report cumulative CPU seconds instead of cores.</summary>
    private static readonly HashSet<string> CpuTimeNames = new(["process.cpu.time", "container.cpu.usage"], StringComparer.Ordinal);
    private static readonly string[] MemoryNames = ["process.memory.usage", "container.memory.usage", "system.memory.usage"];
    private static readonly string[] DiskNames = ["system.filesystem.usage", "container.filesystem.usage"];
    /// <summary>The release id Studio's deploy injects first (M5), then the sender's own conventions.</summary>
    private static readonly string[] ReleaseAttributes = [DeployTelemetryVocabulary.ReleaseAttribute, "service.version", "release.id"];
    private static readonly string[] NetworkNames = ["system.network.io", "container.network.io", "process.network.io"];

    public OperationsMetrics? ReadMetrics(string project, string environmentId, DateTimeOffset since, DateTimeOffset until)
    {
        try { return Read(project, environmentId, since, until); }
        catch (Exception exception) when (exception is not OperationsTelemetryReadException)
        { throw new OperationsTelemetryReadException(exception); }
    }

    private OperationsMetrics Read(string project, string environmentId, DateTimeOffset since, DateTimeOffset until)
    {
        var key = new TelemetryEnvironmentKey(project, environmentId);
        var names = store.MetricNames(key).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var plan = MetricBucketPolicy.Plan(until, since, until, 3600, options, out _)!;
        var buckets = new SortedDictionary<DateTimeOffset, Sample>();
        Sample At(DateTimeOffset time)
        {
            if (!buckets.TryGetValue(time, out var sample)) buckets[time] = sample = new Sample();
            return sample;
        }
        MetricQueryResponse Query(string name, MetricAggregation aggregation)
            => store.QueryMetrics(key, new MetricQuery(name, NoAttributes, plan, aggregation, 500));

        var duration = DurationNames.FirstOrDefault(names.Contains);
        var requestName = duration ?? RequestNames.FirstOrDefault(names.Contains);
        var durationSum = 0d;
        var durationCount = 0d;
        var durationMax = double.MinValue;
        var durationBuckets = new Dictionary<double, double>();
        var hourlyBuckets = new Dictionary<DateTimeOffset, Dictionary<double, double>>();
        var endpoints = new Dictionary<string, long>(StringComparer.Ordinal);
        if (requestName is not null)
        {
            var count = Query(requestName, duration is null ? MetricAggregation.Sum : MetricAggregation.Count);
            foreach (var series in count.Series)
            {
                var status = Status(series.Attributes);
                var route = Route(series.Attributes);
                foreach (var point in series.Points)
                {
                    var sample = At(point.Time);
                    sample.Requests = (sample.Requests ?? 0) + point.Value;
                    if (status >= 500) sample.Errors += point.Value;
                    if (route is not null) endpoints[route] = endpoints.GetValueOrDefault(route) + (long)Math.Round(point.Value);
                }
            }
            if (duration is not null)
            {
                foreach (var series in Query(duration, MetricAggregation.Sum).Series)
                    foreach (var point in series.Points) durationSum += Milliseconds(point.Value, series.Unit);
                foreach (var series in Query(duration, MetricAggregation.Max).Series)
                    foreach (var point in series.Points)
                    {
                        var value = Milliseconds(point.Value, series.Unit);
                        durationMax = Math.Max(durationMax, value);
                        var sample = At(point.Time);
                        sample.ResponseP95 = Math.Max(sample.ResponseP95 ?? double.MinValue, value);
                    }
                durationCount = buckets.Values.Sum(item => item.Requests ?? 0);
                if (names.Contains("http.server.request.duration.bucket"))
                {
                    foreach (var series in Query("http.server.request.duration.bucket", MetricAggregation.Sum).Series)
                    {
                        var boundText = Attribute(series.Attributes, "otel.histogram.bound");
                        var bound = boundText == "+Inf" ? double.PositiveInfinity
                            : double.TryParse(boundText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                                ? Milliseconds(parsed, series.Unit) : double.NaN;
                        if (double.IsNaN(bound)) continue;
                        foreach (var point in series.Points)
                        {
                            durationBuckets[bound] = durationBuckets.GetValueOrDefault(bound) + point.Value;
                            if (!hourlyBuckets.TryGetValue(point.Time, out var hour))
                                hourlyBuckets[point.Time] = hour = new();
                            hour[bound] = hour.GetValueOrDefault(bound) + point.Value;
                        }
                    }
                }
            }
        }

        var cpuName = First(names, CpuNames);
        if (cpuName is not null && CpuTimeNames.Contains(cpuName)) AddCpuCores(cpuName);
        else AddGauge(cpuName, (sample, value) => sample.Cpu = (sample.Cpu ?? 0) + value);
        AddGauge(First(names, MemoryNames), (sample, value) => sample.Memory = (sample.Memory ?? 0) + value);
        AddGauge(First(names, DiskNames), (sample, value) => sample.Disk = (sample.Disk ?? 0) + value);
        AddGauge(First(names, NetworkNames), (sample, value) => sample.Network = (sample.Network ?? 0) + value);

        // CPU time is a cumulative counter of CPU seconds per process and state. The store
        // turns cumulative sums into per-point deltas when it writes them (restarts included),
        // so a bucket's sum is the CPU seconds spent inside it. Dividing by the wall time the
        // bucket covers gives the average cores. The latest bucket is still running and covers
        // only the time since it began, at least a minute because its first delta spans the
        // export interval before it. A series that is not a sum (a gauge carrying the running
        // total) holds no deltas; summing it would count the same seconds with every export.
        void AddCpuCores(string name)
        {
            var untilSeconds = until.ToUnixTimeSeconds();
            var minimumSeconds = MetricBucketPolicy.NativeSeconds(MetricResolution.Minute);
            foreach (var series in Query(name, MetricAggregation.Sum).Series)
            {
                if (!string.Equals(series.Kind, nameof(MetricKind.Sum), StringComparison.Ordinal)) continue;
                foreach (var point in series.Points)
                {
                    var covered = Math.Clamp(untilSeconds - point.Time.ToUnixTimeSeconds(), minimumSeconds, plan.StepSeconds);
                    var sample = At(point.Time);
                    sample.Cpu = (sample.Cpu ?? 0) + point.Value / covered;
                }
            }
        }

        void AddGauge(string? name, Action<Sample, double> add)
        {
            if (name is null) return;
            var aggregation = name is "system.network.io" or "container.network.io" or "process.network.io"
                ? MetricAggregation.Sum : MetricAggregation.Last;
            foreach (var series in Query(name, aggregation).Series)
            {
                var state = Attribute(series.Attributes, "state");
                if (name is "system.filesystem.usage" or "system.memory.usage"
                    && state is not null && state != "used") continue;
                if (name == "system.cpu.utilization" && state == "idle") continue;
                foreach (var point in series.Points) add(At(point.Time), point.Value);
            }
        }

        var observed = buckets.Values.Sum(item => item.Requests ?? 0);
        var errors = buckets.Values.Sum(item => item.Errors);
        var latestCpu = buckets.LastOrDefault(item => item.Value.Cpu is not null).Value?.Cpu;
        var latestMemory = buckets.LastOrDefault(item => item.Value.Memory is not null).Value?.Memory;
        var latestDisk = buckets.LastOrDefault(item => item.Value.Disk is not null).Value?.Disk;
        var network = buckets.Values.Sum(item => item.Network ?? 0);
        var samples = buckets.Select(item => new OperationsMetricSample(item.Key,
            hourlyBuckets.TryGetValue(item.Key, out var hour)
                ? Quantile(hour, 0.95, item.Value.ResponseP95) : item.Value.ResponseP95,
            item.Value.Requests > 0 ? item.Value.Errors / item.Value.Requests : null,
            item.Value.Requests is null ? null : (long)Math.Round(item.Value.Requests.Value),
            item.Value.Cpu, ToLong(item.Value.Memory), ToLong(item.Value.Disk))).ToArray();
        double? maximum = durationMax == double.MinValue ? null : durationMax;
        return new(durationCount > 0 ? Quantile(durationBuckets, 0.5, durationSum / durationCount) : null,
            Quantile(durationBuckets, 0.95, maximum),
            observed > 0 ? errors / observed : null,
            null, observed > 0 ? (long)Math.Round(observed) : null, null, null,
            latestCpu, null, ToLong(latestMemory), null, ToLong(latestDisk), null,
            network > 0 ? (long)Math.Round(network) : null,
            endpoints.OrderByDescending(item => item.Value).Take(10)
                .Select(item => new OperationsEndpointCount(item.Key, item.Value)).ToArray(), [], samples);
    }

    public OperationsLogPage? SearchLogs(OperationsLogQuery query)
    {
        try { return Search(query); }
        catch (Exception exception) when (exception is not OperationsTelemetryReadException)
        { throw new OperationsTelemetryReadException(exception); }
    }

    private OperationsLogPage Search(OperationsLogQuery query)
    {
        var key = new TelemetryEnvironmentKey(query.Project, query.EnvironmentId);
        var lines = new List<OperationsLogLine>();
        var cursor = query.Cursor;
        do
        {
            var page = store.QueryLogs(key, new LogQuery(TelemetryAdmissionPolicy.UnixNanos(query.Since),
                TelemetryAdmissionPolicy.UnixNanos(query.Until), TelemetrySeverity.Parse(query.Level), query.Text,
                NoAttributes, query.Limit - lines.Count, LogCursor.Decode(cursor)));
            foreach (var record in page.Records)
            {
                if (query.Level is not null && !string.Equals(TelemetrySeverity.Name(record.SeverityNumber),
                        TelemetrySeverity.Name(TelemetrySeverity.Parse(query.Level)!.Value), StringComparison.Ordinal)) continue;
                var release = ReleaseAttributes.Select(name => Attribute(record.Attributes, name)).FirstOrDefault(value => value is not null);
                if (query.ReleaseId is not null
                    && !ReleaseAttributes.Any(name => Attribute(record.Attributes, name) == query.ReleaseId)) continue;
                lines.Add(new(record.Time, TelemetrySeverity.Name(record.SeverityNumber), record.Body, release,
                    record.TraceId));
            }
            cursor = page.NextCursor;
        } while (cursor is not null && lines.Count < query.Limit);
        return new(lines, cursor);
    }

    private static string? First(HashSet<string> names, IEnumerable<string> candidates) => candidates.FirstOrDefault(names.Contains);
    private static string? Attribute(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) ? value : null;
    private static int Status(IReadOnlyDictionary<string, string> attributes)
        => int.TryParse(Attribute(attributes, "http.response.status_code") ?? Attribute(attributes, "http.status_code"), out var value) ? value : 0;
    private static string? Route(IReadOnlyDictionary<string, string> attributes)
    {
        var route = Attribute(attributes, "http.route");
        return route is { Length: > 0 } && route.StartsWith('/') ? route.Split('?', 2)[0] : null;
    }
    private static double Milliseconds(double value, string unit) => unit == "s" ? value * 1000 : unit == "ms" ? value : value;
    private static double? Quantile(Dictionary<double, double> buckets, double fraction, double? fallback)
    {
        var total = buckets.Values.Sum();
        if (total <= 0) return fallback;
        var target = total * fraction;
        var previous = 0d;
        var lower = 0d;
        foreach (var (upper, count) in buckets.OrderBy(item => item.Key))
        {
            if (previous + count >= target)
                return double.IsPositiveInfinity(upper) ? fallback ?? lower
                    : count <= 0 ? upper : lower + (upper - lower) * (target - previous) / count;
            previous += count;
            lower = upper;
        }
        return fallback;
    }
    private static long? ToLong(double? value) => value is null ? null : (long)Math.Round(value.Value);

    private sealed class Sample
    {
        public double? Requests;
        public double? ResponseP95;
        public double Errors;
        public double? Cpu;
        public double? Memory;
        public double? Disk;
        public double? Network;
    }
}
