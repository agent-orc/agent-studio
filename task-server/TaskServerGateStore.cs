using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions GateJson = new(JsonSerializerDefaults.Web);

    internal static async Task ApplyGateMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS gate_subjects(
                id TEXT PRIMARY KEY,
                source_run_id TEXT NOT NULL REFERENCES runs(id),
                gate_id TEXT NOT NULL,
                plan_hash TEXT NOT NULL,
                policy_hash TEXT NOT NULL,
                subject_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(source_run_id, gate_id, plan_hash)
            );
            CREATE TABLE IF NOT EXISTS gate_attempts(
                id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL REFERENCES gate_subjects(id),
                attempt_number INTEGER NOT NULL,
                state TEXT NOT NULL,
                executor_id TEXT,
                host_id TEXT,
                lease_json TEXT,
                fence INTEGER NOT NULL DEFAULT 0,
                expires_at TEXT,
                classification TEXT,
                outcome TEXT,
                report_json TEXT,
                report_key TEXT,
                created_at TEXT NOT NULL,
                claimed_at TEXT,
                reported_at TEXT,
                cleaned_at TEXT,
                UNIQUE(subject_id, attempt_number)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_gate_live_subject
                ON gate_attempts(subject_id)
                WHERE state IN ('queued', 'claimed', 'materializing', 'running', 'reporting', 'cleaning');
            CREATE TABLE IF NOT EXISTS gate_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                attempt_id TEXT NOT NULL REFERENCES gate_attempts(id),
                state TEXT NOT NULL,
                classification TEXT,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_gate_attempt_queue ON gate_attempts(state, created_at);
            """, ct);
        await ExecuteAsync(connection, """
            UPDATE principals
               SET scopes_json = json_insert(scopes_json, '$[#]', 'gates:dispatch')
             WHERE kind = 'engine'
               AND NOT EXISTS (
                   SELECT 1 FROM json_each(principals.scopes_json)
                    WHERE value = 'gates:dispatch');
            """, ct);
        await ExecuteAsync(connection, """
            INSERT INTO meta(key, value) VALUES ('gate_authority_epoch', '1')
            ON CONFLICT(key) DO NOTHING;
            """, ct);
    }

    public async Task<GateSubject> CreateGateSubjectAsync(CreateGateSubjectRequest request, string actor, CancellationToken ct)
    {
        RequireWritable();
        request = request with { PlanHash = (request.PlanHash ?? string.Empty).Trim().ToLowerInvariant() };
        ValidateGateSubject(request);
        GateSubject? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadGateSubjectByKeyAsync(connection, transaction,
                request.SourceRunId, request.Plan.GateId, request.PlanHash, ct);
            if (replay is not null)
            {
                if (!SameGateSubject(replay, request))
                    throw new TaskServerConflictException("gate-subject-conflict", "Gate subject key was reused with different immutable facts.");
                result = replay;
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
                if (!string.Equals(reader.GetString(0), request.TaskId, StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(reader.IsDBNull(2) ? null : reader.GetString(2), request.RepositoryId, StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(3) ? null : reader.GetString(3), request.RepositoryUrl, StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), request.ResultRef, StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), request.SourceBundleId, StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), request.SourceBundleSha256, StringComparison.OrdinalIgnoreCase))
                    throw new TaskServerConflictException("gate-source-mismatch", "Gate subject differs from the fenced source run.");
            }

            var now = UtcNow;
            result = new GateSubject(
                $"gsb_{Guid.NewGuid():N}", request.TaskId, request.SourceRunId,
                request.RepositoryId, request.RepositoryUrl, request.ExpectedSha,
                request.ResultRef, request.SourceBundleId, request.SourceBundleSha256,
                request.PlanHash, request.PolicyHash, request.PipelineDefinitionVersion,
                request.TestSelectionAuditDigest, request.Plan,
                request.DispatchDeadline.ToUniversalTime(), request.RetryBudget, now);
            await ExecuteAsync(connection, """
                INSERT INTO gate_subjects(id, source_run_id, gate_id, plan_hash, policy_hash, subject_json, created_at)
                VALUES ($id, $run, $gate, $plan, $policy, $json, $now);
                """, ct, transaction,
                ("$id", result.SubjectId), ("$run", request.SourceRunId),
                ("$gate", request.Plan.GateId), ("$plan", request.PlanHash),
                ("$policy", request.PolicyHash), ("$json", JsonSerializer.Serialize(result, GateJson)),
                ("$now", Iso(now)));
            await InsertGateAttemptAsync(connection, transaction, result.SubjectId, 1, now, ct);
            await AuditAsync(connection, transaction, actor, "gate.subject-created", "gate-subject",
                result.SubjectId, JsonSerializer.Serialize(new { request.SourceRunId, request.Plan.GateId }), ct);
        }, ct);
        return result!;
    }

    public async Task<GateClaimResponse> ClaimGateAsync(GateClaimRequest request, string actor, CancellationToken ct)
    {
        RequireAdmission();
        GateClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ReconcileGateDeadlinesAsync(connection, transaction, UtcNow, ct);
            if (request.AvailableGateSlots <= 0)
            {
                response = new GateClaimResponse("empty", Message: "Gate executor has no free gate slot.");
                return;
            }
            var executor = await ReadGateExecutorAsync(connection, transaction, request.ExecutorId, request.InstanceId, ct);
            var occupied = await ScalarAsync(connection, """
                SELECT 1 FROM gate_attempts WHERE host_id = $host
                   AND state IN ('claimed', 'materializing', 'running', 'reporting', 'cleaning') LIMIT 1;
                """, ct, transaction, ("$host", executor));
            if (occupied is not null)
            {
                response = new GateClaimResponse("empty", Message: "Gate executor already has an active attempt.");
                return;
            }
            var queue = new List<(GateAttempt Attempt, GateSubject Subject)>();
            await using (var command = Command(connection, """
                SELECT a.id, a.subject_id, a.attempt_number, a.state, a.executor_id, a.host_id,
                       a.classification, a.outcome, a.created_at, a.claimed_at, a.reported_at, a.cleaned_at,
                       s.subject_json
                  FROM gate_attempts a JOIN gate_subjects s ON s.id = a.subject_id
                 WHERE a.state = 'queued' ORDER BY a.created_at LIMIT 32;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    queue.Add((ReadGateAttempt(reader), JsonSerializer.Deserialize<GateSubject>(reader.GetString(12), GateJson)!));
            }
            foreach (var candidate in queue)
            {
                if (candidate.Attempt.AttemptNumber > 1 && await ScalarAsync(connection, """
                    SELECT 1 FROM gate_attempts
                     WHERE subject_id = $subject AND attempt_number = $previous AND host_id = $host;
                    """, ct, transaction, ("$subject", candidate.Subject.SubjectId),
                    ("$previous", candidate.Attempt.AttemptNumber - 1), ("$host", executor)) is not null)
                    continue;
                var sourceCapability = candidate.Subject.ResultRef is null
                    ? CapabilityProtocol.GateSourceBundle
                    : CapabilityProtocol.GateRepository(candidate.Subject.RepositoryId);
                var required = new[] { CapabilityProtocol.GateExecutor,
                        CapabilityProtocol.GateGit, CapabilityProtocol.RepositoryAccess,
                        CapabilityProtocol.TaskServerConnectivity, sourceCapability }
                    .Concat(candidate.Subject.Plan.RequiredCapabilities)
                    .Distinct(StringComparer.Ordinal).ToArray();
                var admission = await EvaluateCapabilityAdmissionAsync(connection, transaction,
                    request.ExecutorId, executor, required, ct);
                if (!admission.Eligible) continue;

                var now = UtcNow;
                var fence = Convert.ToInt64(await ScalarAsync(connection,
                    "SELECT COALESCE(MAX(fence), 0) FROM gate_attempts WHERE subject_id = $subject;",
                    ct, transaction, ("$subject", candidate.Subject.SubjectId))) + 1;
                var lease = new GateLease($"gls_{Guid.NewGuid():N}", candidate.Attempt.AttemptId,
                    request.ExecutorId, request.InstanceId, executor, fence,
                    await GateAuthorityEpochAsync(connection, transaction, ct), now,
                    now.AddSeconds(Math.Clamp(request.LeaseSeconds, 30, 300)),
                    $"gate-{candidate.Attempt.AttemptId}-{fence}");
                await ExecuteAsync(connection, """
                    UPDATE gate_attempts SET state = 'claimed', executor_id = $executor,
                        host_id = $host, lease_json = $lease, fence = $fence,
                        expires_at = $expiry, claimed_at = $now WHERE id = $id;
                    """, ct, transaction, ("$executor", request.ExecutorId), ("$host", executor),
                    ("$lease", JsonSerializer.Serialize(lease, GateJson)), ("$fence", fence),
                    ("$expiry", Iso(lease.ExpiresAt)), ("$now", Iso(now)), ("$id", candidate.Attempt.AttemptId));
                await AddGateEventAsync(connection, transaction, candidate.Attempt.AttemptId, GateStates.Claimed, null, now, ct);
                response = new GateClaimResponse("claimed", candidate.Subject,
                    candidate.Attempt with { State = GateStates.Claimed, ExecutorId = request.ExecutorId, HostId = executor, ClaimedAt = now }, lease);
                await AuditAsync(connection, transaction, actor, "gate.claimed", "gate-attempt",
                    candidate.Attempt.AttemptId, JsonSerializer.Serialize(new { lease.Fence, lease.ResourceNamespace }), ct);
                return;
            }
            response = new GateClaimResponse("empty", Message: "No queued gate matches fresh host capabilities.");
        }, ct);
        return response!;
    }

    public async Task<GateLease> RenewGateAsync(string attemptId, GateRenewRequest request, CancellationToken ct)
    {
        RequireWritable();
        GateLease? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadGateRowAsync(connection, transaction, attemptId, ct);
            var lease = RequireGateAuthority(row, request.Authority);
            result = lease with { ExpiresAt = UtcNow.AddSeconds(Math.Clamp(request.LeaseSeconds, 30, 300)) };
            await ExecuteAsync(connection,
                "UPDATE gate_attempts SET lease_json = $lease, expires_at = $expiry WHERE id = $id;",
                ct, transaction, ("$lease", JsonSerializer.Serialize(result, GateJson)),
                ("$expiry", Iso(result.ExpiresAt)), ("$id", attemptId));
        }, ct);
        return result!;
    }

    public async Task<GateAttempt> AdvanceGatePhaseAsync(string attemptId, GatePhaseRequest request, CancellationToken ct)
    {
        RequireWritable();
        GateAttempt? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadGateRowAsync(connection, transaction, attemptId, ct);
            RequireGateAuthority(row, request.Authority);
            if (!GatePhaseAllowed(row.Attempt.State, request.State))
                throw new TaskServerConflictException("gate-phase-conflict", "Gate phase transition is not allowed.");
            if (row.Attempt.State != request.State)
            {
                await ExecuteAsync(connection, "UPDATE gate_attempts SET state = $state WHERE id = $id;",
                    ct, transaction, ("$state", request.State), ("$id", attemptId));
                await AddGateEventAsync(connection, transaction, attemptId, request.State, null, UtcNow, ct);
            }
            result = row.Attempt with { State = request.State };
        }, ct);
        return result!;
    }

    public async Task<GateAttemptView> ReportGateAsync(
        string attemptId, SubmitGateReportRequest request, string actor, CancellationToken ct)
    {
        RequireWritable();
        GateAttemptView? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadGateRowAsync(connection, transaction, attemptId, ct);
            if (row.Report is not null)
            {
                if (!MatchesGateAuthority(row.Lease, request.Authority))
                    throw new TaskServerConflictException("gate-stale-fence", "Gate lease, fence or authority epoch is stale.");
                if (row.ReportKey != request.IdempotencyKey
                    || JsonSerializer.Serialize(row.Report, GateJson) != JsonSerializer.Serialize(request.Report, GateJson))
                    throw new TaskServerConflictException("gate-report-conflict", "Conflicting duplicate gate report.");
                result = new GateAttemptView(row.Attempt, row.Lease, row.Report);
                return;
            }
            var lease = RequireGateAuthority(row, request.Authority);
            var subject = await ReadGateSubjectAsync(connection, transaction, row.Attempt.SubjectId, ct)
                ?? throw new KeyNotFoundException("Gate subject was not found.");
            ValidateGateReport(subject, request.Report);
            if (row.Attempt.State != GateStates.Cleaning)
                throw new TaskServerConflictException("gate-phase-conflict", "Gate report requires the cleaning phase.");
            var (state, outcome, classification, retry) = GateReportDecision.Decide(
                request.Report, row.Attempt.AttemptNumber, subject.RetryBudget);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $state, outcome = $outcome,
                    classification = $classification, report_json = $report,
                    report_key = $key, reported_at = $now,
                    cleaned_at = CASE WHEN $clean = 'complete' THEN $now ELSE NULL END
                 WHERE id = $id;
                """, ct, transaction, ("$state", state), ("$outcome", outcome),
                ("$classification", classification), ("$report", JsonSerializer.Serialize(request.Report, GateJson)),
                ("$key", request.IdempotencyKey), ("$now", Iso(now)),
                ("$clean", request.Report.CleanupStatus), ("$id", attemptId));
            await AddGateEventAsync(connection, transaction, attemptId, state, classification, now, ct);
            if (retry)
                await InsertGateAttemptAsync(connection, transaction, subject.SubjectId,
                    row.Attempt.AttemptNumber + 1, now, ct);
            await AuditAsync(connection, transaction, actor, "gate.reported", "gate-attempt",
                attemptId, JsonSerializer.Serialize(new { state, outcome, classification, retry }), ct);
            result = new GateAttemptView(row.Attempt with
            {
                State = state, Outcome = outcome, FailureClassification = classification,
                ReportedAt = now, CleanedAt = request.Report.CleanupStatus == "complete" ? now : null,
            }, lease, request.Report);
        }, ct);
        return result!;
    }

    public async Task<GateStatusView?> GetGateStatusAsync(string subjectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var subject = await ReadGateSubjectAsync(connection, null, subjectId, ct);
        if (subject is null) return null;
        var attempts = new List<GateAttemptView>();
        await using var command = Command(connection, """
            SELECT id, subject_id, attempt_number, state, executor_id, host_id, classification,
                   outcome, created_at, claimed_at, reported_at, cleaned_at, lease_json,
                   report_json, report_key
              FROM gate_attempts WHERE subject_id = $subject ORDER BY attempt_number;
            """, ("$subject", subjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            attempts.Add(ReadGateView(reader));
        var latest = attempts.Last();
        var active = attempts.LastOrDefault(a => a.Attempt.State is GateStates.Claimed or GateStates.Materializing
            or GateStates.Running or GateStates.Reporting or GateStates.Cleaning);
        var queueEnd = latest.Attempt.ClaimedAt ?? UtcNow;
        return new GateStatusView(subject, attempts, queueEnd - latest.Attempt.CreatedAt,
            active?.Attempt.HostId, latest.Attempt.State, attempts.Count,
            active?.Attempt.ClaimedAt?.AddSeconds(subject.Plan.OverallDeadlineSeconds)
                ?? subject.DispatchDeadline,
            attempts.LastOrDefault(a => a.Report is not null)?.Report?.TestedSha,
            GateStates.IsTerminal(latest.Attempt.State) ? latest.Attempt.Outcome : null,
            attempts.Any(a => a.Report is not null));
    }

    public async Task<IReadOnlyList<GateStatusView>> ListGateStatusesAsync(int limit, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var ids = new List<string>();
        await using (var command = Command(connection,
            "SELECT id FROM gate_subjects ORDER BY created_at DESC LIMIT $limit;",
            ("$limit", Math.Clamp(limit, 1, 200))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }
        var result = new List<GateStatusView>(ids.Count);
        foreach (var id in ids)
        {
            var status = await GetGateStatusAsync(id, ct);
            if (status is not null) result.Add(status);
        }
        return result;
    }

    public async Task<GateStatusView> CancelGateSubjectAsync(
        string subjectId, CancelGateRequest request, string actor, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Gate cancellation requires a reason.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attemptId = await ScalarAsync(connection, """
                SELECT id FROM gate_attempts WHERE subject_id = $subject
                 ORDER BY attempt_number DESC LIMIT 1;
                """, ct, transaction, ("$subject", subjectId)) as string
                ?? throw new KeyNotFoundException("Gate subject was not found.");
            var row = await ReadGateRowAsync(connection, transaction, attemptId, ct);
            if (GateStates.IsTerminal(row.Attempt.State)) return;
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = 'cancelled', outcome = 'cancelled',
                    classification = NULL, reported_at = $now WHERE id = $id;
                """, ct, transaction, ("$now", Iso(now)), ("$id", attemptId));
            await AddGateEventAsync(connection, transaction, attemptId, GateStates.Cancelled, null, now, ct);
            await AuditAsync(connection, transaction, actor, "gate.cancelled", "gate-subject",
                subjectId, JsonSerializer.Serialize(new { request.Reason }), ct);
        }, ct);
        return await GetGateStatusAsync(subjectId, ct)
            ?? throw new KeyNotFoundException("Gate subject was not found.");
    }

    public async Task<GateStatusView> RecoverGateAfterHostLossAsync(
        string attemptId, GateContainmentReceipt receipt, string actor, CancellationToken ct)
    {
        RequireWritable();
        if (!receipt.NoProcesses || !receipt.WorkspaceAbsent
            || string.IsNullOrWhiteSpace(receipt.IdempotencyKey)
            || receipt.ObservedAt > UtcNow.AddMinutes(2)
            || receipt.ObservedAt < UtcNow.AddMinutes(-5))
            throw new ArgumentException("Gate recovery requires fresh positive containment proof.");
        string? subjectId = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadGateRowAsync(connection, transaction, attemptId, ct);
            subjectId = row.Attempt.SubjectId;
            if (!MatchesGateAuthority(row.Lease, receipt.PreviousAuthority)
                || row.Lease!.ResourceNamespace != receipt.ResourceNamespace)
                throw new TaskServerConflictException("gate-stale-fence", "Gate recovery lease or namespace is stale.");
            await using (var command = Command(connection,
                "SELECT host_id, instance_id, status FROM runners WHERE id = $executor;",
                transaction, ("$executor", receipt.PreviousAuthority.ExecutorId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)
                    || reader.GetString(0) != row.Lease.HostId
                    || reader.GetString(1) != receipt.CurrentInstanceId
                    || reader.GetString(2) != "active")
                    throw new TaskServerConflictException("gate-recovery-host-mismatch",
                        "Containment proof must come from the registered replacement on the original host.");
            }
            if (receipt.CurrentInstanceId == row.Lease.InstanceId)
                throw new TaskServerConflictException("gate-recovery-instance-unchanged",
                    "Gate recovery requires a replacement host process instance.");
            if (row.Report is not null) return;
            var newest = Convert.ToInt32(await ScalarAsync(connection, """
                SELECT MAX(attempt_number) FROM gate_attempts WHERE subject_id = $subject;
                """, ct, transaction, ("$subject", subjectId)));
            if (newest > row.Attempt.AttemptNumber) return;
            if (row.Attempt.State == GateStates.InfraRetry
                && row.Attempt.FailureClassification == GateClassifications.LostLease
                && row.Attempt.ReportedAt >= UtcNow.AddMinutes(-5))
            {
                // The expired lease is waiting for positive containment proof.
            }
            else if (row.Attempt.State is not (GateStates.Claimed or GateStates.Materializing
                or GateStates.Running or GateStates.Reporting or GateStates.Cleaning))
                throw new TaskServerConflictException("gate-recovery-state-conflict",
                    "Gate attempt cannot be recovered from this state.");
            var subject = await ReadGateSubjectAsync(connection, transaction, subjectId, ct)
                ?? throw new KeyNotFoundException("Gate subject was not found.");
            var now = UtcNow;
            var retry = row.Attempt.AttemptNumber <= subject.RetryBudget;
            var state = retry ? GateStates.InfraRetry : GateStates.InfraFailed;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $state, outcome = 'GateInfra',
                    classification = 'LostLease', reported_at = $now, cleaned_at = $now
                 WHERE id = $id;
                """, ct, transaction, ("$state", state), ("$now", Iso(now)), ("$id", attemptId));
            await AddGateEventAsync(connection, transaction, attemptId, state,
                GateClassifications.LostLease, now, ct);
            if (retry)
                await InsertGateAttemptAsync(connection, transaction, subjectId,
                    row.Attempt.AttemptNumber + 1, now, ct);
            await AuditAsync(connection, transaction, actor, "gate.host-loss-contained", "gate-attempt",
                attemptId, JsonSerializer.Serialize(new
                {
                    receipt.CurrentInstanceId,
                    receipt.ResourceNamespace,
                    receipt.ObservedAt,
                    receipt.IdempotencyKey,
                    retry,
                }), ct);
        }, ct);
        return await GetGateStatusAsync(subjectId!, ct)
            ?? throw new KeyNotFoundException("Gate subject was not found.");
    }

    public async Task ReconcileGateDeadlinesAsync(CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync((connection, transaction) =>
            ReconcileGateDeadlinesAsync(connection, transaction, UtcNow, ct), ct);
    }

    private static bool GatePhaseAllowed(string current, string next)
        => current == next || (current, next) is
            (GateStates.Claimed, GateStates.Materializing) or
            (GateStates.Claimed, GateStates.Reporting) or
            (GateStates.Materializing, GateStates.Running) or
            (GateStates.Materializing, GateStates.Reporting) or
            (GateStates.Running, GateStates.Reporting) or
            (GateStates.Reporting, GateStates.Cleaning);

    private void ValidateGateSubject(CreateGateSubjectRequest request)
    {
        if (request.Plan.GateId != "post-build-test-gate" || request.Plan.Version <= 0
            || request.Plan.Commands.Count is < 1 or > 16
            || request.Plan.OverallDeadlineSeconds is < 1 or > 14400
            || request.Plan.MaxOutputBytes is < 1 or > 10_000_000
            || request.Plan.RequiredCapabilities.Count > 16
            || request.Plan.RequiredCapabilities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)
            || request.RetryBudget is < 0 or > 3
            || request.DispatchDeadline <= UtcNow
            || string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.SourceRunId)
            || string.IsNullOrWhiteSpace(request.RepositoryId)
            || (string.IsNullOrWhiteSpace(request.RepositoryUrl) && string.IsNullOrWhiteSpace(request.SourceBundleId))
            || (!string.IsNullOrWhiteSpace(request.RepositoryUrl)
                && RepositoryIdentityContract.FromUrl(request.RepositoryUrl) != request.RepositoryId)
            || string.IsNullOrWhiteSpace(request.ExpectedSha)
            || (string.IsNullOrWhiteSpace(request.ResultRef) && string.IsNullOrWhiteSpace(request.SourceBundleId))
            || (!string.IsNullOrWhiteSpace(request.SourceBundleId) && string.IsNullOrWhiteSpace(request.SourceBundleSha256))
            || string.IsNullOrWhiteSpace(request.PolicyHash)
            || string.IsNullOrWhiteSpace(request.TestSelectionAuditDigest)
            || request.PipelineDefinitionVersion < 0
            || request.Plan.CleanupPolicy != "always")
            throw new ArgumentException("Gate subject requires a bounded catalogued plan and exact source facts.");
        if (request.Plan.Commands.Any(c => string.IsNullOrWhiteSpace(c.StepId)
            || string.IsNullOrWhiteSpace(c.FileName) || c.DeadlineSeconds is < 1 or > 7200
            || c.StepId.Length > 128 || c.FileName.Length > 512
            || c.Arguments.Count > 64 || c.Arguments.Any(arg => arg.Length > 4096)
            || Path.IsPathRooted(c.WorkingSubdirectory)
            || c.WorkingSubdirectory.Replace('\\', '/').Split('/').Contains(".."))
            || Path.IsPathRooted(request.Plan.WorkingSubdirectory)
            || request.Plan.WorkingSubdirectory.Replace('\\', '/').Split('/').Contains("..")
            || request.Plan.Commands.Select(command => command.StepId).Distinct(StringComparer.Ordinal).Count()
               != request.Plan.Commands.Count)
            throw new ArgumentException("Gate command path or deadline is invalid.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request.Plan, GateJson)))).ToLowerInvariant();
        if (!string.Equals(hash, request.PlanHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Gate plan hash does not match the frozen plan.");
    }

    private static void ValidateGateReport(GateSubject subject, GateReport report)
    {
        if (report.TestedSha is not null
            && !string.Equals(subject.ExpectedSha, report.TestedSha, StringComparison.OrdinalIgnoreCase))
            throw new TaskServerConflictException("gate-tested-sha-mismatch", "Gate report tested SHA differs from its immutable subject.");
        if ((report.Outcome is "passed" or "product-failed"
                && (string.IsNullOrWhiteSpace(report.TestedSha) || string.IsNullOrWhiteSpace(report.TestedTree)))
            || report.Commands.Count > subject.Plan.Commands.Count
            || report.Commands.Any(c => c.OutputBytes > subject.Plan.MaxOutputBytes)
            || report.OutputDigests.Count > subject.Plan.Commands.Count
            || report.ArtifactDigests.Count > 32
            || report.Commands.Where((command, index) => command.StepId != subject.Plan.Commands[index].StepId).Any()
            || (report.Outcome == "passed" && report.Commands.Count != subject.Plan.Commands.Count)
            || report.CleanupStatus is not ("complete" or "failed"))
            throw new ArgumentException("Gate report lacks bounded tree, command or cleanup evidence.");
    }

    private static bool SameGateSubject(GateSubject subject, CreateGateSubjectRequest request)
        => subject.TaskId == request.TaskId && subject.SourceRunId == request.SourceRunId
           && subject.RepositoryId == request.RepositoryId && subject.RepositoryUrl == request.RepositoryUrl
           && string.Equals(subject.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
           && subject.ResultRef == request.ResultRef && subject.SourceBundleId == request.SourceBundleId
           && subject.SourceBundleSha256 == request.SourceBundleSha256 && subject.PolicyHash == request.PolicyHash
           && subject.PipelineDefinitionVersion == request.PipelineDefinitionVersion
           && subject.TestSelectionAuditDigest == request.TestSelectionAuditDigest
           && subject.RetryBudget == request.RetryBudget;

    private static async Task<GateSubject?> ReadGateSubjectByKeyAsync(SqliteConnection connection,
        SqliteTransaction transaction, string run, string gate, string plan, CancellationToken ct)
    {
        var json = await ScalarAsync(connection, """
            SELECT subject_json FROM gate_subjects
             WHERE source_run_id = $run AND gate_id = $gate AND plan_hash = $plan;
            """, ct, transaction, ("$run", run), ("$gate", gate), ("$plan", plan));
        return json is null ? null : JsonSerializer.Deserialize<GateSubject>((string)json, GateJson);
    }

    private static async Task<GateSubject?> ReadGateSubjectAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        var json = await ScalarAsync(connection, "SELECT subject_json FROM gate_subjects WHERE id = $id;",
            ct, transaction, ("$id", id));
        return json is null ? null : JsonSerializer.Deserialize<GateSubject>((string)json, GateJson);
    }

    private static async Task InsertGateAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        string subjectId, int number, DateTime now, CancellationToken ct)
    {
        var id = $"gat_{Guid.NewGuid():N}";
        await ExecuteAsync(connection, """
            INSERT INTO gate_attempts(id, subject_id, attempt_number, state, created_at)
            VALUES ($id, $subject, $number, 'queued', $now);
            """, ct, transaction, ("$id", id), ("$subject", subjectId), ("$number", number), ("$now", Iso(now)));
        await AddGateEventAsync(connection, transaction, id, GateStates.Queued, null, now, ct);
    }

    private static Task AddGateEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string attemptId, string state, string? classification, DateTime now, CancellationToken ct)
        => ExecuteAsync(connection, """
            INSERT INTO gate_events(attempt_id, state, classification, occurred_at)
            VALUES ($id, $state, $classification, $now);
            """, ct, transaction, ("$id", attemptId), ("$state", state),
            ("$classification", classification), ("$now", Iso(now)));

    private static async Task<string> ReadGateExecutorAsync(SqliteConnection connection,
        SqliteTransaction transaction, string executorId, string instanceId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT host_id, instance_id, capabilities_json, status FROM runners WHERE id = $id;",
            transaction, ("$id", executorId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Gate executor was not found.");
        if (reader.GetString(1) != instanceId || reader.GetString(3) != "active")
            throw new TaskServerConflictException("gate-executor-stale", "Gate executor instance is not active.");
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(2), GateJson) ?? [];
        if (!capabilities.Contains("gate-executor", StringComparer.Ordinal))
            throw new TaskServerConflictException("gate-executor-required", "Runner has no gate executor role.");
        return reader.GetString(0);
    }

    private async Task<long> GateAuthorityEpochAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var value = await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'gate_authority_epoch';", ct, transaction);
        return value is null ? 1 : Convert.ToInt64(value);
    }

    private static GateAttempt ReadGateAttempt(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : Parse(reader.GetString(11)));

    private static GateAttemptView ReadGateView(SqliteDataReader reader)
        => new(ReadGateAttempt(reader),
            reader.IsDBNull(12) ? null : JsonSerializer.Deserialize<GateLease>(reader.GetString(12), GateJson),
            reader.IsDBNull(13) ? null : JsonSerializer.Deserialize<GateReport>(reader.GetString(13), GateJson));

    private static async Task<(GateAttempt Attempt, GateLease? Lease, GateReport? Report, string? ReportKey)> ReadGateRowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string attemptId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, subject_id, attempt_number, state, executor_id, host_id, classification,
                   outcome, created_at, claimed_at, reported_at, cleaned_at, lease_json,
                   report_json, report_key FROM gate_attempts WHERE id = $id;
            """, transaction, ("$id", attemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Gate attempt was not found.");
        var view = ReadGateView(reader);
        return (view.Attempt, view.Lease, view.Report, reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private GateLease RequireGateAuthority(
        (GateAttempt Attempt, GateLease? Lease, GateReport? Report, string? ReportKey) row,
        GateAuthorityRequest request)
    {
        var lease = row.Lease;
        if (GateStates.IsTerminal(row.Attempt.State) || row.Attempt.State == GateStates.InfraRetry
            || lease is null || !MatchesGateAuthority(lease, request)
            || lease.ExpiresAt <= UtcNow)
            throw new TaskServerConflictException("gate-stale-fence", "Gate lease, fence or authority epoch is stale.");
        return lease;
    }

    private static bool MatchesGateAuthority(GateLease? lease, GateAuthorityRequest request)
        => lease is not null && lease.LeaseId == request.LeaseId
           && lease.ExecutorId == request.ExecutorId && lease.InstanceId == request.InstanceId
           && lease.Fence == request.Fence && lease.AuthorityEpoch == request.AuthorityEpoch;

    private static async Task ReconcileGateDeadlinesAsync(SqliteConnection connection,
        SqliteTransaction transaction, DateTime now, CancellationToken ct)
    {
        var overdue = new List<(string Id, string State, string Classification)>();
        await using (var command = Command(connection, """
            SELECT a.id, a.state, s.subject_json, a.expires_at, a.claimed_at,
                   a.attempt_number
              FROM gate_attempts a JOIN gate_subjects s ON s.id = a.subject_id
             WHERE a.state IN ('queued', 'claimed', 'materializing', 'running', 'reporting', 'cleaning');
            """, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var state = reader.GetString(1);
                var subject = JsonSerializer.Deserialize<GateSubject>(reader.GetString(2), GateJson)!;
                var leaseExpiry = reader.IsDBNull(3) ? (DateTime?)null : Parse(reader.GetString(3));
                var overallExpiry = reader.IsDBNull(4) ? (DateTime?)null
                    : Parse(reader.GetString(4)).AddSeconds(subject.Plan.OverallDeadlineSeconds + 60);
                if (state == GateStates.Queued && subject.DispatchDeadline <= now)
                    overdue.Add((reader.GetString(0), state, GateClassifications.NoEligibleGateExecutor));
                else if (state != GateStates.Queued && overallExpiry <= now
                    && (leaseExpiry is null || overallExpiry <= leaseExpiry))
                    overdue.Add((reader.GetString(0), state, GateClassifications.ExecutionTimeout));
                else if (state != GateStates.Queued && leaseExpiry <= now)
                    overdue.Add((reader.GetString(0),
                        reader.GetInt32(5) <= subject.RetryBudget
                            ? GateStates.InfraRetry : GateStates.InfraFailed,
                        GateClassifications.LostLease));
            }
        }
        foreach (var item in overdue)
        {
            var classification = item.Classification;
            var terminalState = classification == GateClassifications.ExecutionTimeout
                ? GateStates.TimedOut : classification == GateClassifications.LostLease
                    ? item.State : GateStates.InfraFailed;
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = $state, outcome = 'GateInfra',
                    classification = $classification, reported_at = $now WHERE id = $id;
                """, ct, transaction, ("$state", terminalState),
                ("$classification", classification), ("$now", Iso(now)), ("$id", item.Id));
            await AddGateEventAsync(connection, transaction, item.Id, terminalState, classification, now, ct);
        }
        var abandoned = new List<string>();
        await using (var command = Command(connection, """
            SELECT a.id FROM gate_attempts a
             WHERE a.state = 'infra-retry' AND a.classification = 'LostLease'
               AND a.reported_at <= $cutoff
               AND NOT EXISTS (SELECT 1 FROM gate_attempts newer
                   WHERE newer.subject_id = a.subject_id
                     AND newer.attempt_number > a.attempt_number);
            """, transaction, ("$cutoff", Iso(now.AddMinutes(-5)))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) abandoned.Add(reader.GetString(0));
        foreach (var id in abandoned)
        {
            await ExecuteAsync(connection, """
                UPDATE gate_attempts SET state = 'infra-failed', outcome = 'GateInfra'
                 WHERE id = $id;
                """, ct, transaction, ("$id", id));
            await AddGateEventAsync(connection, transaction, id, GateStates.InfraFailed,
                GateClassifications.LostLease, now, ct);
        }
    }
}

internal static class GateReportDecision
{
    internal static (string State, string Outcome, string? Classification, bool Retry) Decide(
        GateReport report, int attemptNumber, int retryBudget)
    {
        if (report.CleanupStatus != "complete")
            return (GateStates.InfraFailed, "GateInfra", GateClassifications.CleanupFailure, false);
        if (report.DirtyBefore)
            return (GateStates.InfraFailed, "GateInfra", GateClassifications.ToolFailure, false);
        if (report.Outcome == "passed" && report.Classification is null
            && report.Commands.All(c => c.ExitCode == 0 && !c.TimedOut))
            return (GateStates.Passed, "passed", null, false);
        if (report.Classification == GateClassifications.ProductFailure
            && report.Commands.Any(c => c.ExitCode is > 0 and not (126 or 127) && !c.TimedOut))
            return (GateStates.ProductFailed, "product-failed", GateClassifications.ProductFailure, false);
        var classification = report.Classification is GateClassifications.ExecutionTimeout
            or GateClassifications.MissingSnapshot or GateClassifications.ToolFailure
            or GateClassifications.LostLease ? report.Classification : GateClassifications.ToolFailure;
        if (attemptNumber <= retryBudget)
            return (GateStates.InfraRetry, "GateInfra", classification, true);
        return (classification == GateClassifications.ExecutionTimeout ? GateStates.TimedOut : GateStates.InfraFailed,
            "GateInfra", classification, false);
    }
}
