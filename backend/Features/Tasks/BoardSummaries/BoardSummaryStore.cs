using System.Globalization;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tasks;

/// <summary>
/// The persisted slim board (docs/git-facts-architecture, slice S5): SQLite
/// <c>board-summaries.db</c> next to <c>git-facts.db</c> in the backend state
/// directory, with the tables of <see cref="BoardSummaryStoreSchema"/>.
/// <para>
/// <see cref="BoardSummaryPersister"/> is the only writer; it replaces the
/// rows of the projects that changed in one WAL transaction. The only reader
/// is <see cref="RestoredBoard"/>, once per process start. Request handlers
/// never open this store.
/// </para>
/// <para>
/// The rows are derived from task folders and published facts. A file that
/// cannot be read, fails <c>PRAGMA quick_check</c> or carries another schema
/// version is deleted and rebuilt by the next write; until then a cold board
/// read waits for the first publication, as it did before S5.
/// </para>
/// </summary>
public sealed class BoardSummaryStore
{
    internal const string StorePathKey = "BoardSummaries:StorePath";

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _opened;

    public BoardSummaryStore(IConfiguration configuration, ILogger<BoardSummaryStore> logger)
        : this(ResolvePath(configuration), logger)
    {
    }

    internal BoardSummaryStore(string? path, ILogger logger)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _logger = logger;
    }

    internal string? DatabasePath => _path;

    internal static string? ResolvePath(IConfiguration configuration)
    {
        var configured = configuration[StorePathKey];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var taskRepository = configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(taskRepository)
            ? null : Path.Combine(taskRepository, ".metadata", BoardSummaryStoreSchema.FileName);
    }

    /// <summary>
    /// Every persisted project with its cards in index order. Empty when the
    /// store is disabled, absent, unreadable or of another schema version; a
    /// read never creates the file.
    /// </summary>
    public IReadOnlyList<BoardSummaryProject> Load()
    {
        lock (_gate)
        {
            if (_path is null || !File.Exists(_path)) return [];
            try
            {
                // Not read-only: a WAL file needs its shared-memory index.
                using var connection = Connect(_path, SqliteOpenMode.ReadWrite);
                if (Scalar(connection, null, "PRAGMA user_version") != BoardSummaryStoreSchema.Version) return [];
                var cards = new Dictionary<string, List<BoardSummaryCard>>(StringComparer.Ordinal);
                using (var command = Command(connection, null, """
                    SELECT project_name, task_key, watch_path, state, fixture, lane_rank, scan_rank, summary
                    FROM board_summary_cards ORDER BY project_name, scan_rank
                    """))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var project = reader.GetString(0);
                        if (!cards.TryGetValue(project, out var list)) cards[project] = list = [];
                        list.Add(new BoardSummaryCard(project, reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetInt64(4) != 0, reader.GetInt32(5), reader.GetInt32(6),
                            (byte[])reader.GetValue(7)));
                    }
                }
                var projects = new List<BoardSummaryProject>();
                using (var command = Command(connection, null, """
                    SELECT project_name, index_generation, persisted_at, git_state_at, git_stale, content_hash
                    FROM board_summary_projects ORDER BY project_name
                    """))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetString(0);
                        projects.Add(new BoardSummaryProject(name, reader.GetInt64(1),
                            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                            reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                            reader.GetInt64(4) != 0, reader.GetString(5),
                            cards.GetValueOrDefault(name) ?? []));
                    }
                }
                return projects;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                or InvalidCastException)
            {
                _logger.LogWarning(ex, "board-summaries-unreadable path={Path}", _path);
                return [];
            }
        }
    }

    /// <summary>
    /// Replaces the rows of <paramref name="changed"/> and deletes the
    /// projects in <paramref name="removed"/>, in one transaction. False when
    /// the store is disabled or the write failed; the previous rows then stay.
    /// </summary>
    public bool Write(IReadOnlyList<BoardSummaryProject> changed, IReadOnlyList<string> removed)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return false;
            try
            {
                using var connection = Connect(_path!, SqliteOpenMode.ReadWriteCreate);
                using var transaction = connection.BeginTransaction(deferred: false);
                foreach (var name in removed.Concat(changed.Select(project => project.ProjectName)))
                {
                    Execute(connection, transaction, "DELETE FROM board_summary_cards WHERE project_name = $p", ("$p", name));
                    Execute(connection, transaction, "DELETE FROM board_summary_projects WHERE project_name = $p", ("$p", name));
                }
                foreach (var project in changed)
                {
                    Execute(connection, transaction, """
                        INSERT INTO board_summary_projects
                          (project_name, index_generation, persisted_at, git_state_at, git_stale, content_hash, card_count)
                        VALUES ($p, $g, $at, $git, $stale, $hash, $count)
                        """,
                        ("$p", project.ProjectName), ("$g", project.IndexGeneration),
                        ("$at", project.PersistedAt.ToUnixTimeMilliseconds()),
                        ("$git", project.GitStateAt?.ToUnixTimeMilliseconds()),
                        ("$stale", project.GitStale ? 1 : 0), ("$hash", project.ContentHash),
                        ("$count", project.Cards.Count));
                    using var insert = Command(connection, transaction, """
                        INSERT INTO board_summary_cards
                          (project_name, scan_rank, task_key, watch_path, state, fixture, lane_rank, summary)
                        VALUES ($p, $scan, $key, $watch, $state, $fixture, $lane, $summary)
                        """, ("$p", project.ProjectName), ("$scan", 0), ("$key", ""), ("$watch", ""),
                        ("$state", ""), ("$fixture", 0), ("$lane", 0), ("$summary", Array.Empty<byte>()));
                    foreach (var card in project.Cards)
                    {
                        insert.Parameters["$scan"].Value = card.ScanRank;
                        insert.Parameters["$key"].Value = card.TaskKey;
                        insert.Parameters["$watch"].Value = card.WatchPath;
                        insert.Parameters["$state"].Value = card.State;
                        insert.Parameters["$fixture"].Value = card.Fixture ? 1 : 0;
                        insert.Parameters["$lane"].Value = card.LaneRank;
                        insert.Parameters["$summary"].Value = card.Summary;
                        insert.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
                return true;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "board-summaries-write-failed path={Path}", _path);
                return false;
            }
        }
    }

    // Caller holds _gate. Creates the file and schema on the first write; a
    // corrupt file or another schema version is discarded first.
    private bool EnsureOpen()
    {
        if (_path is null) return false;
        if (_opened) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && Verify(_path) is { } failure)
            {
                _logger.LogWarning("board-summaries-discarded path={Path} reason={Reason}", _path, failure);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    File.Delete(_path + suffix);
            }
            using var connection = Connect(_path, SqliteOpenMode.ReadWriteCreate);
            foreach (var statement in BoardSummaryStoreSchema.CreateStatements)
                Execute(connection, null, statement);
            Execute(connection, null, $"PRAGMA user_version = {BoardSummaryStoreSchema.Version}");
            _opened = true;
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "board-summaries-unavailable path={Path}", _path);
            return false;
        }
    }

    private static string? Verify(string path)
    {
        try
        {
            using var connection = Connect(path, SqliteOpenMode.ReadWrite);
            using (var check = Command(connection, null, "PRAGMA quick_check"))
            {
                var result = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) return "quick_check: " + result;
            }
            var version = Scalar(connection, null, "PRAGMA user_version");
            return version is 0 or BoardSummaryStoreSchema.Version ? null : $"schema version {version}";
        }
        catch (SqliteException ex)
        {
            return "open: " + ex.Message;
        }
    }

    /// <summary>A pool-free connection, so no native handle keeps the file open after dispose.</summary>
    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        return connection;
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

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }
}
