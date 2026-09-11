using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio task lifecycle actions (move, start, continue, stop, delete) for
/// the pull-based Task Server model: a Runner claims '2-ready' work itself
/// through the existing <c>/runners/{runnerId}/claims</c> endpoint, so these
/// actions only ever change durable task state - they never spawn a process
/// or fence a run directly.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<MoveTaskResponse> MoveTaskAsync(
        string projectId, string taskIdentity, MoveTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.TargetState) || !StudioTaskLanes.All.Contains(request.TargetState))
            throw new ArgumentException($"Unknown target state '{request.TargetState}'.");
        MoveTaskResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var now = UtcNow;
            var position = await PlaceInLaneAsync(
                connection, transaction, existing.ProjectId, request.TargetState, existing.TaskId, request.TargetIndex, ct);
            await ExecuteAsync(connection, """
                UPDATE tasks SET state = $state, version = version + 1, updated_at = $updated WHERE id = $id;
                UPDATE orchestrator_contexts
                   SET hidden_at = CASE WHEN $state = '7-archive' THEN COALESCE(hidden_at, $updated) ELSE NULL END
                 WHERE task_id = $id;
                """, ct, transaction,
                ("$state", request.TargetState), ("$updated", Iso(now)), ("$id", existing.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.moved", "task", existing.TaskId,
                JsonSerializer.Serialize(new { from = existing.State, to = request.TargetState, request.Reason }), ct);
            result = new MoveTaskResponse(
                existing with { State = request.TargetState, Version = existing.Version + 1, UpdatedAt = now },
                position);
        }, ct);
        return result!;
    }

    public async Task<MoveTaskResponse> MoveTaskToTopAsync(
        string projectId, string taskIdentity, string actorId, CancellationToken ct)
    {
        RequireWritable();
        MoveTaskResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            if (existing.State != StudioTaskLanes.Ready)
                throw new TaskServerConflictException(
                    "task-not-ready", "Only a task in the ready lane can be moved to the top.");
            var position = await PlaceInLaneAsync(
                connection, transaction, existing.ProjectId, StudioTaskLanes.Ready, existing.TaskId, 0, ct);
            var now = UtcNow;
            await ExecuteAsync(connection,
                "UPDATE tasks SET version = version + 1, updated_at = $updated WHERE id = $id;",
                ct, transaction, ("$updated", Iso(now)), ("$id", existing.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.moved-to-top", "task", existing.TaskId, "{}", ct);
            result = new MoveTaskResponse(existing with { Version = existing.Version + 1, UpdatedAt = now }, position);
        }, ct);
        return result!;
    }

    public async Task<TaskLifecycleResponse> StartTaskAsync(
        string projectId, string taskIdentity, StartTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        TaskLifecycleResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            if (existing.State == StudioTaskLanes.Progress)
            {
                result = new TaskLifecycleResponse(existing);
                return;
            }
            if (existing.State is not (StudioTaskLanes.Backlog or StudioTaskLanes.Ready))
                throw new TaskServerConflictException(
                    "task-not-startable",
                    $"Task is in state '{existing.State}'; only backlog or ready tasks can be started.");
            var now = UtcNow;
            var rank = await NextRankAsync(connection, transaction, existing.ProjectId, StudioTaskLanes.Ready, ct);
            await ExecuteAsync(connection, """
                UPDATE tasks SET state = $state, rank = $rank, version = version + 1, updated_at = $updated
                 WHERE id = $id;
                """, ct, transaction,
                ("$state", StudioTaskLanes.Ready), ("$rank", rank), ("$updated", Iso(now)), ("$id", existing.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.start-requested", "task", existing.TaskId,
                JsonSerializer.Serialize(new { request.Model, request.CliType, request.ThinkingLevel }), ct);
            result = new TaskLifecycleResponse(
                existing with { State = StudioTaskLanes.Ready, Version = existing.Version + 1, UpdatedAt = now });
        }, ct);
        return result!;
    }

    public async Task<TaskLifecycleResponse> ContinueTaskAsync(
        string projectId, string taskIdentity, ContinueTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("A continuation prompt is required.");
        TaskLifecycleResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            if (existing.State == StudioTaskLanes.Archive)
                throw new TaskServerConflictException("task-archived", "An archived task cannot be continued.");
            if (existing.State == StudioTaskLanes.Progress)
                throw new TaskServerConflictException(
                    "task-active", "Stop the active run before continuing this task.");
            var now = UtcNow;
            var rank = await NextRankAsync(connection, transaction, existing.ProjectId, StudioTaskLanes.Ready, ct);
            await ExecuteAsync(connection, """
                UPDATE tasks SET state = $state, rank = $rank, version = version + 1, updated_at = $updated
                 WHERE id = $id;
                """, ct, transaction,
                ("$state", StudioTaskLanes.Ready), ("$rank", rank), ("$updated", Iso(now)), ("$id", existing.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.continue-requested", "task", existing.TaskId,
                JsonSerializer.Serialize(new { request.Model, request.CliType, request.ThinkingLevel, request.Mode }), ct);
            result = new TaskLifecycleResponse(
                existing with { State = StudioTaskLanes.Ready, Version = existing.Version + 1, UpdatedAt = now });
        }, ct);
        // The continuation instruction itself is persisted on the task's durable
        // orchestrator context turn timeline, so the runner that next claims this
        // task can read the latest instruction the same way it reads chat turns.
        await AppendOrchestratorContextTurnAsync(
            result!.Task.ProjectId,
            result.Task.TaskId,
            new AppendOrchestratorContextTurnRequest(
                new OrchestratorContextTurnDto($"continue_{Guid.NewGuid():N}", UtcNow, "user", request.Prompt)),
            actorId,
            ct);
        return result;
    }

    public async Task<TaskLifecycleResponse> StopTaskAsync(
        string projectId, string taskIdentity, StopTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        TaskLifecycleResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            string? runId = null;
            long fence = 0;
            await using (var command = Command(connection, """
                SELECT run_id, fence FROM leases WHERE task_id = $task AND status = 'active' LIMIT 1;
                """, transaction, ("$task", existing.TaskId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    runId = reader.GetString(0);
                    fence = reader.GetInt64(1);
                }
            }
            if (runId is null)
                throw new KeyNotFoundException("The task has no active run to stop.");

            await ExecuteAsync(connection,
                "UPDATE runs SET status = 'stop-requested' WHERE id = $run AND status = 'running';",
                ct, transaction, ("$run", runId));
            await AppendLifecycleEventAsync(connection, transaction, runId, existing.TaskId, fence,
                "lifecycle.stop-requested", new { reason = request.Reason ?? "user" }, ct);
            await AuditAsync(connection, transaction, actorId, "task.stop-requested", "task", existing.TaskId,
                JsonSerializer.Serialize(new { runId, request.Reason }), ct);
            var run = await ReadRunAsync(connection, transaction, runId, ct);
            result = new TaskLifecycleResponse(existing, run);
        }, ct);
        return result!;
    }

    public async Task DeleteTaskAsync(string projectId, string taskIdentity, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var runCount = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM runs WHERE task_id = $task;", ct, transaction, ("$task", existing.TaskId)));
            if (runCount > 0)
                throw new TaskServerConflictException(
                    "task-has-history", "A task with run history cannot be deleted; move it to the archive lane instead.");
            await ExecuteAsync(connection, """
                DELETE FROM orchestrator_context_turns WHERE context_key IN (
                    SELECT context_key FROM orchestrator_contexts WHERE task_id = $task);
                DELETE FROM orchestrator_contexts WHERE task_id = $task;
                DELETE FROM tasks WHERE id = $task;
                """, ct, transaction, ("$task", existing.TaskId));
            await AuditAsync(connection, transaction, actorId, "task.deleted", "task", existing.TaskId, "{}", ct);
        }, ct);
    }

    private static async Task<long> NextRankAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string state, CancellationToken ct)
    {
        var max = await ScalarAsync(
            connection, "SELECT MAX(rank) FROM tasks WHERE project_id = $project AND state = $state;",
            ct, transaction, ("$project", projectId), ("$state", state));
        return (max is null or DBNull ? 0 : Convert.ToInt64(max)) + 1;
    }

    /// <summary>
    /// Places <paramref name="movingTaskId"/> at <paramref name="targetIndex"/>
    /// (or the end, when null) within the destination lane and reassigns
    /// contiguous ranks to every task in that lane, so ordering never depends
    /// on interpolating between two integers that can run out of room.
    /// </summary>
    private static async Task<int> PlaceInLaneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string state,
        string movingTaskId,
        int? targetIndex,
        CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var command = Command(connection, """
            SELECT id FROM tasks WHERE project_id = $project AND state = $state AND id <> $moving
             ORDER BY rank, task_key;
            """, transaction, ("$project", projectId), ("$state", state), ("$moving", movingTaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var index = targetIndex is null ? ids.Count : Math.Clamp(targetIndex.Value, 0, ids.Count);
        ids.Insert(index, movingTaskId);
        for (var i = 0; i < ids.Count; i++)
        {
            await ExecuteAsync(connection, "UPDATE tasks SET rank = $rank WHERE id = $id;",
                ct, transaction, ("$rank", (long)i), ("$id", ids[i]));
        }
        return index + 1;
    }
}

public static class StudioTaskLanes
{
    public const string Backlog = "0-backlog";
    public const string Ready = "2-ready";
    public const string Progress = "3-progress";
    public const string AutoReview = "4-auto-review";
    public const string HumanReview = "5-human-review";
    public const string Escalated = "5e-escalated";
    public const string Completed = "6-completed";
    public const string Archive = "7-archive";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Backlog, Ready, Progress, AutoReview, HumanReview, Escalated, Completed, Archive],
        StringComparer.Ordinal);
}
