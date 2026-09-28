using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<SteeringActionReceipt> ApplySteeringActionAsync(
        string projectId, string taskIdentity, SteeringActionRequest request, string actor, CancellationToken ct)
    {
        RequireWritable();
        if (request.ContractVersion != 1 || string.IsNullOrWhiteSpace(request.CommandId)
            || request.CommandId.Length > 128 || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 2048 || request.ExpectedTaskVersion < 1
            || request.ExpectedGeneration < 0 || projectId == UnscopedProjectToken)
            throw new ArgumentException("A supported contract version, command id, expected versions and reason are required.");
        if (request.Action is not ("queue" or "park"))
            throw new ArgumentException("Unsupported steering action.");

        SteeringActionReceipt? receipt = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var previous = await ReadSteeringReceiptAsync(connection, transaction, request.CommandId, ct);
            if (previous is not null)
            {
                if (previous.TaskId != task.TaskId || previous.Action != request.Action
                    || previous.ExpectedTaskVersion != request.ExpectedTaskVersion
                    || previous.ExpectedGeneration != request.ExpectedGeneration
                    || previous.Reason != request.Reason.Trim() || previous.Actor != actor)
                    throw new TaskServerConflictException("steering-command-conflict", "Command id is bound to different input.");
                receipt = previous;
                return;
            }

            var generation = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction,
                ("$task", task.TaskId)) ?? 0L);
            if (task.Version != request.ExpectedTaskVersion || generation != request.ExpectedGeneration)
                throw new TaskServerConflictException("steering-generation-stale",
                    $"Current task version is {task.Version} and run generation is {generation}.");

            var active = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(*) FROM leases WHERE task_id = $task AND status = 'active' AND expires_at > $now;",
                ct, transaction, ("$task", task.TaskId), ("$now", Iso(UtcNow))) ?? 0L);
            if (active > 0)
                throw new TaskServerConflictException("task-attempt-active", "An active run owns this task.");

            var nextState = request.Action switch
            {
                "queue" when task.State is "0-backlog" or "5-human-review" or "5e-escalated" => "2-ready",
                "park" when task.State == "2-ready" => "0-backlog",
                _ => throw new TaskServerConflictException("steering-action-ineligible",
                    $"Action '{request.Action}' is not eligible from '{task.State}'."),
            };
            var now = UtcNow;
            await PlaceInLaneAsync(connection, transaction, task.ProjectId, nextState, task.TaskId, null, ct);
            await ExecuteAsync(connection,
                "UPDATE tasks SET state = $state, version = version + 1, updated_at = $now WHERE id = $task;",
                ct, transaction, ("$state", nextState), ("$now", Iso(now)), ("$task", task.TaskId));
            await ExecuteAsync(connection, """
                INSERT INTO steering_actions(command_id, project_id, task_id, action,
                    expected_task_version, expected_generation, result_task_version,
                    result_state, actor, reason, accepted_at)
                VALUES ($command, $project, $task, $action, $expectedVersion, $generation,
                    $resultVersion, $state, $actor, $reason, $at);
                """, ct, transaction,
                ("$command", request.CommandId), ("$project", task.ProjectId),
                ("$task", task.TaskId), ("$action", request.Action),
                ("$expectedVersion", request.ExpectedTaskVersion),
                ("$generation", request.ExpectedGeneration),
                ("$resultVersion", task.Version + 1), ("$state", nextState),
                ("$actor", actor), ("$reason", request.Reason.Trim()), ("$at", Iso(now)));
            await AuditAsync(connection, transaction, actor, "steering.action-accepted", "task", task.TaskId,
                System.Text.Json.JsonSerializer.Serialize(new { request.CommandId, request.Action, request.Reason, generation }), ct);
            receipt = new SteeringActionReceipt(request.CommandId, task.ProjectId, task.TaskId,
                request.Action, request.ExpectedTaskVersion, request.ExpectedGeneration,
                task.Version + 1, nextState, actor, request.Reason.Trim(), now);
        }, ct);
        return receipt!;
    }

    public async Task<SteeringActionReceipt?> GetSteeringActionAsync(
        string projectId, string taskIdentity, string commandId, CancellationToken ct)
    {
        if (projectId == UnscopedProjectToken) return null;
        var task = await GetTaskAsync(projectId, taskIdentity, ct);
        if (task is null) return null;
        await using var connection = await OpenReadyAsync(ct);
        var receipt = await ReadSteeringReceiptAsync(connection, null, commandId, ct);
        return receipt?.TaskId == task.TaskId ? receipt : null;
    }

    private static async Task<SteeringActionReceipt?> ReadSteeringReceiptAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string commandId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT command_id, project_id, task_id, action, expected_task_version,
                   expected_generation, result_task_version, result_state, actor, reason, accepted_at
              FROM steering_actions WHERE command_id = $command;
            """, transaction, ("$command", commandId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SteeringActionReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), Parse(reader.GetString(10)))
            : null;
    }
}
