using AgentStudio.OrchestrationDecisions;
using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<FlowDefinitionDto> UpsertFlowDefinitionAsync(
        string projectId,
        UpsertFlowDefinitionRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateFlowDefinition(request);
        FlowDefinitionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var currentVersion = Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT version FROM flow_definitions WHERE project_id = $project;",
                ct,
                transaction,
                ("$project", projectId)) ?? 0L,
                CultureInfo.InvariantCulture);
            if (currentVersion == 0)
            {
                _ = await ScalarAsync(
                    connection,
                    "SELECT id FROM projects WHERE id = $project;",
                    ct,
                    transaction,
                    ("$project", projectId))
                    ?? throw new KeyNotFoundException("Project was not found.");
                if (request.ExpectedVersion is not null and not 0)
                    throw new TaskServerConflictException(
                        "resource-version-mismatch",
                        "The flow definition does not exist.");
            }
            else if (request.ExpectedVersion != currentVersion)
            {
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected flow definition version {request.ExpectedVersion?.ToString() ?? "missing"}, current version is {currentVersion}.");
            }

            var nextVersion = currentVersion + 1;
            var updatedAt = UtcNow;
            var stagesJson = JsonSerializer.Serialize(request.Stages);
            await ExecuteAsync(connection, """
                INSERT INTO flow_definitions(project_id, version, stages_json, max_reissue_attempts, updated_at)
                VALUES ($project, $version, $stages, $max_reissues, $updated)
                ON CONFLICT(project_id) DO UPDATE SET
                    version = excluded.version,
                    stages_json = excluded.stages_json,
                    max_reissue_attempts = excluded.max_reissue_attempts,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", projectId),
                ("$version", nextVersion),
                ("$stages", stagesJson),
                ("$max_reissues", request.MaxReissueAttempts),
                ("$updated", Iso(updatedAt)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.flow-definition-upserted",
                "project",
                projectId,
                JsonSerializer.Serialize(new { version = nextVersion, request.Stages, request.MaxReissueAttempts }),
                ct);
            result = new FlowDefinitionDto(
                projectId,
                nextVersion,
                request.Stages.ToArray(),
                request.MaxReissueAttempts,
                updatedAt);
        }, ct);
        return result!;
    }

    public async Task<FlowDefinitionDto?> GetFlowDefinitionAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT project_id, version, stages_json, max_reissue_attempts, updated_at
              FROM flow_definitions
             WHERE project_id = $project;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFlowDefinition(reader) : null;
    }

    public async Task<OrchestrationRunDto> CreateOrchestrationRunAsync(
        string projectId,
        CreateOrchestrationRunRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            result = await CreateOrchestrationRunCoreAsync(
                connection, transaction, projectId, request, actorId, ct);
        }, ct);
        return result!;
    }

    private async Task<OrchestrationRunDto> CreateOrchestrationRunCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CreateOrchestrationRunRequest request,
        string actorId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Task id and idempotency key are required.");
        ValidateJson(request.PayloadJson, "payloadJson");

        var existing = await ReadOrchestrationRunByKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            ct);
        if (existing is not null)
        {
            var originalPayload = await ScalarAsync(connection, """
                SELECT request_payload_json FROM orchestration_runs WHERE id = $run;
                """, ct, transaction, ("$run", existing.RunId)) as string;
            if (!string.Equals(existing.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(existing.TaskId, request.TaskId, StringComparison.Ordinal)
                || !JsonEquivalent(originalPayload ?? existing.PayloadJson, request.PayloadJson))
            {
                throw new TaskServerConflictException(
                    "idempotency-conflict",
                    "The orchestration idempotency key is already bound to different input.");
            }

            return existing;
        }

        string? taskProject = null;
        string? taskState = null;
        long taskVersion = 0;
        await using (var command = Command(connection, """
            SELECT project_id, state, version FROM tasks WHERE id = $task;
            """, transaction, ("$task", request.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                taskProject = reader.GetString(0);
                taskState = reader.GetString(1);
                taskVersion = reader.GetInt64(2);
            }
        }
        if (string.IsNullOrWhiteSpace(taskProject)
            || !string.Equals(taskProject, projectId, StringComparison.Ordinal))
            throw new KeyNotFoundException("Task was not found in the requested project.");
        if (!string.Equals(taskState, "4-auto-review", StringComparison.Ordinal))
        {
            throw new TaskServerConflictException(
                "task-not-auto-review",
                $"Orchestration can only start in Auto Review; task state is '{taskState}'.");
        }

        var activeRuns = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT count(*) FROM orchestration_runs
             WHERE task_id = $task AND status IN ('pending', 'leased');
            """, ct, transaction, ("$task", request.TaskId)) ?? 0L, CultureInfo.InvariantCulture);
        if (activeRuns > 0)
        {
            throw new TaskServerConflictException(
                "orchestration-already-active",
                "The task already has an active orchestration run.");
        }

        var definition = await ReadFlowDefinitionAsync(connection, transaction, projectId, ct)
            ?? throw new TaskServerConflictException(
                "flow-definition-missing",
                "The project has no orchestration flow definition.");
        var priorReissues = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT count(*)
              FROM orchestration_stage_results result
              JOIN orchestration_runs run ON run.id = result.run_id
             WHERE run.task_id = $task
               AND result.action = 'Reissue'
               AND run.status IN ('reissued', 'escalated');
            """, ct, transaction, ("$task", request.TaskId)) ?? 0L, CultureInfo.InvariantCulture);
        var frozenPayload = request.PayloadJson;
        var decisionPayload = System.Text.Json.Nodes.JsonNode.Parse(request.PayloadJson);
        if (decisionPayload is System.Text.Json.Nodes.JsonObject decisionObject
            && decisionObject["verdicts"] is System.Text.Json.Nodes.JsonArray)
        {
            var priorPayloads = await ReadPriorDecisionPayloadsAsync(connection, transaction, request.TaskId, string.Empty, ct);
            var priorReports = priorPayloads.AsEnumerable().Reverse().Select(payload =>
                JsonSerializer.Deserialize<StagePayload>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            decisionObject["maximumReissues"] = definition.MaxReissueAttempts;
            decisionObject["automaticRoundsUsed"] = priorReissues;
            decisionObject["concernRoundsUsed"] = StageDecisionPolicy.ConcernRoundsUsed(priorReports);
            decisionObject["deliveredRounds"] = JsonSerializer.SerializeToNode(
                StageDecisionPolicy.DeliveredHistory(priorReports), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            frozenPayload = decisionObject.ToJsonString();
        }
        var now = UtcNow;
        var runId = $"orch_{Guid.NewGuid():N}";
        await ExecuteAsync(connection, """
            INSERT INTO orchestration_runs(
                id, project_id, task_id, task_version, definition_version, stages_json,
                max_reissue_attempts, status, current_stage, payload_json, request_payload_json,
                idempotency_key, reissue_attempts, created_at, updated_at)
            VALUES (
                $id, $project, $task, $task_version, $definition_version, $stages,
                $max_reissues, 'pending', $stage, $payload, $request_payload,
                $key, $reissues, $now, $now);
            """, ct, transaction,
            ("$id", runId),
            ("$project", projectId),
            ("$task", request.TaskId),
            ("$task_version", taskVersion),
            ("$definition_version", definition.Version),
            ("$stages", JsonSerializer.Serialize(definition.Stages)),
            ("$max_reissues", definition.MaxReissueAttempts),
            ("$stage", definition.Stages[0].ToString()),
            ("$payload", frozenPayload),
            ("$request_payload", request.PayloadJson),
            ("$key", request.IdempotencyKey),
            ("$reissues", priorReissues),
            ("$now", Iso(now)));
        await AuditAsync(
            connection,
            transaction,
            actorId,
            "orchestration.run-created",
            "orchestration-run",
            runId,
            JsonSerializer.Serialize(new
            {
                projectId,
                request.TaskId,
                definition.Version,
                taskVersion,
                priorReissues,
            }),
            ct);
        return new OrchestrationRunDto(
            runId,
            projectId,
            request.TaskId,
            definition.Version,
            "pending",
            definition.Stages[0],
            request.PayloadJson,
            priorReissues,
            now,
            now,
            null,
            [],
            taskVersion);
    }

    public async Task<OrchestrationRunDto?> GetOrchestrationRunAsync(
        string runId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadOrchestrationRunAsync(connection, null, runId, ct);
    }

    public async Task<IReadOnlyList<OrchestrationRunDto>> ListOrchestrationRunsAsync(
        string? projectId,
        string? status,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = """
            SELECT id FROM orchestration_runs
             WHERE ($project IS NULL OR project_id = $project)
               AND ($status IS NULL OR status = $status)
             ORDER BY created_at, id;
            """;
        await using var command = Command(
            connection,
            sql,
            ("$project", string.IsNullOrWhiteSpace(projectId) ? null : projectId),
            ("$status", string.IsNullOrWhiteSpace(status) ? null : status));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var runs = new List<OrchestrationRunDto>(ids.Count);
        foreach (var id in ids)
        {
            var run = await ReadOrchestrationRunAsync(connection, null, id, ct);
            if (run is not null) runs.Add(run);
        }
        return runs;
    }

    public async Task<OrchestrationClaimResponse> ClaimOrchestrationAsync(
        OrchestrationClaimRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireAdmission();
        ValidateEngineIdentity(request.EngineId, request.InstanceId);
        if (request.SupportedStages.Count == 0)
            throw new ArgumentException("At least one supported orchestration stage is required.");
        OrchestrationClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = UtcNow;
            await ExpireOrchestrationLeasesAsync(connection, transaction, now, ct);
            var supported = request.SupportedStages.ToHashSet();
            string? runId = null;
            await using (var command = Command(connection, """
                SELECT id, current_stage
                  FROM orchestration_runs
                 WHERE status = 'pending'
                 ORDER BY updated_at, created_at, id;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (Enum.TryParse<OrchestrationStage>(reader.GetString(1), out var stage)
                        && supported.Contains(stage))
                    {
                        runId = reader.GetString(0);
                        break;
                    }
                }
            }

            if (runId is null)
            {
                response = new OrchestrationClaimResponse("empty");
                return;
            }

            var lastFence = Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT last_fence FROM orchestration_fence_counters WHERE run_id = $run;",
                ct,
                transaction,
                ("$run", runId)) ?? 0L,
                CultureInfo.InvariantCulture);
            var fence = checked(lastFence + 1);
            var ttl = NormalizeTtl(request.RequestedTtlSeconds);
            var leaseId = $"olease_{Guid.NewGuid():N}";
            var expires = now.AddSeconds(ttl);
            await ExecuteAsync(connection, """
                INSERT INTO orchestration_fence_counters(run_id, last_fence)
                VALUES ($run, $fence)
                ON CONFLICT(run_id) DO UPDATE SET last_fence = excluded.last_fence;
                INSERT INTO orchestration_leases(
                    run_id, lease_id, engine_id, instance_id, fence,
                    acquired_at, expires_at, status)
                VALUES ($run, $lease, $engine, $instance, $fence, $now, $expires, 'active')
                ON CONFLICT(run_id) DO UPDATE SET
                    lease_id = excluded.lease_id,
                    engine_id = excluded.engine_id,
                    instance_id = excluded.instance_id,
                    fence = excluded.fence,
                    acquired_at = excluded.acquired_at,
                    expires_at = excluded.expires_at,
                    status = excluded.status;
                UPDATE orchestration_runs
                   SET status = 'leased', updated_at = $now
                 WHERE id = $run AND status = 'pending';
                """, ct, transaction,
                ("$run", runId),
                ("$fence", fence),
                ("$lease", leaseId),
                ("$engine", request.EngineId),
                ("$instance", request.InstanceId),
                ("$now", Iso(now)),
                ("$expires", Iso(expires)));
            var run = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Claimed orchestration run disappeared.");
            var lease = new OrchestrationLeaseDto(
                leaseId,
                runId,
                request.EngineId,
                request.InstanceId,
                fence,
                now,
                expires,
                "active");
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.claimed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.InstanceId, fence, run.CurrentStage }),
                ct);
            response = new OrchestrationClaimResponse("claimed", run, lease);
        }, ct);
        return response!;
    }

    public async Task<OrchestrationLeaseDto> RenewOrchestrationLeaseAsync(
        string runId,
        OrchestrationLeaseRenewRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestrationLeaseDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var expires = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, """
                UPDATE orchestration_leases SET expires_at = $expires WHERE run_id = $run;
                """, ct, transaction, ("$expires", Iso(expires)), ("$run", runId));
            result = lease with { ExpiresAt = expires };
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.lease-renewed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.Fence }),
                ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestrationRunDto> CompleteOrchestrationStageAsync(
        string runId,
        CompleteOrchestrationStageRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.");
        ValidateJson(request.OutputJson, "outputJson");
        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadStageResultByKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.RunId, runId, StringComparison.Ordinal)
                    || replay.Value.Result.Stage != request.Stage
                    || replay.Value.Result.Action != request.Action
                    || !JsonEquivalent(replay.Value.Result.OutputJson, request.OutputJson))
                    throw new TaskServerConflictException(
                        "idempotency-conflict",
                        "The stage result idempotency key is already bound to different output.");
                result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                    ?? throw new KeyNotFoundException("Orchestration run was not found.");
                return;
            }

            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var run = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration run was not found.");
            if (run.Status != "leased" || run.CurrentStage != request.Stage)
                throw new TaskServerConflictException(
                    "orchestration-stage-mismatch",
                    $"Run is '{run.Status}' at {run.CurrentStage}, not leased at {request.Stage}.");

            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO orchestration_stage_results(
                    run_id, stage, action, output_json, idempotency_key, completed_at)
                VALUES ($run, $stage, $action, $output, $key, $completed);
                UPDATE orchestration_leases SET status = 'settled' WHERE run_id = $run;
                """, ct, transaction,
                ("$run", runId),
                ("$stage", request.Stage.ToString()),
                ("$action", request.Action.ToString()),
                ("$output", request.OutputJson),
                ("$key", request.IdempotencyKey),
                ("$completed", Iso(now)));

            var stages = await ReadRunStagesAsync(connection, transaction, runId, ct);
            string? currentTaskState = null;
            long currentTaskVersion = 0;
            await using (var command = Command(connection, """
                SELECT state, version FROM tasks WHERE id = $task;
                """, transaction, ("$task", run.TaskId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    currentTaskState = reader.GetString(0);
                    currentTaskVersion = reader.GetInt64(1);
                }
            }
            if (currentTaskState is null)
                throw new KeyNotFoundException("Orchestration task was not found.");

            var maxReissues = await ReadRunMaxReissuesAsync(connection, transaction, runId, ct);
            RepeatedReviewBlockDiagnosis? repeatedBlock = null;
            if (request.Action == OrchestrationAction.Reissue)
            {
                var priorDecisionPayloads = await ReadPriorDecisionPayloadsAsync(
                    connection,
                    transaction,
                    run.TaskId,
                    runId,
                    ct);
                repeatedBlock = RepeatedReviewBlockPolicy.Diagnose(
                    run.PayloadJson,
                    priorDecisionPayloads,
                    OrchestrationDefaults.MaxIdenticalAspectBlockRounds);
            }
            var stagePayload = JsonSerializer.Deserialize<StagePayload>(run.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // D14: the engine is the decider. Its typed decision carries the
            // findings and the D17 continuation base. A stage without one
            // (post-processing, gate, completion judge) applies the engine's
            // stage action under the decision the run already accepted. Only
            // an engine that sends no typed decision at all gets the same
            // library decision derived from the frozen payload.
            var engineTransition = ReadEngineTransition(request.OutputJson, run, request.Action, stagePayload);
            var acceptedAction = await ReadAcceptedTransitionAsync(connection, transaction, runId, ct);
            var transition = engineTransition ?? (acceptedAction is null && stagePayload?.Verdicts is not null
                ? TaskServerTransitionAdapter.Decide(StageDecisionPolicy.Facts(run.TaskId, run.ReissueAttempts, stagePayload))
                : null);
            var decider = engineTransition is not null ? "engine"
                : acceptedAction is not null ? "engine-stage-action" : "task-server-compatibility";
            acceptedAction ??= transition?.Action;
            var decision = OrchestrationSettlementPolicy.Decide(
                request.Action,
                stages,
                run.CurrentStage,
                run.ReissueAttempts,
                maxReissues,
                currentTaskState,
                run.TaskVersion,
                currentTaskVersion,
                repeatedBlock, transition, acceptedAction,
                await IsReviewedPublicationAsync(connection, transaction, run.ProjectId, ct));
            if (decision.Transition?.RequiresLinkedFollowUp == true)
                await ApplyDecisionFollowUpAsync(connection, transaction, run, decision.Transition, actorId, now, ct);
            if (decision.Transition?.ContinuationBase is { } continuationBase
                && stagePayload?.RunAttemptId is { } sourceRunId)
                await ExecuteAsync(connection, """
                    INSERT INTO task_starting_points(task_id, source_run_id, result_ref, result_sha, reason)
                    VALUES ($task, $run, $ref, $sha, $reason)
                    ON CONFLICT(task_id) DO UPDATE SET source_run_id = excluded.source_run_id,
                        result_ref = excluded.result_ref, result_sha = excluded.result_sha,
                        reason = excluded.reason;
                    """, ct, transaction, ("$task", run.TaskId), ("$run", sourceRunId),
                    ("$ref", continuationBase.ResultRef), ("$sha", continuationBase.ResultSha),
                    ("$reason", ContinuationBaseReasons.ReviewFixRound));
            if (decision.TaskState == AgentStudio.Shared.TaskStates.Ready
                && decision.Transition is { Action: TransitionAction.FindingRound or TransitionAction.ConcernRound })
                await QueueFixRoundPromptAsync(connection, transaction, run, decision.Transition,
                    currentTaskVersion, actorId, now, ct);
            if (decision.RunStatus == OrchestrationSettlementPolicy.IntegratingStatus)
                await QueueIntegrationAsync(connection, transaction, run, stagePayload!, actorId, now, ct);
            var completedAt = decision.IsTerminal ? now : (DateTime?)null;

            await ExecuteAsync(connection, """
                UPDATE orchestration_runs
                   SET status = $status,
                       current_stage = $stage,
                       reissue_attempts = $reissues,
                       updated_at = $updated,
                       completed_at = $completed
                 WHERE id = $run;
                """, ct, transaction,
                ("$status", decision.RunStatus),
                ("$stage", decision.NextStage.ToString()),
                ("$reissues", decision.ReissueAttempts),
                ("$updated", Iso(now)),
                ("$completed", completedAt is null ? null : Iso(completedAt.Value)),
                ("$run", runId));
            if (decision.TaskState is not null)
            {
                var changed = await ExecuteAsync(connection, """
                    UPDATE tasks
                       SET state = $state, version = version + 1, updated_at = $updated
                     WHERE id = $task AND state = '4-auto-review' AND version = $task_version;
                    """, ct, transaction,
                    ("$state", decision.TaskState),
                    ("$updated", Iso(now)),
                    ("$task_version", currentTaskVersion),
                    ("$task", run.TaskId));
                if (changed != 1)
                {
                    throw new TaskServerConflictException(
                        "task-version-mismatch",
                        "The task changed while the orchestration settlement transaction was applying its decision.");
                }

                var sourceRun = await ReadPayloadRunAuthorityAsync(
                    connection, transaction, run, ct);
                if (sourceRun is not null)
                {
                    await AppendLifecycleEventAsync(
                        connection,
                        transaction,
                        sourceRun.Value.RunAttemptId,
                        run.TaskId,
                        sourceRun.Value.Fence,
                        LifecycleEventKinds.ReviewCompleted,
                        new
                        {
                            authority = "task-server",
                            orchestrationRunId = runId,
                            stage = request.Stage.ToString(),
                            action = request.Action.ToString(),
                            nextState = decision.TaskState,
                            reason = decision.SettlementReason,
                        },
                        ct);
                    await AppendLifecycleEventAsync(
                        connection,
                        transaction,
                        sourceRun.Value.RunAttemptId,
                        run.TaskId,
                        sourceRun.Value.Fence,
                        decision.TaskState == "2-ready"
                            ? LifecycleEventKinds.Reissued
                            : LifecycleEventKinds.TerminalHandoff,
                        new
                        {
                            authority = "task-server",
                            orchestrationRunId = runId,
                            decision = decision.RunStatus,
                            reissueAttempts = decision.ReissueAttempts,
                            nextState = decision.TaskState,
                            reason = decision.SettlementReason,
                        },
                        ct);
                }
            }
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.stage-completed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new
                {
                    request.Stage,
                    request.Action,
                    request.Fence,
                    nextStatus = decision.RunStatus,
                    nextStage = decision.NextStage,
                    previousTaskState = currentTaskState,
                    expectedTaskVersion = run.TaskVersion,
                    previousTaskVersion = currentTaskVersion,
                    nextTaskState = decision.TaskState,
                    reissueAttempts = decision.ReissueAttempts,
                    decision.SupersededReason,
                    decision.SettlementReason,
                    decision.Transition,
                    decider,
                }),
                ct);
            result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Settled orchestration run disappeared.");
        }, ct);
        return result!;
    }

    public async Task<OrchestrationRunDto> ReleaseOrchestrationLeaseAsync(
        string runId,
        ReleaseOrchestrationLeaseRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE orchestration_leases SET status = 'released' WHERE run_id = $run;
                UPDATE orchestration_runs SET status = 'pending', updated_at = $updated WHERE id = $run;
                """, ct, transaction, ("$run", runId), ("$updated", Iso(now)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.lease-released",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.Fence, request.Reason }),
                ct);
            result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Released orchestration run disappeared.");
        }, ct);
        return result!;
    }

    private static void ValidateFlowDefinition(UpsertFlowDefinitionRequest request)
    {
        if (request.Stages.Count == 0)
            throw new ArgumentException("A flow definition requires at least one stage.");
        if (request.Stages.Distinct().Count() != request.Stages.Count)
            throw new ArgumentException("A flow definition cannot contain duplicate stages.");
        if (request.MaxReissueAttempts is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(request), "Max reissue attempts must be between 0 and 20.");
    }

    private static void ValidateEngineIdentity(string engineId, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(engineId) || string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Engine and instance ids are required.");
    }

    private void EnsureOrchestrationLeaseActive(OrchestrationLeaseDto lease)
    {
        if (lease.Status != "active")
            throw new TaskServerConflictException("lease-not-active", $"Orchestration lease status is '{lease.Status}'.");
        if (lease.ExpiresAt <= UtcNow)
            throw new TaskServerConflictException("lease-expired", "Orchestration lease expired and may be reclaimed.");
    }

    private static void ValidateOrchestrationLease(
        OrchestrationLeaseDto lease,
        string engineId,
        string instanceId,
        string leaseId,
        long fence)
    {
        if (!string.Equals(lease.EngineId, engineId, StringComparison.Ordinal)
            || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || lease.Fence != fence)
            throw new TaskServerConflictException(
                "stale-fence",
                "Orchestration lease id, engine instance, or fence does not match current authority.");
    }

    private static void ValidateJson(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.");
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} must contain valid JSON.", exception);
        }
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftJson = JsonDocument.Parse(left);
        using var rightJson = JsonDocument.Parse(right);
        return JsonSerializer.Serialize(leftJson.RootElement)
            == JsonSerializer.Serialize(rightJson.RootElement);
    }

    private static FlowDefinitionDto ReadFlowDefinition(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetInt64(1),
            JsonSerializer.Deserialize<List<OrchestrationStage>>(reader.GetString(2)) ?? [],
            reader.GetInt32(3),
            Parse(reader.GetString(4)));

    private static async Task<FlowDefinitionDto?> ReadFlowDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT project_id, version, stages_json, max_reissue_attempts, updated_at
              FROM flow_definitions WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFlowDefinition(reader) : null;
    }

    private static async Task<OrchestrationRunDto?> ReadOrchestrationRunByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken ct)
    {
        var runId = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT id FROM orchestration_runs WHERE idempotency_key = $key;",
            ct,
            transaction,
            ("$key", idempotencyKey)),
            CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(runId)
            ? null
            : await ReadOrchestrationRunAsync(connection, transaction, runId, ct);
    }

    private static async Task<OrchestrationRunDto?> ReadOrchestrationRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, task_id, definition_version, status, current_stage,
                   payload_json, reissue_attempts, created_at, updated_at, completed_at,
                   task_version
              FROM orchestration_runs WHERE id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var run = new OrchestrationRunDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            Enum.Parse<OrchestrationStage>(reader.GetString(5)),
            reader.GetString(6),
            reader.GetInt32(7),
            Parse(reader.GetString(8)),
            Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            null,
            reader.GetInt64(11));
        await reader.DisposeAsync();

        await using var resultsCommand = Command(connection, """
            SELECT sequence, stage, action, output_json, completed_at
              FROM orchestration_stage_results
             WHERE run_id = $run
             ORDER BY sequence;
            """, transaction, ("$run", runId));
        await using var resultsReader = await resultsCommand.ExecuteReaderAsync(ct);
        var results = new List<OrchestrationStageResultDto>();
        while (await resultsReader.ReadAsync(ct))
        {
            results.Add(new OrchestrationStageResultDto(
                resultsReader.GetInt64(0),
                Enum.Parse<OrchestrationStage>(resultsReader.GetString(1)),
                Enum.Parse<OrchestrationAction>(resultsReader.GetString(2)),
                resultsReader.GetString(3),
                Parse(resultsReader.GetString(4))));
        }
        return run with { StageResults = results };
    }

    private static async Task<OrchestrationLeaseDto?> ReadOrchestrationLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT lease_id, run_id, engine_id, instance_id, fence,
                   acquired_at, expires_at, status
              FROM orchestration_leases WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new OrchestrationLeaseDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                Parse(reader.GetString(5)),
                Parse(reader.GetString(6)),
                reader.GetString(7))
            : null;
    }

    private async Task ExpireOrchestrationLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE orchestration_runs
               SET status = 'pending', updated_at = $now
             WHERE status = 'leased'
               AND id IN (
                   SELECT run_id FROM orchestration_leases
                    WHERE status = 'active' AND expires_at <= $now
               );
            UPDATE orchestration_leases
               SET status = 'expired'
             WHERE status = 'active' AND expires_at <= $now;
            """, ct, transaction, ("$now", Iso(now)));

    private static async Task<List<OrchestrationStage>> ReadRunStagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT stages_json FROM orchestration_runs WHERE id = $run;",
            ct,
            transaction,
            ("$run", runId)),
            CultureInfo.InvariantCulture);
        return JsonSerializer.Deserialize<List<OrchestrationStage>>(json ?? "[]") ?? [];
    }

    private static async Task<int> ReadRunMaxReissuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
        => Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT max_reissue_attempts FROM orchestration_runs WHERE id = $run;",
            ct,
            transaction,
            ("$run", runId)) ?? 0,
            CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<string>> ReadPriorDecisionPayloadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string currentRunId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run.payload_json
              FROM orchestration_runs run
             WHERE run.task_id = $task
               AND run.id <> $current_run
               AND EXISTS (
                   SELECT 1
                     FROM orchestration_stage_results result
                    WHERE result.run_id = run.id
                      AND result.stage = 'ReviewDecision'
               )
             ORDER BY run.updated_at DESC, run.id DESC;
            """, transaction,
            ("$task", taskId),
            ("$current_run", currentRunId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var payloads = new List<string>();
        while (await reader.ReadAsync(ct)) payloads.Add(reader.GetString(0));
        return payloads;
    }

    private static async Task<(string RunAttemptId, long Fence)?> ReadPayloadRunAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(run.PayloadJson);
        if (!payload.RootElement.TryGetProperty("runAttemptId", out var runAttemptIdElement)
            || runAttemptIdElement.ValueKind != JsonValueKind.String)
        {
            // Explicitly created compatibility flows can still settle, but do
            // not mint lifecycle facts against an inferred coding attempt.
            return null;
        }

        var runAttemptId = runAttemptIdElement.GetString();
        var reviewSubjectId = ReadRequiredPayloadIdentity(payload.RootElement, "reviewSubjectId");
        var reviewAttemptId = ReadRequiredPayloadIdentity(payload.RootElement, "reviewAttemptId");
        var resultSha = ReadRequiredPayloadIdentity(payload.RootElement, "resultSha");
        var reviewPolicyHash = ReadRequiredPayloadIdentity(payload.RootElement, "reviewPolicyHash");
        var reportSha256 = ReadRequiredPayloadIdentity(payload.RootElement, "reviewReportSha256");
        if (string.IsNullOrWhiteSpace(runAttemptId))
            throw new TaskServerConflictException(
                "orchestration-envelope-incomplete",
                "The post-processing payload has an empty coding RunAttempt identity.");

        await using var command = Command(connection, """
            SELECT run.id, coalesce(run.fence, 0)
              FROM runs run
              JOIN review_subjects subject
                ON subject.source_run_id = run.id
               AND subject.task_id = run.task_id
              JOIN review_attempts attempt
                ON attempt.subject_id = subject.id
               AND attempt.task_id = run.task_id
             WHERE run.id = $run
               AND run.task_id = $task
               AND lower(run.result_sha) = lower($result_sha)
               AND subject.id = $subject
               AND subject.review_policy_hash = $policy
               AND attempt.id = $attempt
               AND attempt.report_sha256 = $report_sha;
            """, transaction,
            ("$run", runAttemptId),
            ("$task", run.TaskId),
            ("$result_sha", resultSha),
            ("$subject", reviewSubjectId),
            ("$policy", reviewPolicyHash),
            ("$attempt", reviewAttemptId),
            ("$report_sha", reportSha256));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new TaskServerConflictException(
                "orchestration-envelope-stale",
                "The post-processing payload no longer matches its fenced RunAttempt, ReviewSubject, review report, and Result-SHA.");
        }

        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static string ReadRequiredPayloadIdentity(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new TaskServerConflictException(
            "orchestration-envelope-incomplete",
            $"The post-processing payload is missing '{propertyName}'.");
    }

    private static async Task<(string RunId, OrchestrationStageResultDto Result)?> ReadStageResultByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, sequence, stage, action, output_json, completed_at
              FROM orchestration_stage_results
             WHERE idempotency_key = $key;
            """, transaction, ("$key", idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (
            reader.GetString(0),
            new OrchestrationStageResultDto(
                reader.GetInt64(1),
                Enum.Parse<OrchestrationStage>(reader.GetString(2)),
                Enum.Parse<OrchestrationAction>(reader.GetString(3)),
                reader.GetString(4),
                Parse(reader.GetString(5))));
    }
    private async Task ApplyDecisionFollowUpAsync(
        SqliteConnection connection, SqliteTransaction transaction, OrchestrationRunDto run,
        TransitionDecision decision, string actorId, DateTime now, CancellationToken ct)
    {
        var state = decision.LinkedFollowUpLane
            ?? throw new InvalidOperationException("The linked follow-up decision has no lane.");
        var (prefix, next) = await ReadProjectCounterAsync(connection, transaction, run.ProjectId, ct);
        var followUpId = "follow-up:" + run.RunId;
        var followUpKey = $"{prefix}-{next}";
        var findings = string.Join("\n", decision.Findings.Select(item =>
            $"- {item.Aspect}: {item.Finding ?? item.Summary}"));
        await ExecuteAsync(connection, """
            INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at)
            VALUES ($id, $project, $key, $title, $body, $state, 1, $now, $now);
            """, ct, transaction, ("$id", followUpId), ("$project", run.ProjectId),
            ("$key", followUpKey), ("$title", $"Review follow-up for {run.TaskId}"),
            ("$body", $"Follow-up for /tasks/{run.TaskId} from review {decision.Receipt.AttemptId}.\n\n{findings}"),
            ("$state", state),
            ("$now", Iso(now)));
        await ExecuteAsync(connection,
            "UPDATE projects SET next_task_number = MAX(next_task_number, $next), updated_at = $now WHERE id = $id;",
            ct, transaction, ("$next", next + 1), ("$now", Iso(now)), ("$id", run.ProjectId));
        await ExecuteAsync(connection, "UPDATE tasks SET body = body || $link WHERE id = $id;",
            ct, transaction, ("$id", run.TaskId), ("$link", $"\n\nReview budget follow-up: [{followUpKey}](/tasks/{followUpId})."));
        await AuditAsync(connection, transaction, actorId, "task.created", "task", followUpId,
            JsonSerializer.Serialize(new { run.ProjectId, followUpKey, sourceTaskId = run.TaskId, decision.Receipt }), ct);
    }
}
