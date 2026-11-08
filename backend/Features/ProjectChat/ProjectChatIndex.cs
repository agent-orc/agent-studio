using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentStudio.ProjectChat;

/// <summary>
/// Per-project SQLite FTS5 index over the chat markdown corpus. The DB
/// lives at <c>&lt;projectFolder&gt;/chat/.index.db</c>; the markdown
/// files are the source of truth (ADR-0023). A missing or corrupt
/// index is non-fatal — callers ask <see cref="EnsureFresh"/> to
/// rebuild it.
///
/// We keep the connection footprint tiny (one open connection per
/// invocation, opened with shared cache off) so an accidental
/// concurrent reader cannot lock writers out for long. Slice D's
/// throughput is dominated by FTS scans, not by INSERTs, so the cost
/// of opening a connection per request is negligible relative to the
/// query itself.
/// </summary>
public sealed class ProjectChatIndex
{
    private readonly ProjectChatStore _store;
    private readonly ILogger<ProjectChatIndex> _logger;

    public ProjectChatIndex(ProjectChatStore store, ILogger<ProjectChatIndex> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent: opens the index DB at the canonical location, creates
    /// the FTS5 virtual table + a tiny <c>meta</c> table if missing, and
    /// returns the (possibly rebuilt) connection string. The caller must
    /// open and dispose its own connection.
    ///
    /// <para>Pooling stays OFF on purpose: Microsoft.Data.Sqlite's pool keeps
    /// the native handle - and with it the <c>.index.db</c> file - open after
    /// <c>Dispose</c> for the lifetime of the process. On Windows that single
    /// pooled handle makes the data directory un-zippable
    /// (<c>backup-create</c> failed with a sharing violation on
    /// <c>chat/.index.db</c>) and the project folder un-deletable. Closing the
    /// handle with the connection is the "one open connection per invocation"
    /// footprint this class promises; the per-call open costs well under a
    /// millisecond against the FTS scan it precedes.</para>
    /// </summary>
    private static string ConnectionString(string projectFolder)
    {
        var db = ProjectChatPaths.IndexDbPath(projectFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    private static SqliteConnection Open(string projectFolder)
    {
        var conn = new SqliteConnection(ConnectionString(projectFolder));
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS turns USING fts5(
                turn_id UNINDEXED,
                author UNINDEXED,
                kind UNINDEXED,
                ts UNINDEXED,
                body,
                path UNINDEXED,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuild the index when (a) the DB is missing or empty, (b) any
    /// markdown file under <c>chat/</c> has an mtime newer than the
    /// index's <c>meta.last_built</c>, or (c) the on-disk file count
    /// disagrees with the indexed row count. The check is bounded by
    /// the directory walk; on a 5 000-turn project the walk takes
    /// well under 200 ms, which we amortise across the lifetime of the
    /// search session.
    /// </summary>
    public void EnsureFresh(string projectFolder, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var conn = Open(projectFolder);
            var (lastBuilt, indexedCount) = ReadMeta(conn);
            var (latestMtime, fileCount) = ScanCorpus(projectFolder, ct);

            if (fileCount == 0 && indexedCount > 0)
            {
                // Project's chat folder was wiped from disk. Drop the index.
                Truncate(conn);
                WriteMeta(conn, DateTime.UtcNow, 0);
                return;
            }

            if (indexedCount == fileCount && lastBuilt.HasValue && latestMtime <= lastBuilt.Value) return;

            Rebuild(conn, projectFolder, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure project chat index for {Project}", projectFolder);
        }
    }

    private (DateTime LatestMtime, int FileCount) ScanCorpus(
        string projectFolder, CancellationToken ct)
    {
        var chatRoot = ProjectChatPaths.ChatRoot(projectFolder);
        if (!Directory.Exists(chatRoot)) return (DateTime.MinValue, 0);
        int count = 0;
        DateTime latest = DateTime.MinValue;
        foreach (var month in ProjectChatPaths.EnumerateMonthFolders(chatRoot))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in Directory.EnumerateFiles(month, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                count++;
                var m = File.GetLastWriteTimeUtc(f);
                if (m > latest) latest = m;
            }
        }
        return (latest, count);
    }

    private static void Truncate(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM turns;";
        cmd.ExecuteNonQuery();
    }

    private void Rebuild(SqliteConnection conn, string projectFolder, CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        Truncate(conn);

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO turns (turn_id, author, kind, ts, body, path)
                VALUES ($turnId, $author, $kind, $ts, $body, $path);
                """;
            var pTurn = insert.CreateParameter(); pTurn.ParameterName = "$turnId"; insert.Parameters.Add(pTurn);
            var pAuthor = insert.CreateParameter(); pAuthor.ParameterName = "$author"; insert.Parameters.Add(pAuthor);
            var pKind = insert.CreateParameter(); pKind.ParameterName = "$kind"; insert.Parameters.Add(pKind);
            var pTs = insert.CreateParameter(); pTs.ParameterName = "$ts"; insert.Parameters.Add(pTs);
            var pBody = insert.CreateParameter(); pBody.ParameterName = "$body"; insert.Parameters.Add(pBody);
            var pPath = insert.CreateParameter(); pPath.ParameterName = "$path"; insert.Parameters.Add(pPath);

            foreach (var (path, turn) in _store.EnumerateAll(projectFolder))
            {
                ct.ThrowIfCancellationRequested();
                pTurn.Value = turn.TurnId;
                pAuthor.Value = turn.Author;
                pKind.Value = turn.Kind;
                pTs.Value = turn.Ts.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                pBody.Value = turn.Body ?? "";
                pPath.Value = path;
                insert.ExecuteNonQuery();
            }
        }

        ct.ThrowIfCancellationRequested();
        tx.Commit();

        var (_, fileCount) = ScanCorpus(projectFolder, ct);
        WriteMeta(conn, DateTime.UtcNow, fileCount);
    }

    /// <summary>
    /// Replace one row by turn-id (used after a single file edit/append).
    /// Cheaper than a full rebuild and used by the appender path so the
    /// index is up-to-date the moment the markdown lands on disk.
    /// </summary>
    public void Upsert(string projectFolder, ProjectChatTurn turn, string filePath)
    {
        try
        {
            using var conn = Open(projectFolder);
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM turns WHERE turn_id = $turnId;";
                del.Parameters.AddWithValue("$turnId", turn.TurnId);
                del.ExecuteNonQuery();
            }
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO turns (turn_id, author, kind, ts, body, path)
                    VALUES ($turnId, $author, $kind, $ts, $body, $path);
                    """;
                ins.Parameters.AddWithValue("$turnId", turn.TurnId);
                ins.Parameters.AddWithValue("$author", turn.Author);
                ins.Parameters.AddWithValue("$kind", turn.Kind);
                ins.Parameters.AddWithValue("$ts", turn.Ts.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$body", turn.Body ?? "");
                ins.Parameters.AddWithValue("$path", filePath);
                ins.ExecuteNonQuery();
            }
            tx.Commit();

            using var meta = conn.CreateCommand();
            meta.CommandText = "SELECT COUNT(*) FROM turns;";
            var n = Convert.ToInt32(meta.ExecuteScalar(), CultureInfo.InvariantCulture);
            WriteMeta(conn, DateTime.UtcNow, n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upsert into project chat index failed for {TurnId}", turn.TurnId);
        }
    }

    public List<ProjectChatSearchHit> Search(string projectFolder, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        if (limit > 100) limit = 100;
        try
        {
            using var conn = Open(projectFolder);
            using var cmd = conn.CreateCommand();
            // BM25-ranked. The `snippet()` call returns up to ~30 tokens
            // around the best match, with `<b>…</b>` highlight markers
            // that the FE renders as <mark>; the empty separators argument
            // is FTS5's "no inter-snippet glue".
            cmd.CommandText = """
                SELECT turn_id, author, kind, ts,
                       snippet(turns, 4, '<b>', '</b>', '…', 24) AS snip,
                       bm25(turns) AS score
                  FROM turns
                 WHERE turns MATCH $q
                 ORDER BY bm25(turns) ASC, ts DESC
                 LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$q", BuildFtsQuery(query));
            cmd.Parameters.AddWithValue("$lim", limit);

            var hits = new List<ProjectChatSearchHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new ProjectChatSearchHit(
                    TurnId: reader.GetString(0),
                    Author: reader.GetString(1),
                    Kind: reader.GetString(2),
                    Ts: DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Snippet: SafeSnippet(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    Score: reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
                ));
            }
            return hits;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // FTS syntax errors come back as generic SQLITE_ERROR; quietly
            // fall back to a literal phrase search instead of crashing the
            // request. Operators see only the empty result set.
            return SearchPhrase(projectFolder, query, limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Project chat search failed for '{Query}'", query);
            return [];
        }
    }

    private List<ProjectChatSearchHit> SearchPhrase(string projectFolder, string query, int limit)
    {
        try
        {
            using var conn = Open(projectFolder);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT turn_id, author, kind, ts,
                       snippet(turns, 4, '<b>', '</b>', '…', 24) AS snip,
                       bm25(turns) AS score
                  FROM turns
                 WHERE turns MATCH $q
                 ORDER BY bm25(turns) ASC, ts DESC
                 LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$q", "\"" + query.Replace("\"", "") + "\"");
            cmd.Parameters.AddWithValue("$lim", limit);

            var hits = new List<ProjectChatSearchHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new ProjectChatSearchHit(
                    TurnId: reader.GetString(0),
                    Author: reader.GetString(1),
                    Kind: reader.GetString(2),
                    Ts: DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Snippet: SafeSnippet(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    Score: reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
                ));
            }
            return hits;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Massage a free-text query into a tolerant FTS5 expression. We
    /// replace every non-alphanumeric character with a space (matching
    /// the unicode61 tokenizer's word-boundary behaviour), split, and
    /// emit one prefix-match term per token so partial words still hit.
    /// FTS5 treats unparenthesised whitespace-separated terms as AND.
    /// </summary>
    private static string BuildFtsQuery(string raw)
    {
        var normalised = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            normalised.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        var pieces = normalised.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in pieces)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p).Append('*');
        }
        return sb.Length == 0 ? "\"\"" : sb.ToString();
    }

    private static string SafeSnippet(string snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return "";
        // Escape HTML except for our marker tags so the FE can render
        // the highlight without inviting injection from message bodies.
        var escaped = System.Net.WebUtility.HtmlEncode(snippet);
        return escaped
            .Replace("&lt;b&gt;", "<b>")
            .Replace("&lt;/b&gt;", "</b>");
    }

    private static (DateTime? LastBuilt, int IndexedCount) ReadMeta(SqliteConnection conn)
    {
        DateTime? lastBuilt = null;
        int indexed;
        using (var meta = conn.CreateCommand())
        {
            meta.CommandText = "SELECT key, value FROM meta;";
            using var reader = meta.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (key == "last_built")
                {
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                        lastBuilt = t;
                }
            }
        }
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM turns;";
            indexed = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return (lastBuilt, indexed);
    }

    private static void WriteMeta(SqliteConnection conn, DateTime lastBuilt, int indexedCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES('last_built', $built)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO meta(key, value) VALUES('indexed_count', $count)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$built", lastBuilt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$count", indexedCount.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}

public sealed record ProjectChatSearchHit(
    string TurnId,
    string Author,
    string Kind,
    DateTime Ts,
    string Snippet,
    double Score
);
