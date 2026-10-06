using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task RecordRevokedRunReferenceAsync(
        string runId, RevokedRunReferenceRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Branch)
            || string.IsNullOrWhiteSpace(request.CommitSha)
            || !request.Branch.StartsWith("agent-studio/quarantine/", StringComparison.Ordinal)
            || request.CommitSha.Length is not (40 or 64)
            || !request.CommitSha.All(Uri.IsHexDigit)
            || !request.Branch.EndsWith("/" + request.CommitSha, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A generation-scoped quarantine ref and commit SHA are required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId,
                request.LeaseId, request.Fence);
            if (lease.Status != "revoked")
                throw new TaskServerConflictException("run-not-revoked",
                    "Only an operator-revoked run may report a quarantine reference.");
            var prior = await ScalarAsync(connection,
                "SELECT branch || '|' || commit_sha FROM revoked_run_references WHERE run_id = $run;",
                ct, transaction, ("$run", runId)) as string;
            if (prior is not null)
            {
                if (prior != request.Branch + "|" + request.CommitSha)
                    throw new TaskServerConflictException("quarantine-reference-conflict",
                        "This run already reported a different quarantine reference.");
                return;
            }
            await ExecuteAsync(connection, """
                INSERT INTO revoked_run_references(run_id, task_id, branch, commit_sha, reported_at)
                VALUES ($run, $task, $branch, $sha, $now);
                """, ct, transaction,
                ("$run", runId), ("$task", lease.TaskId),
                ("$branch", request.Branch), ("$sha", request.CommitSha), ("$now", Iso(UtcNow)));
            await AuditAsync(connection, transaction, actorId, "run.quarantine-retained", "run", runId,
                JsonSerializer.Serialize(new { request.Branch, request.CommitSha, lease.TaskId }), ct);
        }, ct);
    }

    public async Task<OlderBriefDeliveryDto?> GetOlderBriefDeliveryAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        var task = await GetTaskAsync(projectId, taskIdentity, ct);
        return task is null ? null : await ReadOlderBriefDeliveryAsync(connection, null, task.TaskId, ct);
    }

    public async Task<MoveTaskResponse> DecideOlderBriefDeliveryAsync(
        string projectId, string taskIdentity, string decision, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (decision is not ("accept" or "starting-point" or "discard"))
            throw new ArgumentException("Decision must be accept, starting-point, or discard.");
        MoveTaskResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var offer = await ReadOlderBriefDeliveryAsync(connection, transaction, task.TaskId, ct);
            if (task.State != "5e-escalated" || offer is not { Status: "pending" })
                throw new TaskServerConflictException("older-brief-offer-not-pending",
                    "No pending older-brief delivery is available for this card.");
            var currentRun = Convert.ToString(await ScalarAsync(connection,
                "SELECT id FROM runs WHERE task_id = $task ORDER BY created_at DESC LIMIT 1;",
                ct, transaction, ("$task", task.TaskId)));
            if (!string.Equals(currentRun, offer.RunId, StringComparison.Ordinal))
                throw new TaskServerConflictException("older-brief-attempt-superseded",
                    "A newer run superseded this offer.");
            if (decision == "accept"
                && (string.IsNullOrWhiteSpace(offer.ResultSha) || string.IsNullOrWhiteSpace(offer.ResultRef)))
                throw new TaskServerConflictException("older-brief-result-required",
                    "The offered result has no immutable result to accept.");
            var startRef = offer.ResultRef ?? offer.SalvageBranch;
            var startSha = offer.ResultRef is null ? offer.SalvageCommitSha : offer.ResultSha;
            if (decision == "starting-point"
                && (string.IsNullOrWhiteSpace(startRef) || string.IsNullOrWhiteSpace(startSha)))
                throw new TaskServerConflictException("older-brief-starting-point-required",
                    "The offered result has no published ref to start from.");
            var target = decision == "accept" ? "4-auto-review" : "2-ready";
            var position = await PlaceInLaneAsync(connection, transaction, task.ProjectId, target,
                task.TaskId, null, ct);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE tasks SET state = $target, version = version + 1, updated_at = $now WHERE id = $task;
                UPDATE older_brief_deliveries SET status = $decision, decided_at = $now WHERE task_id = $task;
                """, ct, transaction,
                ("$target", target), ("$decision", decision), ("$now", Iso(now)), ("$task", task.TaskId));
            if (decision == "starting-point")
                await ExecuteAsync(connection, """
                    INSERT INTO task_starting_points(task_id, source_run_id, result_ref, result_sha)
                    VALUES ($task, $run, $ref, $sha)
                    ON CONFLICT(task_id) DO UPDATE SET source_run_id = excluded.source_run_id,
                        result_ref = excluded.result_ref, result_sha = excluded.result_sha;
                    """, ct, transaction,
                    ("$task", task.TaskId), ("$run", offer.RunId),
                    ("$ref", startRef), ("$sha", startSha));
            await AuditAsync(connection, transaction, actorId, "run.older-brief-decided", "run", offer.RunId,
                JsonSerializer.Serialize(new { decision, taskId = task.TaskId, target }), ct);
            response = new MoveTaskResponse(task with
            {
                State = target, Version = task.Version + 1, UpdatedAt = now,
            }, position);
        }, ct);
        return response!;
    }

    private static async Task<OlderBriefDeliveryDto?> ReadOlderBriefDeliveryAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, brief_version, current_brief_version, result_sha, result_ref,
                   salvage_branch, salvage_commit_sha, status, offered_at, decided_at
              FROM older_brief_deliveries WHERE task_id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new OlderBriefDeliveryDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)));
    }
}
