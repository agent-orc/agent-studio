using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions GateJson = new(JsonSerializerDefaults.Web);

    internal async Task ApplyGateMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS gate_subjects(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                source_run_id TEXT NOT NULL REFERENCES runs(id),
                gate_id TEXT NOT NULL,
                plan_hash TEXT NOT NULL,
                policy_hash TEXT NOT NULL,
                subject_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                dispatch_deadline TEXT NOT NULL,
                max_attempts INTEGER NOT NULL,
                last_fence INTEGER NOT NULL DEFAULT 0,
                UNIQUE(source_run_id, gate_id, plan_hash)
            );
            CREATE TABLE IF NOT EXISTS gate_attempts(
                id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL REFERENCES gate_subjects(id),
                attempt_number INTEGER NOT NULL,
                state TEXT NOT NULL,
                executor_id TEXT,
                instance_id TEXT,
                host_id TEXT,
                lease_id TEXT,
                fence INTEGER NOT NULL DEFAULT 0,
                authority_epoch INTEGER NOT NULL DEFAULT 0,
                acquired_at TEXT,
                expires_at TEXT,
                resource_namespace TEXT,
                port_base INTEGER,
                failure_classification TEXT,
                outcome TEXT,
                report_json TEXT,
                report_digest TEXT,
                created_at TEXT NOT NULL,
                claimed_at TEXT,
                reported_at TEXT,
                cleaned_at TEXT,
                deadline TEXT,
                containment_deadline TEXT,
                UNIQUE(subject_id, attempt_number)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_gate_one_live_attempt
                ON gate_attempts(subject_id)
                WHERE state IN ('queued', 'claimed', 'materializing', 'running', 'reporting', 'cleaning');
            CREATE INDEX IF NOT EXISTS ix_gate_claim_queue ON gate_attempts(state, created_at);
            CREATE TABLE IF NOT EXISTS gate_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                attempt_id TEXT NOT NULL REFERENCES gate_attempts(id),
                event TEXT NOT NULL,
                at TEXT NOT NULL,
                detail TEXT
            );
            """, ct);
    }

    public async Task<GateStatus> CreateGateSubjectAsync(CreateGateSubjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ValidateGateSubject(request);
        GateStatus? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadGateSubjectByKeyAsync(connection, transaction,
                request.SourceRunId, request.Plan.GateId, request.PlanHash, ct);
            if (existing is not null)
            {
                if (!SameGateSubject(existing, request))
                    throw new TaskServerConflictException("gate-subject-conflict", "Gate subject replay differs from the immutable source.");
                result = await ReadGateStatusAsync(connection, transaction, existing.SubjectId, ct);
                return;
            }
            await using (var command = Command(connection, """
                SELECT task_id, result_sha, repository_id, repository_url, result_ref,
                       source_bundle_artifact_id, source_bundle_sha256
                  FROM runs WHERE id = $run;
                """, transaction, ("$run", request.SourceRunId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Gate source run was not found.");
                if (reader.GetString(0) != request.TaskId
                    || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                    || (reader.IsDBNull(2) ? null : reader.GetString(2)) != request.RepositoryId)
                    throw new TaskServerConflictException("gate-source-mismatch", "Gate source does not match the fenced run result.");
                ValidateOptionalSourceField(reader, 3, request.RepositoryUrl, "repository URL");
                ValidateOptionalSourceField(reader, 4, request.ResultRef, "result ref");
                ValidateOptionalSourceField(reader, 5, request.SourceBundleArtifactId, "source bundle artifact");
                ValidateOptionalSourceField(reader, 6, request.SourceBundleSha256, "source bundle digest");
            }
            var now = UtcNow;
            var subject = new GateSubject($"gsub_{Guid.NewGuid():N}", request.TaskId, request.SourceRunId,
                request.RepositoryId, request.RepositoryUrl, request.ExpectedSha.ToLowerInvariant(), request.ResultRef,
                request.SourceBundleArtifactId, request.SourceBundleSha256, request.PlanHash.ToLowerInvariant(),
                request.PolicyHash, request.PipelineDefinitionVersion, request.TestSelectionAuditDigest,
                request.Plan, now, request.DispatchDeadline.ToUniversalTime(), request.MaxAttempts);
            var attemptId = $"gat_{Guid.NewGuid():N}";
            await ExecuteAsync(connection, """
                INSERT INTO gate_subjects(id, task_id, source_run_id, gate_id, plan_hash, policy_hash,
                    subject_json, created_at, dispatch_deadline, max_attempts)
                VALUES ($id, $task, $run, $gate, $hash, $policy, $json, $now, $deadline, $max);
                INSERT INTO gate_attempts(id, subject_id, attempt_number, state, created_at)
                VALUES ($attempt, $id, 1, 'queued', $now);
                INSERT INTO gate_events(attempt_id, event, at) VALUES ($attempt, 'queued', $now);
                """, ct, transaction,
                ("$id", subject.SubjectId), ("$task", subject.TaskId), ("$run", subject.SourceRunId),
                ("$gate", subject.Plan.GateId), ("$hash", subject.PlanHash), ("$policy", subject.PolicyHash),
                ("$json", JsonSerializer.Serialize(subject, GateJson)), ("$now", Iso(now)),
                ("$deadline", Iso(subject.DispatchDeadline)), ("$max", subject.MaxAttempts),
                ("$attempt", attemptId));
            await AuditAsync(connection, transaction, actorId, "gate.subject-created", "gate-subject",
                subject.SubjectId, JsonSerializer.Serialize(new { subject.SourceRunId, subject.Plan.GateId, subject.PlanHash }), ct);
            result = await ReadGateStatusAsync(connection, transaction, subject.SubjectId, ct);
        }, ct);
        return result!;
    }

    public async Task<GateStatus?> GetGateStatusAsync(string subjectId, CancellationToken ct)
    {
        if (_mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance)
        {
            await using var connection = Open();
            await connection.OpenAsync(ct);
            return await ReadGateStatusAsync(connection, null, subjectId, ct);
        }
        GateStatus? status = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ReconcileGateTimeoutsAsync(connection, transaction, ct);
            status = await ReadGateStatusAsync(connection, transaction, subjectId, ct);
        }, ct);
        return status;
    }

    public async Task<GateStatus> CancelGateSubjectAsync(string subjectId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        GateStatus? status = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            status = await ReadGateStatusAsync(connection, transaction, subjectId, ct)
                ?? throw new KeyNotFoundException("Gate subject was not found.");
            var current = status.Attempts[^1];
            if (GateStates.IsTerminal(current.State)) return;
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = 'cancelled', outcome = 'cancelled',
                    expires_at = $now, reported_at = $now, containment_deadline = NULL
                 WHERE id = $attempt;
                INSERT INTO gate_events(attempt_id, event, at) VALUES ($attempt, 'cancelled', $now);
                """, ct, transaction, ("$attempt", current.AttemptId), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "gate.cancelled", "gate-subject",
                subjectId, JsonSerializer.Serialize(new { current.AttemptId }), ct);
            status = await ReadGateStatusAsync(connection, transaction, subjectId, ct);
        }, ct);
        return status!;
    }

    public async Task<IReadOnlyList<GateStatus>> ListTaskGateStatusesAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var connection = Open())
        {
            await connection.OpenAsync(ct);
            await using var command = Command(connection, """
                SELECT s.id FROM gate_subjects s JOIN tasks t ON t.id = s.task_id
                 WHERE t.project_id = $project AND (t.id = $task OR t.task_key = upper($task))
                 ORDER BY s.created_at DESC;
                """, ("$project", projectId), ("$task", taskIdentity));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }
        var statuses = new List<GateStatus>(ids.Count);
        foreach (var id in ids)
            if (await GetGateStatusAsync(id, ct) is { } status) statuses.Add(status);
        return statuses;
    }

    public async Task<GateClaimResponse> ClaimGateAsync(GateClaimRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        GateClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ReconcileGateTimeoutsAsync(connection, transaction, ct);
            if (request.AvailableGateSlots <= 0)
            {
                response = new GateClaimResponse("empty", Message: "No free gate slot.");
                return;
            }
            string hostId;
            await using (var command = Command(connection, """
                SELECT host_id, instance_id, status, capabilities_json FROM runners WHERE id = $id;
                """, transaction, ("$id", request.ExecutorId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Gate executor is not registered.");
                if (reader.GetString(1) != request.InstanceId || reader.GetString(2) != "active")
                    throw new TaskServerConflictException("runner-instance-stale", "Gate executor instance is not active.");
                var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3), GateJson) ?? [];
                if (!capabilities.Contains(GateCapabilities.Executor, StringComparer.Ordinal))
                    throw new TaskServerConflictException("gate-capability-required", "Runner is not a gate executor.");
                hostId = reader.GetString(0);
            }
            var occupied = Convert.ToInt32(await ScalarAsync(connection, """
                SELECT count(*) FROM gate_attempts
                 WHERE host_id = $host
                   AND state IN ('claimed','materializing','running','reporting','cleaning');
                """, ct, transaction, ("$host", hostId)) ?? 0);
            if (occupied >= 1)
            {
                response = new GateClaimResponse("empty", Message: "Gate host slot is occupied.");
                return;
            }
            var candidates = new List<(string AttemptId, GateSubject Subject, int Number)>();
            await using (var command = Command(connection, """
                SELECT a.id, s.subject_json, a.attempt_number
                  FROM gate_attempts a JOIN gate_subjects s ON s.id = a.subject_id
                 WHERE a.state = 'queued' ORDER BY a.created_at LIMIT 32;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    candidates.Add((reader.GetString(0), JsonSerializer.Deserialize<GateSubject>(reader.GetString(1), GateJson)!, reader.GetInt32(2)));
            foreach (var candidate in candidates)
            {
                var required = new HashSet<string>(candidate.Subject.Plan.RequiredCapabilities, StringComparer.Ordinal)
                {
                    GateCapabilities.Executor, CapabilityProtocol.RepositoryAccess,
                    CapabilityProtocol.TaskServerConnectivity, CapabilityProtocol.Disk,
                    GateCapabilities.GitMaterialization,
                };
                if (candidate.Subject.ResultRef is null)
                    required.Add(GateCapabilities.BundleMaterialization);
                if (candidate.Subject.ResultRef is not null) required.Add(CapabilityProtocol.GitFetch);
                var admission = await EvaluateCapabilityAdmissionAsync(connection, transaction,
                    request.ExecutorId, hostId, required.ToArray(), ct);
                if (!admission.Eligible) continue;
                var fence = Convert.ToInt64(await ScalarAsync(connection,
                    "SELECT last_fence FROM gate_subjects WHERE id = $id;", ct, transaction,
                    ("$id", candidate.Subject.SubjectId)) ?? 0L) + 1;
                var now = UtcNow;
                var expiry = now.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
                var leaseId = $"gls_{Guid.NewGuid():N}";
                var resourceNamespace = $"gate-{candidate.AttemptId}-{fence}";
                var portCursor = Convert.ToInt32(await ScalarAsync(connection,
                    "SELECT value FROM meta WHERE key = 'review_port_cursor';", ct, transaction) ?? 23992);
                var portBase = portCursor >= 59992 ? 24000 : portCursor + 8;
                await SetMetaAsync(connection, transaction, "review_port_cursor",
                    portBase.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
                var deadline = now.AddSeconds(candidate.Subject.Plan.OverallTimeoutSeconds);
                await ExecuteAsync(connection, """
                    UPDATE gate_subjects SET last_fence = $fence WHERE id = $subject;
                    UPDATE gate_attempts SET state = 'claimed', executor_id = $executor, instance_id = $instance,
                        host_id = $host, lease_id = $lease, fence = $fence,
                        acquired_at = $now, expires_at = $expiry, resource_namespace = $namespace,
                        port_base = $portBase,
                        claimed_at = $now, deadline = $deadline WHERE id = $attempt AND state = 'queued';
                    INSERT INTO gate_events(attempt_id, event, at) VALUES ($attempt, 'claimed', $now);
                    """, ct, transaction, ("$fence", fence), ("$subject", candidate.Subject.SubjectId),
                    ("$executor", request.ExecutorId), ("$instance", request.InstanceId), ("$host", hostId),
                    ("$lease", leaseId), ("$now", Iso(now)), ("$expiry", Iso(expiry)),
                    ("$namespace", resourceNamespace), ("$portBase", portBase),
                    ("$deadline", Iso(deadline)), ("$attempt", candidate.AttemptId));
                await AuditAsync(connection, transaction, actorId, "gate.claimed", "gate-attempt",
                    candidate.AttemptId, JsonSerializer.Serialize(new { fence, hostId }), ct);
                var attempt = await ReadGateAttemptAsync(connection, transaction, candidate.AttemptId, ct);
                response = new GateClaimResponse("claimed", candidate.Subject, attempt,
                    new GateLease(leaseId, candidate.AttemptId, request.ExecutorId, request.InstanceId,
                        hostId, fence, 0, now, expiry, resourceNamespace, portBase));
                return;
            }
            response = new GateClaimResponse("empty", Message: "No eligible gate subject is queued.");
        }, ct);
        return response!;
    }

    public async Task<GateLease> RenewGateAsync(string attemptId, GateRenewRequest request, CancellationToken ct)
    {
        RequireWritable();
        GateLease? lease = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadGateAttemptAsync(connection, transaction, attemptId, ct)
                ?? throw new KeyNotFoundException("Gate attempt was not found.");
            await ValidateGateAuthorityAsync(connection, transaction, attempt, request.Authority, ct);
            var expiry = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, "UPDATE gate_attempts SET expires_at = $expiry WHERE id = $id;", ct,
                transaction, ("$expiry", Iso(expiry)), ("$id", attemptId));
            lease = await ReadGateLeaseAsync(connection, transaction, attemptId, ct);
        }, ct);
        return lease!;
    }

    public async Task<GateAttempt> AdvanceGateAsync(string attemptId, GatePhaseRequest request, CancellationToken ct)
    {
        RequireWritable();
        GateAttempt? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadGateAttemptAsync(connection, transaction, attemptId, ct)
                ?? throw new KeyNotFoundException("Gate attempt was not found.");
            await ValidateGateAuthorityAsync(connection, transaction, attempt, request.Authority, ct);
            var next = attempt.State switch
            {
                GateStates.Claimed => GateStates.Materializing,
                GateStates.Materializing => GateStates.Running,
                GateStates.Running => GateStates.Reporting,
                GateStates.Reporting => GateStates.Cleaning,
                _ => null,
            };
            var failedBeforeRun = request.Phase == GateStates.Reporting
                && attempt.State is GateStates.Claimed or GateStates.Materializing;
            if (request.Phase != next && !failedBeforeRun)
                throw new TaskServerConflictException("gate-phase-conflict", "Gate phase does not follow the durable state machine.");
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $phase WHERE id = $id;
                INSERT INTO gate_events(attempt_id, event, at) VALUES ($id, $phase, $now);
                """, ct, transaction, ("$phase", request.Phase), ("$id", attemptId), ("$now", Iso(UtcNow)));
            updated = await ReadGateAttemptAsync(connection, transaction, attemptId, ct);
        }, ct);
        return updated!;
    }

    public async Task<GateStatus> ReportGateAsync(string attemptId, SubmitGateReportRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        GateStatus? status = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadGateAttemptAsync(connection, transaction, attemptId, ct)
                ?? throw new KeyNotFoundException("Gate attempt was not found.");
            await ValidateGateAuthorityAsync(connection, transaction, attempt, request.Authority, ct, allowTerminalReplay: true);
            var subject = await ReadGateSubjectAsync(connection, transaction, attempt.SubjectId, ct)
                ?? throw new KeyNotFoundException("Gate subject was not found.");
            if (!string.Equals(subject.ExpectedSha, request.Report.TestedSha, StringComparison.OrdinalIgnoreCase))
                throw new TaskServerConflictException("gate-tested-sha-mismatch", "Tested SHA differs from the immutable subject.");
            ValidateGateReport(subject.Plan, request.Report);
            var json = JsonSerializer.Serialize(request.Report, GateJson);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            var priorDigest = await ScalarAsync(connection,
                "SELECT report_digest FROM gate_attempts WHERE id = $id;", ct, transaction, ("$id", attemptId)) as string;
            if (priorDigest is not null)
            {
                if (priorDigest != digest)
                    throw new TaskServerConflictException("gate-report-conflict", "A different report was already accepted for this attempt.");
                status = await ReadGateStatusAsync(connection, transaction, attempt.SubjectId, ct);
                return;
            }
            if (attempt.State != GateStates.Cleaning)
                throw new TaskServerConflictException("gate-phase-conflict", "Gate report requires completed cleanup phase.");
            var terminal = GateOutcomePolicy.Decide(request.Report, attempt.AttemptNumber, subject.MaxAttempts);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $state, failure_classification = $failure,
                    outcome = $outcome, report_json = $report, report_digest = $digest,
                    reported_at = $now, cleaned_at = CASE WHEN $clean = 'clean' THEN $now ELSE NULL END
                 WHERE id = $id;
                INSERT INTO gate_events(attempt_id, event, at, detail) VALUES ($id, $state, $now, $failure);
                """, ct, transaction, ("$state", terminal.State), ("$failure", terminal.FailureClassification),
                ("$outcome", terminal.Outcome), ("$report", json), ("$digest", digest),
                ("$now", Iso(now)), ("$clean", request.Report.CleanupStatus), ("$id", attemptId));
            if (terminal.State == GateStates.InfraRetry)
                await InsertGateRetryAsync(connection, transaction, attempt.SubjectId, attempt.AttemptNumber + 1, ct);
            await AuditAsync(connection, transaction, actorId, "gate.reported", "gate-attempt", attemptId,
                JsonSerializer.Serialize(new { terminal.State, terminal.FailureClassification }), ct);
            status = await ReadGateStatusAsync(connection, transaction, attempt.SubjectId, ct);
        }, ct);
        return status!;
    }

    public async Task<GateStatus> ConfirmGateContainmentAsync(
        string attemptId, GateContainmentRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        GateStatus? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ReconcileGateTimeoutsAsync(connection, transaction, ct);
            var attempt = await ReadGateAttemptAsync(connection, transaction, attemptId, ct)
                ?? throw new KeyNotFoundException("Gate attempt was not found.");
            await using var command = Command(connection, """
                SELECT host_id, instance_id, status FROM runners WHERE id = $id;
                """, transaction, ("$id", request.ExecutorId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != request.HostId
                || reader.GetString(1) != request.InstanceId || reader.GetString(2) != "active")
                throw new TaskServerConflictException("gate-containment-identity", "Containment must come from a live registered host instance.");
            await reader.DisposeAsync();
            var storedNamespace = await ScalarAsync(connection,
                "SELECT resource_namespace FROM gate_attempts WHERE id = $id;", ct, transaction,
                ("$id", attemptId)) as string;
            if (attempt.HostId != request.HostId || attempt.Fence != request.Fence
                || storedNamespace != request.ResourceNamespace
                || !request.NoProcesses || !request.WorkspaceRemoved)
                throw new TaskServerConflictException("gate-containment-invalid", "Gate containment proof does not match the fenced namespace.");
            if (attempt.CleanedAt is not null &&
                (attempt.State == GateStates.InfraRetry || GateStates.IsTerminal(attempt.State)))
            {
                result = await ReadGateStatusAsync(connection, transaction, attempt.SubjectId, ct);
                return;
            }
            if (attempt.State is GateStates.Claimed or GateStates.Materializing or GateStates.Running
                or GateStates.Reporting or GateStates.Cleaning)
            {
                await ExecuteAsync(connection, """
                    UPDATE gate_attempts SET state = 'infra-retry', failure_classification = $failure,
                        outcome = $failure, reported_at = $now,
                        containment_deadline = $deadline WHERE id = $id;
                    INSERT INTO gate_events(attempt_id, event, at, detail)
                    VALUES ($id, 'infra-retry', $now, $failure);
                    """, ct, transaction, ("$failure", GateFailureClasses.LostLease),
                    ("$now", Iso(UtcNow)), ("$deadline", Iso(UtcNow.AddMinutes(5))), ("$id", attemptId));
            }
            else if (attempt.State != GateStates.InfraRetry)
                throw new TaskServerConflictException("gate-containment-stale", "Gate attempt is no longer waiting for containment.");
            var subject = await ReadGateSubjectAsync(connection, transaction, attempt.SubjectId, ct)!;
            if (attempt.AttemptNumber >= subject!.MaxAttempts)
            {
                await ExecuteAsync(connection, """
                    UPDATE gate_attempts SET state = 'infra-failed', outcome = $outcome,
                        cleaned_at = $now, containment_deadline = NULL WHERE id = $id;
                    """, ct, transaction, ("$outcome", GateFailureClasses.GateInfra),
                    ("$now", Iso(UtcNow)), ("$id", attemptId));
            }
            else
            {
                await ExecuteAsync(connection, """
                    UPDATE gate_attempts SET cleaned_at = $now, containment_deadline = NULL WHERE id = $id;
                    """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", attemptId));
                await InsertGateRetryAsync(connection, transaction, attempt.SubjectId,
                    attempt.AttemptNumber + 1, ct);
            }
            await AuditAsync(connection, transaction, actorId, "gate.contained", "gate-attempt", attemptId,
                JsonSerializer.Serialize(new { request.HostId, request.Fence, request.ResourceNamespace }), ct);
            result = await ReadGateStatusAsync(connection, transaction, attempt.SubjectId, ct);
        }, ct);
        return result!;
    }

    private static void ValidateGateSubject(CreateGateSubjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRunId) || string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.RepositoryId)
            || string.IsNullOrWhiteSpace(request.PolicyHash) || string.IsNullOrWhiteSpace(request.PipelineDefinitionVersion)
            || string.IsNullOrWhiteSpace(request.TestSelectionAuditDigest)
            || request.MaxAttempts is < 1 or > 5
            || !ValidDigest(request.ExpectedSha, 40, 64) || !ValidDigest(request.PlanHash, 64)
            || (string.IsNullOrWhiteSpace(request.ResultRef) &&
                (string.IsNullOrWhiteSpace(request.SourceBundleArtifactId) || !ValidDigest(request.SourceBundleSha256, 64))))
            throw new ArgumentException("Gate subject requires a bounded, exact source and all audit identities.");
        var plan = request.Plan;
        if (plan.GateId != "post-build-test-gate" || plan.Version < 1 || plan.Commands.Count is < 1 or > 32
            || plan.OverallTimeoutSeconds is < 1 or > 21600 || plan.MaxOutputBytes is < 1 or > 1048576
            || plan.CleanupPolicy != "always" || Path.IsPathRooted(plan.WorkingSubdirectory)
            || plan.WorkingSubdirectory.Split('/', '\\').Any(segment => segment == "..")
            || plan.Commands.Select(command => command.StepId).Distinct(StringComparer.Ordinal).Count() != plan.Commands.Count
            || plan.Commands.Any(command => string.IsNullOrWhiteSpace(command.StepId)
                || string.IsNullOrWhiteSpace(command.FileName) || command.TimeoutSeconds is < 1 or > 21600
                || Path.IsPathRooted(command.WorkingSubdirectory)
                || command.WorkingSubdirectory.Split('/', '\\').Any(segment => segment == "..")))
            throw new ArgumentException("Gate plan must be a bounded catalogued post-build-test plan.");
        var calculated = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(plan, GateJson)))).ToLowerInvariant();
        if (!string.Equals(calculated, request.PlanHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Gate plan hash does not match its frozen commands.");
    }

    private static bool SameGateSubject(GateSubject existing, CreateGateSubjectRequest request)
        => existing.TaskId == request.TaskId && existing.RepositoryId == request.RepositoryId
            && existing.RepositoryUrl == request.RepositoryUrl
            && string.Equals(existing.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
            && existing.ResultRef == request.ResultRef
            && existing.SourceBundleArtifactId == request.SourceBundleArtifactId
            && existing.SourceBundleSha256 == request.SourceBundleSha256
            && existing.PolicyHash == request.PolicyHash
            && existing.PipelineDefinitionVersion == request.PipelineDefinitionVersion
            && existing.TestSelectionAuditDigest == request.TestSelectionAuditDigest
            && existing.DispatchDeadline == request.DispatchDeadline.ToUniversalTime()
            && existing.MaxAttempts == request.MaxAttempts;

    private static void ValidateGateReport(GatePlan plan, GateReport report)
    {
        if (report.Outcome is not (GateStates.Passed or GateStates.ProductFailed or GateStates.InfraFailed)
            || report.CleanupStatus is not ("clean" or "failed")
            || string.IsNullOrWhiteSpace(report.EnvironmentIdentity)
            || string.IsNullOrWhiteSpace(report.ToolchainIdentity)
            || report.Commands.Count > plan.Commands.Count
            || report.Commands.Select(item => item.StepId).Distinct(StringComparer.Ordinal).Count() != report.Commands.Count
            || report.Commands.Where((item, index) =>
                item.StepId != plan.Commands[index].StepId).Any()
            || report.Commands.Any(item => !ValidDigest(item.OutputSha256, 64)
                || Encoding.UTF8.GetByteCount(item.OutputExcerpt) > plan.MaxOutputBytes)
            || report.OutputDigests.Any(digest => !ValidDigest(digest, 64))
            || report.ArtifactDigests.Any(digest => !ValidDigest(digest, 64)))
            throw new TaskServerConflictException("gate-report-invalid", "Gate report is outside its frozen plan or evidence bounds.");
        if (report.FailureClassification is not null
            && report.FailureClassification is not (GateFailureClasses.ProductFailure
                or GateFailureClasses.ExecutionTimeout or GateFailureClasses.LostLease
                or GateFailureClasses.SnapshotUnavailable or GateFailureClasses.ToolFailure
                or GateFailureClasses.CleanupFailure))
            throw new TaskServerConflictException("gate-report-invalid", "Gate failure classification is unknown.");
        if (report.FailureClassification is null
            && (report.Outcome != GateStates.Passed || report.Commands.Count != plan.Commands.Count
                || report.Commands.Any(item => item.ExitCode != 0 || item.TimedOut)
                || !ValidDigest(report.TestedTree, 40, 64) || report.DirtyBefore))
            throw new TaskServerConflictException("gate-report-invalid", "Passing gate lacks full clean command proof.");
        if (report.FailureClassification == GateFailureClasses.ProductFailure
            && (report.Outcome != GateStates.ProductFailed || !ValidDigest(report.TestedTree, 40, 64)
                || report.DirtyBefore || !report.Commands.Any(item => item.ExitCode is > 0 && !item.TimedOut)))
            throw new TaskServerConflictException("gate-report-invalid", "Product failure lacks normal failing command proof.");
    }

    private async Task ReconcileGateTimeoutsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var expired = new List<(string Id, string SubjectId, int Number, int Maximum, string State, DateTime? Deadline)>();
        await using (var command = Command(connection, """
            SELECT a.id, a.subject_id, a.attempt_number, s.max_attempts, a.state,
                   a.deadline FROM gate_attempts a JOIN gate_subjects s ON s.id = a.subject_id
             WHERE (a.state = 'queued' AND s.dispatch_deadline <= $now)
                OR (a.state IN ('claimed','materializing','running','reporting','cleaning')
                    AND (a.expires_at <= $now OR a.deadline <= $deadlineGrace))
                OR (a.state = 'infra-retry' AND a.containment_deadline <= $now);
            """, transaction, ("$now", Iso(UtcNow)),
            ("$deadlineGrace", Iso(UtcNow.AddMinutes(-5)))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                expired.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? null : Parse(reader.GetString(5))));
        foreach (var item in expired)
        {
            if (item.State == GateStates.InfraRetry)
            {
                await ExecuteAsync(connection, """
                    UPDATE gate_attempts SET state = 'infra-failed', outcome = $outcome,
                        containment_deadline = NULL WHERE id = $id;
                    INSERT INTO gate_events(attempt_id, event, at, detail)
                    VALUES ($id, 'infra-failed', $now, $outcome);
                    """, ct, transaction, ("$outcome", GateFailureClasses.GateInfra),
                    ("$id", item.Id), ("$now", Iso(UtcNow)));
                continue;
            }
            var failure = item.State == GateStates.Queued ? GateFailureClasses.NoEligibleGateExecutor
                : item.Deadline <= UtcNow ? GateFailureClasses.ExecutionTimeout : GateFailureClasses.LostLease;
            // Lease expiry fences reports, but retry waits for a host cleanup
            // attestation. A missing host eventually settles as GateInfra.
            var retry = item.State != GateStates.Queued && item.Number < item.Maximum;
            var state = retry ? GateStates.InfraRetry
                : failure == GateFailureClasses.ExecutionTimeout ? GateStates.TimedOut : GateStates.InfraFailed;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $state, failure_classification = $failure,
                    outcome = $outcome, reported_at = $now,
                    containment_deadline = $containment WHERE id = $id;
                INSERT INTO gate_events(attempt_id, event, at, detail) VALUES ($id, $state, $now, $failure);
                """, ct, transaction, ("$state", state), ("$failure", failure),
                ("$outcome", retry ? failure : GateFailureClasses.GateInfra),
                ("$now", Iso(UtcNow)), ("$id", item.Id),
                ("$containment", retry ? Iso(UtcNow.AddMinutes(5)) : null));
        }
    }

    private async Task InsertGateRetryAsync(SqliteConnection connection, SqliteTransaction transaction,
        string subjectId, int number, CancellationToken ct)
    {
        var id = $"gat_{Guid.NewGuid():N}";
        await ExecuteAsync(connection, """
            INSERT INTO gate_attempts(id, subject_id, attempt_number, state, created_at)
            VALUES ($id, $subject, $number, 'queued', $now);
            INSERT INTO gate_events(attempt_id, event, at) VALUES ($id, 'queued', $now);
            """, ct, transaction, ("$id", id), ("$subject", subjectId),
            ("$number", number), ("$now", Iso(UtcNow)));
    }

    private async Task ValidateGateAuthorityAsync(SqliteConnection connection, SqliteTransaction transaction,
        GateAttempt attempt, GateAuthority authority, CancellationToken ct, bool allowTerminalReplay = false)
    {
        await using var command = Command(connection, """
            SELECT instance_id, lease_id, authority_epoch, expires_at FROM gate_attempts WHERE id = $id;
            """, transaction, ("$id", attempt.AttemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (attempt.ExecutorId != authority.ExecutorId || reader.GetString(0) != authority.InstanceId
            || reader.GetString(1) != authority.LeaseId || attempt.Fence != authority.Fence
            || reader.GetInt64(2) != authority.AuthorityEpoch)
            throw new TaskServerConflictException("stale-gate-fence", "Gate lease, executor, instance, fence or epoch is stale.");
        if (allowTerminalReplay && GateStates.IsTerminal(attempt.State)) return;
        if (attempt.State == GateStates.InfraRetry || GateStates.IsTerminal(attempt.State)
            || Parse(reader.GetString(3)) <= UtcNow)
            throw new TaskServerConflictException("stale-gate-fence", "Gate lease is no longer active.");
    }

    private static async Task<GateSubject?> ReadGateSubjectByKeyAsync(SqliteConnection connection,
        SqliteTransaction transaction, string run, string gate, string hash, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT subject_json FROM gate_subjects WHERE source_run_id = $run AND gate_id = $gate AND plan_hash = $hash;
            """, transaction, ("$run", run), ("$gate", gate), ("$hash", hash.ToLowerInvariant()));
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<GateSubject>(json, GateJson);
    }

    private static async Task<GateSubject?> ReadGateSubjectAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var command = Command(connection, "SELECT subject_json FROM gate_subjects WHERE id = $id;",
            transaction, ("$id", id));
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<GateSubject>(json, GateJson);
    }

    private static async Task<GateAttempt?> ReadGateAttemptAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, subject_id, attempt_number, state, executor_id, host_id, failure_classification,
                   outcome, created_at, claimed_at, reported_at, cleaned_at, fence, deadline
              FROM gate_attempts WHERE id = $id;
            """, transaction, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ParseGateAttempt(reader) : null;
    }

    private static GateAttempt ParseGateAttempt(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : Parse(reader.GetString(11)), reader.GetInt64(12),
            reader.IsDBNull(13) ? null : Parse(reader.GetString(13)));

    private static async Task<GateLease> ReadGateLeaseAsync(SqliteConnection connection,
        SqliteTransaction transaction, string id, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT lease_id, executor_id, instance_id, host_id, fence, authority_epoch,
                   acquired_at, expires_at, resource_namespace, port_base FROM gate_attempts WHERE id = $id;
            """, transaction, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new GateLease(reader.GetString(0), id, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), Parse(reader.GetString(6)),
            Parse(reader.GetString(7)), reader.GetString(8), reader.GetInt32(9));
    }

    private async Task<GateStatus?> ReadGateStatusAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string subjectId, CancellationToken ct)
    {
        var subject = await ReadGateSubjectAsync(connection, transaction, subjectId, ct);
        if (subject is null) return null;
        var attempts = new List<GateAttempt>();
        await using (var command = Command(connection, """
            SELECT id, subject_id, attempt_number, state, executor_id, host_id, failure_classification,
                   outcome, created_at, claimed_at, reported_at, cleaned_at, fence, deadline
              FROM gate_attempts WHERE subject_id = $id ORDER BY attempt_number;
            """, transaction, ("$id", subjectId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) attempts.Add(ParseGateAttempt(reader));
        var current = attempts[^1];
        await using var reportCommand = Command(connection,
            "SELECT report_json FROM gate_attempts WHERE id = $id;", transaction, ("$id", current.AttemptId));
        var reportJson = await reportCommand.ExecuteScalarAsync(ct) as string;
        var report = reportJson is null ? null : JsonSerializer.Deserialize<GateReport>(reportJson, GateJson);
        return new GateStatus(subject, attempts, report,
            Math.Max(0, ((current.ClaimedAt ?? UtcNow) - current.CreatedAt).TotalSeconds),
            GateStates.IsTerminal(current.State) || current.State is GateStates.Queued or GateStates.InfraRetry
                ? null : current.HostId,
            current.State, attempts.Count, current.Deadline, report?.TestedSha,
            GateStates.IsTerminal(current.State) ? current.Outcome : null, report is not null);
    }
}

public static class GateOutcomePolicy
{
    public static (string State, string Outcome, string? FailureClassification) Decide(
        GateReport report, int attemptNumber, int maxAttempts)
    {
        if (report.CleanupStatus != "clean")
            return (GateStates.InfraFailed, GateFailureClasses.GateInfra, GateFailureClasses.CleanupFailure);
        if (report.FailureClassification == GateFailureClasses.ProductFailure)
            return (GateStates.ProductFailed, GateFailureClasses.ProductFailure, GateFailureClasses.ProductFailure);
        if (report.FailureClassification is null && report.Outcome == GateStates.Passed)
            return (GateStates.Passed, GateStates.Passed, null);
        if (report.FailureClassification == GateFailureClasses.ExecutionTimeout)
            return attemptNumber < maxAttempts
                ? (GateStates.InfraRetry, GateFailureClasses.ExecutionTimeout, GateFailureClasses.ExecutionTimeout)
                : (GateStates.TimedOut, GateFailureClasses.GateInfra, GateFailureClasses.ExecutionTimeout);
        return Infra(report.FailureClassification ?? GateFailureClasses.ToolFailure, attemptNumber, maxAttempts);
    }

    private static (string State, string Outcome, string FailureClassification) Infra(
        string failure, int attemptNumber, int maxAttempts)
        => attemptNumber < maxAttempts
            ? (GateStates.InfraRetry, failure, failure)
            : (GateStates.InfraFailed, GateFailureClasses.GateInfra, failure);
}
