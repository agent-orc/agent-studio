using System.Text;
using AgentStudio.Projects;
using AgentStudio.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperationsTelemetryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operations-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Otlp_json_is_visible_in_the_correct_environment()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance);
        var time = TelemetryAdmissionPolicy.UnixNanos(_now.AddMinutes(-5)).ToString();
        var metrics = """
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"http.server.request.duration","unit":"s","histogram":{"aggregationTemporality":1,"dataPoints":[
                {"timeUnixNano":"__TIME__","count":"4","sum":0.4,"min":0.01,"max":0.3,"bucketCounts":["2","1","1"],"explicitBounds":[0.02,0.1],"attributes":[{"key":"http.response.status_code","value":{"stringValue":"500"}},{"key":"http.route","value":{"stringValue":"/orders"}}]}]}},
              {"name":"system.filesystem.usage","unit":"By","gauge":{"dataPoints":[{"timeUnixNano":"__TIME__","asDouble":4096}]}}
            ]}]}]}
            """.Replace("__TIME__", time);
        var logs = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.version","value":{"stringValue":"v2"}}]},"scopeLogs":[{"logRecords":[
              {"timeUnixNano":"__TIME__","severityNumber":17,"body":{"stringValue":"upstream timeout"}}
            ]}]}]}
            """.Replace("__TIME__", time);
        var factory = new OtlpRecordFactory(OtlpLimits.From(options), TelemetryAdmissionPolicy.UnixNanos(_now));
        var decodedMetrics = OtlpJsonDecoder.DecodeMetrics(Encoding.UTF8.GetBytes(metrics), factory).Items;
        var decodedLogs = OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(logs),
            new OtlpRecordFactory(OtlpLimits.From(options), TelemetryAdmissionPolicy.UnixNanos(_now))).Items;
        store.Write(new("shop", "prod"), decodedMetrics, decodedLogs, _now);

        var reader = new StoreOperationsTelemetryReader(store, options);
        var result = reader.ReadMetrics("shop", "prod", _now.AddHours(-1), _now);
        Assert.NotNull(result);
        Assert.Equal(4, result.Requests);
        Assert.Equal(1, result.ErrorRate);
        Assert.Equal(4096, result.DiskBytes);
        Assert.Equal(20, result.ResponseP50Ms);
        Assert.Equal(300, result.ResponseP95Ms);
        Assert.Null(reader.ReadMetrics("shop", "stage", _now.AddHours(-1), _now)?.Requests);
        var page = reader.SearchLogs(new("shop", "prod", _now.AddHours(-1), _now, "ERROR", "v2", "timeout", 100, null));
        Assert.Equal("upstream timeout", Assert.Single(page!.Lines).Message);
    }

    [Fact]
    public void Log_search_filters_release_and_level_and_pages_past_100()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance);
        var key = new TelemetryEnvironmentKey("shop", "prod");
        var attributes = TelemetryAttributes.From([KeyValuePair.Create("release.id", "v2")]);
        var now = TelemetryAdmissionPolicy.UnixNanos(_now);
        var logs = Enumerable.Range(0, 125).Select(index => new TelemetryLogRecord(
            now - (index + 1) * MetricBucketPolicy.NanosPerSecond, 17, "ERROR", "disk full " + index,
            attributes, null, null, null)).ToArray();
        store.Write(key, [], logs, _now);
        var reader = new StoreOperationsTelemetryReader(store, options);
        OperationsLogQuery Query(string? cursor, string? release = "v2")
            => new("shop", "prod", _now.AddHours(-1), _now, "error", release, "disk full", 100, cursor);
        var first = reader.SearchLogs(Query(null))!;
        Assert.Equal(100, first.Lines.Count);
        Assert.NotNull(first.NextCursor);
        var second = reader.SearchLogs(Query(first.NextCursor))!;
        Assert.Equal(25, second.Lines.Count);
        Assert.Null(second.NextCursor);
        Assert.True(first.Lines[^1].At > second.Lines[0].At);
        Assert.Empty(reader.SearchLogs(Query(null, "v3"))!.Lines);
    }

    [Fact]
    public void Failed_store_read_is_named_instead_of_unavailable()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance);
        var database = store.DatabasePath(new("shop", "prod"));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllText(database, "not a SQLite database");
        var reader = new StoreOperationsTelemetryReader(store, options);
        var error = Assert.Throws<OperationsTelemetryReadException>(() =>
            reader.ReadMetrics("shop", "prod", _now.AddHours(-1), _now));
        Assert.Equal("Operations telemetry store read failed.", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
