using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Task creation and bulk/lifecycle-adjacent operations for the P1
/// "task lifecycle extras" Studio bundle: task creation from the unscoped
/// legacy body shape, the reverse task-reference lookup, bulk
/// reorder/batch-move, the archive listing, and the task-lifecycle side
/// actions (concept dossier, context-usage refresh, integration rebase,
/// planning closure, promote-concept, promote-to-coding) that have no
/// dedicated backing subsystem elsewhere in the standalone Task Server.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions ExtrasJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Additive schema for this bundle. Wired into <c>ApplyMigrationsAsync</c>
    /// by the integrator once every P1 group's migration method exists.
    /// <c>task_references</c> is owned and authoritatively written by the
    /// G4_TaskMetadata group (its <c>PUT /api/tasks/{taskId}/references</c>
    /// route); it is declared here defensively, with the exact same shape,
    /// only so <see cref="GetTaskDependentsAsync"/> can read it even if this
    /// migration runs before that group's.
    /// </summary>
    internal async Task ApplyStudioTaskLifecycleExtrasMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, "tasks", "mode", "TEXT", ct);
        await EnsureColumnAsync(connection, "tasks", "magazine_deliverable_json", "TEXT", ct);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS task_batch_moves(
                batch_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                requested_json TEXT NOT NULL,
                result_json TEXT,
                created_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS task_lifecycle_actions(
                task_id TEXT NOT NULL,
                action TEXT NOT NULL,
                status TEXT NOT NULL,
                detail_json TEXT,
                actor_id TEXT,
                requested_at TEXT NOT NULL,
                completed_at TEXT,
                PRIMARY KEY(task_id, action)
            );
            CREATE TABLE IF NOT EXISTS task_references(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                reference_task_id TEXT NOT NULL REFERENCES tasks(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY(task_id, reference_task_id)
            );
            """, ct);
    }

    // ---- Task creation (POST /api/tasks -> /api/v1/studio/tasks) ----

    public Task<TaskDto> CreateStudioTaskAsync(StudioCreateTaskRequest request, string actorId, CancellationToken ct)
        => CreateStudioTaskAsync(request, actorId, null, ct);

    internal async Task<TaskDto> CreateStudioTaskAsync(StudioCreateTaskRequest request, string actorId,
        StudioAuthUserDto? user, CancellationToken ct)
    {
        var projectIdentity = request.Project ?? request.ProjectId;
        if (string.IsNullOrWhiteSpace(projectIdentity))
            throw new ArgumentException("A project id is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var requestedState = request.TargetState ?? request.State;
        var state = string.IsNullOrWhiteSpace(requestedState) ? StudioTaskLanes.Backlog : requestedState;
        var mode = request.Mode?.Trim().ToLowerInvariant();
        string? scope = null;
        if (request.MagazineDeliverable is { } magazine)
        {
            if (!MagazineDeliverableContract.IsResearch(mode, request.Kind)
                || state is StudioTaskLanes.Completed or StudioTaskLanes.Archive)
                throw new ArgumentException("Declare a Magazine destination on a research card before acceptance.");
            await using var connection = await OpenReadyAsync(ct);
            scope = await ResolveMagazineScopeAsync(connection, null, magazine, ct);
            RequireMagazineProjectAccess(user, project.ProjectId, scope);
        }
        return await CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest(request.Title, request.PromptMarkdown ?? request.Body, state, request.TaskId, request.TaskKey),
            actorId, ct, mode, request.MagazineDeliverable, scope);
    }

    private static readonly HashSet<string> MagazineCatalogueIds = typeof(TaskServerStore).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("MagazineCatalogue.", StringComparison.Ordinal)
                       && name.EndsWith(".persona", StringComparison.Ordinal))
        .Select(name => name["MagazineCatalogue.".Length..^".persona".Length])
        .ToHashSet(StringComparer.Ordinal);

    private static async Task<string> ResolveMagazineScopeAsync(SqliteConnection connection,
        SqliteTransaction? transaction, MagazineDeliverableRequest request, CancellationToken ct)
    {
        if (MagazineDeliverableContract.Validate(request, MagazineCatalogueIds.Contains) is { } error)
            throw new ArgumentException(error);
        var scope = request.Scope.Trim();
        if (scope.Equals("installation", StringComparison.OrdinalIgnoreCase)) return "installation";
        var parts = scope.Split(':', 2);
        var kind = parts[0].ToLowerInvariant();
        var sql = kind == "workspace"
            ? "SELECT id FROM workspaces WHERE id = $id COLLATE NOCASE;"
            : "SELECT id FROM projects WHERE id = $id COLLATE NOCASE;";
        var id = await ScalarAsync(connection, sql, ct, transaction, ("$id", parts[1])) as string
            ?? throw new KeyNotFoundException("The Magazine audience does not exist.");
        return kind + ":" + id;
    }

    internal async Task<object> AttachMagazineDeliverableAsync(string projectId, string taskIdentity,
        AttachMagazineDeliverableRequest request, string actorId, StudioAuthUserDto? user, bool previewOnly, CancellationToken ct)
    {
        if (!previewOnly) RequireWritable();
        if (request.MagazineDeliverable is null) throw new ArgumentException("A Magazine deliverable is required.");
        object? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            if (!MagazineDeliverableContract.IsResearch(task.Mode, null))
                throw new ArgumentException("Only research cards can declare a Magazine destination.");
            var scope = await ResolveMagazineScopeAsync(connection, transaction, request.MagazineDeliverable, ct);
            RequireMagazineProjectAccess(user, task.ProjectId, scope);
            var completed = task.State is StudioTaskLanes.Completed or StudioTaskLanes.Archive || task.ArchivedAt is not null;
            var version = request.AcceptedResultVersion?.Trim();
            if (completed != (version is not null))
                throw new TaskServerConflictException("magazine-version-required", "An accepted card requires an exact accepted result version.");
            if (completed && !await IsAcceptedMagazineRunAsync(connection, transaction, task.TaskId, version!, ct))
                throw new TaskServerConflictException("magazine-version-unknown", "The requested version is not a known accepted Magazine bundle for this card.");

            var declaration = MagazineDeliverableContract.Declare(request.MagazineDeliverable, scope, task.TaskKey,
                actorId, UtcNow, version);
            var preview = MagazineDeliverableContract.Preview(task.TaskKey, declaration);
            if (task.MagazineDeliverable is { } existing)
            {
                if (previewOnly || MagazineDeliverableContract.Preview(task.TaskKey, existing) != preview
                    || completed && request.Confirmation != preview.Confirmation)
                    throw new TaskServerConflictException("magazine-already-declared", "This card already has a Magazine declaration.");
                result = existing;
                return;
            }
            if (previewOnly)
            {
                result = preview;
                return;
            }
            if (completed && request.Confirmation != preview.Confirmation)
                throw new TaskServerConflictException("magazine-confirmation-required", "Preview and confirm the exact audience and accepted result version.");
            await ExecuteAsync(connection, """
                UPDATE tasks SET magazine_deliverable_json = $declaration, version = version + 1, updated_at = $now
                 WHERE id = $id;
                """, ct, transaction, ("$declaration", JsonSerializer.Serialize(declaration, ExtrasJson)),
                ("$now", Iso(UtcNow)), ("$id", task.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.magazine-declared", "task", task.TaskId,
                JsonSerializer.Serialize(preview with { Confirmation = "" }, ExtrasJson), ct);
            result = declaration;
        }, ct);
        return result!;
    }

    private static void RequireMagazineProjectAccess(StudioAuthUserDto? user, string project, string scope)
    {
        if (user is not { Role: StudioUserRoles.Operator, ProjectIds.Count: > 0 }) return;
        const string prefix = "project:";
        if (!user.ProjectIds.Contains(project, StringComparer.Ordinal)
            || !scope.StartsWith(prefix, StringComparison.Ordinal)
            || !user.ProjectIds.Contains(scope[prefix.Length..], StringComparer.Ordinal))
            throw new StudioAuthorizationException("magazine-scope-denied",
                "This operator needs access to both the card project and the Magazine audience.");
    }

    private static async Task<bool> IsAcceptedMagazineRunAsync(SqliteConnection connection, SqliteTransaction transaction,
        string taskId, string version, CancellationToken ct)
    {
        // A completed run has an immutable completion envelope and artifact inventory.
        // Acceptance must follow that completion and identify the latest run at that point.
        if (!version.StartsWith("run-", StringComparison.Ordinal) || version.Length <= 4) return false;
        var run = version[4..];
        var accepted = await ScalarAsync(connection, """
            SELECT a.occurred_at
              FROM audit a
              JOIN run_completions c ON c.run_id = $run AND c.completed_at <= a.occurred_at
             WHERE a.target_type = 'task' AND a.target_id = $task AND a.action = 'task.moved'
               AND json_extract(a.detail_json, '$.from') = '5-human-review'
               AND json_extract(a.detail_json, '$.to') = '6-completed'
               AND $run = (
                   SELECT id FROM runs WHERE task_id = $task AND created_at <= a.occurred_at
                   ORDER BY created_at DESC, id DESC LIMIT 1
               )
             ORDER BY a.sequence LIMIT 1;
            """, ct, transaction, ("$task", taskId), ("$run", run)) as string;
        if (accepted is null) return false;
        foreach (var path in MagazineDeliverableContract.ResultFiles)
        {
            var count = await ScalarAsync(connection, """
                SELECT COUNT(*) FROM artifacts a
                  JOIN run_completions c ON c.run_id = a.run_id
                 WHERE a.run_id = $run AND (a.name = $path OR a.name = $name) AND a.created_at <= c.completed_at;
                """, ct, transaction, ("$run", run), ("$path", path), ("$name", path["results/".Length..]));
            if (count is not 1L) return false;
        }
        return true;
    }

    // ---- Dependents (reverse task_references lookup) ----

    public async Task<IReadOnlyList<TaskDependentDto>> GetTaskDependentsAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct) ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.id, t.task_key, t.title, t.state
              FROM task_references r
              JOIN tasks t ON t.id = r.task_id
             WHERE r.reference_task_id = $task
             ORDER BY t.task_key;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TaskDependentDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new TaskDependentDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    // ---- Reference status projection (reference-picker validation) ----

    public async Task<ReferenceStatusResponse> GetReferenceStatusesAsync(ReferenceStatusRequest request, CancellationToken ct)
    {
        var keys = (request.Keys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await using var connection = await OpenReadyAsync(ct);
        var items = new List<TaskReferenceStatusDto>(keys.Count);
        foreach (var key in keys)
        {
            await using var command = Command(connection, """
                SELECT id, task_key, title, state FROM tasks WHERE task_key = upper($key) LIMIT 1;
                """, ("$key", key));
            await using var reader = await command.ExecuteReaderAsync(ct);
            items.Add(await reader.ReadAsync(ct)
                ? new TaskReferenceStatusDto(key, true, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                : new TaskReferenceStatusDto(key, false));
        }
        return new ReferenceStatusResponse(items);
    }

    // ---- Reorder (bulk rank assignment within each task's current lane) ----

    public async Task<ReorderTasksResponse> ReorderTasksAsync(ReorderTasksRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (request.TaskIds is null || request.TaskIds.Count == 0)
            throw new ArgumentException("At least one task id is required.");
        var updated = new List<TaskDto>(request.TaskIds.Count);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var laneRanks = new Dictionary<(string ProjectId, string State), long>();
            foreach (var identity in request.TaskIds)
            {
                var existing = await ReadTaskAsync(connection, transaction, UnscopedProjectToken, identity, ct)
                    ?? throw new KeyNotFoundException($"Task '{identity}' was not found.");
                var laneKey = (existing.ProjectId, existing.State);
                var rank = laneRanks.TryGetValue(laneKey, out var next) ? next : 0;
                var now = UtcNow;
                await ExecuteAsync(connection, """
                    UPDATE tasks SET rank = $rank, version = version + 1, updated_at = $updated WHERE id = $id;
                    """, ct, transaction, ("$rank", rank), ("$updated", Iso(now)), ("$id", existing.TaskId));
                laneRanks[laneKey] = rank + 1;
                updated.Add(existing with { Version = existing.Version + 1, UpdatedAt = now });
            }
            await AuditAsync(connection, transaction, actorId, "task.reordered", "task", string.Join(",", request.TaskIds),
                JsonSerializer.Serialize(new { count = request.TaskIds.Count }, ExtrasJson), ct);
        }, ct);
        return new ReorderTasksResponse(updated);
    }

    // ---- Archive listing ----

    public async Task<ArchivedTasksResponse> GetArchivedTasksAsync(
        string? projectId, int offset, int limit, string? search, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        await using var connection = await OpenReadyAsync(ct);

        var filters = new List<string> { "(archive_state IS NOT NULL OR archived_at IS NOT NULL)" };
        var parameters = new List<(string Name, object? Value)>();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            filters.Add("project_id = $project");
            parameters.Add(("$project", projectId));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add("(title LIKE $search OR task_key LIKE $search)");
            parameters.Add(("$search", $"%{search.Trim()}%"));
        }
        var where = string.Join(" AND ", filters);

        await using var countCommand = Command(connection, $"SELECT count(*) FROM tasks WHERE {where};", parameters.ToArray());
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        var pageParameters = new List<(string Name, object? Value)>(parameters)
        {
            ("$limit", (long)limit),
            ("$offset", (long)offset),
        };
        await using var command = Command(connection, $"""
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at, mode, magazine_deliverable_json
              FROM tasks
             WHERE {where}
             ORDER BY archived_at DESC, updated_at DESC
             LIMIT $limit OFFSET $offset;
            """, pageParameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<TaskDto>();
        while (await reader.ReadAsync(ct)) items.Add(ReadTask(reader));
        return new ArchivedTasksResponse(items, total);
    }

    // ---- Batch move (synchronous; durably recorded) ----

    public async Task<BatchMoveJobResponse> StartBatchMoveAsync(
        BatchMoveRequest request, string actorId, StudioLifecycleCoordinator coordinator, CancellationToken ct)
    {
        RequireWritable();
        if (request.Items is null || request.Items.Count == 0)
            throw new ArgumentException("At least one batch-move item is required.");

        var results = new List<BatchMoveItemResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            try
            {
                var response = await coordinator.MoveTaskAsync(
                    UnscopedProjectToken, item.TaskId, new MoveTaskRequest(item.TargetState, item.TargetIndex, item.Reason),
                    actorId, ct);
                results.Add(new BatchMoveItemResult(item.TaskId, true, Task: response.Task));
            }
            catch (KeyNotFoundException exception)
            {
                results.Add(new BatchMoveItemResult(item.TaskId, false, "not-found", exception.Message));
            }
            catch (ArgumentException exception)
            {
                results.Add(new BatchMoveItemResult(item.TaskId, false, "invalid-request", exception.Message));
            }
            catch (TaskServerConflictException exception)
            {
                results.Add(new BatchMoveItemResult(item.TaskId, false, exception.Code, exception.Message));
            }
        }

        var batchId = $"batch_{Guid.NewGuid():N}";
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO task_batch_moves(batch_id, status, requested_json, result_json, created_at, completed_at)
                VALUES ($id, 'completed', $requested, $result, $created, $completed);
                """, ct, transaction,
                ("$id", batchId),
                ("$requested", JsonSerializer.Serialize(request.Items, ExtrasJson)),
                ("$result", JsonSerializer.Serialize(results, ExtrasJson)),
                ("$created", Iso(now)),
                ("$completed", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "task.batch-move", "task-batch", batchId,
                JsonSerializer.Serialize(new { count = request.Items.Count }, ExtrasJson), ct);
        }, ct);

        return new BatchMoveJobResponse(batchId, "completed", results, now, now);
    }

    public async Task<BatchMoveJobResponse?> GetBatchMoveAsync(string batchId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT status, result_json, created_at, completed_at FROM task_batch_moves WHERE batch_id = $id;
            """, ("$id", batchId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var status = reader.GetString(0);
        var resultJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var items = string.IsNullOrWhiteSpace(resultJson)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<BatchMoveItemResult>>(resultJson, ExtrasJson) ?? [];
        return new BatchMoveJobResponse(
            batchId,
            status,
            items,
            Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3)));
    }

    // ---- Task-lifecycle side actions with no dedicated backing subsystem ----
    //
    // A POST upserts the (task_id, action) row. `planning-closure` is a pure
    // state-tag flip the Task Server can resolve itself, so it is marked
    // `completed` synchronously. Every other action here conceptually
    // requires a Runner/coding-agent process to actually execute
    // (dossier generation, a real rebase against a checkout, context-usage
    // recomputation, or the real work a promote-* transition kicks off) -
    // per the architecture rule that Studio/Task-Server never gets direct
    // Runner or workspace access, those are recorded as `requested` only;
    // there is no downstream consumer wired up yet to resolve them.

    public Task<TaskLifecycleActionStatusDto> RequestConceptDossierAsync(
        string projectId, string taskIdentity, ConceptDossierRequest request, string actorId, CancellationToken ct)
        => UpsertLifecycleActionAsync(
            projectId, taskIdentity, TaskLifecycleActionKinds.ConceptDossier, actorId, request, completeSynchronously: false, ct);

    public Task<TaskLifecycleActionStatusDto> RefreshContextUsageAsync(
        string projectId, string taskIdentity, string actorId, CancellationToken ct)
        => UpsertLifecycleActionAsync(
            projectId, taskIdentity, TaskLifecycleActionKinds.ContextUsageRefresh, actorId, null, completeSynchronously: false, ct);

    public Task<TaskLifecycleActionStatusDto> RequestIntegrationRebaseAsync(
        string projectId, string taskIdentity, string actorId, CancellationToken ct)
        => UpsertLifecycleActionAsync(
            projectId, taskIdentity, TaskLifecycleActionKinds.IntegrationRebase, actorId, null, completeSynchronously: false, ct);

    public Task<TaskLifecycleActionStatusDto> SetPlanningClosureAsync(
        string projectId, string taskIdentity, PlanningClosureRequest request, string actorId, CancellationToken ct)
        => UpsertLifecycleActionAsync(
            projectId, taskIdentity, TaskLifecycleActionKinds.PlanningClosure, actorId, request, completeSynchronously: true, ct);

    public Task<TaskLifecycleActionStatusDto> GetPromoteConceptStatusAsync(
        string projectId, string taskIdentity, CancellationToken ct)
        => GetLifecycleActionStatusAsync(projectId, taskIdentity, TaskLifecycleActionKinds.PromoteConcept, ct);

    public Task<TaskLifecycleActionStatusDto> RequestPromoteConceptAsync(
        string projectId, string taskIdentity, PromoteConceptRequest request, string actorId, CancellationToken ct)
        => UpsertLifecycleActionAsync(
            projectId, taskIdentity, TaskLifecycleActionKinds.PromoteConcept, actorId, request, completeSynchronously: false, ct);

    public Task<TaskLifecycleActionStatusDto> GetPromoteToCodingStatusAsync(
        string projectId, string taskIdentity, CancellationToken ct)
        => GetLifecycleActionStatusAsync(projectId, taskIdentity, TaskLifecycleActionKinds.PromoteToCoding, ct);

    private async Task<TaskLifecycleActionStatusDto> UpsertLifecycleActionAsync(
        string projectId,
        string taskIdentity,
        string action,
        string actorId,
        object? requestDetail,
        bool completeSynchronously,
        CancellationToken ct)
    {
        RequireWritable();
        var detailJson = requestDetail is null ? null : JsonSerializer.Serialize(requestDetail, ExtrasJson);
        TaskLifecycleActionStatusDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var now = UtcNow;
            var status = completeSynchronously ? "completed" : "requested";
            await ExecuteAsync(connection, """
                INSERT INTO task_lifecycle_actions(task_id, action, status, detail_json, actor_id, requested_at, completed_at)
                VALUES ($task, $action, $status, $detail, $actor, $requested, $completed)
                ON CONFLICT(task_id, action) DO UPDATE SET
                    status = excluded.status,
                    detail_json = excluded.detail_json,
                    actor_id = excluded.actor_id,
                    requested_at = excluded.requested_at,
                    completed_at = excluded.completed_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$action", action), ("$status", status), ("$detail", detailJson),
                ("$actor", actorId), ("$requested", Iso(now)), ("$completed", completeSynchronously ? Iso(now) : null));
            await AuditAsync(connection, transaction, actorId, $"task.lifecycle-action.{action}", "task", task.TaskId,
                JsonSerializer.Serialize(new { action, status }, ExtrasJson), ct);
            result = new TaskLifecycleActionStatusDto(task.TaskId, action, status, detailJson, actorId, now, completeSynchronously ? now : null);
        }, ct);
        return result!;
    }

    private async Task<TaskLifecycleActionStatusDto> GetLifecycleActionStatusAsync(
        string projectId, string taskIdentity, string action, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct) ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT status, detail_json, actor_id, requested_at, completed_at
              FROM task_lifecycle_actions WHERE task_id = $task AND action = $action;
            """, ("$task", task.TaskId), ("$action", action));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new TaskLifecycleActionStatusDto(task.TaskId, action, "not-requested", null, null, task.UpdatedAt, null);
        return new TaskLifecycleActionStatusDto(
            task.TaskId,
            action,
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));
    }
}
