using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Review;

/// <summary>
/// Durable review-effectiveness ledger (AGT-3269): review observations,
/// defect evidence, and the misses attributed between them. One SQLite file
/// under <c>{TaskRepository}/review-effectiveness/</c> (or
/// <c>ReviewEffectiveness:StoreRoot</c>). Writes are append-only and
/// idempotent by id, so a replayed step or report never counts twice. Without
/// a configured root the store is disabled and reads return nothing.
/// </summary>
public sealed class ReviewEffectivenessStore
{
    public const string FileName = "review-effectiveness.sqlite";

    private readonly object _gate = new();
    private readonly string? _databasePath;
    private bool _schemaReady;

    public ReviewEffectivenessStore(IConfiguration configuration)
        : this(RootFrom(configuration))
    {
    }

    internal ReviewEffectivenessStore(string? root)
    {
        _databasePath = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, FileName);
    }

    public bool Enabled => _databasePath is not null;

    private static string? RootFrom(IConfiguration configuration)
    {
        var configured = configuration["ReviewEffectiveness:StoreRoot"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var repository = configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(repository) ? null : Path.Combine(repository, "review-effectiveness");
    }

    /// <summary>Stores the observation unless its id exists; returns true when it was new.</summary>
    public bool AddObservation(ReviewObservation observation)
        => Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO reviews (id, project, job_id, attempt, round, level, subject, model, reasoning,
                    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, duration_ms, cost_usd,
                    verdict, outcome, f_blocker, f_major, f_minor, f_nit, f_unclassified, observed_at, origin, evidence_ref)
                VALUES ($id, $project, $job, $attempt, $round, $level, $subject, $model, $reasoning,
                    $input, $output, $cacheRead, $cacheCreation, $duration, $cost,
                    $verdict, $outcome, $blocker, $major, $minor, $nit, $unclassified, $observedAt, $origin, $evidenceRef);
                """;
            command.Parameters.AddWithValue("$id", observation.Id);
            AddDelivery(command, observation.Delivery);
            command.Parameters.AddWithValue("$level", observation.Level);
            command.Parameters.AddWithValue("$subject", observation.Subject);
            command.Parameters.AddWithValue("$model", (object?)observation.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$reasoning", (object?)observation.ReasoningLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("$input", observation.InputTokens);
            command.Parameters.AddWithValue("$output", observation.OutputTokens);
            command.Parameters.AddWithValue("$cacheRead", observation.CacheReadTokens);
            command.Parameters.AddWithValue("$cacheCreation", observation.CacheCreationTokens);
            command.Parameters.AddWithValue("$duration", observation.DurationMs);
            command.Parameters.AddWithValue("$cost", observation.CostUsd is { } cost
                ? cost.ToString(CultureInfo.InvariantCulture)
                : DBNull.Value);
            command.Parameters.AddWithValue("$verdict", observation.Verdict);
            command.Parameters.AddWithValue("$outcome", observation.Outcome);
            command.Parameters.AddWithValue("$blocker", observation.Findings.Blocker);
            command.Parameters.AddWithValue("$major", observation.Findings.Major);
            command.Parameters.AddWithValue("$minor", observation.Findings.Minor);
            command.Parameters.AddWithValue("$nit", observation.Findings.Nit);
            command.Parameters.AddWithValue("$unclassified", observation.Findings.Unclassified);
            command.Parameters.AddWithValue("$observedAt", Stamp(observation.ObservedAtUtc));
            command.Parameters.AddWithValue("$origin", observation.Origin);
            command.Parameters.AddWithValue("$evidenceRef", (object?)observation.EvidenceRef ?? DBNull.Value);
            return command.ExecuteNonQuery() > 0;
        });

    /// <summary>Stores the evidence unless its id exists; returns true when it was new.</summary>
    public bool AddEvidence(ReviewDefectEvidence evidence)
        => Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO evidence (id, project, job_id, attempt, round, source, summary, observed_at,
                    recorded_by, finder_level, finder_observation_id, severity, reference)
                VALUES ($id, $project, $job, $attempt, $round, $source, $summary, $observedAt,
                    $recordedBy, $finderLevel, $finderObservation, $severity, $reference);
                """;
            command.Parameters.AddWithValue("$id", evidence.Id);
            AddDelivery(command, evidence.Delivery);
            command.Parameters.AddWithValue("$source", evidence.Source);
            command.Parameters.AddWithValue("$summary", evidence.Summary);
            command.Parameters.AddWithValue("$observedAt", Stamp(evidence.ObservedAtUtc));
            command.Parameters.AddWithValue("$recordedBy", evidence.RecordedBy);
            command.Parameters.AddWithValue("$finderLevel", (object?)evidence.FinderLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("$finderObservation", (object?)evidence.FinderObservationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$severity", (object?)evidence.Severity ?? DBNull.Value);
            command.Parameters.AddWithValue("$reference", (object?)evidence.Reference ?? DBNull.Value);
            return command.ExecuteNonQuery() > 0;
        });

    /// <summary>Stores the misses that are new; returns how many were added.</summary>
    public int AddMisses(IReadOnlyList<ReviewMiss> misses)
    {
        if (misses.Count == 0) return 0;
        return Write(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var added = 0;
            foreach (var miss in misses)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO misses (observation_id, evidence_id, level, model, reasoning, source, recorded_at)
                    VALUES ($observation, $evidence, $level, $model, $reasoning, $source, $recordedAt);
                    """;
                command.Parameters.AddWithValue("$observation", miss.ObservationId);
                command.Parameters.AddWithValue("$evidence", miss.EvidenceId);
                command.Parameters.AddWithValue("$level", miss.Level);
                command.Parameters.AddWithValue("$model", (object?)miss.Model ?? DBNull.Value);
                command.Parameters.AddWithValue("$reasoning", (object?)miss.ReasoningLevel ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", miss.Source);
                command.Parameters.AddWithValue("$recordedAt", Stamp(miss.RecordedAtUtc));
                added += command.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        });
    }

    public IReadOnlyList<ReviewObservation> ReadObservations(ReviewDelivery delivery)
        => Read(connection => ReadObservations(connection, DeliveryFilter, command => AddDelivery(command, delivery)), []);

    public IReadOnlyList<ReviewDefectEvidence> ReadEvidence(ReviewDelivery delivery)
        => Read(connection => ReadEvidence(connection, DeliveryFilter, command => AddDelivery(command, delivery)), []);

    /// <summary>Observations at or after <paramref name="sinceUtc"/>, optionally for the given projects only.</summary>
    public IReadOnlyList<ReviewObservation> ReadObservations(DateTime? sinceUtc, Func<string, bool>? projectFilter = null)
    {
        var rows = Read(connection => ReadObservations(connection, "observed_at >= $since",
            command => command.Parameters.AddWithValue("$since", Stamp(sinceUtc ?? DateTime.MinValue))), []);
        return projectFilter is null ? rows : rows.Where(row => projectFilter(row.Delivery.Project)).ToArray();
    }

    public IReadOnlyList<ReviewDefectEvidence> ReadEvidence(DateTime? sinceUtc, Func<string, bool>? projectFilter = null)
    {
        var rows = Read(connection => ReadEvidence(connection, "observed_at >= $since",
            command => command.Parameters.AddWithValue("$since", Stamp(sinceUtc ?? DateTime.MinValue))), []);
        return projectFilter is null ? rows : rows.Where(row => projectFilter(row.Delivery.Project)).ToArray();
    }

    /// <summary>Every miss whose observation is in <paramref name="observationIds"/>.</summary>
    public IReadOnlyList<ReviewMiss> ReadMisses(IReadOnlyCollection<string> observationIds)
    {
        if (observationIds.Count == 0) return [];
        var all = Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT observation_id, evidence_id, level, model, reasoning, source, recorded_at FROM misses;";
            using var reader = command.ExecuteReader();
            var rows = new List<ReviewMiss>();
            while (reader.Read())
            {
                rows.Add(new ReviewMiss(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    NullableString(reader, 3), NullableString(reader, 4), reader.GetString(5), Parse(reader.GetString(6))));
            }
            return (IReadOnlyList<ReviewMiss>)rows;
        }, []);
        var wanted = observationIds as IReadOnlySet<string> ?? observationIds.ToHashSet(StringComparer.Ordinal);
        return all.Where(miss => wanted.Contains(miss.ObservationId)).ToArray();
    }

    /// <summary>The most recent delivery recorded for a task, by attempt then round.</summary>
    public ReviewDelivery? LatestDelivery(string project, string jobId)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT attempt, round FROM (
                    SELECT attempt, round FROM reviews WHERE project = $project AND job_id = $job
                    UNION ALL
                    SELECT attempt, round FROM evidence WHERE project = $project AND job_id = $job)
                ORDER BY attempt DESC, round DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$job", jobId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new ReviewDelivery(project, jobId, reader.GetInt32(0), reader.GetInt32(1))
                : null;
        }, null);

    private const string DeliveryFilter = "project = $project AND job_id = $job AND attempt = $attempt AND round = $round";

    private static IReadOnlyList<ReviewObservation> ReadObservations(
        SqliteConnection connection, string filter, Action<SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, project, job_id, attempt, round, level, subject, model, reasoning,
                input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, duration_ms, cost_usd,
                verdict, outcome, f_blocker, f_major, f_minor, f_nit, f_unclassified, observed_at, origin, evidence_ref
            FROM reviews WHERE {filter} ORDER BY observed_at, id;
            """;
        bind(command);
        using var reader = command.ExecuteReader();
        var rows = new List<ReviewObservation>();
        while (reader.Read())
        {
            rows.Add(new ReviewObservation(
                reader.GetString(0),
                new ReviewDelivery(reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)),
                reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8),
                reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13),
                reader.IsDBNull(14) ? null : decimal.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                reader.GetString(15), reader.GetString(16),
                new ReviewFindingCounts(reader.GetInt32(17), reader.GetInt32(18), reader.GetInt32(19),
                    reader.GetInt32(20), reader.GetInt32(21)),
                Parse(reader.GetString(22)), reader.GetString(23), NullableString(reader, 24)));
        }
        return rows;
    }

    private static IReadOnlyList<ReviewDefectEvidence> ReadEvidence(
        SqliteConnection connection, string filter, Action<SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, project, job_id, attempt, round, source, summary, observed_at, recorded_by,
                finder_level, finder_observation_id, severity, reference
            FROM evidence WHERE {filter} ORDER BY observed_at, id;
            """;
        bind(command);
        using var reader = command.ExecuteReader();
        var rows = new List<ReviewDefectEvidence>();
        while (reader.Read())
        {
            rows.Add(new ReviewDefectEvidence(
                reader.GetString(0),
                new ReviewDelivery(reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)),
                reader.GetString(5), reader.GetString(6), Parse(reader.GetString(7)), reader.GetString(8),
                NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12)));
        }
        return rows;
    }

    private static void AddDelivery(SqliteCommand command, ReviewDelivery delivery)
    {
        command.Parameters.AddWithValue("$project", delivery.Project);
        command.Parameters.AddWithValue("$job", delivery.JobId);
        command.Parameters.AddWithValue("$attempt", delivery.PipelineAttempt);
        command.Parameters.AddWithValue("$round", delivery.DeliveryRound);
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // Fixed-width round-trip stamps sort lexically, so range filters run in SQL.
    private static string Stamp(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private T Write<T>(Func<SqliteConnection, T> action)
    {
        if (_databasePath is null) return default!;
        lock (_gate)
        {
            using var connection = Open(create: true)!;
            return action(connection);
        }
    }

    private T Read<T>(Func<SqliteConnection, T> action, T empty)
    {
        if (_databasePath is null) return empty;
        lock (_gate)
        {
            using var connection = Open(create: false);
            return connection is null ? empty : action(connection);
        }
    }

    private SqliteConnection? Open(bool create)
    {
        if (!create && !File.Exists(_databasePath)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        // Pooling off: a pooled handle would keep the file open after use and
        // block test cleanup and backups, as in ProjectChatIndex.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (!_schemaReady)
        {
            EnsureSchema(connection);
            _schemaReady = true;
        }
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS reviews (
                id TEXT PRIMARY KEY, project TEXT NOT NULL, job_id TEXT NOT NULL, attempt INTEGER NOT NULL,
                round INTEGER NOT NULL, level TEXT NOT NULL, subject TEXT NOT NULL, model TEXT, reasoning TEXT,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL, cache_read_tokens INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL, duration_ms INTEGER NOT NULL, cost_usd TEXT,
                verdict TEXT NOT NULL, outcome TEXT NOT NULL, f_blocker INTEGER NOT NULL, f_major INTEGER NOT NULL,
                f_minor INTEGER NOT NULL, f_nit INTEGER NOT NULL, f_unclassified INTEGER NOT NULL,
                observed_at TEXT NOT NULL, origin TEXT NOT NULL, evidence_ref TEXT);
            CREATE INDEX IF NOT EXISTS reviews_delivery ON reviews (project, job_id, attempt, round);
            CREATE INDEX IF NOT EXISTS reviews_observed ON reviews (observed_at);
            CREATE TABLE IF NOT EXISTS evidence (
                id TEXT PRIMARY KEY, project TEXT NOT NULL, job_id TEXT NOT NULL, attempt INTEGER NOT NULL,
                round INTEGER NOT NULL, source TEXT NOT NULL, summary TEXT NOT NULL, observed_at TEXT NOT NULL,
                recorded_by TEXT NOT NULL, finder_level TEXT, finder_observation_id TEXT, severity TEXT, reference TEXT);
            CREATE INDEX IF NOT EXISTS evidence_delivery ON evidence (project, job_id, attempt, round);
            CREATE TABLE IF NOT EXISTS misses (
                observation_id TEXT NOT NULL, evidence_id TEXT NOT NULL, level TEXT NOT NULL, model TEXT,
                reasoning TEXT, source TEXT NOT NULL, recorded_at TEXT NOT NULL,
                PRIMARY KEY (observation_id, evidence_id));
            """;
        command.ExecuteNonQuery();
    }
}
