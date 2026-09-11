using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio "task metadata" P1 bundle: single-field task mutations (project,
/// cli type, epic, model, references, release, tags, task type, thinking
/// level, title). <c>title</c> reuses the existing <see cref="TaskServerStore.UpdateTaskAsync"/>
/// path unchanged. <c>change-project</c> mutates the shared <c>tasks</c> row
/// directly. Every other field lives on the lazily-created
/// <c>task_studio_fields</c> row for the task (one row per task, created on
/// first write), whose <c>version</c> column is the shared optimistic
/// concurrency token for all of: cli type, model, thinking level, task type,
/// release, epic assignment, the reference set, and the tag set - a write to
/// any one of them bumps that same counter, exactly like a normal
/// single-column-per-concept resource version.
/// </summary>
public sealed partial class TaskServerStore
{
    /// <summary>
    /// Creates the tables this bundle owns outright (<c>task_studio_fields</c>,
    /// <c>task_references</c>), plus defensive <c>IF NOT EXISTS</c> copies of
    /// the <c>epics</c>/<c>epic_tasks</c>/<c>tags</c>/<c>task_tags</c> tables
    /// owned by the project-meta group, so this file's own tests can run
    /// standalone before every group's migration is wired together. The
    /// <c>epic_tasks</c> and <c>task_tags</c> shapes here match the
    /// project-meta group's contract exactly (their group also creates them
    /// with <c>IF NOT EXISTS</c>, so whichever migration runs first wins and
    /// the second is a harmless no-op); the <c>epics</c>/<c>tags</c> shapes
    /// are only a best guess of enough columns to stand the tables up for
    /// isolated testing; the real shape is owned by that group.
    /// </summary>
    internal async Task ApplyStudioTaskMetadataMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS task_studio_fields(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                cli_type TEXT,
                model TEXT,
                thinking_level TEXT,
                task_type TEXT,
                release TEXT,
                epic_id TEXT,
                version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_references(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                reference_task_id TEXT NOT NULL REFERENCES tasks(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(task_id, reference_task_id)
            );
            CREATE TABLE IF NOT EXISTS epics(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS epic_tasks(
                epic_id TEXT NOT NULL REFERENCES epics(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(epic_id, task_id)
            );
            CREATE TABLE IF NOT EXISTS tags(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_tags(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                tag_id TEXT NOT NULL REFERENCES tags(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(task_id, tag_id)
            );
            """, ct);
    }

    public async Task<TaskDto> ChangeTaskProjectAsync(
        string projectId, string taskIdentity, ChangeTaskProjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ProjectId))
            throw new ArgumentException("A destination project id is required.");
        var destinationProject = await RequireProjectAsync(request.ProjectId, ct);
        TaskDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = UtcNow;
            var nextVersion = existing.Version + 1;
            var rowsAffected = await ExecuteAsync(connection, """
                UPDATE tasks SET project_id = $project, version = $version, updated_at = $updated
                 WHERE id = $id AND version = $expected;
                """, ct, transaction,
                ("$project", destinationProject.ProjectId), ("$version", nextVersion), ("$updated", Iso(now)),
                ("$id", existing.TaskId), ("$expected", request.ExpectedVersion));
            if (rowsAffected == 0)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task version {request.ExpectedVersion}, current version is {existing.Version}.");

            await AuditAsync(connection, transaction, actorId, "task.project-changed", "task", existing.TaskId,
                JsonSerializer.Serialize(new { from = existing.ProjectId, to = destinationProject.ProjectId }), ct);
            updated = existing with { ProjectId = destinationProject.ProjectId, Version = nextVersion, UpdatedAt = now };
        }, ct);
        return updated!;
    }

    public Task<TaskStudioFieldsDto> SetTaskCliTypeAsync(
        string projectId, string taskIdentity, SetTaskCliTypeRequest request, string actorId, CancellationToken ct)
        => SetStudioFieldAsync(
            projectId, taskIdentity, "cli_type", request.CliType, request.ExpectedVersion, actorId, "task.cli-type-set", ct);

    public Task<TaskStudioFieldsDto> SetTaskModelAsync(
        string projectId, string taskIdentity, SetTaskModelRequest request, string actorId, CancellationToken ct)
        => SetStudioFieldAsync(
            projectId, taskIdentity, "model", request.Model, request.ExpectedVersion, actorId, "task.model-set", ct);

    public Task<TaskStudioFieldsDto> SetTaskThinkingLevelAsync(
        string projectId, string taskIdentity, SetTaskThinkingLevelRequest request, string actorId, CancellationToken ct)
        => SetStudioFieldAsync(
            projectId, taskIdentity, "thinking_level", request.ThinkingLevel, request.ExpectedVersion, actorId,
            "task.thinking-level-set", ct);

    public Task<TaskStudioFieldsDto> SetTaskTaskTypeAsync(
        string projectId, string taskIdentity, SetTaskTaskTypeRequest request, string actorId, CancellationToken ct)
        => SetStudioFieldAsync(
            projectId, taskIdentity, "task_type", request.TaskType, request.ExpectedVersion, actorId,
            "task.task-type-set", ct);

    public Task<TaskStudioFieldsDto> SetTaskReleaseAsync(
        string projectId, string taskIdentity, SetTaskReleaseRequest request, string actorId, CancellationToken ct)
        => SetStudioFieldAsync(
            projectId, taskIdentity, "release", request.Release, request.ExpectedVersion, actorId, "task.release-set", ct);

    /// <summary>
    /// Shared upsert idiom for the single-column studio fields on
    /// <c>task_studio_fields</c> (mirrors <c>UpdateHostProjectPolicyAsync</c>'s
    /// insert-with-<c>ON CONFLICT</c>-update-and-version-bump shape). The
    /// column name is always one of a fixed, hardcoded set from this file, so
    /// interpolating it into SQL carries no injection risk.
    /// </summary>
    private async Task<TaskStudioFieldsDto> SetStudioFieldAsync(
        string projectId,
        string taskIdentity,
        string column,
        string? value,
        long expectedVersion,
        string actorId,
        string auditAction,
        CancellationToken ct)
    {
        RequireWritable();
        TaskStudioFieldsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var existing = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != expectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task studio fields version {expectedVersion}, current version is {currentVersion}.");

            var now = UtcNow;
            var nextVersion = currentVersion + 1;
            await ExecuteAsync(connection, $"""
                INSERT INTO task_studio_fields(task_id, {column}, version, updated_at)
                VALUES ($task, $value, $version, $updated)
                ON CONFLICT(task_id) DO UPDATE SET
                    {column} = excluded.{column},
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$value", value), ("$version", nextVersion), ("$updated", Iso(now)));
            await AuditAsync(connection, transaction, actorId, auditAction, "task", task.TaskId,
                JsonSerializer.Serialize(new { column, value, expectedVersion, nextVersion }), ct);
            result = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
        }, ct);
        return result!;
    }

    public async Task<TaskStudioFieldsDto> SetTaskEpicAsync(
        string projectId, string taskIdentity, SetTaskEpicRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        TaskStudioFieldsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var existing = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task studio fields version {request.ExpectedVersion}, current version is {currentVersion}.");

            if (!string.IsNullOrWhiteSpace(request.EpicId))
            {
                var epicExists = Convert.ToInt32(await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM epics WHERE id = $epic;", ct, transaction,
                    ("$epic", request.EpicId)) ?? 0) == 1;
                if (!epicExists)
                    throw new KeyNotFoundException($"Epic '{request.EpicId}' was not found.");
            }

            var now = UtcNow;
            var nextVersion = currentVersion + 1;
            await ExecuteAsync(
                connection, "DELETE FROM epic_tasks WHERE task_id = $task;", ct, transaction, ("$task", task.TaskId));
            if (!string.IsNullOrWhiteSpace(request.EpicId))
            {
                await ExecuteAsync(connection, """
                    INSERT INTO epic_tasks(epic_id, task_id, created_at) VALUES ($epic, $task, $now);
                    """, ct, transaction, ("$epic", request.EpicId), ("$task", task.TaskId), ("$now", Iso(now)));
            }
            await ExecuteAsync(connection, """
                INSERT INTO task_studio_fields(task_id, epic_id, version, updated_at)
                VALUES ($task, $epic, $version, $updated)
                ON CONFLICT(task_id) DO UPDATE SET
                    epic_id = excluded.epic_id,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$epic", request.EpicId), ("$version", nextVersion), ("$updated", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "task.epic-set", "task", task.TaskId,
                JsonSerializer.Serialize(new { request.EpicId, request.ExpectedVersion, nextVersion }), ct);
            result = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
        }, ct);
        return result!;
    }

    public async Task<TaskReferencesDto> SetTaskReferencesAsync(
        string projectId, string taskIdentity, SetTaskReferencesRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var referenceIds = NormalizeIds(request.ReferenceTaskIds);
        TaskReferencesDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var existing = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task references version {request.ExpectedVersion}, current version is {currentVersion}.");

            foreach (var referenceTaskId in referenceIds)
            {
                _ = await ReadTaskAsync(connection, transaction, UnscopedProjectToken, referenceTaskId, ct)
                    ?? throw new KeyNotFoundException($"Referenced task '{referenceTaskId}' was not found.");
            }

            var now = UtcNow;
            await ExecuteAsync(
                connection, "DELETE FROM task_references WHERE task_id = $task;", ct, transaction, ("$task", task.TaskId));
            foreach (var referenceTaskId in referenceIds)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO task_references(task_id, reference_task_id, created_at) VALUES ($task, $ref, $now);
                    """, ct, transaction, ("$task", task.TaskId), ("$ref", referenceTaskId), ("$now", Iso(now)));
            }

            var nextVersion = currentVersion + 1;
            await UpsertStudioFieldsVersionAsync(connection, transaction, task.TaskId, nextVersion, now, ct);
            await AuditAsync(connection, transaction, actorId, "task.references-set", "task", task.TaskId,
                JsonSerializer.Serialize(new { referenceIds, request.ExpectedVersion, nextVersion }), ct);
            result = new TaskReferencesDto(task.TaskId, referenceIds, nextVersion, now);
        }, ct);
        return result!;
    }

    public async Task<TaskTagsDto> SetTaskTagsAsync(
        string projectId, string taskIdentity, SetTaskTagsRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var tagIds = NormalizeIds(request.TagIds);
        TaskTagsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var existing = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected task tags version {request.ExpectedVersion}, current version is {currentVersion}.");

            foreach (var tagId in tagIds)
            {
                var exists = Convert.ToInt32(await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM tags WHERE id = $tag;", ct, transaction, ("$tag", tagId)) ?? 0) == 1;
                if (!exists)
                    throw new KeyNotFoundException($"Tag '{tagId}' was not found.");
            }

            var now = UtcNow;
            await ExecuteAsync(
                connection, "DELETE FROM task_tags WHERE task_id = $task;", ct, transaction, ("$task", task.TaskId));
            foreach (var tagId in tagIds)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO task_tags(task_id, tag_id, created_at) VALUES ($task, $tag, $now);
                    """, ct, transaction, ("$task", task.TaskId), ("$tag", tagId), ("$now", Iso(now)));
            }

            var nextVersion = currentVersion + 1;
            await UpsertStudioFieldsVersionAsync(connection, transaction, task.TaskId, nextVersion, now, ct);
            await AuditAsync(connection, transaction, actorId, "task.tags-set", "task", task.TaskId,
                JsonSerializer.Serialize(new { tagIds, request.ExpectedVersion, nextVersion }), ct);
            result = new TaskTagsDto(task.TaskId, tagIds, nextVersion, now);
        }, ct);
        return result!;
    }

    private static async Task UpsertStudioFieldsVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, long nextVersion, DateTime now, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO task_studio_fields(task_id, version, updated_at)
            VALUES ($task, $version, $updated)
            ON CONFLICT(task_id) DO UPDATE SET
                version = excluded.version,
                updated_at = excluded.updated_at;
            """, ct, transaction, ("$task", taskId), ("$version", nextVersion), ("$updated", Iso(now)));

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? ids)
        => (ids ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static async Task<TaskStudioFieldsDto?> ReadStudioFieldsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT task_id, cli_type, model, thinking_level, task_type, release, epic_id, version, updated_at
              FROM task_studio_fields WHERE task_id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TaskStudioFieldsDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt64(7),
            Parse(reader.GetString(8)));
    }
}
