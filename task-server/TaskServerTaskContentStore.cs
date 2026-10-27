using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Task-folder artefacts in the Task Server (pipeline delivery target,
/// section 05 "Task-folder artefacts", migration M3 and M6, AGT-3320). Task
/// files and run artifacts carry a SHA-256 and a size; content above
/// <see cref="TaskServerOptions.ContentInlineLimitBytes"/> lives in the
/// <see cref="TaskContentObjectStore"/> and the row keeps only the pointer.
/// While the workspace is still the write authority the monolith mirrors task
/// files here; release card attribution reads the integration records from
/// these rows instead of scanning task folders.
/// </summary>
public sealed partial class TaskServerStore
{
    /// <summary>Task file that holds the card's integration verdict (monolith <c>IntegrationVerificationStore</c>).</summary>
    public const string IntegrationRecordPath = "integration-verification.json";

    internal const string IntegratedVerifiedState = "integrated-verified";

    private const int MaximumTaskFilePathLength = 400;

    private TaskContentObjectStore? _contentObjects;

    public TaskContentObjectStore ContentObjects => _contentObjects ??= new TaskContentObjectStore(
        _options.ResolveContentObjectStorePath(), _options.ContentInlineLimitBytes);

    internal async Task ApplyTaskContentMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var table in new[] { "task_files", "task_file_revisions" })
        {
            await EnsureColumnAsync(connection, table, "sha256", "TEXT", ct);
            await EnsureColumnAsync(connection, table, "size_bytes", "INTEGER", ct);
            await EnsureColumnAsync(connection, table, "object_stored", "INTEGER NOT NULL DEFAULT 0", ct);
        }
        await EnsureColumnAsync(connection, "artifacts", "object_stored", "INTEGER NOT NULL DEFAULT 0", ct);
        await ExecuteAsync(connection, """
            CREATE INDEX IF NOT EXISTS ix_task_files_path ON task_files(path, task_id);
            """, ct);
        await BackfillTaskFileHashesAsync(connection, "task_files", "task_id, path", ct);
        await BackfillTaskFileHashesAsync(connection, "task_file_revisions", "task_id, path, version", ct);
    }

    /// <summary>Rows written before hashes existed are inline; hash them once.</summary>
    private static async Task BackfillTaskFileHashesAsync(
        SqliteConnection connection, string table, string keyColumns, CancellationToken ct)
    {
        var pending = new List<(object[] Key, string Sha, long Size)>();
        await using (var command = Command(connection,
                         $"SELECT {keyColumns}, content_base64 FROM {table} WHERE sha256 IS NULL;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var keys = new object[reader.FieldCount - 1];
                for (var index = 0; index < keys.Length; index++) keys[index] = reader.GetValue(index);
                var content = Convert.FromBase64String(reader.GetString(reader.FieldCount - 1));
                pending.Add((keys, TaskContentObjectStore.Sha256(content), content.LongLength));
            }
        }
        if (pending.Count == 0) return;
        var names = keyColumns.Split(',', StringSplitOptions.TrimEntries);
        var where = string.Join(" AND ", names.Select(name => $"{name} = ${name}"));
        foreach (var row in pending)
        {
            var parameters = names.Select((name, index) => ("$" + name, (object?)row.Key[index]))
                .Append(("$sha", row.Sha)).Append(("$size", row.Size)).ToArray();
            await ExecuteAsync(connection,
                $"UPDATE {table} SET sha256 = $sha, size_bytes = $size WHERE {where};", ct, parameters);
        }
    }

    /// <summary>Hash, size and the inline column value; content above the inline limit goes to the object store first.</summary>
    internal StoredTaskContent StoreContent(byte[] content)
    {
        var sha = TaskContentObjectStore.Sha256(content);
        if (!ContentObjects.Spills(content.LongLength))
            return new StoredTaskContent(sha, content.LongLength, Convert.ToBase64String(content), false);
        ContentObjects.Put(sha, content);
        return new StoredTaskContent(sha, content.LongLength, string.Empty, true);
    }

    internal byte[] ReadTaskContent(string contentBase64, bool objectStored, string? sha256)
    {
        if (!objectStored) return Convert.FromBase64String(contentBase64);
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidDataException("An object-stored task file has no SHA-256.");
        return ContentObjects.Read(sha256);
    }

    internal byte[] ReadArtifactBytes(object? inlineContent, bool objectStored, string sha256)
        => objectStored
            ? ContentObjects.Read(sha256)
            : inlineContent is byte[] bytes ? bytes : [];

    /// <summary>
    /// Copies workspace task files of one project. An entry whose hash equals
    /// the current row is unchanged; an entry without content whose hash
    /// differs is answered as needed; an entry with content writes the next
    /// revision. A task key the Task Server does not hold in this project is
    /// rejected, so the board mirror has to create the card first.
    /// </summary>
    public async Task<TaskFileMirrorResponse> MirrorWorkspaceTaskFilesAsync(
        string projectIdentity, TaskFileMirrorRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var tasks = request.Tasks ?? [];
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskKey)) throw new ArgumentException("Every mirrored task needs a task key.");
            foreach (var entry in task.Files ?? [])
            {
                RequireTaskFilePath(entry.Path);
                if (string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Length != 64)
                    throw new ArgumentException($"Task file {entry.Path} of {task.TaskKey} needs a SHA-256.");
            }
            foreach (var removed in task.Removed ?? []) RequireTaskFilePath(removed);
        }

        TaskFileMirrorResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (projectId, _) = await RequireBoardProjectAsync(connection, transaction, projectIdentity, ct);
            var now = Iso(UtcNow);
            int written = 0, unchanged = 0, removedCount = 0;
            var needed = new List<TaskFileMirrorNeed>();
            var rejected = new List<BoardImportRejection>();
            foreach (var task in tasks)
            {
                var key = task.TaskKey.Trim().ToUpperInvariant();
                var row = await ReadBoardRowAsync(connection, transaction, key, ct);
                if (row is not { } found || !string.Equals(found.ProjectId, projectId, StringComparison.Ordinal))
                {
                    rejected.Add(new BoardImportRejection(key, "task-missing",
                        $"Task {key} is not a card of this Task Server project yet."));
                    continue;
                }
                foreach (var entry in task.Files ?? [])
                {
                    var path = NormalizeTaskFilePath(entry.Path);
                    var current = await ReadTaskFileHeadAsync(connection, transaction, found.TaskId, path, ct);
                    if (current is { } head && string.Equals(head.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged++;
                        continue;
                    }
                    if (entry.ContentBase64 is null)
                    {
                        needed.Add(new TaskFileMirrorNeed(key, path));
                        continue;
                    }
                    byte[] content;
                    try { content = Convert.FromBase64String(entry.ContentBase64); }
                    catch (FormatException) { throw new ArgumentException($"Task file {path} of {key} is not valid base64."); }
                    var stored = StoreContent(content);
                    if (!string.Equals(stored.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Task file {path} of {key} does not match its SHA-256.");
                    var mediaType = string.IsNullOrWhiteSpace(entry.MediaType) ? "application/octet-stream" : entry.MediaType;
                    await WriteTaskFileRevisionAsync(connection, transaction, found.TaskId, path, mediaType,
                        (current?.Version ?? 0) + 1, stored, now, actorId, ct);
                    written++;
                }
                foreach (var removed in task.Removed ?? [])
                {
                    removedCount += await ExecuteAsync(connection,
                        "DELETE FROM task_files WHERE task_id = $task AND path = $path;", ct, transaction,
                        ("$task", found.TaskId), ("$path", NormalizeTaskFilePath(removed)));
                }
            }
            if (written > 0 || removedCount > 0)
                await AuditAsync(connection, transaction, actorId, "task-files.mirrored", "project", projectId,
                    JsonSerializer.Serialize(new { written, removed = removedCount }), ct);
            result = new TaskFileMirrorResponse(projectId, written, unchanged, removedCount, needed, rejected);
        }, ct);
        return result!;
    }

    /// <summary>
    /// Cards of the project, archive included, whose integration record is
    /// verified, with the integration SHA and delivery SHAs it names. One
    /// indexed query over task file rows; no task folder is read.
    /// </summary>
    public async Task<IntegratedCardsResponse> ListIntegratedCardsAsync(string projectIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var (projectId, _) = await RequireBoardProjectAsync(connection, null, projectIdentity, ct);
        var rows = new List<(string Key, string Title, string State, string Content, bool ObjectStored, string? Sha)>();
        await using (var command = Command(connection, """
            SELECT t.task_key, t.title, t.state, f.content_base64, f.object_stored, f.sha256
              FROM task_files f
              JOIN tasks t ON t.id = f.task_id
             WHERE f.path = $path AND t.project_id = $project
             ORDER BY t.task_key;
            """, ("$path", IntegrationRecordPath), ("$project", projectId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        var cards = new List<IntegratedCardDto>();
        foreach (var row in rows)
        {
            if (IntegrationRecordAttribution.TryRead(ReadTaskContent(row.Content, row.ObjectStored, row.Sha)) is not { } record)
                continue;
            cards.Add(new IntegratedCardDto(row.Key, row.Title, row.State, record.Shas, record.IntegrationBranch));
        }
        return new IntegratedCardsResponse(projectId, cards);
    }

    private async Task WriteTaskFileRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string path,
        string mediaType,
        long version,
        StoredTaskContent stored,
        string now,
        string actorId,
        CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            INSERT INTO task_files(task_id, path, content_base64, media_type, version, updated_at, sha256, size_bytes, object_stored)
            VALUES ($task, $path, $content, $media, $version, $now, $sha, $size, $object)
            ON CONFLICT(task_id, path) DO UPDATE SET
                content_base64 = excluded.content_base64,
                media_type = excluded.media_type,
                version = excluded.version,
                updated_at = excluded.updated_at,
                sha256 = excluded.sha256,
                size_bytes = excluded.size_bytes,
                object_stored = excluded.object_stored;
            INSERT INTO task_file_revisions(task_id, path, version, content_base64, updated_at, actor_id, sha256, size_bytes, object_stored)
            VALUES ($task, $path, $version, $content, $now, $actor, $sha, $size, $object);
            """, ct, transaction,
            ("$task", taskId), ("$path", path), ("$content", stored.InlineBase64), ("$media", mediaType),
            ("$version", version), ("$now", now), ("$actor", actorId), ("$sha", stored.Sha256),
            ("$size", stored.SizeBytes), ("$object", stored.ObjectStored ? 1 : 0));
    }

    private static async Task<(long Version, string? Sha256)?> ReadTaskFileHeadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, string path, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT version, sha256 FROM task_files WHERE task_id = $task AND path = $path;",
            transaction, ("$task", taskId), ("$path", path));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    /// <summary>A task file path is relative, forward-slashed, and stays inside the task folder.</summary>
    internal static string NormalizeTaskFilePath(string path)
    {
        RequireTaskFilePath(path);
        return path.Replace('\\', '/').Trim('/');
    }

    private static void RequireTaskFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumTaskFilePathLength)
            throw new ArgumentException("A task file path is required and at most 400 characters long.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new ArgumentException($"Task file path {path} must be relative to the task folder.");
        var segments = normalized.Trim('/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Task file path {path} must stay inside the task folder.");
    }
}

internal sealed record StoredTaskContent(string Sha256, long SizeBytes, string InlineBase64, bool ObjectStored);

internal sealed record IntegrationRecordAttribution(IReadOnlyList<string> Shas, string IntegrationBranch)
{
    /// <summary>
    /// Pure read of one <c>integration-verification.json</c>: a verified record
    /// names its integration SHA and the delivery SHAs it contained. Anything
    /// else, including a malformed file, attributes nothing, exactly as the
    /// monolith store reads a malformed file as "no record".
    /// </summary>
    public static IntegrationRecordAttribution? TryRead(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!string.Equals(Text(root, "state"), TaskServerStore.IntegratedVerifiedState, StringComparison.Ordinal))
                return null;
            var shas = new List<string>();
            if (Text(root, "sha") is { Length: > 0 } sha) shas.Add(sha);
            if (root.TryGetProperty("deliveryShas", out var delivery) && delivery.ValueKind == JsonValueKind.Array)
                shas.AddRange(delivery.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(item => item.Length > 0));
            return new IntegrationRecordAttribution(shas, Text(root, "integrationBranch") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
