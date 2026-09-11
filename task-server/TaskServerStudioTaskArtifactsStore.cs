using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// P1 "task-owned files, attachments, artifacts, screenshots, and results"
/// Studio bundle (group G6). <c>artifacts</c>/<c>screenshots</c>/<c>results</c>
/// reuse the existing run-artifact infrastructure joined through
/// <c>runs.task_id</c> (the same pattern <c>GetTaskHistoryAsync</c> already
/// uses). <c>attachments</c> and <c>files</c> are genuinely new task-owned
/// resources backed by the tables created in
/// <see cref="ApplyStudioTaskArtifactsMigrationAsync"/>.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioTaskArtifactsMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS task_attachments(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                file_name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                content_base64 TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(task_id, file_name)
            );
            CREATE TABLE IF NOT EXISTS task_files(
                task_id TEXT NOT NULL,
                path TEXT NOT NULL,
                content_base64 TEXT NOT NULL,
                media_type TEXT,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(task_id, path)
            );
            CREATE TABLE IF NOT EXISTS task_file_revisions(
                task_id TEXT NOT NULL,
                path TEXT NOT NULL,
                version INTEGER NOT NULL,
                content_base64 TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                actor_id TEXT,
                PRIMARY KEY(task_id, path, version)
            );
            """, ct);
    }

    // ---- artifacts / screenshots / results: task-scoped view over run artifacts ----

    public async Task<IReadOnlyList<ArtifactDto>> ListTaskArtifactsAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.id, a.run_id, a.name, a.media_type, a.sha256, a.size_bytes,
                   a.idempotency_key, a.fence, a.created_at, a.sequence
              FROM artifacts a
              JOIN runs r ON r.id = a.run_id
             WHERE r.task_id = $task
             ORDER BY a.created_at, a.id;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArtifactDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadArtifact(reader));
        return result;
    }

    public async Task<IReadOnlyList<ArtifactDto>> ListTaskScreenshotArtifactsAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.id, a.run_id, a.name, a.media_type, a.sha256, a.size_bytes,
                   a.idempotency_key, a.fence, a.created_at, a.sequence
              FROM artifacts a
              JOIN runs r ON r.id = a.run_id
             WHERE r.task_id = $task AND a.media_type LIKE 'image/%'
             ORDER BY a.created_at, a.id;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArtifactDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadArtifact(reader));
        return result;
    }

    public async Task<ArtifactContentDto?> GetLatestTaskScreenshotContentAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        var located = await LocateLatestTaskArtifactAsync(task.TaskId, "AND a.media_type LIKE 'image/%'", ct);
        return located is null ? null : await GetArtifactContentAsync(located.Value.RunId, located.Value.ArtifactId, ct);
    }

    public async Task<ArtifactContentDto?> GetTaskResultContentAsync(
        string projectId, string taskIdentity, string path, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var located = await LocateLatestTaskArtifactAsync(
            task.TaskId, "AND (a.name = $path OR a.name LIKE $prefix ESCAPE '\\')", ct,
            ("$path", path), ("$prefix", EscapeLikePrefix(path) + "/%"));
        return located is null ? null : await GetArtifactContentAsync(located.Value.RunId, located.Value.ArtifactId, ct);
    }

    private async Task<(string RunId, string ArtifactId)?> LocateLatestTaskArtifactAsync(
        string taskId, string extraFilter, CancellationToken ct, params (string Name, object? Value)[] extraParameters)
    {
        var parameters = new List<(string Name, object? Value)> { ("$task", taskId) };
        parameters.AddRange(extraParameters);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, $"""
            SELECT a.run_id, a.id
              FROM artifacts a
              JOIN runs r ON r.id = a.run_id
             WHERE r.task_id = $task {extraFilter}
             ORDER BY a.created_at DESC, a.id DESC
             LIMIT 1;
            """, parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    private static string EscapeLikePrefix(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<TaskOutputDto> GetTaskOutputAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        var attempts = await ListAttemptsAsync(task.ProjectId, task.TaskId, ct);
        var latestRun = attempts.Count == 0 ? null : attempts[^1].Run;
        if (latestRun is null) return new TaskOutputDto(null, 0, null);

        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT COUNT(*), MAX(occurred_at) FROM events WHERE run_id = $run;
            """, ("$run", latestRun.RunId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var eventCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var lastEventAt = reader.IsDBNull(1) ? (DateTime?)null : Parse(reader.GetString(1));
        return new TaskOutputDto(latestRun, (int)eventCount, lastEventAt);
    }

    // ---- attachments ----

    public async Task<TaskAttachmentDto> SaveTaskAttachmentAsync(
        string projectId,
        string taskIdentity,
        string fileName,
        byte[] content,
        string mediaType,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var normalizedName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(normalizedName) || !string.Equals(normalizedName, fileName, StringComparison.Ordinal))
            throw new ArgumentException("File name must not contain path separators.");
        if (content.Length == 0) throw new ArgumentException("Attachment content is empty.");

        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var now = UtcNow;
        var id = StableOrGeneratedId(null, "tat");
        TaskAttachmentDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO task_attachments(id, task_id, file_name, media_type, content_base64, sha256, size_bytes, created_at)
                VALUES ($id, $task, $name, $media, $content, $sha, $size, $now)
                ON CONFLICT(task_id, file_name) DO UPDATE SET
                    media_type = excluded.media_type,
                    content_base64 = excluded.content_base64,
                    sha256 = excluded.sha256,
                    size_bytes = excluded.size_bytes,
                    created_at = excluded.created_at;
                """, ct, transaction,
                ("$id", id), ("$task", task.TaskId), ("$name", normalizedName), ("$media", mediaType),
                ("$content", Convert.ToBase64String(content)), ("$sha", sha256), ("$size", content.LongLength),
                ("$now", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "task-attachment.uploaded", "task", task.TaskId,
                JsonSerializer.Serialize(new { fileName = normalizedName, sha256, sizeBytes = content.LongLength }), ct);
            result = new TaskAttachmentDto(task.TaskId, normalizedName, mediaType, sha256, content.LongLength, now);
        }, ct);
        return result!;
    }

    public async Task<(TaskAttachmentDto Meta, byte[] Content)?> GetTaskAttachmentContentAsync(
        string projectId, string taskIdentity, string fileName, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT file_name, media_type, content_base64, sha256, size_bytes, created_at
              FROM task_attachments
             WHERE task_id = $task AND file_name = $name;
            """, ("$task", task.TaskId), ("$name", fileName));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var meta = new TaskAttachmentDto(
            task.TaskId, reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetInt64(4),
            Parse(reader.GetString(5)));
        var content = Convert.FromBase64String(reader.GetString(2));
        return (meta, content);
    }

    // ---- files (task-owned default scope only; scope=code is rejected at the endpoint) ----

    public async Task<TaskFileDto> PutTaskFileAsync(
        string projectId,
        string taskIdentity,
        string path,
        UpdateTaskFileRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is required.");
        byte[] content;
        try { content = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("File content is not valid base64."); }

        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        var mediaType = string.IsNullOrWhiteSpace(request.MediaType) ? "application/octet-stream" : request.MediaType;
        var now = UtcNow;
        TaskFileDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            long? currentVersion = null;
            await using (var command = Command(connection, """
                SELECT version FROM task_files WHERE task_id = $task AND path = $path;
                """, transaction, ("$task", task.TaskId), ("$path", path)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct)) currentVersion = reader.GetInt64(0);
            }

            if (currentVersion is null && request.ExpectedVersion != 0)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task file version {request.ExpectedVersion}, but the file does not exist yet.");
            if (currentVersion is not null && currentVersion != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task file version {request.ExpectedVersion}, current version is {currentVersion}.");

            var nextVersion = (currentVersion ?? 0) + 1;
            var contentBase64 = Convert.ToBase64String(content);
            await ExecuteAsync(connection, """
                INSERT INTO task_files(task_id, path, content_base64, media_type, version, updated_at)
                VALUES ($task, $path, $content, $media, $version, $now)
                ON CONFLICT(task_id, path) DO UPDATE SET
                    content_base64 = excluded.content_base64,
                    media_type = excluded.media_type,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$path", path), ("$content", contentBase64), ("$media", mediaType),
                ("$version", nextVersion), ("$now", Iso(now)));
            await ExecuteAsync(connection, """
                INSERT INTO task_file_revisions(task_id, path, version, content_base64, updated_at, actor_id)
                VALUES ($task, $path, $version, $content, $now, $actor);
                """, ct, transaction,
                ("$task", task.TaskId), ("$path", path), ("$version", nextVersion), ("$content", contentBase64),
                ("$now", Iso(now)), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, "task-file.updated", "task", task.TaskId,
                JsonSerializer.Serialize(new { path, version = nextVersion }), ct);
            result = new TaskFileDto(task.TaskId, path, mediaType, nextVersion, content.LongLength, now);
        }, ct);
        return result!;
    }

    public async Task<(TaskFileDto Meta, byte[] Content)?> GetTaskFileContentAsync(
        string projectId, string taskIdentity, string path, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT content_base64, media_type, version, updated_at
              FROM task_files
             WHERE task_id = $task AND path = $path;
            """, ("$task", task.TaskId), ("$path", path));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var content = Convert.FromBase64String(reader.GetString(0));
        var mediaType = reader.IsDBNull(1) ? "application/octet-stream" : reader.GetString(1);
        var meta = new TaskFileDto(task.TaskId, path, mediaType, reader.GetInt64(2), content.LongLength, Parse(reader.GetString(3)));
        return (meta, content);
    }

    public async Task<TaskFileHistoryResponse> GetTaskFileHistoryAsync(
        string projectId, string taskIdentity, string path, CancellationToken ct)
    {
        var task = await RequireTaskAsync(projectId, taskIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT version, updated_at, actor_id
              FROM task_file_revisions
             WHERE task_id = $task AND path = $path
             ORDER BY version;
            """, ("$task", task.TaskId), ("$path", path));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var revisions = new List<TaskFileRevisionDto>();
        while (await reader.ReadAsync(ct))
            revisions.Add(new TaskFileRevisionDto(
                reader.GetInt64(0), Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return new TaskFileHistoryResponse(task.TaskId, path, revisions);
    }

    private async Task<TaskDto> RequireTaskAsync(string projectId, string taskIdentity, CancellationToken ct)
        => await GetTaskAsync(projectId, taskIdentity, ct) ?? throw new KeyNotFoundException("Task was not found.");
}
