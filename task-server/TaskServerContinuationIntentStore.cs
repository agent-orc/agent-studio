using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<ContinuationIntentReceipt> SubmitContinuationIntentAsync(
        string projectId, string taskIdentity, ContinuationIntentRequest request,
        string actor, CancellationToken ct)
    {
        RequireWritable();
        ValidateContinuation(request);
        ContinuationIntentReceipt? accepted = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var task = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct)
                ?? throw new KeyNotFoundException("Task was not found.");
            var prior = await ReadContinuationAsync(connection, transaction, request.CommandId, ct);
            if (prior is not null)
            {
                var priorPayload = Convert.ToString(await ScalarAsync(connection,
                    "SELECT payload_json FROM continuation_intents WHERE command_id = $command;",
                    ct, transaction, ("$command", request.CommandId)));
                if (prior.Receipt.TaskId != task.TaskId
                    || priorPayload != System.Text.Json.JsonSerializer.Serialize(request)
                    || prior.Receipt.Actor != actor)
                    throw new TaskServerConflictException("continuation-command-conflict",
                        "Command id is bound to different input.");
                accepted = prior.Receipt;
                return;
            }
            if (task.Version != request.ExpectedTaskVersion)
                throw new TaskServerConflictException("continuation-task-stale",
                    $"Expected task version {request.ExpectedTaskVersion}, current version is {task.Version}.");
            if (task.State == StudioTaskLanes.Archive)
                throw new TaskServerConflictException("task-archived", "An archived task cannot be continued.");
            var active = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(*) FROM leases WHERE task_id = $task AND status IN ('active', 'process-unknown');",
                ct, transaction, ("$task", task.TaskId)) ?? 0L);
            // A running attempt keeps its current prompt and route. This intent
            // targets a later round and becomes Ready only after settlement.
            var nextState = active > 0 ? task.State : StudioTaskLanes.Ready;

            var round = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT COALESCE(MAX(round), 0) FROM continuation_intents WHERE task_id = $task;",
                ct, transaction, ("$task", task.TaskId)) ?? 0L) + 1;
            var fields = await ReadStudioFieldsAsync(connection, transaction, task.TaskId, ct);
            var effectiveCli = request.CliType ?? fields?.CliType;
            if (effectiveCli is not null)
            {
                var policy = ModelRoutingPolicyDocument.Value;
                if (request.Model is not null
                    && (!policy.ExplicitPinModels.TryGetValue(effectiveCli, out var models)
                        || !models.Contains(request.Model)))
                    throw new ArgumentException("Model is not supported by the task's selected CLI policy catalogue.");
                if (request.ThinkingLevel is not null
                    && (!policy.ExplicitThinkingLevels.TryGetValue(effectiveCli, out var levels)
                        || !levels.Contains(request.ThinkingLevel)))
                    throw new ArgumentException("Thinking level is not supported by the task's selected CLI policy catalogue.");
            }
            var model = request.Model ?? fields?.Model;
            var cliType = request.CliType ?? fields?.CliType;
            var thinkingLevel = request.ThinkingLevel ?? fields?.ThinkingLevel;
            var now = UtcNow;
            // The row and Ready promotion commit together. Every round remains
            // ordered even when another continue arrives before the next claim.
            await ExecuteAsync(connection, """
                INSERT INTO continuation_intents(command_id, project_id, task_id, round,
                    expected_task_version, result_task_version, payload_json, prompt, model, cli_type,
                    thinking_level, mode, reason, actor, accepted_at, policy_version, explicit_selection)
                VALUES ($command, $project, $task, $round, $expected, $result, $payload, $prompt,
                    $model, $cli, $thinking, $mode, $reason, $actor, $at, $policy, $explicit);
                """, ct, transaction,
                ("$command", request.CommandId), ("$project", task.ProjectId),
                ("$task", task.TaskId), ("$round", round),
                ("$expected", task.Version), ("$result", task.Version + 1),
                ("$payload", System.Text.Json.JsonSerializer.Serialize(request)),
                ("$prompt", request.Prompt), ("$model", model),
                ("$cli", cliType), ("$thinking", thinkingLevel),
                ("$mode", request.Mode), ("$reason", request.Reason),
                ("$actor", actor), ("$at", Iso(now)),
                ("$policy", ModelRoutingPolicyDocument.Value.Version),
                ("$explicit", request.Model is not null || request.CliType is not null
                    || request.ThinkingLevel is not null ? 1 : 0));
            if (nextState == StudioTaskLanes.Ready && task.State != StudioTaskLanes.Ready)
                await PlaceInLaneAsync(connection, transaction, task.ProjectId, StudioTaskLanes.Ready, task.TaskId, null, ct);
            await ExecuteAsync(connection,
                "UPDATE tasks SET state = $state, version = version + 1, updated_at = $now WHERE id = $task;",
                ct, transaction, ("$state", nextState), ("$now", Iso(now)), ("$task", task.TaskId));
            var contextTarget = await ResolveOrchestratorContextTargetAsync(
                connection, transaction, task.ProjectId, task.TaskId, ct);
            await AppendTurnToContextAsync(connection, transaction, contextTarget,
                new AppendOrchestratorContextTurnRequest(
                    new OrchestratorContextTurnDto($"continue_{Guid.NewGuid():N}", now,
                        "user", request.Prompt)), actor, ct);
            await AuditAsync(connection, transaction, actor, "continuation.accepted", "task", task.TaskId,
                System.Text.Json.JsonSerializer.Serialize(new { request.CommandId, round, request.Reason }), ct);
            accepted = new ContinuationIntentReceipt(request.CommandId, task.ProjectId, task.TaskId,
                task.Version, task.Version + 1, round, round, round, actor, request.Reason, now,
                ModelRoutingPolicyDocument.Value.Version,
                request.Model is not null || request.CliType is not null
                    || request.ThinkingLevel is not null);
        }, ct);
        return accepted!;
    }

    public async Task<ContinuationIntentProjection?> GetContinuationIntentAsync(
        string projectId, string taskIdentity, string commandId, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct);
        if (task is null) return null;
        await using var connection = await OpenReadyAsync(ct);
        var projection = await ReadContinuationAsync(connection, null, commandId, ct);
        return projection?.Receipt.TaskId == task.TaskId ? projection : null;
    }

    public async Task<IReadOnlyList<ContinuationIntentProjection>> ListContinuationIntentsAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        var commandIds = new List<string>();
        await using (var command = Command(connection, """
            SELECT command_id FROM continuation_intents
             WHERE task_id = $task ORDER BY round;
            """, null, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) commandIds.Add(reader.GetString(0));
        var projections = new List<ContinuationIntentProjection>(commandIds.Count);
        foreach (var commandId in commandIds)
            projections.Add((await ReadContinuationAsync(connection, null, commandId, ct))!);
        return projections;
    }

    private static async Task<ContinuationIntentProjection?> ReadContinuationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string commandId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT command_id, project_id, task_id, round, expected_task_version,
                   result_task_version, prompt, model, cli_type, thinking_level,
                   mode, reason, actor, accepted_at, status, run_id, fence, consumed_at,
                   policy_version, explicit_selection, payload_json
              FROM continuation_intents WHERE command_id = $command;
            """, transaction, ("$command", commandId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var round = reader.GetInt64(3);
        var receipt = new ContinuationIntentReceipt(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetInt64(4), reader.GetInt64(5), round, round, round,
            reader.GetString(12), reader.GetString(11), Parse(reader.GetString(13)),
            reader.GetString(18), reader.GetInt64(19) != 0);
        var submitted = System.Text.Json.JsonSerializer.Deserialize<ContinuationIntentRequest>(reader.GetString(20))!;
        return new ContinuationIntentProjection(receipt, reader.GetString(14), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.IsDBNull(17) ? null : Parse(reader.GetString(17)),
            new ContinuationSelectionMask(submitted.Model is not null,
                submitted.CliType is not null, submitted.ThinkingLevel is not null));
    }

    private static async Task<ContinuationIntentProjection?> ReadNextContinuationAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        var commandId = Convert.ToString(await ScalarAsync(connection, """
            SELECT command_id FROM continuation_intents
             WHERE task_id = $task AND status = 'queued' ORDER BY round LIMIT 1;
            """, ct, transaction, ("$task", taskId)));
        return string.IsNullOrEmpty(commandId) ? null : await ReadContinuationAsync(connection, transaction, commandId, ct);
    }

    private static FollowUpDeliveryDto ToFollowUpDelivery(
        ContinuationIntentProjection intent, string runId) =>
        new(intent.Prompt, intent.Mode, FollowUpPromptDigest.Compute(intent.Prompt),
            intent.Receipt.AcceptedAt, intent.Receipt.Reason, intent.Receipt.Actor, runId);

    /// <summary>
    /// Consumes the round bound to this run when its worker-start acknowledgement
    /// names the delivered prompt. Returns false when the run carries no round.
    /// </summary>
    private async Task<bool> ConsumeClaimedContinuationAsync(
        SqliteConnection connection, SqliteTransaction transaction, LeaseDto lease,
        string startedPromptSha256, string actor, CancellationToken ct)
    {
        var commandId = Convert.ToString(await ScalarAsync(connection, """
            SELECT command_id FROM continuation_intents
             WHERE task_id = $task AND run_id = $run AND status IN ('claimed', 'consumed');
            """, ct, transaction, ("$task", lease.TaskId), ("$run", lease.RunId)));
        // Convert.ToString maps a missing row to ""; a follow-up saved before
        // continuation intents then falls through to the reserved follow-up path.
        if (string.IsNullOrEmpty(commandId)) return false;
        var intent = (await ReadContinuationAsync(connection, transaction, commandId, ct))!;
        if (intent.Fence != lease.Fence
            || !string.Equals(FollowUpPromptDigest.Compute(intent.Prompt), startedPromptSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new TaskServerConflictException("follow-up-prompt-mismatch",
                "The worker-start prompt hash does not match the continuation round reserved by this run.");
        // A repeated acknowledgement keeps the first consumption receipt.
        if (intent.Status == "consumed") return true;
        var now = UtcNow;
        await ExecuteAsync(connection, """
            UPDATE continuation_intents SET status = 'consumed', consumed_at = $now
             WHERE command_id = $command AND run_id = $run AND status = 'claimed';
            """, ct, transaction, ("$now", Iso(now)), ("$command", commandId), ("$run", lease.RunId));
        var detail = System.Text.Json.JsonSerializer.Serialize(new
        {
            taskId = lease.TaskId,
            intent.Receipt.CommandId,
            intent.Receipt.Round,
            intent.Mode,
            Author = intent.Receipt.Actor,
            SavedAt = intent.Receipt.AcceptedAt,
            SavedReason = intent.Receipt.Reason,
            PromptSha256 = startedPromptSha256.ToLowerInvariant(),
            lease.Fence,
            state = "delivered",
        });
        await AuditAsync(connection, transaction, actor, "follow-up.delivered", "run", lease.RunId, detail, ct);
        await AuditAsync(connection, transaction, actor, "continuation.consumed", "run", lease.RunId, detail, ct);
        return true;
    }

    /// <summary>A claim that never acknowledged a started worker returns its round to the queue.</summary>
    private static async Task RequeueClaimedContinuationAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct) =>
        await ExecuteAsync(connection, """
            UPDATE continuation_intents
               SET status = 'queued', run_id = NULL, fence = NULL
             WHERE run_id = $run AND status = 'claimed';
            """, ct, transaction, ("$run", runId));

    /// <summary>
    /// Completion or archive explicitly supersedes every waiting round. The
    /// receipt stays readable with status <c>superseded</c>.
    /// </summary>
    private async Task SupersedeQueuedContinuationsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId,
        string actor, CancellationToken ct)
    {
        var commandIds = new List<string>();
        await using (var command = Command(connection, """
            SELECT command_id FROM continuation_intents
             WHERE task_id = $task AND status = 'queued' ORDER BY round;
            """, transaction, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) commandIds.Add(reader.GetString(0));
        if (commandIds.Count == 0) return;
        await ExecuteAsync(connection, """
            UPDATE continuation_intents SET status = 'superseded'
             WHERE task_id = $task AND status = 'queued';
            """, ct, transaction, ("$task", taskId));
        await AuditAsync(connection, transaction, actor, "follow-up.superseded", "task", taskId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                state = "superseded-by-completion",
                commandIds,
            }), ct);
    }

    private static void ValidateContinuation(ContinuationIntentRequest request)
    {
        if (request.ContractVersion != 1 || string.IsNullOrWhiteSpace(request.CommandId)
            || request.CommandId.Length > 128 || request.ExpectedTaskVersion < 1
            || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 100_000
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2048
            || request.Mode is not ("continue" or "steer" or "extend" or "newTask")
            || request.Model?.Length > 128 || request.CliType?.Length > 64
            || request.ThinkingLevel?.Length > 32
            || request.Model is not null && string.IsNullOrWhiteSpace(request.Model)
            || request.ThinkingLevel is not null && string.IsNullOrWhiteSpace(request.ThinkingLevel))
            throw new ArgumentException("A supported continuation command, task version, prompt, mode and reason are required.");
        if (request.CliType is not null
            && !ModelRoutingPolicyDocument.Value.ExplicitPinModels.ContainsKey(request.CliType))
            throw new ArgumentException("Invalid CLI selection.");
        if (request.Model is not null)
        {
            var catalogues = ModelRoutingPolicyDocument.Value.ExplicitPinModels;
            if (request.CliType is { } cli)
            {
                if (!catalogues[cli].Contains(request.Model))
                    throw new ArgumentException("Model is not supported by the selected CLI policy catalogue.");
            }
            else if (!catalogues.Values.Any(models => models.Contains(request.Model)))
                throw new ArgumentException("Model is not in the routing policy catalogue.");
        }
        var policyLevels = ModelRoutingPolicyDocument.Value.ExplicitThinkingLevels.Values
            .SelectMany(levels => levels);
        if (request.ThinkingLevel is not null
            && !policyLevels.Contains(request.ThinkingLevel, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported thinking level.");
        // The canonical routing policy permits explicit pins below its floor
        // with an explanation. Preserve the exact model and rung selected here.
    }
}
