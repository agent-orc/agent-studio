using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Git;

/// <summary>
/// Placement B of the Git fact store (docs/git-facts-architecture, section 03,
/// slice S2): SQLite <c>git-facts.db</c> in the backend state directory, with
/// the tables of <see cref="GitFactStoreSchema"/>.
/// <para>
/// <see cref="GitStateIndexService"/> is the only writer. A repository
/// generation, its card rows and the generation pointer are written in one
/// transaction in WAL mode, so a crash before commit leaves the previous
/// generation current. Request handlers never open this store; they read the
/// in-memory snapshot the producer swaps in after a commit.
/// </para>
/// <para>
/// The store holds derived facts only. <see cref="Open"/> runs
/// <c>PRAGMA quick_check</c>; a corrupt or foreign file is moved aside, a typed
/// <see cref="GitFactStoreHealthEvent"/> is raised and the producer rebuilds
/// from Git while readers answer warming per card.
/// </para>
/// </summary>
public sealed partial class GitFactStore
{
    internal const string StorePathKey = "GitFacts:StorePath";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _opened;
    private bool _unavailable;

    public GitFactStore(IConfiguration configuration, ILogger<GitFactStore> logger)
        : this(ResolvePath(configuration), logger, TimeProvider.System)
    {
    }

    internal GitFactStore(string? path, ILogger logger, TimeProvider timeProvider)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Raised when the store detects and recovers from a corrupt file.</summary>
    public event Action<GitFactStoreHealthEvent>? HealthEventRaised;

    internal string? DatabasePath => _path;

    /// <summary>True once <see cref="Open"/> succeeded; every operation is a no-op otherwise.</summary>
    internal bool IsAvailable
    {
        get { lock (_gate) return _opened && !_unavailable; }
    }

    /// <summary>The last typed health event, for the admin Git telemetry surface.</summary>
    public GitFactStoreHealthEvent? LastHealthEvent { get; private set; }

    internal static string? ResolvePath(IConfiguration configuration)
    {
        var configured = configuration[StorePathKey];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var taskRepository = configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(taskRepository)
            ? null : Path.Combine(taskRepository, ".metadata", GitFactStoreSchema.FileName);
    }

    /// <summary>
    /// Opens (or creates) the store and verifies it. Idempotent. Returns
    /// <c>disabled</c>, <c>created</c>, <c>opened</c>, <c>recovered</c> or
    /// <c>unavailable</c>.
    /// </summary>
    internal string Open()
    {
        lock (_gate)
        {
            if (_path is null) return "disabled";
            if (_unavailable) return "unavailable";
            if (_opened) return "opened";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var existed = File.Exists(_path);
                var failure = existed ? Verify(_path) : null;
                if (failure is not null) MoveAside(failure);
                using (var connection = Connect(_path))
                {
                    foreach (var statement in GitFactStoreSchema.CreateStatements)
                        Execute(connection, null, statement);
                    Execute(connection, null, $"PRAGMA user_version = {GitFactStoreSchema.Version}");
                }
                _opened = true;
                return failure is not null ? "recovered" : existed ? "opened" : "created";
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Without a store the producer keeps its in-memory behaviour:
                // facts are computed and served, only restart persistence is lost.
                _unavailable = true;
                _logger.LogWarning(ex, "git-facts-store-unavailable path={Path}", _path);
                return "unavailable";
            }
        }
    }

    /// <summary>
    /// Takes the producer lease for a repository: the stored epoch plus one.
    /// Every later publish of this producer carries it; a producer that took
    /// the lease after it makes this producer's publishes fail the fence.
    /// </summary>
    internal long AcquireProducerEpoch(string repoId, string producerId)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return 0;
            using var connection = Connect(_path!);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = Command(connection, transaction, """
                INSERT INTO git_repositories (repo_id, producer_id, producer_epoch, updated_at)
                VALUES ($repo, $producer,
                  1 + COALESCE((SELECT MAX(producer_epoch) FROM git_ref_generations WHERE repo_id = $repo), 0),
                  $now)
                ON CONFLICT (repo_id) DO UPDATE SET
                  producer_id = $producer,
                  producer_epoch = 1 + MAX(git_repositories.producer_epoch,
                    COALESCE((SELECT MAX(producer_epoch) FROM git_ref_generations WHERE repo_id = $repo), 0)),
                  updated_at = $now
                RETURNING producer_epoch
                """, ("$repo", repoId), ("$producer", producerId), ("$now", Now()));
            var epoch = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return epoch;
        }
    }

    /// <summary>
    /// Publishes one repository generation in one transaction: the generation
    /// row (the pointer: it becomes the only <c>current</c> row), the
    /// repository mapping and configuration, and every card row. A publish
    /// whose producer epoch is lower than the stored one is rejected and
    /// writes nothing. <paramref name="beforeCommit"/> is a test seam that runs
    /// inside the open transaction.
    /// </summary>
    internal GitFactPublishResult Publish(GitFactPublication publication, Action? beforeCommit = null)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return new GitFactPublishResult(GitFactPublishOutcome.StoreUnavailable, 0, 0);
            var repository = publication.Repository;
            var generation = publication.Generation;
            using var connection = Connect(_path!);
            using var transaction = connection.BeginTransaction(deferred: false);
            var storedEpoch = Scalar(connection, transaction, """
                SELECT MAX(COALESCE((SELECT producer_epoch FROM git_repositories WHERE repo_id = $repo), 0),
                           COALESCE((SELECT MAX(producer_epoch) FROM git_ref_generations WHERE repo_id = $repo), 0))
                """, ("$repo", repository.RepoId));
            if (!GitFactPublishPolicy.Accepts(generation.ProducerEpoch, storedEpoch))
            {
                transaction.Rollback();
                return new GitFactPublishResult(GitFactPublishOutcome.RejectedStaleEpoch, 0, storedEpoch);
            }

            var next = 1 + Scalar(connection, transaction,
                "SELECT COALESCE(MAX(generation), 0) FROM git_ref_generations WHERE repo_id = $repo",
                ("$repo", repository.RepoId));
            Execute(connection, transaction,
                "UPDATE git_ref_generations SET state = $superseded WHERE repo_id = $repo AND state = $current",
                ("$superseded", GitFactStoreSchema.SupersededGeneration),
                ("$current", GitFactStoreSchema.CurrentGeneration), ("$repo", repository.RepoId));
            Execute(connection, transaction, """
                INSERT INTO git_ref_generations (repo_id, generation, ref_object_ids, ref_fingerprint,
                  config_generation, settings_version, task_input_signature, input_version,
                  producer_id, producer_epoch, published_at, state)
                VALUES ($repo, $generation, $refs, $fingerprint, $configGeneration, $settings, $tasks,
                  $input, $producer, $epoch, $published, $current)
                """,
                ("$repo", repository.RepoId), ("$generation", next),
                ("$refs", JsonSerializer.Serialize(generation.RefObjectIds, Json)),
                ("$fingerprint", generation.RefFingerprint), ("$configGeneration", generation.ConfigGeneration),
                ("$settings", generation.SettingsVersion), ("$tasks", generation.TaskInputSignature),
                ("$input", generation.InputVersion), ("$producer", generation.ProducerId),
                ("$epoch", generation.ProducerEpoch), ("$published", generation.PublishedAt.ToUnixTimeMilliseconds()),
                ("$current", GitFactStoreSchema.CurrentGeneration));
            Execute(connection, transaction,
                "DELETE FROM git_ref_generations WHERE repo_id = $repo AND generation <= $oldest",
                ("$repo", repository.RepoId), ("$oldest", next - GitFactStoreSchema.RetainedGenerations - 1));
            Execute(connection, transaction, """
                INSERT INTO git_repositories (repo_id, canonical_root, workspace_root, origin_url,
                  config_signature, config_sources, config_generation, producer_id, producer_epoch, updated_at)
                VALUES ($repo, $root, $workspace, $origin, $signature, $sources, $configGeneration,
                  $producer, $epoch, $now)
                ON CONFLICT (repo_id) DO UPDATE SET
                  canonical_root = excluded.canonical_root, workspace_root = excluded.workspace_root,
                  origin_url = excluded.origin_url, config_signature = excluded.config_signature,
                  config_sources = excluded.config_sources, config_generation = excluded.config_generation,
                  producer_id = excluded.producer_id,
                  producer_epoch = MAX(git_repositories.producer_epoch, excluded.producer_epoch),
                  updated_at = excluded.updated_at
                """,
                ("$repo", repository.RepoId), ("$root", repository.CanonicalRoot),
                ("$workspace", repository.WorkspaceRoot), ("$origin", repository.Config?.OriginUrl),
                ("$signature", repository.Config?.Signature),
                ("$sources", repository.Config is null ? null : SerializeSources(repository.Config)),
                ("$configGeneration", repository.ConfigGeneration), ("$producer", generation.ProducerId),
                ("$epoch", generation.ProducerEpoch), ("$now", Now()));
            Execute(connection, transaction, "DELETE FROM card_git_facts WHERE repo_id = $repo",
                ("$repo", repository.RepoId));
            using (var insert = Command(connection, transaction, """
                INSERT INTO card_git_facts (repo_id, task_key, input_signature, generation, facts, error_code)
                VALUES ($repo, $key, $signature, $generation, $facts, $error)
                """, ("$repo", repository.RepoId), ("$key", ""), ("$signature", ""),
                ("$generation", next), ("$facts", null), ("$error", null)))
            {
                foreach (var card in publication.Cards)
                {
                    insert.Parameters["$key"].Value = card.TaskKey;
                    insert.Parameters["$signature"].Value = card.InputSignature;
                    insert.Parameters["$facts"].Value = (object?)card.Facts ?? DBNull.Value;
                    insert.Parameters["$error"].Value = (object?)card.ErrorCode ?? DBNull.Value;
                    insert.ExecuteNonQuery();
                }
            }
            // S3: the card facts of this run commit with the generation pointer.
            if (publication.CardFacts is { } cardFacts) WriteCardFacts(connection, transaction, cardFacts, next);
            beforeCommit?.Invoke();
            transaction.Commit();
            return new GitFactPublishResult(GitFactPublishOutcome.Published, next, storedEpoch);
        }
    }

    /// <summary>The current generation of a repository with its card rows, or null.</summary>
    internal GitFactSnapshot? LoadCurrent(string repoId)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return null;
            using var connection = Connect(_path!);
            GitRefGeneration? generation = null;
            using (var command = Command(connection, null, """
                SELECT generation, ref_object_ids, ref_fingerprint, config_generation, settings_version,
                  task_input_signature, input_version, producer_id, producer_epoch, published_at
                FROM git_ref_generations WHERE repo_id = $repo AND state = $current
                ORDER BY generation DESC LIMIT 1
                """, ("$repo", repoId), ("$current", GitFactStoreSchema.CurrentGeneration)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    generation = new GitRefGeneration(
                        reader.GetInt64(0),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1), Json) ?? [],
                        reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                        reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
                }
            }
            if (generation is null) return null;

            GitRepositoryFacts? repository = null;
            using (var command = Command(connection, null, """
                SELECT canonical_root, workspace_root, origin_url, config_signature, config_sources,
                  config_generation
                FROM git_repositories WHERE repo_id = $repo
                """, ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    var config = reader.IsDBNull(3) || reader.IsDBNull(4) ? null
                        : DeserializeSources(reader.GetString(3), reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.GetString(4));
                    repository = new GitRepositoryFacts(repoId,
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1), config, reader.GetInt64(5));
                }
            }
            if (repository is null) return null;

            var cards = new List<CardGitFactRow>();
            using (var command = Command(connection, null, """
                SELECT task_key, input_signature, facts, error_code FROM card_git_facts
                WHERE repo_id = $repo AND generation = $generation
                """, ("$repo", repoId), ("$generation", generation.Generation)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cards.Add(new CardGitFactRow(reader.GetString(0), reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }
            return new GitFactSnapshot(repository, generation, cards);
        }
    }

    /// <summary>Upserts one repository's producer health row.</summary>
    internal void RecordHealth(GitProducerHealth health)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return;
            using var connection = Connect(_path!);
            Execute(connection, null, """
                INSERT INTO fact_producer_health (repo_id, state, last_good_generation, last_good_at,
                  consecutive_failures, last_error_reason, queue_length, spawns_per_minute,
                  slowest_command, slowest_ms, updated_at)
                VALUES ($repo, $state, $generation, $goodAt, $failures, $reason, $queue, $spawns,
                  $command, $slowMs, $now)
                ON CONFLICT (repo_id) DO UPDATE SET
                  state = excluded.state, last_good_generation = excluded.last_good_generation,
                  last_good_at = excluded.last_good_at, consecutive_failures = excluded.consecutive_failures,
                  last_error_reason = excluded.last_error_reason, queue_length = excluded.queue_length,
                  spawns_per_minute = excluded.spawns_per_minute, slowest_command = excluded.slowest_command,
                  slowest_ms = excluded.slowest_ms, updated_at = excluded.updated_at
                """,
                ("$repo", health.RepoId), ("$state", health.State), ("$generation", health.LastGoodGeneration),
                ("$goodAt", health.LastGoodAt?.ToUnixTimeMilliseconds()), ("$failures", health.ConsecutiveFailures),
                ("$reason", health.LastErrorReason), ("$queue", health.QueueLength),
                ("$spawns", health.SpawnsPerMinute), ("$command", health.SlowestCommand),
                ("$slowMs", health.SlowestMs), ("$now", health.UpdatedAt.ToUnixTimeMilliseconds()));
        }
    }

    internal GitProducerHealth? ReadHealth(string repoId)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return null;
            using var connection = Connect(_path!);
            using var command = Command(connection, null, """
                SELECT state, last_good_generation, last_good_at, consecutive_failures, last_error_reason,
                  queue_length, spawns_per_minute, slowest_command, slowest_ms, updated_at
                FROM fact_producer_health WHERE repo_id = $repo
                """, ("$repo", repoId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new GitProducerHealth(repoId, reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5),
                reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
        }
    }

    private bool EnsureOpen()
    {
        // Callers hold _gate; Monitor is re-entrant, so Open() may take it again.
        if (_path is null || _unavailable) return false;
        return _opened || Open() is "created" or "opened" or "recovered";
    }

    /// <summary>Null when the file is a healthy store of this schema version, else the typed reason.</summary>
    private static string? Verify(string path)
    {
        try
        {
            using var connection = Connect(path);
            using (var check = Command(connection, null, "PRAGMA quick_check"))
            {
                var result = check.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.Ordinal))
                    return "quick-check-failed";
            }
            var version = Scalar(connection, null, "PRAGMA user_version");
            return version is 0 or GitFactStoreSchema.Version ? null : "schema-version-" + version;
        }
        catch (SqliteException ex)
        {
            return "sqlite-error-" + ex.SqliteErrorCode.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void MoveAside(string reason)
    {
        var path = _path!;
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var target = $"{path}.corrupt-{stamp}";
        SqliteConnection.ClearAllPools();
        File.Move(path, target);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + suffix)) File.Move(path + suffix, target + suffix);
        }
        var evt = new GitFactStoreHealthEvent(GitFactStoreHealthEvent.CorruptStoreRecovered, path, target,
            reason, _timeProvider.GetUtcNow());
        LastHealthEvent = evt;
        _logger.LogWarning("git-facts-store-corrupt path={Path} movedTo={MovedTo} reason={Reason}",
            path, target, reason);
        try { HealthEventRaised?.Invoke(evt); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "git-facts-store health event handler failed");
        }
    }

    /// <summary>
    /// A pool-free connection (as <c>TelemetryStore</c> and <c>ProjectChatIndex</c>
    /// use), so no native handle keeps the file open after dispose and a
    /// corrupt file can be moved aside.
    /// </summary>
    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        Execute(connection, null, """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            """);
        return connection;
    }

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static string SerializeSources(GitEffectiveConfig config)
        => JsonSerializer.Serialize(new StoredConfigSources(
            config.Files.Select(file => new StoredConfigFile(file.Path, file.Hash)).ToArray(),
            config.AbsentSources ?? [], config.DependsOnHead), Json);

    private static GitEffectiveConfig? DeserializeSources(string signature, string? origin, string json)
    {
        var sources = JsonSerializer.Deserialize<StoredConfigSources>(json, Json);
        return sources is null ? null : new GitEffectiveConfig(signature, origin,
            sources.Files.Select(file => new GitConfigFileStamp(file.Path, file.Hash)).ToArray(),
            sources.Absent, sources.DependsOnHead);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private sealed record StoredConfigFile(string Path, string? Hash);
    private sealed record StoredConfigSources(IReadOnlyList<StoredConfigFile> Files,
        IReadOnlyList<string> Absent, bool DependsOnHead);
}

/// <summary>A typed store health signal (raised once per recovery).</summary>
public sealed record GitFactStoreHealthEvent(string Kind, string Path, string? MovedTo, string Reason,
    DateTimeOffset At)
{
    public const string CorruptStoreRecovered = "git-facts-store-corrupt";
}

/// <summary>One repository row: root mappings, the resolved configuration and its generation.</summary>
internal sealed record GitRepositoryFacts(string RepoId, string? CanonicalRoot, string? WorkspaceRoot,
    GitEffectiveConfig? Config, long ConfigGeneration);

/// <summary>The input identity of a generation, as the producer publishes it.</summary>
internal sealed record GitRefGenerationDraft(
    IReadOnlyDictionary<string, string> RefObjectIds,
    string RefFingerprint,
    long ConfigGeneration,
    string SettingsVersion,
    string TaskInputSignature,
    string InputVersion,
    string ProducerId,
    long ProducerEpoch,
    DateTimeOffset PublishedAt);

/// <summary>A stored generation, numbered per repository.</summary>
internal sealed record GitRefGeneration(
    long Generation,
    IReadOnlyDictionary<string, string> RefObjectIds,
    string RefFingerprint,
    long ConfigGeneration,
    string SettingsVersion,
    string TaskInputSignature,
    string InputVersion,
    string ProducerId,
    long ProducerEpoch,
    DateTimeOffset PublishedAt);

/// <summary>One card row. <see cref="Facts"/> is the codec document; <see cref="ErrorCode"/> the typed failure.</summary>
internal sealed record CardGitFactRow(string TaskKey, string InputSignature, string? Facts, string? ErrorCode);

internal sealed record GitFactPublication(GitRepositoryFacts Repository, GitRefGenerationDraft Generation,
    IReadOnlyList<CardGitFactRow> Cards, GitCardFactsWrite? CardFacts = null);

internal sealed record GitFactSnapshot(GitRepositoryFacts Repository, GitRefGeneration Generation,
    IReadOnlyList<CardGitFactRow> Cards);

internal enum GitFactPublishOutcome { Published, RejectedStaleEpoch, StoreUnavailable }

internal sealed record GitFactPublishResult(GitFactPublishOutcome Outcome, long Generation, long StoredEpoch);

internal sealed record GitProducerHealth(
    string RepoId,
    string State,
    long? LastGoodGeneration,
    DateTimeOffset? LastGoodAt,
    int ConsecutiveFailures,
    string? LastErrorReason,
    int QueueLength,
    double SpawnsPerMinute,
    string? SlowestCommand,
    long? SlowestMs,
    DateTimeOffset UpdatedAt);
