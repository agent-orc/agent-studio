using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private const long HostPolicyVersion = 1;
    private static readonly TimeSpan PermitLifetime = TimeSpan.FromMinutes(5);

    public async Task<HostReportResponse> AcceptHostReportAsync(
        string runnerId,
        HostReportRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateHostContract(request.SchemaVersion);
        if (request.Sequence <= 0)
            throw new TaskServerConflictException("host-report-sequence-invalid", "Host report sequence must be positive.");
        ValidateCapacity(request.Capacity);
        HostReportResponse? response = null;
        var reportJson = JsonSerializer.Serialize(request);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reportJson))).ToLowerInvariant();

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, runnerId, request.InstanceId, ct);
            await ValidateHostContractDeclarationAsync(connection, transaction, runnerId, request.HostId, ct);

            long? acceptedSequence = null;
            string? acceptedDigest = null;
            await using (var command = Command(connection, """
                SELECT sequence, payload_sha256 FROM host_reports WHERE runner_id = $runner;
                """, transaction, ("$runner", runnerId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    acceptedSequence = reader.GetInt64(0);
                    acceptedDigest = reader.GetString(1);
                }
            }

            if (acceptedSequence == request.Sequence
                && !string.Equals(acceptedDigest, digest, StringComparison.Ordinal))
            {
                throw new TaskServerConflictException(
                    "host-report-sequence-conflict",
                    $"Host report sequence {request.Sequence} was already accepted with a different payload.");
            }

            var status = "accepted";
            if (acceptedSequence is not null && request.Sequence < acceptedSequence)
            {
                status = "stale";
            }
            else if (acceptedSequence == request.Sequence)
            {
                status = "replayed";
            }
            else
            {
                var receivedAt = UtcNow;
                await ExecuteAsync(connection, """
                    INSERT INTO host_reports(
                        runner_id, instance_id, sequence, payload_sha256,
                        observed_at, received_at, report_json)
                    VALUES ($runner, $instance, $sequence, $digest, $observed, $received, $json)
                    ON CONFLICT(runner_id) DO UPDATE SET
                        instance_id = excluded.instance_id,
                        sequence = excluded.sequence,
                        payload_sha256 = excluded.payload_sha256,
                        observed_at = excluded.observed_at,
                        received_at = excluded.received_at,
                        report_json = excluded.report_json;
                    UPDATE runners SET last_seen_at = $received WHERE id = $runner;
                    """, ct, transaction,
                    ("$runner", runnerId),
                    ("$instance", request.InstanceId),
                    ("$sequence", request.Sequence),
                    ("$digest", digest),
                    ("$observed", Iso(request.ObservedAt)),
                    ("$received", Iso(receivedAt)),
                    ("$json", reportJson));
                acceptedSequence = request.Sequence;
                await AuditAsync(connection, transaction, actorId, "host.report.accepted", "runner", runnerId,
                    JsonSerializer.Serialize(new
                    {
                        request.Sequence,
                        request.ObservedAt,
                        request.Capacity,
                        work = request.Work.Count,
                        postProcessing = request.PostProcessing.Count,
                        faults = request.Faults.Count,
                    }), ct);
            }

            if (_mode == TaskServerMode.Normal)
                await RefreshAvailablePermitsAsync(connection, transaction, ct);
            var permits = _mode == TaskServerMode.Normal
                ? await ReadAvailablePermitsAsync(connection, transaction, request.HostId, ct)
                : [];
            response = new HostReportResponse(
                status,
                acceptedSequence ?? request.Sequence,
                SupportedHostContract(),
                HostPolicyVersion,
                _mode == TaskServerMode.Normal ? "active" : _mode.ToString().ToLowerInvariant(),
                permits,
                []);
        }, ct);

        return response!;
    }

    public async Task<IReadOnlyList<HostProjectionDto>> ListHostProjectionsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT r.id, r.host_id, h.instance_id, h.sequence, h.observed_at, h.received_at, h.report_json
              FROM host_reports h
              JOIN runners r ON r.id = h.runner_id
             ORDER BY r.id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<HostProjectionDto>();
        while (await reader.ReadAsync(ct))
        {
            var report = JsonSerializer.Deserialize<HostReportRequest>(reader.GetString(6))
                         ?? throw new InvalidOperationException("Stored host report is unreadable.");
            result.Add(new HostProjectionDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                Parse(reader.GetString(4)),
                Parse(reader.GetString(5)),
                report.Capacity,
                report.Capabilities,
                report.Work,
                report.PostProcessing,
                report.Faults));
        }
        return result;
    }

    public async Task<WorkPermitAcceptanceDto> AcceptWorkPermitAsync(
        string permitId,
        WorkPermitAcceptRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireAdmission();
        ValidateHostContract(request.SchemaVersion);
        WorkPermitAcceptanceDto? response = null;

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await ValidateRunnerHostAsync(connection, transaction, request.RunnerId, request.HostId, ct);
            await ValidateAcceptedReportSequenceAsync(
                connection, transaction, request.RunnerId, request.InstanceId, request.ReportSequence, ct);
            if (request.PolicyVersion != HostPolicyVersion)
                throw new TaskServerConflictException("host-policy-stale", "The host policy version is stale.");

            string taskId;
            string status;
            string? acceptedRunner;
            string? acceptedInstance;
            string? acceptedKey;
            string? runId;
            string taskState;
            string projectId;
            DateTime expiresAt;
            await using (var command = Command(connection, """
                SELECT p.task_id, p.status, p.accepted_runner_id, p.accepted_instance_id,
                       p.accept_idempotency_key, p.run_id, p.expires_at, t.state,
                       t.project_id
                  FROM work_permits p
                  JOIN tasks t ON t.id = p.task_id
                 WHERE p.id = $permit;
                """, transaction, ("$permit", permitId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Work permit was not found.");
                taskId = reader.GetString(0);
                status = reader.GetString(1);
                acceptedRunner = reader.IsDBNull(2) ? null : reader.GetString(2);
                acceptedInstance = reader.IsDBNull(3) ? null : reader.GetString(3);
                acceptedKey = reader.IsDBNull(4) ? null : reader.GetString(4);
                runId = reader.IsDBNull(5) ? null : reader.GetString(5);
                expiresAt = Parse(reader.GetString(6));
                taskState = reader.GetString(7);
                projectId = reader.GetString(8);
            }

            if (status == "accepted")
            {
                if (acceptedRunner == request.RunnerId
                    && acceptedInstance == request.InstanceId
                    && acceptedKey == request.IdempotencyKey
                    && runId is not null)
                {
                    response = await ReadPermitAcceptanceAsync(
                        connection, transaction, permitId, runId, "replayed", ct);
                    return;
                }
                throw new TaskServerConflictException(
                    "work-permit-already-accepted",
                    $"Permit '{permitId}' was already accepted by another host or idempotency key.");
            }
            var hostProjectPolicy = await ReadHostProjectPolicyAsync(
                connection,
                transaction,
                request.HostId,
                ct);
            if (hostProjectPolicy is
                {
                    AllowAllProjects: false,
                }
                && !hostProjectPolicy.AllowedProjectIds.Contains(
                    projectId,
                    StringComparer.Ordinal))
            {
                throw new TaskServerConflictException(
                    "work-permit-project-not-allowed",
                    $"Host '{request.HostId}' is not allowed to claim project '{projectId}'.");
            }
            if (expiresAt <= UtcNow)
                throw new TaskServerConflictException("work-permit-expired", $"Permit '{permitId}' has expired.");
            if (!string.Equals(taskState, "2-ready", StringComparison.Ordinal))
            {
                throw new TaskServerConflictException(
                    "work-permit-task-not-ready",
                    $"Permit '{permitId}' refers to task state '{taskState}', not '2-ready'.");
            }

            var fence = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction, ("$task", taskId))
                ?? 0L, CultureInfo.InvariantCulture) + 1;
            var createdRunId = $"run_{Guid.NewGuid():N}";
            var leaseId = $"lse_{Guid.NewGuid():N}";
            var now = UtcNow;
            var authorityDeadline = now.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            var containmentStepExecutionId = $"pst_01_{Guid.NewGuid():N}";
            var resultStepExecutionId = $"pst_02_{Guid.NewGuid():N}";
            await ExecuteAsync(connection, """
                INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, $fence)
                ON CONFLICT(task_id) DO UPDATE SET last_fence = excluded.last_fence;
                INSERT INTO runs(id, task_id, status, runner_id, fence, created_at, started_at)
                VALUES ($run, $task, 'running', $runner, $fence, $now, $now);
                INSERT INTO leases(task_id, lease_id, run_id, runner_id, instance_id, fence, acquired_at, expires_at, status)
                VALUES ($task, $lease, $run, $runner, $instance, $fence, $now, $deadline, 'active');
                UPDATE tasks SET state = '3-progress', version = version + 1, updated_at = $now
                 WHERE id = $task AND state = '2-ready';
                UPDATE work_permits
                   SET status = 'accepted',
                       accepted_runner_id = $runner,
                       accepted_instance_id = $instance,
                       accepted_at = $now,
                       accept_idempotency_key = $key,
                       run_id = $run
                 WHERE id = $permit AND status = 'available';
                INSERT INTO post_step_executions(id, run_id, step_id, eligible_runner_id, status)
                VALUES ($containment_step, $run, $containment_step_id, $runner, 'available');
                INSERT INTO post_step_executions(id, run_id, step_id, eligible_runner_id, status)
                VALUES ($result_step, $run, $result_step_id, $runner, 'available');
                """, ct, transaction,
                ("$task", taskId),
                ("$fence", fence),
                ("$run", createdRunId),
                ("$runner", request.RunnerId),
                ("$instance", request.InstanceId),
                ("$lease", leaseId),
                ("$now", Iso(now)),
                ("$deadline", Iso(authorityDeadline)),
                ("$key", request.IdempotencyKey),
                ("$permit", permitId),
                ("$containment_step", containmentStepExecutionId),
                ("$containment_step_id", HostPostStepIds.WorktreeContainment),
                ("$result_step", resultStepExecutionId),
                ("$result_step_id", HostPostStepIds.ResultFinalization));
            await AuditAsync(connection, transaction, actorId, "work.permit.accepted", "run", createdRunId,
                JsonSerializer.Serialize(new
                {
                    permitId,
                    runnerId = request.RunnerId,
                    request.InstanceId,
                    request.ReportSequence,
                    fence,
                    projectId,
                    hostProjectPolicyVersion = hostProjectPolicy?.Version,
                }), ct);
            response = await ReadPermitAcceptanceAsync(
                connection, transaction, permitId, createdRunId, "accepted", ct);
        }, ct);

        return response!;
    }

    public async Task<RunReconcileResponse> ReconcileRunAsync(
        string runId,
        RunReconcileRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateHostContract(request.SchemaVersion);
        RunReconcileResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await ValidateRunnerHostAsync(connection, transaction, request.RunnerId, request.HostId, ct);
            await ValidateAcceptedReportSequenceAsync(
                connection, transaction, request.RunnerId, request.InstanceId, request.ReportSequence, ct);
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                        ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(
                lease,
                request.RunnerId,
                request.LeaseInstanceId ?? request.InstanceId,
                request.LeaseId,
                request.Fence);
            if (lease.ExpiresAt <= UtcNow)
            {
                throw new TaskServerConflictException(
                    "offline-authority-expired",
                    "The persisted offline authority deadline passed before reconciliation.");
            }
            if (lease.Status is not ("active" or "process-unknown"))
                throw new TaskServerConflictException("lease-not-reconcilable", $"Lease status is '{lease.Status}'.");

            var expiresAt = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'active', expires_at = $expires WHERE run_id = $run;
                UPDATE runs SET status = 'running' WHERE id = $run;
                """, ct, transaction, ("$expires", Iso(expiresAt)), ("$run", runId));
            var reconciled = lease with { Status = "active", ExpiresAt = expiresAt };
            await AuditAsync(connection, transaction, actorId, "run.reconciled", "run", runId,
                JsonSerializer.Serialize(new { request.RunnerId, request.InstanceId, request.Fence, request.ReportSequence }), ct);
            response = new RunReconcileResponse("reconciled", reconciled, request.ReportSequence);
        }, ct);
        return response!;
    }

    public async Task<PostStepClaimResponse> ClaimPostStepAsync(
        string runId,
        string stepExecutionId,
        PostStepClaimRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateHostContract(request.SchemaVersion);
        PostStepClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await ValidateRunnerHostAsync(connection, transaction, request.RunnerId, request.HostId, ct);
            await ValidateAcceptedReportSequenceAsync(
                connection, transaction, request.RunnerId, request.InstanceId, request.ReportSequence, ct);
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                        ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(
                lease,
                request.RunnerId,
                request.LeaseInstanceId ?? request.InstanceId,
                request.LeaseId,
                request.RunFence);
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);

            var step = await ReadPostStepAsync(connection, transaction, runId, stepExecutionId, ct);
            if (!string.Equals(step.EligibleRunnerId, request.RunnerId, StringComparison.Ordinal))
                throw new TaskServerConflictException("post-step-host-mismatch", "The post-step is bound to another host.");
            if (step.Status == "completed")
            {
                var replayKey = Convert.ToString(await ScalarAsync(connection, """
                    SELECT claim_idempotency_key FROM post_step_executions WHERE id = $step;
                    """, ct, transaction, ("$step", stepExecutionId)), CultureInfo.InvariantCulture);
                if (replayKey == request.IdempotencyKey)
                {
                    response = new PostStepClaimResponse("completed", step, request.RunFence);
                    return;
                }
                throw new TaskServerConflictException("post-step-already-completed", "The post-step is already complete.");
            }
            if (step.Status == "running")
            {
                var replayKey = Convert.ToString(await ScalarAsync(connection, """
                    SELECT claim_idempotency_key FROM post_step_executions WHERE id = $step;
                    """, ct, transaction, ("$step", stepExecutionId)), CultureInfo.InvariantCulture);
                if (replayKey == request.IdempotencyKey)
                {
                    response = new PostStepClaimResponse("replayed", step, request.RunFence);
                    return;
                }
                throw new TaskServerConflictException("post-step-already-claimed", "The post-step already has a claimant.");
            }
            if (step.Status != "available")
                throw new TaskServerConflictException("post-step-not-claimable", $"Post-step status is '{step.Status}'.");

            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE post_step_executions
                   SET status = 'running',
                       claim_fence = $fence,
                       claimed_instance_id = $instance,
                       started_at = $now,
                       claim_idempotency_key = $key
                 WHERE id = $step AND status = 'available';
                """, ct, transaction,
                ("$fence", request.RunFence),
                ("$instance", request.InstanceId),
                ("$now", Iso(now)),
                ("$key", request.IdempotencyKey),
                ("$step", stepExecutionId));
            var claimed = step with { Status = "running" };
            await AuditAsync(connection, transaction, actorId, "post-step.claimed", "post-step", stepExecutionId,
                JsonSerializer.Serialize(new { runId, request.RunnerId, request.RunFence }), ct);
            response = new PostStepClaimResponse("claimed", claimed, request.RunFence);
        }, ct);
        return response!;
    }

    public async Task<PostStepCompleteResponse> CompletePostStepAsync(
        string runId,
        string stepExecutionId,
        PostStepCompleteRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateHostContract(request.SchemaVersion);
        PostStepCompleteResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await ValidateRunnerHostAsync(connection, transaction, request.RunnerId, request.HostId, ct);
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                        ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(
                lease,
                request.RunnerId,
                request.LeaseInstanceId ?? request.InstanceId,
                request.LeaseId,
                request.RunFence);
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
            var step = await ReadPostStepAsync(connection, transaction, runId, stepExecutionId, ct);

            if (step.Status == "completed")
            {
                var replayKey = Convert.ToString(await ScalarAsync(connection, """
                    SELECT complete_idempotency_key FROM post_step_executions WHERE id = $step;
                    """, ct, transaction, ("$step", stepExecutionId)), CultureInfo.InvariantCulture);
                if (replayKey == request.IdempotencyKey)
                {
                    var hashes = await ReadPostStepArtifactHashesAsync(connection, transaction, stepExecutionId, ct);
                    var outcome = Convert.ToString(await ScalarAsync(connection,
                        "SELECT outcome FROM post_step_executions WHERE id = $step;", ct, transaction, ("$step", stepExecutionId)),
                        CultureInfo.InvariantCulture) ?? string.Empty;
                    response = new PostStepCompleteResponse("replayed", step, outcome, hashes);
                    return;
                }
                throw new TaskServerConflictException("post-step-already-completed", "The post-step already has a different completion.");
            }
            if (step.Status != "running" || request.ClaimFence != request.RunFence)
                throw new TaskServerConflictException("post-step-stale-fence", "The post-step claim fence is stale.");

            var now = UtcNow;
            var hashesJson = JsonSerializer.Serialize(request.ArtifactHashes);
            await ExecuteAsync(connection, """
                UPDATE post_step_executions
                   SET status = 'completed',
                       finished_at = $now,
                       outcome = $outcome,
                       artifact_hashes_json = $hashes,
                       complete_idempotency_key = $key
                 WHERE id = $step AND status = 'running' AND claim_fence = $fence;
                """, ct, transaction,
                ("$now", Iso(now)),
                ("$outcome", request.Outcome),
                ("$hashes", hashesJson),
                ("$key", request.IdempotencyKey),
                ("$step", stepExecutionId),
                ("$fence", request.ClaimFence));
            var completed = step with { Status = "completed" };
            await AuditAsync(connection, transaction, actorId, "post-step.completed", "post-step", stepExecutionId,
                JsonSerializer.Serialize(new
                {
                    runId,
                    request.RunnerId,
                    request.Outcome,
                    request.ClaimFence,
                    artifacts = request.ArtifactHashes.Count,
                }), ct);
            response = new PostStepCompleteResponse("completed", completed, request.Outcome, request.ArtifactHashes);
        }, ct);
        return response!;
    }

    private async Task RefreshAvailablePermitsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var expiresAt = Iso(UtcNow.Add(PermitLifetime));
        await ExecuteAsync(connection, """
            UPDATE work_permits
               SET id = 'prm_' || lower(hex(randomblob(16))),
                   policy_version = $policy,
                   expires_at = $expires,
                   status = 'available',
                   accepted_runner_id = NULL,
                   accepted_instance_id = NULL,
                   accepted_at = NULL,
                   accept_idempotency_key = NULL,
                   run_id = NULL
             WHERE status IN ('completed', 'released', 'fenced')
               AND EXISTS (
                   SELECT 1 FROM tasks t
                    WHERE t.id = work_permits.task_id AND t.state = '2-ready')
               AND NOT EXISTS (
                   SELECT 1 FROM leases l
                    WHERE l.task_id = work_permits.task_id
                      AND l.status IN ('active', 'process-unknown'));
            INSERT INTO work_permits(id, task_id, policy_version, expires_at, status)
            SELECT 'prm_' || lower(hex(randomblob(16))), t.id, $policy, $expires, 'available'
              FROM tasks t
             WHERE t.state = '2-ready'
               AND NOT EXISTS (
                   SELECT 1 FROM leases l
                    WHERE l.task_id = t.id AND l.status IN ('active', 'process-unknown'))
               AND NOT EXISTS (
                   SELECT 1 FROM work_permits p
                    WHERE p.task_id = t.id);
            UPDATE work_permits
               SET expires_at = $expires
             WHERE status = 'available' AND expires_at <= $now;
            """, ct, transaction,
            ("$policy", HostPolicyVersion),
            ("$expires", expiresAt),
            ("$now", Iso(UtcNow)));
    }

    private async Task<IReadOnlyList<WorkPermitDto>> ReadAvailablePermitsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT p.id, p.policy_version, p.expires_at,
                   t.id, t.project_id, t.task_key, t.title, t.state,
                   t.version, t.created_at, t.updated_at, t.body
              FROM work_permits p
              JOIN tasks t ON t.id = p.task_id
             WHERE p.status = 'available' AND p.expires_at > $now AND t.state = '2-ready'
               AND (
                   NOT EXISTS (
                       SELECT 1 FROM host_project_policies policy
                        WHERE policy.host_id = $host)
                   OR EXISTS (
                       SELECT 1 FROM host_project_policies policy
                        WHERE policy.host_id = $host
                          AND policy.allow_all_projects = 1)
                   OR EXISTS (
                       SELECT 1 FROM host_allowed_projects allowed
                        WHERE allowed.host_id = $host
                          AND allowed.project_id = t.project_id))
             ORDER BY t.created_at, t.task_key;
            """, transaction, ("$now", Iso(UtcNow)), ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkPermitDto>();
        while (await reader.ReadAsync(ct))
        {
            var task = new TaskDto(
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8),
                Parse(reader.GetString(9)),
                Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11));
            result.Add(new WorkPermitDto(
                reader.GetString(0),
                task,
                reader.GetInt64(1),
                Parse(reader.GetString(2))));
        }
        return result;
    }

    private async Task<WorkPermitAcceptanceDto> ReadPermitAcceptanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string permitId,
        string runId,
        string status,
        CancellationToken ct)
    {
        var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                    ?? throw new InvalidOperationException("Accepted permit has no lease.");
        TaskDto task;
        RunDto run;
        await using (var command = Command(connection, """
            SELECT t.id, t.project_id, t.task_key, t.title, t.state, t.version, t.created_at, t.updated_at, t.body,
                   t.archive_state, t.archived_at,
                   r.status, r.runner_id, r.fence, r.created_at, r.started_at, r.finished_at
              FROM runs r JOIN tasks t ON t.id = r.task_id
             WHERE r.id = $run;
            """, transaction, ("$run", runId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Accepted permit has no run.");
            task = ReadTask(reader);
            run = new RunDto(
                runId,
                task.TaskId,
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                Parse(reader.GetString(14)),
                reader.IsDBNull(15) ? null : Parse(reader.GetString(15)),
                reader.IsDBNull(16) ? null : Parse(reader.GetString(16)));
        }

        var steps = await ReadPostStepsAsync(connection, transaction, runId, ct);
        return new WorkPermitAcceptanceDto(
            status,
            permitId,
            run,
            task,
            lease,
            lease.ExpiresAt,
            steps);
    }

    private static async Task<PostStepPlanDto> ReadPostStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepExecutionId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, run_id, step_id, eligible_runner_id, status
              FROM post_step_executions
             WHERE id = $step AND run_id = $run;
            """, transaction, ("$step", stepExecutionId), ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Post-step execution was not found.");
        return new PostStepPlanDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static async Task<IReadOnlyList<PostStepPlanDto>> ReadPostStepsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, run_id, step_id, eligible_runner_id, status
              FROM post_step_executions WHERE run_id = $run ORDER BY id;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PostStepPlanDto>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PostStepPlanDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadPostStepArtifactHashesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stepExecutionId,
        CancellationToken ct)
    {
        var raw = Convert.ToString(await ScalarAsync(connection, """
            SELECT artifact_hashes_json FROM post_step_executions WHERE id = $step;
            """, ct, transaction, ("$step", stepExecutionId)), CultureInfo.InvariantCulture);
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(raw ?? "[]") ?? [];
    }

    private static async Task ValidateAcceptedReportSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string instanceId,
        long sequence,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT instance_id, sequence FROM host_reports WHERE runner_id = $runner;
            """, transaction, ("$runner", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new TaskServerConflictException("host-report-required", "A host report must be accepted before this operation.");
        if (!string.Equals(reader.GetString(0), instanceId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-instance-stale", "The accepted report belongs to another host instance.");
        if (reader.GetInt64(1) != sequence)
            throw new TaskServerConflictException("host-report-stale", "The host report sequence is not current.");
    }

    private static async Task ValidateRunnerHostAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string hostId,
        CancellationToken ct)
    {
        var registeredHost = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT host_id FROM runners WHERE id = $runner;",
            ct,
            transaction,
            ("$runner", runnerId)), CultureInfo.InvariantCulture);
        if (!string.Equals(registeredHost, hostId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-host-mismatch", "Runner registration and request host ids differ.");
    }

    private static async Task ValidateHostContractDeclarationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT host_id, host_orchestrator_minimum, host_orchestrator_maximum
              FROM runners WHERE id = $runner;
            """, transaction, ("$runner", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Runner is not registered.");
        if (!string.Equals(reader.GetString(0), hostId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-host-mismatch", "Runner registration and report host ids differ.");
        var minimum = reader.IsDBNull(1) ? null : reader.GetString(1);
        var maximum = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (!HostOrchestratorContract.Overlaps(minimum, maximum))
            throw new HostOrchestratorContractException(minimum, maximum);
    }

    private static void ValidateCapacity(HostCapacityDto capacity)
    {
        if (capacity.Configured < 0
            || capacity.Effective < 0
            || capacity.Active < 0
            || capacity.Queued < 0
            || capacity.Free < 0
            || capacity.Effective > capacity.Configured
            || capacity.Active > capacity.Effective
            || capacity.Free != capacity.Effective - capacity.Active)
        {
            throw new TaskServerConflictException(
                "host-capacity-invalid",
                "Capacity must be non-negative with effective <= configured, active <= effective, and free = effective - active.");
        }
    }

    private static void ValidateHostContract(string? schemaVersion)
    {
        if (!HostOrchestratorContract.Supports(schemaVersion))
            throw new HostOrchestratorContractException(schemaVersion, schemaVersion);
    }

    private static HostContractRangeDto SupportedHostContract()
        => new(HostOrchestratorContract.MinimumSupported, HostOrchestratorContract.MaximumSupported);
}

public sealed class HostOrchestratorContractException(string? minimum, string? maximum)
    : Exception(
        $"Host orchestrator contract range '{minimum ?? "missing"}' to '{maximum ?? "missing"}' " +
        $"does not overlap supported range {HostOrchestratorContract.MinimumSupported} to {HostOrchestratorContract.MaximumSupported}.")
{
    public string? Minimum { get; } = minimum;
    public string? Maximum { get; } = maximum;
}
