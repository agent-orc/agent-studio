using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio task lifecycle actions (move, start, continue, stop, delete) for
/// the pull-based Task Server model: a Runner claims '2-ready' work itself
/// through the existing <c>/runners/{runnerId}/claims</c> endpoint, so these
/// actions change durable task state and may revoke a live lease. They never
/// spawn a runner process.
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
            var activeRunId = await ScalarAsync(connection, """
                SELECT run_id FROM leases
                 WHERE task_id = $task AND status IN ('active', 'process-unknown')
                 ORDER BY acquired_at DESC LIMIT 1;
                """, ct, transaction, ("$task", existing.TaskId)) as string;
            if (request.TargetState != existing.State
                && activeRunId is not null)
            {
                if (request.RunIntent is not ("revoke" or "steer"))
                    throw new TaskServerConflictException("run-intent-required",
                        "Moving a card with a live run requires runIntent: revoke or steer.");
                if (request.RunIntent == "steer")
                {
                    var queued = await ScalarAsync(connection, """
                        SELECT 1 FROM pending_follow_ups
                         WHERE task_id = $task AND state = 'queued'
                        UNION ALL
                        SELECT 1 FROM continuation_intents
                         WHERE task_id = $task AND status = 'queued'
                        LIMIT 1;
                        """, ct, transaction, ("$task", existing.TaskId));
                    if (queued is null)
                        throw new TaskServerConflictException("steer-follow-up-required",
                            "A move that keeps the run alive requires a queued follow-up.");
                }
            }
            var position = await PlaceInLaneAsync(
                connection, transaction, existing.ProjectId, request.TargetState, existing.TaskId, request.TargetIndex, ct);
            await ExecuteAsync(connection, """
                UPDATE tasks SET state = $state, version = version + 1, updated_at = $updated WHERE id = $id;
                UPDATE orchestrator_contexts
                   SET hidden_at = CASE WHEN $state = '7-archive' THEN COALESCE(hidden_at, $updated) ELSE NULL END
                 WHERE task_id = $id;
                """, ct, transaction,
                ("$state", request.TargetState), ("$updated", Iso(now)), ("$id", existing.TaskId));
            // Revoke only after the lane write succeeds; the transaction still
            // rolls both changes back if a later step fails.
            if (request.TargetState != existing.State
                && activeRunId is not null
                && request.RunIntent == "revoke")
            {
                await ExecuteAsync(connection, """
                    UPDATE leases SET status = 'revoked' WHERE run_id = $run;
                    UPDATE runs SET status = 'superseded', finished_at = $now WHERE id = $run;
                    INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, 1)
                    ON CONFLICT(task_id) DO UPDATE SET last_fence = last_fence + 1;
                    """, ct, transaction,
                    ("$run", activeRunId), ("$task", existing.TaskId), ("$now", Iso(now)));
                await AuditAsync(connection, transaction, actorId, "run.revoked", "run", activeRunId,
                    JsonSerializer.Serialize(new { reason = request.Reason, taskId = existing.TaskId,
                        salvage = "quarantine ref retained by runner after lease rejection" }), ct);
            }
            if (request.TargetState is StudioTaskLanes.Completed or StudioTaskLanes.Archive)
                await SupersedePendingFollowUpAsync(
                    connection, transaction, existing.TaskId, actorId, ct);
            await AuditAsync(connection, transaction, actorId, "task.moved", "task", existing.TaskId,
                JsonSerializer.Serialize(new { from = existing.State, to = request.TargetState,
                    request.Reason, request.RunIntent, activeRunId }), ct);
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
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        var prior = request.CommandId is null ? null : await GetContinuationIntentAsync(
            projectId, taskIdentity, request.CommandId, ct);
        var receipt = await SubmitContinuationIntentAsync(projectId, taskIdentity,
            new ContinuationIntentRequest(1, request.CommandId ?? $"studio:{Guid.NewGuid():N}",
                request.ExpectedTaskVersion ?? prior?.Receipt.ExpectedTaskVersion ?? task.Version,
                request.Prompt, request.Model,
                request.CliType, request.ThinkingLevel,
                string.IsNullOrWhiteSpace(request.Mode) ? "continue" : request.Mode,
                request.Reason ?? "operator-continue"), actorId, ct);
        var current = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        return new TaskLifecycleResponse(current, ContinuationReceipt: receipt);
    }

    private static async Task<FollowUpDeliveryDto?> ReadPendingFollowUpAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string taskId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT prompt, mode, prompt_sha256, saved_at, saved_reason, author
              FROM pending_follow_ups
             WHERE task_id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new FollowUpDeliveryDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private async Task SupersedePendingFollowUpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string actorId,
        CancellationToken ct)
    {
        await SupersedeQueuedContinuationsAsync(connection, transaction, taskId, actorId, ct);
        var followUp = await ReadPendingFollowUpAsync(connection, transaction, taskId, ct);
        if (followUp is null) return;
        await ExecuteAsync(connection,
            "DELETE FROM pending_follow_ups WHERE task_id = $task;",
            ct, transaction, ("$task", taskId));
        await AuditAsync(
            connection,
            transaction,
            actorId,
            "follow-up.superseded",
            "task",
            taskId,
            JsonSerializer.Serialize(new
            {
                state = "superseded-by-completion",
                followUp.Mode,
                followUp.Author,
                followUp.SavedAt,
                followUp.SavedReason,
                followUp.PromptSha256,
            }),
            ct);
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
