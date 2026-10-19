using AgentStudio.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Retention, aggregation, and query correctness of the built-in telemetry store (IP-15).</summary>
public sealed class TelemetryStoreTests : IDisposable
{
    private const long Second = MetricBucketPolicy.NanosPerSecond;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "telemetry-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly TelemetryEnvironmentKey _stage = new("Shop", "stage");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Gauge_points_aggregate_into_minute_and_hour_buckets()
    {
        var store = Store();
        var t0 = Nanos(_clock.GetUtcNow());
        store.Write(_stage, [Gauge("cpu", 1, t0 + 10 * Second), Gauge("cpu", 3, t0 + 20 * Second), Gauge("cpu", 5, t0 + 70 * Second)], [], _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        var perMinute = Query(store, "cpu", step: 60);
        Assert.Equal("minute", perMinute.Resolution);
        Assert.Equal([(0L, 2d, 2d), (60L, 5d, 1d)], Points(perMinute));

        var twoMinutesMax = Query(store, "cpu", step: 120, aggregation: MetricAggregation.Max);
        Assert.Equal([(0L, 5d, 3d)], Points(twoMinutesMax));

        var last = Query(store, "cpu", step: 120, aggregation: MetricAggregation.Last);
        Assert.Equal(5d, Assert.Single(Points(last)).Value);

        var hourly = Query(store, "cpu", step: 3600, aggregation: MetricAggregation.Sum);
        Assert.Equal("hour", hourly.Resolution);
        Assert.Equal([(0L, 9d, 3d)], Points(hourly));
    }

    [Fact]
    public void Cumulative_histograms_are_stored_as_deltas_across_writes()
    {
        var store = Store();
        var t0 = Nanos(_clock.GetUtcNow());
        var start = t0 - 600 * Second;
        store.Write(_stage, [Histogram(100, 10, t0, start)], [], _clock.GetUtcNow());
        // A fresh store instance reads the cumulative baseline back from the database.
        store = Store();
        store.Write(_stage, [Histogram(140, 14, t0 + 60 * Second, start)], [], _clock.GetUtcNow());
        // The process restarted: a new start time begins a new cumulative run.
        store.Write(_stage, [Histogram(30, 2, t0 + 130 * Second, t0 + 90 * Second)], [], _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        var averages = Query(store, "latency", step: 60);
        Assert.Equal([(60L, 10d, 4d), (120L, 15d, 2d)], Points(averages));
    }

    [Fact]
    public void Retention_drops_minute_buckets_after_14_days_and_hour_buckets_after_90_days()
    {
        var store = Store();
        var written = _clock.GetUtcNow();
        store.Write(_stage, [Gauge("cpu", 4, Nanos(written))], [], written);

        _clock.Advance(TimeSpan.FromDays(15));
        var afterTwoWeeks = store.EnforceRetention(_stage, _clock.GetUtcNow());
        Assert.Equal((1, 0, 0), (afterTwoWeeks.MinuteBuckets, afterTwoWeeks.HourBuckets, afterTwoWeeks.Series));
        var plan = MetricBucketPolicy.Plan(_clock.GetUtcNow(), written.AddHours(-1), written.AddHours(2), 60, new TelemetryOptions(), out _)!;
        var hourly = store.QueryMetrics(_stage, new MetricQuery("cpu", new Dictionary<string, string>(), plan, MetricAggregation.Avg, 10));
        Assert.Equal("hour", hourly.Resolution);
        Assert.Equal(4d, Assert.Single(Assert.Single(hourly.Series).Points).Value);

        _clock.Advance(TimeSpan.FromDays(76));
        var afterNinetyDays = store.EnforceRetention(_stage, _clock.GetUtcNow());
        Assert.Equal((0, 1, 1), (afterNinetyDays.MinuteBuckets, afterNinetyDays.HourBuckets, afterNinetyDays.Series));
        Assert.Empty(store.MetricNames(_stage));
    }

    [Fact]
    public void Points_older_than_the_minute_retention_only_reach_the_hour_buckets()
    {
        var store = Store();
        var old = _clock.GetUtcNow().AddDays(-20);
        store.Write(_stage, [Gauge("cpu", 2, Nanos(old))], [], _clock.GetUtcNow());

        var minute = MetricBucketPolicy.Plan(_clock.GetUtcNow(), _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow(), 60, new TelemetryOptions(), out _)!;
        var hourPlan = new MetricQueryPlan(MetricResolution.Hour, 3600, old.AddHours(-1).ToUnixTimeSeconds(), old.AddHours(1).ToUnixTimeSeconds());
        var minutePlan = minute with { FromSeconds = old.AddHours(-1).ToUnixTimeSeconds(), ToSeconds = old.AddHours(1).ToUnixTimeSeconds() };

        Assert.Empty(Assert.Single(store.QueryMetrics(_stage, new("cpu", new Dictionary<string, string>(), minutePlan, MetricAggregation.Avg, 10)).Series).Points);
        Assert.Single(Assert.Single(store.QueryMetrics(_stage, new("cpu", new Dictionary<string, string>(), hourPlan, MetricAggregation.Avg, 10)).Series).Points);
    }

    [Fact]
    public void Logs_expire_after_seven_days()
    {
        var store = Store();
        store.Write(_stage, [], [Log("first", 9, _clock.GetUtcNow())], _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromDays(6));
        store.Write(_stage, [], [Log("second", 9, _clock.GetUtcNow())], _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, store.EnforceRetention(_stage, _clock.GetUtcNow()).Logs);

        var page = Logs(store, from: _clock.GetUtcNow().AddDays(-30));
        Assert.Equal(["second"], page.Records.Select(item => item.Body));
    }

    [Fact]
    public void Log_size_cap_removes_the_oldest_records_down_to_ninety_percent()
    {
        var store = Store(new TelemetryOptions { StoreRoot = _root, LogMaxBytesPerEnvironment = 4096 });
        var logs = Enumerable.Range(0, 100)
            .Select(index => Log($"record {index:D3} " + new string('x', 100), 9, _clock.GetUtcNow().AddSeconds(index)))
            .ToList();
        var perRecord = TelemetryStore.LogBytes(logs[0]);

        var evicted = 0;
        foreach (var chunk in logs.Chunk(10)) evicted += store.Write(_stage, [], chunk, _clock.GetUtcNow()).LogsEvictedForSize;

        var kept = Logs(store, from: _clock.GetUtcNow().AddMinutes(-1), to: _clock.GetUtcNow().AddMinutes(5), limit: 1000).Records;
        Assert.Equal(100, kept.Count + evicted);
        Assert.True(kept.Count * perRecord <= 4096, $"{kept.Count} records of {perRecord} bytes exceed the cap");
        Assert.True(kept.Count * perRecord > 4096 / 2, "eviction removed far more than needed");
        Assert.Equal("record 099", kept[0].Body[..10]);
        Assert.Equal($"record {100 - kept.Count:D3}", kept[^1].Body[..10]);
    }

    [Fact]
    public void Series_limit_rejects_points_of_new_series_only()
    {
        var store = Store(new TelemetryOptions { StoreRoot = _root, MaxSeriesPerEnvironment = 2 });
        var now = Nanos(_clock.GetUtcNow());

        var result = store.Write(_stage, [Gauge("a", 1, now), Gauge("b", 1, now), Gauge("c", 1, now), Gauge("a", 2, now + Second)], [], _clock.GetUtcNow());

        Assert.Equal((3, 1), (result.StoredPoints, result.RejectedSeriesPoints));
        Assert.Equal(["a", "b"], store.MetricNames(_stage).Select(item => item.Name));
    }

    [Fact]
    public void Metric_query_filters_by_attributes_and_lists_names()
    {
        var store = Store();
        var now = Nanos(_clock.GetUtcNow());
        store.Write(_stage, [
            Gauge("requests", 10, now, ("http.route", "/cart"), ("service.name", "shop")),
            Gauge("requests", 20, now, ("http.route", "/pay"), ("service.name", "shop")),
            Gauge("memory", 1, now)], [], _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));

        var filtered = Query(store, "requests", step: 60, attributes: new() { ["http.route"] = "/pay" });
        var series = Assert.Single(filtered.Series);
        Assert.Equal("/pay", series.Attributes["http.route"]);
        Assert.Equal(20d, Assert.Single(series.Points).Value);
        Assert.Equal(2, Query(store, "requests", step: 60, attributes: new() { ["service.name"] = "shop" }).Series.Count);
        Assert.Empty(Query(store, "requests", step: 60, attributes: new() { ["http.route"] = "/none" }).Series);

        var truncated = store.QueryMetrics(_stage, new MetricQuery("requests", new Dictionary<string, string>(),
            PlanFor(60), MetricAggregation.Avg, MaxSeries: 1));
        Assert.True(truncated.Truncated);
        Assert.Single(truncated.Series);

        Assert.Equal([("memory", 1), ("requests", 2)], store.MetricNames(_stage).Select(item => (item.Name, item.SeriesCount)));
    }

    [Fact]
    public void Log_query_filters_by_severity_text_and_attributes_and_pages_without_gaps()
    {
        var store = Store();
        var at = _clock.GetUtcNow();
        var logs = new List<TelemetryLogRecord>
        {
            Log("checkout started", 9, at, ("order.id", "1")),
            Log("Payment FAILED for order", 17, at.AddSeconds(1), ("order.id", "1")),
            Log("100% done_ok", 9, at.AddSeconds(2), ("order.id", "2")),
            Log("disk low", 13, at.AddSeconds(3), ("order.id", "2")),
        };
        // Records with the same timestamp still page in a stable order.
        logs.AddRange(Enumerable.Range(0, 5).Select(index => Log($"tick {index}", 5, at.AddSeconds(4))));
        store.Write(_stage, [], logs, at);

        Assert.Equal(["disk low", "Payment FAILED for order"], Logs(store, minSeverity: 13).Records.Select(item => item.Body));
        Assert.Equal(["Payment FAILED for order"], Logs(store, text: "payment failed").Records.Select(item => item.Body));
        Assert.Equal(["100% done_ok"], Logs(store, text: "0% d").Records.Select(item => item.Body));
        Assert.Empty(Logs(store, text: "_ok%").Records);
        Assert.Equal(["disk low", "100% done_ok"],
            Logs(store, attributes: new() { ["order.id"] = "2" }).Records.Select(item => item.Body));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = Logs(store, limit: 2, cursor: LogCursor.Decode(cursor));
            seen.AddRange(page.Records.Select(item => item.Body));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(5, pages);
        Assert.Equal(logs.Select(item => item.Body).Reverse(), seen);
        var first = Logs(store, limit: 1).Records[0];
        Assert.Equal(("DEBUG", 5), (first.Severity, first.SeverityNumber));
    }

    [Fact]
    public void Queries_of_an_environment_without_data_are_empty_and_create_nothing()
    {
        var store = Store();
        var unknown = new TelemetryEnvironmentKey("Shop", "production");

        Assert.Empty(store.MetricNames(unknown));
        Assert.Empty(store.QueryMetrics(unknown, new MetricQuery("cpu", new Dictionary<string, string>(), PlanFor(60), MetricAggregation.Avg, 10)).Series);
        Assert.Empty(store.QueryLogs(unknown, new LogQuery(0, long.MaxValue, null, null, new Dictionary<string, string>(), 10, null)).Records);
        Assert.False(File.Exists(store.DatabasePath(unknown)));
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("Stage")]
    [InlineData("")]
    public void Environment_ids_cannot_leave_the_store_root(string environment)
        => Assert.Throws<ArgumentException>(() => Store().DatabasePath(new TelemetryEnvironmentKey("Shop", environment)));

    [Fact]
    public void Known_environments_lists_written_databases()
    {
        var store = Store();
        store.Write(_stage, [], [Log("x", 9, _clock.GetUtcNow())], _clock.GetUtcNow());
        store.Write(new("Other App", "production"), [], [Log("y", 9, _clock.GetUtcNow())], _clock.GetUtcNow());

        Assert.Equal([new TelemetryEnvironmentKey("other app", "production"), new TelemetryEnvironmentKey("shop", "stage")], store.KnownEnvironments());
    }

    private TelemetryStore Store(TelemetryOptions? options = null)
        => new(options ?? new TelemetryOptions { StoreRoot = _root }, NullLogger<TelemetryStore>.Instance);

    private MetricQueryPlan PlanFor(long step)
        => MetricBucketPolicy.Plan(_clock.GetUtcNow(), _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow(), step, new TelemetryOptions(), out _)!;

    private MetricQueryResponse Query(TelemetryStore store, string name, long step,
        MetricAggregation aggregation = MetricAggregation.Avg, Dictionary<string, string>? attributes = null)
        => store.QueryMetrics(_stage, new MetricQuery(name, attributes ?? [], PlanFor(step), aggregation, 10));

    private LogPageResponse Logs(TelemetryStore store, DateTimeOffset? from = null, DateTimeOffset? to = null, int? minSeverity = null,
        string? text = null, Dictionary<string, string>? attributes = null, int limit = 100, LogCursor? cursor = null)
        => store.QueryLogs(_stage, new LogQuery(Nanos(from ?? _clock.GetUtcNow().AddHours(-1)), Nanos(to ?? _clock.GetUtcNow().AddHours(1)),
            minSeverity, text, attributes ?? [], limit, cursor));

    /// <summary>Points relative to the clock's start of 12:00 as (seconds, value, count).</summary>
    private static List<(long Offset, double Value, double Count)> Points(MetricQueryResponse response)
        => Assert.Single(response.Series).Points
            .Select(point => (point.Time.ToUnixTimeSeconds() - new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), point.Value, point.Count))
            .ToList();

    private static long Nanos(DateTimeOffset value) => TelemetryAdmissionPolicy.UnixNanos(value);

    private static MetricSample Gauge(string name, double value, long time, params (string Key, string Value)[] attributes)
        => new(name, "1", MetricKind.Gauge, MetricTemporality.Unspecified, Attributes(attributes), time, 0, value, 1, null, null);

    private static MetricSample Histogram(double sum, double count, long time, long start)
        => new("latency", "ms", MetricKind.Histogram, MetricTemporality.Cumulative, TelemetryAttributes.Empty, time, start, sum, count, null, null);

    private static TelemetryLogRecord Log(string body, int severity, DateTimeOffset at, params (string Key, string Value)[] attributes)
        => new(Nanos(at), severity, "", body, Attributes(attributes), null, null, null);

    private static TelemetryAttributes Attributes((string Key, string Value)[] values)
        => TelemetryAttributes.From(values.Select(item => KeyValuePair.Create(item.Key, item.Value)));
}
