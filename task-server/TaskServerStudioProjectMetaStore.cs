using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// G2 "project meta" P1 bundle: epics, tags, per-project autonomy and
/// execution-runner settings, and thin pipeline projections on top of the
/// existing orchestration flow definition (<c>TaskServerOrchestrationStore.cs</c>).
/// New tables owned here: <c>epics</c>, <c>epic_tasks</c>, <c>tags</c>,
/// <c>task_tags</c>, <c>project_studio_settings</c>. <c>task_tags</c> is the
/// canonical join table for task &lt;-&gt; tag membership; the sibling
/// G4_TaskMetadata slice reaches it exclusively through
/// <see cref="SetTaskTagsAsync"/> below rather than writing its own copy.
/// </summary>
public sealed partial class TaskServerStore
{
    /// <summary>
    /// Schema for the G2 project-meta bundle. The orchestrator wires the call
    /// to this method into the main migration sequence; this store never
    /// calls it itself.
    /// </summary>
    internal async Task ApplyStudioProjectMetaMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS epics(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                title TEXT NOT NULL,
                state TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_epics_project_state ON epics(project_id, state);
            CREATE TABLE IF NOT EXISTS epic_tasks(
                epic_id TEXT NOT NULL REFERENCES epics(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(epic_id, task_id)
            );
            CREATE TABLE IF NOT EXISTS tags(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                color TEXT,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_tags(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                tag_id TEXT NOT NULL REFERENCES tags(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(task_id, tag_id)
            );
            CREATE TABLE IF NOT EXISTS project_studio_settings(
                project_id TEXT PRIMARY KEY REFERENCES projects(id),
                autonomy_json TEXT,
                execution_runner_id TEXT,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, ct);
    }

    // ----------------------------------------------------------------
    // Epics
    // ----------------------------------------------------------------

    /// <summary>
    /// Not exposed by any P1 route (the frontend inventory has no epic-create
    /// call) - kept public so tests and a future authoring route can seed
    /// epics without reaching into the schema directly.
    /// </summary>
    public async Task<EpicDto> CreateEpicAsync(string projectId, CreateEpicRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Epic title is required.");
        await RequireProjectAsync(projectId, ct);
        var epicId = StableOrGeneratedId(request.EpicId, "epc");
        var state = string.IsNullOrWhiteSpace(request.State) ? EpicStates.Open : request.State.Trim();
        EpicDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO epics(id, project_id, title, state, version, created_at, updated_at)
                VALUES ($id, $project, $title, $state, 1, $now, $now);
                """, ct, transaction,
                ("$id", epicId), ("$project", projectId), ("$title", request.Title.Trim()),
                ("$state", state), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "epic.created", "epic", epicId,
                JsonSerializer.Serialize(new { projectId, request.Title, state }), ct);
            created = new EpicDto(epicId, projectId, request.Title.Trim(), state, 1, Parse(now), Parse(now));
        }, ct);
        return created!;
    }

    public async Task<IReadOnlyList<EpicDto>> ListEpicsAsync(string? projectIdentity, string? status, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var projectFilter = await ResolveOptionalProjectFilterAsync(connection, projectIdentity, ct);
        await using var command = Command(connection, """
            SELECT id, project_id, title, state, version, created_at, updated_at
              FROM epics
             WHERE ($project IS NULL OR project_id = $project)
               AND ($status IS NULL OR state = $status)
             ORDER BY created_at, id;
            """, ("$project", projectFilter), ("$status", string.IsNullOrWhiteSpace(status) ? null : status.Trim()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var epics = new List<EpicDto>();
        while (await reader.ReadAsync(ct)) epics.Add(ReadEpic(reader));
        return epics;
    }

    public async Task<EpicDetailDto?> GetEpicAsync(string epicId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var epic = await ReadEpicCoreAsync(connection, null, epicId, ct);
        if (epic is null) return null;

        var tasks = new List<EpicTaskSummaryDto>();
        await using (var taskCommand = Command(connection, """
            SELECT t.id, t.task_key, t.title, t.state
              FROM epic_tasks et
              JOIN tasks t ON t.id = et.task_id
             WHERE et.epic_id = $epic
             ORDER BY et.created_at, t.task_key;
            """, ("$epic", epicId)))
        await using (var taskReader = await taskCommand.ExecuteReaderAsync(ct))
        {
            while (await taskReader.ReadAsync(ct))
                tasks.Add(new EpicTaskSummaryDto(
                    taskReader.GetString(0), taskReader.GetString(1), taskReader.GetString(2), taskReader.GetString(3)));
        }
        return new EpicDetailDto(epic, tasks.Count, tasks);
    }

    /// <summary>
    /// Creates a real task via the shared <see cref="CreateTaskAsync"/> and
    /// links it into the epic's <c>epic_tasks</c> join row. These are two
    /// separate write transactions, not one atomic SQL transaction: reusing
    /// <see cref="CreateTaskAsync"/> as-is (rather than duplicating task-insert
    /// SQL inside this store's own transaction) means the task-creation
    /// commit and the join-row commit are sequential. If the process crashes
    /// between them, the task exists without its epic link; nothing else
    /// depends on the two being atomic, and no caller can observe a task
    /// that both exists and is silently unlinked without the epic-detail
    /// projection surfacing it as missing from the epic's task list.
    /// </summary>
    public async Task<EpicSubTaskResponse> CreateEpicSubTaskAsync(
        string epicId, CreateTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        EpicDto? epic;
        await using (var connection = await OpenReadyAsync(ct))
            epic = await ReadEpicCoreAsync(connection, null, epicId, ct);
        if (epic is null) throw new KeyNotFoundException("Epic was not found.");

        var task = await CreateTaskAsync(epic.ProjectId, request, actorId, ct);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO epic_tasks(epic_id, task_id, created_at) VALUES ($epic, $task, $now)
                ON CONFLICT(epic_id, task_id) DO NOTHING;
                """, ct, transaction, ("$epic", epicId), ("$task", task.TaskId), ("$now", Iso(UtcNow)));
            await AuditAsync(connection, transaction, actorId, "epic.sub-task-linked", "epic", epicId,
                JsonSerializer.Serialize(new { taskId = task.TaskId }), ct);
        }, ct);
        return new EpicSubTaskResponse(epic, task);
    }

    public async Task<int> CountCompletedEpicsAsync(string? projectIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var projectFilter = await ResolveOptionalProjectFilterAsync(connection, projectIdentity, ct);
        var count = await ScalarAsync(connection, """
            SELECT count(*) FROM epics WHERE state = $state AND ($project IS NULL OR project_id = $project);
            """, ct, ("$state", EpicStates.Done), ("$project", projectFilter));
        return Convert.ToInt32(count ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Resolves an optional <c>project</c> query filter (id or name, per the
    /// legacy route's query shape) to a project id best-effort: an absent
    /// filter disables filtering (returns null), while an unresolvable
    /// filter value matches zero rows rather than being silently ignored or
    /// erroring the whole request.
    /// </summary>
    private static async Task<string?> ResolveOptionalProjectFilterAsync(
        SqliteConnection connection, string? identity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        await using var command = Command(connection, """
            SELECT id FROM projects WHERE id = $identity OR name = $identity COLLATE NOCASE LIMIT 1;
            """, ("$identity", identity.Trim()));
        var id = Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(id) ? UnresolvableProjectFilterSentinel : id;
    }

    private const string UnresolvableProjectFilterSentinel = "__unresolvable-project-filter__";

    private static EpicDto ReadEpic(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), Parse(reader.GetString(5)), Parse(reader.GetString(6)));

    private static async Task<EpicDto?> ReadEpicCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string epicId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, title, state, version, created_at, updated_at FROM epics WHERE id = $id;
            """, transaction, ("$id", epicId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEpic(reader) : null;
    }

    // ----------------------------------------------------------------
    // Tags
    // ----------------------------------------------------------------

    public async Task<TagDto> CreateTagAsync(CreateTagRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Tag name is required.");
        var name = request.Name.Trim();
        var tagId = StableOrGeneratedId(request.TagId, TagIdPrefixes.Tag);
        TagDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var conflicting = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM tags WHERE name = $name COLLATE NOCASE;",
                ct, transaction, ("$name", name)) ?? 0L, CultureInfo.InvariantCulture);
            if (conflicting > 0)
                throw new TaskServerConflictException("tag-name-conflict", $"A tag named '{name}' already exists.");
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO tags(id, name, color, version, created_at) VALUES ($id, $name, $color, 1, $now);
                """, ct, transaction, ("$id", tagId), ("$name", name), ("$color", request.Color), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "tag.created", "tag", tagId,
                JsonSerializer.Serialize(new { name, request.Color }), ct);
            created = new TagDto(tagId, name, request.Color, 1, Parse(now));
        }, ct);
        return created!;
    }

    public async Task<IReadOnlyList<TagDto>> ListTagsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT id, name, color, version, created_at FROM tags ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tags = new List<TagDto>();
        while (await reader.ReadAsync(ct))
            tags.Add(new TagDto(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3), Parse(reader.GetString(4))));
        return tags;
    }

    public async Task DeleteTagAsync(string tagId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var exists = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM tags WHERE id = $id;", ct, transaction, ("$id", tagId))
                ?? 0L, CultureInfo.InvariantCulture);
            if (exists == 0)
                throw new KeyNotFoundException("Tag was not found.");
            await ExecuteAsync(connection, """
                DELETE FROM task_tags WHERE tag_id = $id;
                DELETE FROM tags WHERE id = $id;
                """, ct, transaction, ("$id", tagId));
            await AuditAsync(connection, transaction, actorId, "tag.deleted", "tag", tagId, "{}", ct);
        }, ct);
    }

    /// <summary>
    /// Canonical <c>task_tags</c> join writer. G4_TaskMetadata's
    /// <c>PUT /api/v1/projects/{projectId}/tasks/{taskId}/tags</c> calls this
    /// from its own write transaction on this same partial
    /// <see cref="TaskServerStore"/> type instead of writing to
    /// <c>task_tags</c> directly, so there is exactly one writer of the
    /// join table's shape. Replaces the full tag set for the task
    /// (delete-then-insert) rather than diffing; unknown tag ids fail the
    /// insert via the <c>tags(id)</c> foreign key.
    /// </summary>
    internal async Task SetTaskTagsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, IReadOnlyList<string> tagIds, CancellationToken ct)
    {
        await ExecuteAsync(connection, "DELETE FROM task_tags WHERE task_id = $task;", ct, transaction, ("$task", taskId));
        var now = Iso(UtcNow);
        foreach (var tagId in tagIds.Distinct(StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, """
                INSERT INTO task_tags(task_id, tag_id, created_at) VALUES ($task, $tag, $now)
                ON CONFLICT(task_id, tag_id) DO NOTHING;
                """, ct, transaction, ("$task", taskId), ("$tag", tagId), ("$now", now));
        }
    }

    // ----------------------------------------------------------------
    // Project autonomy / execution-runner
    // ----------------------------------------------------------------

    public async Task<ProjectAutonomyDto> GetProjectAutonomyAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var row = await ReadProjectStudioSettingsAsync(connection, null, project.ProjectId, ct);
        return new ProjectAutonomyDto(
            project.ProjectId, row?.AutonomyJson ?? "{}", row?.Version ?? 0, row?.UpdatedAt ?? project.UpdatedAt);
    }

    public async Task<ProjectAutonomyDto> UpdateProjectAutonomyAsync(
        string projectIdentity, UpdateProjectAutonomyRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ValidateJson(request.AutonomyJson, "autonomyJson");
        var project = await RequireProjectAsync(projectIdentity, ct);
        ProjectAutonomyDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadProjectStudioSettingsAsync(connection, transaction, project.ProjectId, ct);
            var currentVersion = row?.Version ?? 0;
            if (request.ExpectedVersion != currentVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected project autonomy version {request.ExpectedVersion?.ToString() ?? "missing"}, current version is {currentVersion}.");
            var nextVersion = currentVersion + 1;
            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO project_studio_settings(project_id, autonomy_json, execution_runner_id, version, updated_at)
                VALUES ($project, $autonomy, $runner, $version, $updated)
                ON CONFLICT(project_id) DO UPDATE SET
                    autonomy_json = excluded.autonomy_json, version = excluded.version, updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", project.ProjectId), ("$autonomy", request.AutonomyJson),
                ("$runner", row?.ExecutionRunnerId), ("$version", nextVersion), ("$updated", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "project.autonomy-updated", "project", project.ProjectId,
                JsonSerializer.Serialize(new { version = nextVersion }), ct);
            result = new ProjectAutonomyDto(project.ProjectId, request.AutonomyJson, nextVersion, now);
        }, ct);
        return result!;
    }

    public async Task<ProjectExecutionRunnerDto> UpdateProjectExecutionRunnerAsync(
        string projectIdentity, UpdateProjectExecutionRunnerRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        ProjectExecutionRunnerDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadProjectStudioSettingsAsync(connection, transaction, project.ProjectId, ct);
            var currentVersion = row?.Version ?? 0;
            if (request.ExpectedVersion != currentVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected project execution-runner version {request.ExpectedVersion?.ToString() ?? "missing"}, current version is {currentVersion}.");
            var nextVersion = currentVersion + 1;
            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO project_studio_settings(project_id, autonomy_json, execution_runner_id, version, updated_at)
                VALUES ($project, $autonomy, $runner, $version, $updated)
                ON CONFLICT(project_id) DO UPDATE SET
                    execution_runner_id = excluded.execution_runner_id, version = excluded.version, updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", project.ProjectId), ("$autonomy", row?.AutonomyJson),
                ("$runner", request.ExecutionRunnerId), ("$version", nextVersion), ("$updated", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "project.execution-runner-updated", "project", project.ProjectId,
                JsonSerializer.Serialize(new { request.ExecutionRunnerId, version = nextVersion }), ct);
            result = new ProjectExecutionRunnerDto(project.ProjectId, request.ExecutionRunnerId, nextVersion, now);
        }, ct);
        return result!;
    }

    private sealed record ProjectStudioSettingsRow(string? AutonomyJson, string? ExecutionRunnerId, long Version, DateTime UpdatedAt);

    private static async Task<ProjectStudioSettingsRow?> ReadProjectStudioSettingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT autonomy_json, execution_runner_id, version, updated_at
              FROM project_studio_settings WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProjectStudioSettingsRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            Parse(reader.GetString(3)));
    }

    // ----------------------------------------------------------------
    // Pipeline (thin projection over the orchestration flow definition)
    // ----------------------------------------------------------------

    /// <summary>
    /// The underlying <see cref="FlowDefinitionDto"/> stage is a fixed enum
    /// with no per-step configurable field of its own, so "missing required
    /// config" always reports empty here - this health check is a
    /// presence/freshness signal (step count, last-updated), not a deep
    /// per-step validation, because there is no per-step config to validate.
    /// </summary>
    public async Task<PipelineHealthResponse> GetPipelineHealthAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var definition = await GetFlowDefinitionAsync(project.ProjectId, ct);
        if (definition is null)
            return new PipelineHealthResponse(project.ProjectId, 0, 0, [], false, project.UpdatedAt);
        return new PipelineHealthResponse(
            project.ProjectId, definition.Version, definition.Stages.Count, [],
            definition.Stages.Count > 0, definition.UpdatedAt);
    }

    /// <summary>
    /// Upserts one step into the project's pipeline: reads the current flow
    /// definition, splices <paramref name="request"/>'s step into the stage
    /// list at its requested order (or the end), and writes it back through
    /// <see cref="UpsertFlowDefinitionAsync"/> - which already owns the
    /// version-conflict check (<c>resource-version-mismatch</c>), so it is not
    /// duplicated here.
    /// </summary>
    public async Task<PipelineDefinitionResponse> UpsertPipelineStepAsync(
        string projectIdentity, UpsertPipelineStepRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (!Enum.TryParse<OrchestrationStage>(request.StepId, ignoreCase: true, out var stage))
            throw new ArgumentException($"Unknown pipeline step id '{request.StepId}'.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var definition = await GetFlowDefinitionAsync(project.ProjectId, ct)
            ?? throw new KeyNotFoundException("The project has no pipeline definition.");
        var stages = definition.Stages.ToList();
        stages.Remove(stage);
        var index = request.Order is null ? stages.Count : Math.Clamp(request.Order.Value, 0, stages.Count);
        stages.Insert(index, stage);
        var updated = await UpsertFlowDefinitionAsync(
            project.ProjectId,
            new UpsertFlowDefinitionRequest(request.ExpectedVersion, stages, definition.MaxReissueAttempts),
            actorId,
            ct);
        return ToPipelineDefinitionResponse(updated);
    }

    public async Task<PipelineDefinitionResponse> UpdatePipelineStepOrderAsync(
        string projectIdentity, UpdatePipelineStepOrderRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (request.StepIds is null || request.StepIds.Count == 0)
            throw new ArgumentException("At least one pipeline step id is required.");
        var stages = new List<OrchestrationStage>(request.StepIds.Count);
        foreach (var stepId in request.StepIds)
        {
            if (!Enum.TryParse<OrchestrationStage>(stepId, ignoreCase: true, out var stage))
                throw new ArgumentException($"Unknown pipeline step id '{stepId}'.");
            stages.Add(stage);
        }
        var project = await RequireProjectAsync(projectIdentity, ct);
        var definition = await GetFlowDefinitionAsync(project.ProjectId, ct)
            ?? throw new KeyNotFoundException("The project has no pipeline definition.");
        if (stages.Count != definition.Stages.Count || !stages.ToHashSet().SetEquals(definition.Stages))
            throw new ArgumentException("The reordered step set must match the project's current pipeline steps exactly.");
        var updated = await UpsertFlowDefinitionAsync(
            project.ProjectId,
            new UpsertFlowDefinitionRequest(request.ExpectedVersion, stages, definition.MaxReissueAttempts),
            actorId,
            ct);
        return ToPipelineDefinitionResponse(updated);
    }

    /// <summary>
    /// Synchronous, best-effort capability check: confirms the step id is a
    /// recognized stage and is present in the project's current pipeline.
    /// This never spawns a process or touches the filesystem - there is no
    /// durable state change, matching the "live check, not durable state"
    /// shape requested for this route.
    /// </summary>
    public async Task<PipelineStepProbeResultDto> ProbePipelineStepAsync(
        string projectIdentity, string stepId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (!Enum.TryParse<OrchestrationStage>(stepId, ignoreCase: true, out var stage))
            return new PipelineStepProbeResultDto(false, $"'{stepId}' is not a recognized pipeline step.");
        var definition = await GetFlowDefinitionAsync(project.ProjectId, ct);
        if (definition is null || !definition.Stages.Contains(stage))
            return new PipelineStepProbeResultDto(false, $"Step '{stepId}' is not part of the project's current pipeline.");
        return new PipelineStepProbeResultDto(true, $"Step '{stepId}' is present and well-formed.");
    }

    private static PipelineDefinitionResponse ToPipelineDefinitionResponse(FlowDefinitionDto definition)
        => new(
            definition.ProjectId,
            definition.Version,
            definition.Stages.Select((stage, index) => new PipelineStepDto(stage.ToString(), index)).ToList(),
            definition.MaxReissueAttempts,
            definition.UpdatedAt);

    // ----------------------------------------------------------------
    // Test-only façade: InWriteTransactionAsync is private on the main
    // TaskServerStore.cs partial (a file this slice does not edit), so
    // task-server.Tests cannot open a (connection, transaction) pair to
    // exercise SetTaskTagsAsync's exact call shape without a thin internal
    // wrapper. These do not widen access to anything beyond what
    // InternalsVisibleTo("TaskServer.Tests") already grants for this file.
    // ----------------------------------------------------------------

    internal Task InWriteTransactionForTests(Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken ct)
        => InWriteTransactionAsync(action, ct);

    internal async Task<IReadOnlyList<string>> ReadTaskTagIdsForTests(string taskId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(
            connection, "SELECT tag_id FROM task_tags WHERE task_id = $task ORDER BY tag_id;", ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }
}
