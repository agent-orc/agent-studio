using AgentStudio.Telemetry;
using Microsoft.Data.Sqlite;
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
    public async Task Staged_batch_survives_restart_and_transient_store_failure()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = Store(options);
        var queue = new TelemetryIntakeQueue(options);
        var now = Nanos(_clock.GetUtcNow());
        var batch = new TelemetryIngestBatch(_stage, [Gauge("recovered", 3, now)], [], 512);
        Assert.Equal(TelemetryEnqueueResult.Accepted, queue.TryEnqueue(batch));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));

        // A new queue instance reads the committed stage file left by the previous process.
        var recovered = new TelemetryIntakeQueue(options);
        var databasePath = store.DatabasePath(_stage);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(databasePath); // SQLite cannot open a directory as its database.
        var writer = new TelemetryWriter(recovered, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);
        Assert.Equal(0, await writer.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(1, recovered.PendingRecords);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));

        Directory.Delete(databasePath);
        Assert.Equal(1, await writer.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, recovered.PendingRecords);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3d, Assert.Single(Assert.Single(Query(store, "recovered", 60).Series).Points).Value);
    }

    [Fact]
    public async Task Failed_cumulative_batch_is_retried_before_the_next_batch()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = Store(options);
        var queue = new TelemetryIntakeQueue(options);
        var writer = new TelemetryWriter(queue, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);
        var now = Nanos(_clock.GetUtcNow());
        var start = now - 10 * 60 * Second;
        MetricSample Counter(double value, long time) => new("ordered", "1", MetricKind.Sum,
            MetricTemporality.Cumulative, TelemetryAttributes.Empty, time, start, value, 1, null, null, true);
        Assert.Equal(TelemetryEnqueueResult.Accepted, queue.TryEnqueue(new(_stage, [Counter(100, now)], [], 256)));
        Assert.Equal(TelemetryEnqueueResult.Accepted, queue.TryEnqueue(new(_stage, [Counter(105, now + 60 * Second)], [], 256)));

        var databasePath = store.DatabasePath(_stage);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(databasePath);
        Assert.Equal(0, await writer.DrainOnceAsync(CancellationToken.None));
        Directory.Delete(databasePath);
        Assert.Equal(2, await writer.DrainOnceAsync(CancellationToken.None));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal([5d], Assert.Single(Query(store, "ordered", 60, MetricAggregation.Sum).Series)
            .Points.Select(point => point.Value));
        Assert.Equal(0, queue.PendingRecords);
    }

    [Fact]
    public async Task Restart_replays_multiple_cumulative_batches_in_intake_order()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var firstQueue = new TelemetryIntakeQueue(options);
        var now = Nanos(_clock.GetUtcNow());
        var start = now - 10 * 60 * Second;
        MetricSample Counter(double value, long time) => new("replayed", "1", MetricKind.Sum,
            MetricTemporality.Cumulative, TelemetryAttributes.Empty, time, start, value, 1, null, null, true);
        Assert.Equal(TelemetryEnqueueResult.Accepted, firstQueue.TryEnqueue(new(_stage, [Counter(100, now)], [], 256)));
        Assert.Equal(TelemetryEnqueueResult.Accepted, firstQueue.TryEnqueue(new(_stage, [Counter(105, now + 60 * Second)], [], 256)));
        var filenames = Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json")
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, filenames.Length);
        Assert.StartsWith("00000000000000000001-", filenames[0]);
        Assert.StartsWith("00000000000000000002-", filenames[1]);

        var recovered = new TelemetryIntakeQueue(options);
        var store = Store(options);
        var writer = new TelemetryWriter(recovered, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);
        Assert.Equal(2, await writer.DrainOnceAsync(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal([5d], Assert.Single(Query(store, "replayed", 60, MetricAggregation.Sum).Series)
            .Points.Select(point => point.Value));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
    }

    [Fact]
    public async Task Committed_stage_replays_idempotently_after_a_crash_before_cleanup()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var store = Store(options);
        var queue = new TelemetryIntakeQueue(options);
        var now = Nanos(_clock.GetUtcNow());
        var sample = Gauge("once", 7, now);
        Assert.Equal(TelemetryEnqueueResult.Accepted, queue.TryEnqueue(new(_stage, [sample], [], 256)));
        var stage = Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
        store.Write(_stage, [sample], [], _clock.GetUtcNow(), Path.GetFileNameWithoutExtension(stage));

        var recovered = new TelemetryIntakeQueue(options);
        var writer = new TelemetryWriter(recovered, Store(options), [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);
        Assert.Equal(1, await writer.DrainOnceAsync(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1d, Assert.Single(Assert.Single(Query(store, "once", 60).Series).Points).Count);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
    }

    [Fact]
    public async Task New_project_directory_is_synced_after_commit_and_before_stage_cleanup()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var durability = new RecordingDurability();
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance, durability);
        var queue = new TelemetryIntakeQueue(options, null, null, durability);
        var databasePath = store.DatabasePath(_stage);
        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
        Assert.Equal(TelemetryEnqueueResult.Accepted,
            queue.TryEnqueue(new(_stage, [Gauge("durable", 1, Nanos(_clock.GetUtcNow()))], [], 256)));
        durability.Events.Clear();
        durability.OnSync = path =>
        {
            if (path != databasePath) return;
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ingest_commits";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        };
        var writer = new TelemetryWriter(queue, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);

        Assert.Equal(1, await writer.DrainOnceAsync(CancellationToken.None));

        var projectSync = durability.Events.IndexOf("sync:" + databasePath);
        var rootSync = durability.Events.IndexOf("sync:" + Path.GetDirectoryName(databasePath));
        var cleanup = durability.Events.FindIndex(item => item.StartsWith("move:", StringComparison.Ordinal)
            && item.EndsWith(".done", StringComparison.Ordinal));
        Assert.True(projectSync >= 0 && rootSync > projectSync && cleanup > rootSync,
            string.Join(" | ", durability.Events));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
    }

    [Fact]
    public async Task Replay_marker_is_removed_only_after_the_stage_completion_rename_is_persisted()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var durability = new RecordingDurability();
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance, durability);
        var queue = new TelemetryIntakeQueue(options, null, null, durability);
        var databasePath = store.DatabasePath(_stage);
        Assert.Equal(TelemetryEnqueueResult.Accepted,
            queue.TryEnqueue(new(_stage, [Gauge("marker", 1, Nanos(_clock.GetUtcNow()))], [], 256)));
        var markersAtCompletion = new List<long>();
        durability.OnMove = destination =>
        {
            if (destination.EndsWith(".done", StringComparison.Ordinal))
                markersAtCompletion.Add(ReplayMarkers(databasePath));
        };
        var writer = new TelemetryWriter(queue, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);

        Assert.Equal(1, await writer.DrainOnceAsync(CancellationToken.None));

        Assert.Equal([1L], markersAtCompletion);
        Assert.Equal(0L, ReplayMarkers(databasePath));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending")));
    }

    [Fact]
    public void Queue_without_an_injected_masker_masks_log_name_and_severity_before_staging()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var queue = new TelemetryIntakeQueue(options);
        var secret = new string('z', 24);
        var credential = "Authorization: Bearer " + secret;
        var log = new TelemetryLogRecord(Nanos(_clock.GetUtcNow()), 17, credential, "event",
            TelemetryAttributes.Empty, null, null, credential);

        Assert.Equal(TelemetryEnqueueResult.Accepted, queue.TryEnqueue(new(_stage, [], [log], 256)));

        var staged = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json")));
        Assert.DoesNotContain(secret, staged);
        Assert.True(new TelemetryIntakeQueue(options).TryDequeue(out var replayed));
        var recovered = Assert.Single(replayed.Logs);
        Assert.DoesNotContain(secret, recovered.SeverityText);
        Assert.DoesNotContain(secret, recovered.EventName);
    }

    [Fact]
    public async Task Failed_store_root_sync_keeps_the_stage_for_retry()
    {
        var options = new TelemetryOptions { StoreRoot = _root };
        var durability = new RecordingDurability();
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance, durability);
        var queue = new TelemetryIntakeQueue(options, null, null, durability);
        var databasePath = store.DatabasePath(_stage);
        Assert.Equal(TelemetryEnqueueResult.Accepted,
            queue.TryEnqueue(new(_stage, [Gauge("retry", 2, Nanos(_clock.GetUtcNow()))], [], 256)));
        durability.OnSync = path =>
        {
            if (path == Path.GetDirectoryName(databasePath)) throw new IOException("root sync failed");
        };
        var writer = new TelemetryWriter(queue, store, [], new CredentialTelemetryValueMasker(),
            options, _clock, NullLogger<TelemetryWriter>.Instance);

        Assert.Equal(0, await writer.DrainOnceAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
        Assert.Equal(1, queue.PendingRecords);
        durability.OnSync = null;
        Assert.Equal(1, await writer.DrainOnceAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1d, Assert.Single(Assert.Single(Query(store, "retry", 60).Series).Points).Count);
    }

    [Fact]
    public void Successive_cumulative_sum_exports_contribute_deltas_and_handle_resets()
    {
        var store = Store();
        var start = Nanos(_clock.GetUtcNow().AddMinutes(-10));
        var t0 = Nanos(_clock.GetUtcNow());
        MetricSample Counter(double value, long time, long runStart) =>
            new("requests", "1", MetricKind.Sum, MetricTemporality.Cumulative,
                TelemetryAttributes.Empty, time, runStart, value, 1, null, null, true);
        store.Write(_stage, [Counter(100, t0, start)], [], _clock.GetUtcNow());
        store.Write(_stage, [Counter(105, t0 + 60 * Second, start)], [], _clock.GetUtcNow());
        store = Store();
        store.Write(_stage, [Counter(108, t0 + 120 * Second, start)], [], _clock.GetUtcNow());
        store.Write(_stage, [Counter(2, t0 + 180 * Second, t0 + 150 * Second)], [], _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal([5d, 3d, 2d], Assert.Single(Query(store, "requests", 60, MetricAggregation.Sum).Series)
            .Points.Select(point => point.Value));
    }

    [Fact]
    public void Same_name_and_attributes_keep_unit_kind_and_temporality_separate()
    {
        var store = Store();
        var now = Nanos(_clock.GetUtcNow());
        MetricSample Sample(string unit, MetricKind kind, MetricTemporality temporality) =>
            new("size", unit, kind, temporality, TelemetryAttributes.Empty, now,
                now - 30 * Second, 4, 1, null, null);
        store.Write(_stage, [Sample("ms", MetricKind.Gauge, MetricTemporality.Unspecified),
            Sample("s", MetricKind.Gauge, MetricTemporality.Unspecified),
            Sample("ms", MetricKind.Sum, MetricTemporality.Delta),
            Sample("ms", MetricKind.Sum, MetricTemporality.Cumulative)], [], _clock.GetUtcNow());

        Assert.Equal(4, Store().MetricNames(_stage).Single().SeriesCount);
        var streams = Query(Store(), "size", 60).Series;
        Assert.Equal(4, streams.Count);
        Assert.Contains(streams, item => item.Kind == "Sum" && item.Temporality == "cumulative");
        Assert.Contains(streams, item => item.Kind == "Sum" && item.Temporality == "delta");
    }

    [Fact]
    public void Legacy_series_schema_migrates_without_losing_bucket_rows()
    {
        var store = Store();
        var now = Nanos(_clock.GetUtcNow());
        store.Write(_stage, [Gauge("size", 7, now)], [], _clock.GetUtcNow());
        var path = store.DatabasePath(_stage);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE old_series (
                  id INTEGER PRIMARY KEY, name TEXT NOT NULL, attributes TEXT NOT NULL,
                  unit TEXT NOT NULL, kind TEXT NOT NULL, temporality INTEGER NOT NULL,
                  last_seen INTEGER NOT NULL, cum_start INTEGER, cum_count REAL, cum_sum REAL, cum_time INTEGER,
                  UNIQUE (name, attributes));
                INSERT INTO old_series SELECT * FROM series;
                DROP TABLE series;
                ALTER TABLE old_series RENAME TO series;
                PRAGMA user_version = 0;
                """;
            command.ExecuteNonQuery();
        }
        var reloaded = Store();
        reloaded.Write(_stage, [new MetricSample("size", "s", MetricKind.Gauge,
            MetricTemporality.Unspecified, TelemetryAttributes.Empty, now, 0, 2, 1, null, null)],
            [], _clock.GetUtcNow());

        Assert.Equal(2, reloaded.MetricNames(_stage).Single().SeriesCount);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([7d, 2d], Query(reloaded, "size", 60).Series.SelectMany(item => item.Points).Select(item => item.Value));
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
        var perRecord = TelemetryStore.LogBytes(logs[0], logs[0].Attributes.Json);

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
    public void Series_byte_budget_rejects_points_of_new_series_only()
    {
        var wide = new string('v', 30_000);
        var oneSeries = TelemetryStore.SeriesBytes("a", Attributes([("k", wide)]).Json);
        var store = Store(new TelemetryOptions { StoreRoot = _root, MaxSeriesBytesPerEnvironment = oneSeries * 2 + 10 });
        var now = Nanos(_clock.GetUtcNow());

        var result = store.Write(_stage, [Gauge("a", 1, now, ("k", wide)), Gauge("b", 1, now, ("k", wide)),
            Gauge("c", 1, now, ("k", wide)), Gauge("a", 2, now + Second, ("k", wide))], [], _clock.GetUtcNow());

        Assert.Equal((3, 1), (result.StoredPoints, result.RejectedSeriesPoints));
        // A fresh store reloads the series and their bytes from the database; the budget is still spent.
        var reloaded = Store(new TelemetryOptions { StoreRoot = _root, MaxSeriesBytesPerEnvironment = oneSeries * 2 + 10 });
        var refused = reloaded.Write(_stage, [Gauge("d", 1, now, ("k", "small"))], [], _clock.GetUtcNow());
        Assert.Equal((0, 1), (refused.StoredPoints, refused.RejectedSeriesPoints));
        Assert.Equal(["a", "b"], reloaded.MetricNames(_stage).Select(item => item.Name));
    }

    [Fact]
    public void Minute_bucket_cap_removes_the_oldest_hours_across_environments_and_keeps_hour_buckets()
    {
        // Ten minute buckets per environment and hour (two series times five minutes), over three hours.
        var store = Store(new TelemetryOptions { StoreRoot = _root, MaxMinuteBucketBytes = 40 * TelemetryStore.BucketRowBytes });
        var production = new TelemetryEnvironmentKey("Shop", "production");
        var start = _clock.GetUtcNow();
        foreach (var environment in new[] { _stage, production })
            for (var hour = 0; hour < 3; hour++)
            for (var minute = 0; minute < 5; minute++)
            {
                var at = Nanos(start.AddHours(hour).AddMinutes(minute));
                store.Write(environment, [Gauge("cpu", 1, at), Gauge("memory", 1, at)], [], start);
            }
        _clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(new TelemetryBucketCapResult(0, null), Store(new TelemetryOptions { StoreRoot = _root }).EnforceMinuteBucketCap(store.KnownEnvironments()));
        // 60 buckets exceed the cap of 40; the oldest hours go until at most 36 remain.
        var trimmed = store.EnforceMinuteBucketCap(store.KnownEnvironments());

        Assert.Equal((40L, start.AddHours(2)), (trimmed.Buckets, trimmed.Cutoff));
        var minutes = store.QueryMetrics(_stage, new MetricQuery("cpu", new Dictionary<string, string>(), MetricBucketPolicy.Plan(_clock.GetUtcNow(),
            start, _clock.GetUtcNow(), 60, new TelemetryOptions(), out _)!, MetricAggregation.Count, 10));
        Assert.Equal("minute", minutes.Resolution);
        Assert.All(Assert.Single(minutes.Series).Points, point => Assert.True(point.Time >= start.AddHours(2)));
        Assert.Equal(5, Assert.Single(minutes.Series).Points.Count);
        var hours = store.QueryMetrics(production, new MetricQuery("cpu", new Dictionary<string, string>(), MetricBucketPolicy.Plan(_clock.GetUtcNow(),
            start, _clock.GetUtcNow(), 3600, new TelemetryOptions(), out _)!, MetricAggregation.Count, 10));
        Assert.Equal([5d, 5d, 5d], Assert.Single(hours.Series).Points.Select(point => point.Value));
    }

    [Fact]
    public void Hour_bucket_cap_removes_the_oldest_days_of_points_older_than_the_minute_retention()
    {
        // Points 28 to 30 days old write only hour buckets: 2 environments x 3 days x 4 hours x 2 series = 48.
        var store = Store(new TelemetryOptions { StoreRoot = _root, MaxHourBucketBytes = 30 * TelemetryStore.BucketRowBytes });
        var production = new TelemetryEnvironmentKey("Shop", "production");
        var start = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        foreach (var environment in new[] { _stage, production })
            for (var day = 0; day < 3; day++)
            for (var hour = 1; hour <= 4; hour++)
            {
                var at = Nanos(start.AddDays(day).AddHours(hour));
                store.Write(environment, [Gauge("cpu", 1, at), Gauge("memory", 1, at)], [], _clock.GetUtcNow());
            }

        Assert.Equal(new TelemetryBucketCapResult(0, null), store.EnforceMinuteBucketCap(store.KnownEnvironments()));
        Assert.Equal(new TelemetryBucketCapResult(0, null), Store().EnforceHourBucketCap(store.KnownEnvironments()));
        // 48 buckets exceed the cap of 30; whole days go, oldest first, until at most 27 remain.
        var trimmed = store.EnforceHourBucketCap(store.KnownEnvironments());

        Assert.Equal((32L, start.AddDays(2)), (trimmed.Buckets, trimmed.Cutoff));
        Assert.Equal(new TelemetryBucketCapResult(0, null), store.EnforceHourBucketCap(store.KnownEnvironments()));
        var hours = store.QueryMetrics(production, new MetricQuery("cpu", new Dictionary<string, string>(), MetricBucketPolicy.Plan(_clock.GetUtcNow(),
            start, _clock.GetUtcNow(), 3600, new TelemetryOptions(), out _)!, MetricAggregation.Count, 10));
        Assert.Equal(4, Assert.Single(hours.Series).Points.Count);
        Assert.All(hours.Series[0].Points, point => Assert.True(point.Time >= start.AddDays(2)));
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

    private static long ReplayMarkers(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ingest_commits";
        return (long)command.ExecuteScalar()!;
    }

    private sealed class RecordingDurability : ITelemetryDurability
    {
        public List<string> Events { get; } = [];
        public Action<string>? OnSync { get; set; }
        public Action<string>? OnMove { get; set; }

        public void FlushParentDirectory(string path)
        {
            Events.Add("sync:" + path);
            OnSync?.Invoke(path);
            TelemetryDurability.Instance.FlushParentDirectory(path);
        }

        public void MoveStagedFile(string source, string destination)
        {
            Events.Add("move:" + destination);
            TelemetryDurability.Instance.MoveStagedFile(source, destination);
            OnMove?.Invoke(destination);
        }
    }
}
