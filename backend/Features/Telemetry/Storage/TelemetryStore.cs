using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Telemetry;

public sealed record TelemetryWriteResult(int StoredPoints, int RejectedSeriesPoints, int StoredLogs, int LogsEvictedForSize);

public sealed record TelemetryRetentionResult(int MinuteBuckets, int HourBuckets, int Series, int Logs);

/// <summary>Minute or hour buckets removed by a global cap, all older than <paramref name="Cutoff"/>.</summary>
public sealed record TelemetryBucketCapResult(long Buckets, DateTimeOffset? Cutoff);

/// <summary>
/// Built-in single-host store: one SQLite database (WAL) per project
/// environment under <c>&lt;TaskRepository&gt;/telemetry/&lt;project&gt;/&lt;environment&gt;.sqlite</c>.
/// Metrics are kept as minute and hour buckets per series (count, sum, min,
/// max, last), both written by the same transaction. Logs are rows with a
/// running byte total; when it passes the per-environment cap, the oldest
/// records are removed until the total is at 90 percent of the cap. New series
/// are refused past <see cref="TelemetryOptions.MaxSeriesPerEnvironment"/> series
/// or <see cref="TelemetryOptions.MaxSeriesBytesPerEnvironment"/> estimated bytes
/// of the series cache (<see cref="SeriesBytes"/>). Minute buckets of all environments together are capped at
/// <see cref="TelemetryOptions.MaxMinuteBucketBytes"/> by
/// <see cref="EnforceMinuteBucketCap"/>, which removes the oldest hours first, and hour buckets at
/// <see cref="TelemetryOptions.MaxHourBucketBytes"/> by <see cref="EnforceHourBucketCap"/>, which
/// removes the oldest days first.
/// <para>Writes and retention run on the single <see cref="TelemetryWriter"/>
/// loop and are serialized by a lock; queries open their own read connection.</para>
/// </summary>
public sealed partial class TelemetryStore
{
    private static readonly Regex EnvironmentId = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private const string LogBytesKey = "log_bytes";
    /// <summary>Estimated disk bytes of one minute or hour bucket: the row plus its entry in the bucket index.</summary>
    internal const long BucketRowBytes = 96;
    /// <summary>One series cache entry without its strings: dictionary slot, key tuple, entry object, cumulative state, and two string headers.</summary>
    private const long SeriesEntryBytes = 192;
    private readonly TelemetryOptions _options;
    private readonly ILogger<TelemetryStore> _logger;
    private readonly ITelemetryDurability _durability;
    private readonly object _writeGate = new();
    private readonly Dictionary<string, SeriesCache> _series = new(StringComparer.Ordinal);

    public TelemetryStore(TelemetryOptions options, ILogger<TelemetryStore> logger)
        : this(options, logger, TelemetryDurability.Instance) { }

    internal TelemetryStore(TelemetryOptions options, ILogger<TelemetryStore> logger, ITelemetryDurability durability)
    {
        _options = options;
        _logger = logger;
        _durability = durability;
    }

    public static bool IsValidEnvironmentId(string? environment) => environment is not null && EnvironmentId.IsMatch(environment);

    public string DatabasePath(TelemetryEnvironmentKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Project) || key.Project.IndexOfAny(['/', '\\', '\0']) >= 0 || key.Project is "." or "..")
            throw new ArgumentException("Invalid project.", nameof(key));
        if (!IsValidEnvironmentId(key.Environment)) throw new ArgumentException("Invalid environment.", nameof(key));
        return Path.Combine(Root, Uri.EscapeDataString(key.Project.ToLowerInvariant()), key.Environment + ".sqlite");
    }

    /// <summary>Environments that have a database, for the retention sweep.</summary>
    public IReadOnlyList<TelemetryEnvironmentKey> KnownEnvironments()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.sqlite")
                .Select(file => new TelemetryEnvironmentKey(Uri.UnescapeDataString(Path.GetFileName(directory)), Path.GetFileNameWithoutExtension(file))))
            .Where(key => IsValidEnvironmentId(key.Environment))
            .OrderBy(key => key.ToString(), StringComparer.Ordinal).ToList();
    }

    public TelemetryWriteResult Write(TelemetryEnvironmentKey key, IReadOnlyList<MetricSample> metrics,
        IReadOnlyList<TelemetryLogRecord> logs, DateTimeOffset now, string? batchId = null)
    {
        var path = DatabasePath(key);
        lock (_writeGate)
        {
            try
            {
                using var connection = Open(path, create: true)!;
                using var transaction = connection.BeginTransaction();
                if (batchId is not null && Execute(connection, transaction,
                    "INSERT OR IGNORE INTO ingest_commits (id) VALUES ($id)", ("$id", batchId)) == 0)
                {
                    transaction.Commit();
                    PersistDatabaseDirectory(path);
                    return new TelemetryWriteResult(0, 0, 0, 0);
                }
                var (stored, rejected) = WriteMetrics(connection, transaction, path, metrics, now);
                var evicted = WriteLogs(connection, transaction, logs);
                transaction.Commit();
                // SQLite FULL syncs the WAL commit; persist a newly created database's
                // directory entry before its stage file can be removed.
                if (batchId is not null) PersistDatabaseDirectory(path);
                return new TelemetryWriteResult(stored, rejected, logs.Count, evicted);
            }
            catch
            {
                // The cache may hold series ids or cumulative state of the rolled-back transaction.
                _series.Remove(path);
                throw;
            }
        }
    }

    private void PersistDatabaseDirectory(string path)
    {
        _durability.FlushParentDirectory(path);
        // The first database creates a project directory. Its entry lives in StoreRoot.
        _durability.FlushParentDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>Conservatively reserves series for queued batches before HTTP acknowledgement.</summary>
    public bool WouldRejectSeries(TelemetryEnvironmentKey key, IEnumerable<MetricSample> pendingAndIncoming)
    {
        lock (_writeGate)
        {
            var path = DatabasePath(key);
            using var connection = Open(path, create: true)!;
            using var transaction = connection.BeginTransaction();
            var cache = SeriesFor(path, connection, transaction);
            var unseen = new HashSet<(string Name, string Attributes, string Unit, string Kind, MetricTemporality Temporality)>();
            var bytes = cache.Bytes;
            foreach (var sample in pendingAndIncoming)
            {
                var json = sample.Attributes.Json;
                var identity = (sample.Name, json, sample.Unit, sample.Kind.ToString(), sample.Temporality);
                if (cache.Entries.ContainsKey(identity) || !unseen.Add(identity)) continue;
                if (cache.Entries.Count + unseen.Count > _options.MaxSeriesPerEnvironment) return true;
                bytes += SeriesBytes(sample.Name, json);
                if (bytes > _options.MaxSeriesBytesPerEnvironment) return true;
            }
            return false;
        }
    }

    /// <summary>Removes a replay marker once its stage file has been deleted.</summary>
    public void ForgetCommittedBatch(TelemetryEnvironmentKey key, string batchId)
    {
        lock (_writeGate)
        {
            using var connection = Open(DatabasePath(key), create: false);
            if (connection is not null)
                Execute(connection, null, "DELETE FROM ingest_commits WHERE id = $id", ("$id", batchId));
        }
    }

    public TelemetryRetentionResult EnforceRetention(TelemetryEnvironmentKey key, DateTimeOffset now)
    {
        var path = DatabasePath(key);
        lock (_writeGate)
        {
            using var connection = Open(path, create: false);
            if (connection is null) return new(0, 0, 0, 0);
            _series.Remove(path);
            var minuteCutoff = (now - _options.MetricMinuteRetention).ToUnixTimeSeconds();
            var hourCutoff = (now - _options.MetricHourRetention).ToUnixTimeSeconds();
            var logCutoff = TelemetryAdmissionPolicy.UnixNanos(now - _options.LogRetention);
            int minutes, hours, series, logs;
            using (var transaction = connection.BeginTransaction())
            {
                minutes = Execute(connection, transaction, "DELETE FROM metric_minute WHERE bucket < $cutoff", ("$cutoff", minuteCutoff));
                hours = Execute(connection, transaction, "DELETE FROM metric_hour WHERE bucket < $cutoff", ("$cutoff", hourCutoff));
                series = Execute(connection, transaction, """
                    DELETE FROM series WHERE last_seen < $cutoff
                      AND NOT EXISTS (SELECT 1 FROM metric_hour WHERE series_id = series.id)
                      AND NOT EXISTS (SELECT 1 FROM metric_minute WHERE series_id = series.id)
                    """, ("$cutoff", hourCutoff * MetricBucketPolicy.NanosPerSecond));
                var freed = Scalar(connection, transaction, "SELECT COALESCE(SUM(bytes), 0) FROM logs WHERE time < $cutoff", ("$cutoff", logCutoff));
                logs = Execute(connection, transaction, "DELETE FROM logs WHERE time < $cutoff", ("$cutoff", logCutoff));
                AddLogBytes(connection, transaction, -freed);
                transaction.Commit();
            }
            Execute(connection, null, "PRAGMA incremental_vacuum");
            return new TelemetryRetentionResult(minutes, hours, series, logs);
        }
    }

    /// <summary>
    /// Keeps the minute buckets of all environments together under
    /// <see cref="TelemetryOptions.MaxMinuteBucketBytes"/> (estimated at
    /// <see cref="BucketRowBytes"/> per bucket). When the total is above
    /// the cap, whole hours are removed oldest first, across every environment,
    /// until the total is at 90 percent of the cap. Hour buckets stay, so older
    /// ranges keep their hourly resolution.
    /// </summary>
    public TelemetryBucketCapResult EnforceMinuteBucketCap(IReadOnlyList<TelemetryEnvironmentKey> environments)
        => TrimOldestBuckets(environments, "metric_minute", _options.MaxMinuteBucketBytes, 3600);

    /// <summary>
    /// Keeps the hour buckets of all environments together under
    /// <see cref="TelemetryOptions.MaxHourBucketBytes"/>. Points 15 to 90 days
    /// old write only hour buckets, which the minute cap does not see. Above the
    /// cap, whole days are removed oldest first, across every environment, until
    /// the total is at 90 percent of the cap.
    /// </summary>
    public TelemetryBucketCapResult EnforceHourBucketCap(IReadOnlyList<TelemetryEnvironmentKey> environments)
        => TrimOldestBuckets(environments, "metric_hour", _options.MaxHourBucketBytes, 86400);

    private TelemetryBucketCapResult TrimOldestBuckets(IReadOnlyList<TelemetryEnvironmentKey> environments, string table,
        long maxBytes, long periodSeconds)
    {
        var maxRows = maxBytes / BucketRowBytes;
        lock (_writeGate)
        {
            var perPeriod = new SortedDictionary<long, long>();
            long total = 0;
            foreach (var environment in environments)
            {
                using var connection = Open(DatabasePath(environment), create: false);
                if (connection is null) continue;
                using var command = Command(connection, null, $"SELECT bucket / {periodSeconds} * {periodSeconds}, COUNT(*) FROM {table} GROUP BY 1");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var (period, count) = (reader.GetInt64(0), reader.GetInt64(1));
                    perPeriod[period] = perPeriod.GetValueOrDefault(period) + count;
                    total += count;
                }
            }
            if (total <= maxRows) return new TelemetryBucketCapResult(0, null);
            var target = maxRows / 10 * 9;
            long cutoff = 0, removed = 0;
            foreach (var (period, count) in perPeriod)
            {
                if (total - removed <= target) break;
                removed += count;
                cutoff = period + periodSeconds;
            }
            foreach (var environment in environments)
            {
                using var connection = Open(DatabasePath(environment), create: false);
                if (connection is null) continue;
                Execute(connection, null, $"DELETE FROM {table} WHERE bucket < $cutoff", ("$cutoff", cutoff));
                Execute(connection, null, "PRAGMA incremental_vacuum");
            }
            return new TelemetryBucketCapResult(removed, DateTimeOffset.FromUnixTimeSeconds(cutoff));
        }
    }

    private string Root => _options.StoreRoot
        ?? throw new InvalidOperationException("Telemetry:StoreRoot or TaskRepository is required for the telemetry store.");

    private (int Stored, int Rejected) WriteMetrics(SqliteConnection connection, SqliteTransaction transaction, string path,
        IReadOnlyList<MetricSample> metrics, DateTimeOffset now)
    {
        if (metrics.Count == 0) return (0, 0);
        var cache = SeriesFor(path, connection, transaction);
        var minuteCutoff = (now - _options.MetricMinuteRetention).ToUnixTimeSeconds();
        var hourCutoff = (now - _options.MetricHourRetention).ToUnixTimeSeconds();
        using var insertSeries = Command(connection, transaction, """
            INSERT INTO series (name, attributes, unit, kind, temporality, last_seen)
            VALUES ($name, $attributes, $unit, $kind, $temporality, $seen) RETURNING id
            """, "$name", "$attributes", "$unit", "$kind", "$temporality", "$seen");
        using var minute = UpsertBucket(connection, transaction, "metric_minute");
        using var hour = UpsertBucket(connection, transaction, "metric_hour");
        var stored = 0;
        var rejected = 0;
        var touched = new HashSet<SeriesEntry>();
        foreach (var sample in metrics.OrderBy(item => item.TimeUnixNano))
        {
            // Rendered once per point and not kept: only shared attribute sets cache their JSON.
            var json = sample.Attributes.Json;
            var identity = (sample.Name, json, sample.Unit, sample.Kind.ToString(), sample.Temporality);
            if (!cache.Entries.TryGetValue(identity, out var entry))
            {
                var seriesBytes = SeriesBytes(sample.Name, json);
                if (cache.Entries.Count >= _options.MaxSeriesPerEnvironment
                    || cache.Bytes + seriesBytes > _options.MaxSeriesBytesPerEnvironment)
                {
                    rejected++;
                    continue;
                }
                Bind(insertSeries, sample.Name, json, sample.Unit, sample.Kind.ToString(), (int)sample.Temporality, sample.TimeUnixNano);
                entry = new SeriesEntry((long)insertSeries.ExecuteScalar()!);
                cache.Entries[identity] = entry;
                cache.Bytes += seriesBytes;
            }
            var (contribution, state) = MetricBucketPolicy.Contribute(sample, entry.Cumulative);
            entry.Cumulative = state;
            entry.LastSeen = Math.Max(entry.LastSeen, sample.TimeUnixNano);
            touched.Add(entry);
            stored++;
            if (contribution is not { } value) continue;
            var minuteBucket = MetricBucketPolicy.BucketStart(sample.TimeUnixNano, MetricResolution.Minute);
            if (minuteBucket >= minuteCutoff) Upsert(minute, entry.Id, minuteBucket, value);
            var hourBucket = MetricBucketPolicy.BucketStart(sample.TimeUnixNano, MetricResolution.Hour);
            if (hourBucket >= hourCutoff) Upsert(hour, entry.Id, hourBucket, value);
        }
        using var updateSeries = Command(connection, transaction, """
            UPDATE series SET last_seen = MAX(last_seen, $seen), cum_start = $start, cum_count = $count, cum_sum = $sum, cum_time = $time
            WHERE id = $id
            """, "$seen", "$start", "$count", "$sum", "$time", "$id");
        foreach (var entry in touched)
        {
            var state = entry.Cumulative;
            Bind(updateSeries, entry.LastSeen, state?.StartTimeUnixNano, state?.Count, state?.Sum, state?.TimeUnixNano, entry.Id);
            updateSeries.ExecuteNonQuery();
        }
        if (rejected > 0)
            _logger.LogWarning(
                "Telemetry series limit of {Limit} series or {ByteLimit} bytes reached in {Path}; {Rejected} points of new series were not stored",
                _options.MaxSeriesPerEnvironment, _options.MaxSeriesBytesPerEnvironment, path, rejected);
        return (stored, rejected);
    }

    private int WriteLogs(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<TelemetryLogRecord> logs)
    {
        if (logs.Count == 0) return 0;
        using var insert = Command(connection, transaction, """
            INSERT INTO logs (time, severity, severity_text, body, attributes, trace_id, span_id, event_name, bytes)
            VALUES ($time, $severity, $text, $body, $attributes, $trace, $span, $event, $bytes)
            """, "$time", "$severity", "$text", "$body", "$attributes", "$trace", "$span", "$event", "$bytes");
        long added = 0;
        foreach (var log in logs)
        {
            var json = log.Attributes.Json;
            var bytes = LogBytes(log, json);
            added += bytes;
            Bind(insert, log.TimeUnixNano, log.SeverityNumber, log.SeverityText, log.Body, json,
                log.TraceId, log.SpanId, log.EventName, bytes);
            insert.ExecuteNonQuery();
        }
        var total = AddLogBytes(connection, transaction, added);
        return total > _options.LogMaxBytesPerEnvironment ? EvictOldestLogs(connection, transaction, total) : 0;
    }

    private int EvictOldestLogs(SqliteConnection connection, SqliteTransaction transaction, long total)
    {
        var target = _options.LogMaxBytesPerEnvironment / 10 * 9;
        var excess = total - target;
        long freed = 0, lastTime = 0, lastId = 0;
        var count = 0;
        using (var oldest = Command(connection, transaction, "SELECT time, id, bytes FROM logs ORDER BY time, id"))
        using (var reader = oldest.ExecuteReader())
        {
            while (freed < excess && reader.Read())
            {
                lastTime = reader.GetInt64(0);
                lastId = reader.GetInt64(1);
                freed += reader.GetInt64(2);
                count++;
            }
        }
        if (count == 0) return 0;
        Execute(connection, transaction, "DELETE FROM logs WHERE (time, id) <= ($time, $id)", ("$time", lastTime), ("$id", lastId));
        AddLogBytes(connection, transaction, -freed);
        return count;
    }

    /// <summary>Stored payload size of one record, the size the intake charged (<see cref="TelemetryLogRecord.StoredBytes"/>).</summary>
    internal static long LogBytes(TelemetryLogRecord log, string attributesJson)
        => log.StoredBytes(Encoding.UTF8.GetByteCount(attributesJson));

    private SeriesCache SeriesFor(string path, SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_series.TryGetValue(path, out var cache)) return cache;
        cache = new SeriesCache();
        using var command = Command(connection, transaction,
            "SELECT id, name, attributes, unit, kind, temporality, last_seen, cum_start, cum_count, cum_sum, cum_time FROM series");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entry = new SeriesEntry(reader.GetInt64(0)) { LastSeen = reader.GetInt64(6) };
            if (!reader.IsDBNull(7))
                entry.Cumulative = new CumulativeState(reader.GetInt64(7), reader.GetDouble(8), reader.GetDouble(9), reader.GetInt64(10));
            var (name, attributes) = (reader.GetString(1), reader.GetString(2));
            cache.Entries[(name, attributes, reader.GetString(3), reader.GetString(4), (MetricTemporality)reader.GetInt32(5))] = entry;
            cache.Bytes += SeriesBytes(name, attributes);
        }
        _series[path] = cache;
        return cache;
    }

    private static SqliteCommand UpsertBucket(SqliteConnection connection, SqliteTransaction transaction, string table)
        => Command(connection, transaction, $"""
            INSERT INTO {table} (series_id, bucket, count_value, sum_value, min_value, max_value, last_value, last_time)
            VALUES ($series, $bucket, $count, $sum, $min, $max, $last, $time)
            ON CONFLICT (series_id, bucket) DO UPDATE SET
              count_value = count_value + excluded.count_value,
              sum_value = sum_value + excluded.sum_value,
              min_value = MIN(min_value, excluded.min_value),
              max_value = MAX(max_value, excluded.max_value),
              last_value = CASE WHEN excluded.last_time >= last_time THEN excluded.last_value ELSE last_value END,
              last_time = MAX(last_time, excluded.last_time)
            """, "$series", "$bucket", "$count", "$sum", "$min", "$max", "$last", "$time");

    private static void Upsert(SqliteCommand command, long seriesId, long bucket, BucketContribution value)
    {
        Bind(command, seriesId, bucket, value.Count, value.Sum, value.Min, value.Max, value.Last, value.LastTimeUnixNano);
        command.ExecuteNonQuery();
    }

    private static long AddLogBytes(SqliteConnection connection, SqliteTransaction transaction, long delta)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO meta (key, value) VALUES ($key, MAX(0, $delta))
            ON CONFLICT (key) DO UPDATE SET value = MAX(0, value + $delta)
            RETURNING value
            """, "$key", "$delta");
        Bind(command, LogBytesKey, delta);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Opens a connection without pooling, so no native handle keeps the file
    /// open after dispose (the reason <c>ProjectChatIndex</c> disables pooling).
    /// Returns null when <paramref name="create"/> is false and no database exists.
    /// </summary>
    private static SqliteConnection? Open(string path, bool create)
    {
        if (!create && !File.Exists(path)) return null;
        if (create) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        Execute(connection, null, """
            PRAGMA busy_timeout = 5000;
            PRAGMA auto_vacuum = INCREMENTAL;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS series (
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL,
              attributes TEXT NOT NULL,
              unit TEXT NOT NULL,
              kind TEXT NOT NULL,
              temporality INTEGER NOT NULL,
              last_seen INTEGER NOT NULL,
              cum_start INTEGER, cum_count REAL, cum_sum REAL, cum_time INTEGER,
              UNIQUE (name, attributes, unit, kind, temporality));
            CREATE TABLE IF NOT EXISTS metric_minute (
              series_id INTEGER NOT NULL, bucket INTEGER NOT NULL,
              count_value REAL NOT NULL, sum_value REAL NOT NULL, min_value REAL NOT NULL, max_value REAL NOT NULL,
              last_value REAL NOT NULL, last_time INTEGER NOT NULL,
              PRIMARY KEY (series_id, bucket)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS metric_minute_bucket ON metric_minute (bucket);
            CREATE TABLE IF NOT EXISTS metric_hour (
              series_id INTEGER NOT NULL, bucket INTEGER NOT NULL,
              count_value REAL NOT NULL, sum_value REAL NOT NULL, min_value REAL NOT NULL, max_value REAL NOT NULL,
              last_value REAL NOT NULL, last_time INTEGER NOT NULL,
              PRIMARY KEY (series_id, bucket)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS metric_hour_bucket ON metric_hour (bucket);
            CREATE TABLE IF NOT EXISTS logs (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              time INTEGER NOT NULL,
              severity INTEGER NOT NULL,
              severity_text TEXT NOT NULL,
              body TEXT NOT NULL,
              attributes TEXT NOT NULL,
              trace_id TEXT, span_id TEXT, event_name TEXT,
              bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS logs_time ON logs (time, id);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ingest_commits (id TEXT PRIMARY KEY);
            """);
        // Version zero has the original name/attributes uniqueness constraint. Rebuild only
        // the series table, preserving ids so every existing bucket keeps its parent.
        if (Scalar(connection, null, "PRAGMA user_version") == 0)
        {
            using var migration = connection.BeginTransaction();
            Execute(connection, migration, """
                CREATE TABLE series_new (
                  id INTEGER PRIMARY KEY, name TEXT NOT NULL, attributes TEXT NOT NULL,
                  unit TEXT NOT NULL, kind TEXT NOT NULL, temporality INTEGER NOT NULL,
                  last_seen INTEGER NOT NULL, cum_start INTEGER, cum_count REAL, cum_sum REAL, cum_time INTEGER,
                  UNIQUE (name, attributes, unit, kind, temporality));
                INSERT INTO series_new SELECT id, name, attributes, unit, kind, temporality,
                  last_seen, cum_start, cum_count, cum_sum, cum_time FROM series;
                DROP TABLE series;
                ALTER TABLE series_new RENAME TO series;
                """);
            migration.Commit();
            Execute(connection, null, "PRAGMA user_version = 1");
        }
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.Add(new SqliteParameter { ParameterName = name });
        return command;
    }

    private static void Bind(SqliteCommand command, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++) command.Parameters[index].Value = values[index] ?? DBNull.Value;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Budget cost of one series as the writer's cache holds it: name and
    /// attribute JSON as UTF-16 strings plus the entry overhead. The series
    /// table stores the same text in UTF-8, which is never larger for ASCII
    /// and at most 1.5 times larger for other text.
    /// </summary>
    internal static long SeriesBytes(string name, string attributesJson)
        => SeriesEntryBytes + 2L * (name.Length + attributesJson.Length);

    private sealed class SeriesCache
    {
        public Dictionary<(string Name, string Attributes, string Unit, string Kind, MetricTemporality Temporality), SeriesEntry> Entries { get; } = new();
        public long Bytes { get; set; }
    }

    private sealed class SeriesEntry(long id)
    {
        public long Id { get; } = id;
        public long LastSeen { get; set; }
        public CumulativeState? Cumulative { get; set; }
    }
}
