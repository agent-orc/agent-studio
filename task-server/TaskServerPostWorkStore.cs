using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Post-work subjects, attempts and cohorts (AGT-3171 DP-S8). A backend pass
/// and a host claim are both attempts of one subject; the partial unique
/// index keeps at most one live attempt per subject. The authority records
/// evidence, proposals and outcomes; card lifecycle effects stay with the
/// backend that owns the lane.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions PostWorkJson = new(JsonSerializerDefaults.Web);
    private const int PostWorkGrantHalfCores = 2;

    private sealed record PostWorkImmutable(
        IReadOnlyList<string> RequiredCapabilities,
        IReadOnlyList<PostWorkArtifactReference> Artifacts,
        IReadOnlyList<PostWorkStep> Steps);

    private sealed record PostWorkLiveAttempt(
        string AttemptId, string Kind, string ExecutorId, string InstanceId, string? HostId,
        string LeaseId, long Fence, long AuthorityEpoch, DateTime AcquiredAt, DateTime ExpiresAt,
        long AllocationVersion, int GrantHalfCores, string State, string? ReportDigest);

    internal async Task ApplyPostWorkMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS post_work_subjects(
                id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                project_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                lane_entry_id TEXT NOT NULL,
                source_run_id TEXT,
                subject_json TEXT NOT NULL,
                policy_digest TEXT NOT NULL,
                owner TEXT NOT NULL,
                state TEXT NOT NULL,
                cohort_phase TEXT NOT NULL DEFAULT 'none',
                cohort_id TEXT,
                last_fence INTEGER NOT NULL DEFAULT 0,
                authority_epoch INTEGER NOT NULL DEFAULT 1,
                deferred_reason TEXT,
                retry_at TEXT,
                deferral_count INTEGER NOT NULL DEFAULT 0,
                decision TEXT,
                decision_reason TEXT,
                decided_by TEXT,
                decided_at TEXT,
                host_proposal TEXT,
                shadow_status TEXT NOT NULL,
                shadow_reasons_json TEXT NOT NULL,
                backend_shadow_outcome TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_post_work_one_open_subject_per_card
                ON post_work_subjects(project_id, task_id)
                WHERE state NOT IN ('applied', 'retired');
            CREATE INDEX IF NOT EXISTS ix_post_work_claim_queue
                ON post_work_subjects(owner, cohort_phase, state, created_at);
            CREATE TABLE IF NOT EXISTS post_work_attempts(
                id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL REFERENCES post_work_subjects(id),
                kind TEXT NOT NULL,
                executor_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                host_id TEXT,
                lease_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                authority_epoch INTEGER NOT NULL,
                state TEXT NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                allocation_version INTEGER NOT NULL DEFAULT 0,
                grant_half_cores INTEGER NOT NULL DEFAULT 0,
                report_json TEXT,
                report_digest TEXT,
                outcome TEXT,
                completed_at TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_post_work_one_live_attempt
                ON post_work_attempts(subject_id) WHERE state = 'live';
            CREATE INDEX IF NOT EXISTS ix_post_work_live_host
                ON post_work_attempts(host_id, state);
            CREATE TABLE IF NOT EXISTS post_work_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                subject_id TEXT,
                attempt_id TEXT,
                cohort_id TEXT,
                event TEXT NOT NULL,
                at TEXT NOT NULL,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_post_work_events_cohort ON post_work_events(cohort_id, id);
            CREATE TABLE IF NOT EXISTS post_work_cohorts(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                task_ids_json TEXT NOT NULL,
                phase TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, ct);
    }

    private string PostWorkMode => PostWorkModes.Normalize(_options.PostWorkMode);

    public async Task<PostWorkSubject> MaterializePostWorkSubjectAsync(
        MaterializePostWorkSubjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (PostWorkMode == PostWorkModes.Off)
            throw new TaskServerConflictException("post-work-disabled", "Post-work subjects are disabled on this Task Server.");
        ValidatePostWorkRequest(request);
        var key = PostWorkIdentity.IdempotencyKey(request.ProjectId, request.TaskId, request.LaneEntryId);
        var digest = PostWorkIdentity.PolicyDigest(request.RequiredCapabilities, request.Artifacts, request.Steps);
        PostWorkSubject? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadPostWorkSubjectAsync(connection, transaction, "idempotency_key", key, ct);
            if (existing is not null)
            {
                // The key names the lane entry; its first recorded inputs stay
                // authoritative. A differing replay is recorded, never applied.
                if (existing.PolicyDigest != digest || existing.SourceRunId != request.SourceRunId)
                    await PostWorkEventAsync(connection, transaction, existing.SubjectId, null, existing.CohortId,
                        "replay-differs", request.Source + ":" + digest, ct);
                result = existing;
                return;
            }
            var now = UtcNow;
            var older = new List<string>();
            await using (var command = Command(connection, """
                SELECT id FROM post_work_subjects
                 WHERE project_id = $project AND task_id = $task AND state NOT IN ('applied', 'retired');
                """, transaction, ("$project", request.ProjectId), ("$task", request.TaskId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) older.Add(reader.GetString(0));
            foreach (var olderId in older)
            {
                var live = await ReadLivePostWorkAttemptAsync(connection, transaction, olderId, ct);
                if (live is not null && live.ExpiresAt > now)
                    throw new TaskServerConflictException("post-work-card-busy",
                        "An earlier lane entry of this card still holds post-work authority.");
                await ExpirePostWorkAttemptsAsync(connection, transaction, olderId, "lost", ct);
                await SetPostWorkStateAsync(connection, transaction, olderId, PostWorkStates.Retired, ct);
                await PostWorkEventAsync(connection, transaction, olderId, null, null,
                    "retired", "lane-entry-superseded", ct);
            }
            var (shadowStatus, shadowReasons) = await EvaluatePostWorkShadowAsync(
                connection, transaction, request.ProjectId, request.RequiredCapabilities, now, ct);
            var (owner, cohortPhase, cohortId) = await CohortPlacementForAsync(
                connection, transaction, request.ProjectId, request.TaskId, ct);
            var id = $"pws_{Guid.NewGuid():N}";
            await ExecuteAsync(connection, """
                INSERT INTO post_work_subjects(id, idempotency_key, project_id, task_id, lane_entry_id,
                    source_run_id, subject_json, policy_digest, owner, state, cohort_phase, cohort_id,
                    shadow_status, shadow_reasons_json, created_at, updated_at)
                VALUES ($id, $key, $project, $task, $lane, $run, $json, $digest, $owner, 'queued',
                    $phase, $cohort, $shadow, $reasons, $now, $now);
                """, ct, transaction, ("$id", id), ("$key", key), ("$project", request.ProjectId),
                ("$task", request.TaskId), ("$lane", request.LaneEntryId), ("$run", request.SourceRunId),
                ("$json", JsonSerializer.Serialize(new PostWorkImmutable(
                    request.RequiredCapabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    request.Artifacts, request.Steps), PostWorkJson)),
                ("$digest", digest), ("$owner", owner), ("$phase", cohortPhase), ("$cohort", cohortId),
                ("$shadow", shadowStatus), ("$reasons", JsonSerializer.Serialize(shadowReasons)),
                ("$now", Iso(now)));
            await PostWorkEventAsync(connection, transaction, id, null, cohortId, "materialized",
                request.Source, ct);
            await AuditAsync(connection, transaction, actorId, "post-work.materialized", "post-work-subject",
                id, JsonSerializer.Serialize(new { key, request.ProjectId, request.TaskId, request.Source, shadowStatus }), ct);
            result = await ReadPostWorkSubjectAsync(connection, transaction, "id", id, ct);
        }, ct);
        return result!;
    }

    public async Task<PostWorkSubject?> GetPostWorkSubjectAsync(string subjectIdOrKey, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadPostWorkSubjectAsync(connection, null,
            subjectIdOrKey.StartsWith("pw_", StringComparison.Ordinal) ? "idempotency_key" : "id",
            subjectIdOrKey, ct);
    }

    public async Task<BackendPassDecision> BeginBackendPassAsync(
        BeginBackendPassRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.BackendInstanceId) || string.IsNullOrWhiteSpace(request.PassId))
            throw new ArgumentException("Backend instance and pass ids are required.");
        BackendPassDecision? decision = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var subject = await ReadPostWorkSubjectAsync(connection, transaction, "idempotency_key", request.IdempotencyKey, ct);
            if (subject is null)
            {
                decision = new BackendPassDecision(PostWorkMode != PostWorkModes.Canary, "subject-unknown");
                return;
            }
            var now = UtcNow;
            var live = await ReadLivePostWorkAttemptAsync(connection, transaction, subject.SubjectId, ct);
            var admission = PostWorkAuthorityPolicy.AdmitBackendPass(Facts(subject, live), request.BackendInstanceId, now);
            if (!admission.Admitted)
            {
                await PostWorkEventAsync(connection, transaction, subject.SubjectId, null, subject.CohortId,
                    "backend-pass-refused", admission.Reason, ct);
                // Shadow mode observes and never changes the existing queue behaviour.
                decision = new BackendPassDecision(PostWorkMode != PostWorkModes.Canary,
                    PostWorkMode == PostWorkModes.Canary ? admission.Reason : "shadow-observed:" + admission.Reason, subject);
                return;
            }
            var lease = await AcquirePostWorkAttemptAsync(connection, transaction, subject,
                PostWorkExecutorKinds.Backend, "backend", request.BackendInstanceId, null,
                Math.Clamp(request.LeaseSeconds, 60, 4 * 3600), 0, 0, now, ct);
            if (subject.State != PostWorkStates.Reported)
                await SetPostWorkStateAsync(connection, transaction, subject.SubjectId, PostWorkStates.Running, ct);
            await PostWorkEventAsync(connection, transaction, subject.SubjectId, lease.AttemptId, subject.CohortId,
                "backend-pass-started", admission.Reason + ";pass=" + request.PassId, ct);
            await AuditAsync(connection, transaction, actorId, "post-work.backend-pass", "post-work-subject",
                subject.SubjectId, JsonSerializer.Serialize(new { lease.Fence, admission.Reason, request.PassId }), ct);
            decision = new BackendPassDecision(true, admission.Reason,
                await ReadPostWorkSubjectAsync(connection, transaction, "id", subject.SubjectId, ct), lease);
        }, ct);
        return decision!;
    }

    public async Task<PostWorkSubject> CompleteBackendPassAsync(
        string idempotencyKey, CompleteBackendPassRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (!PostWorkOutcomes.IsKnown(request.Outcome))
            throw new ArgumentException("Backend pass outcome must be decided, deferred or blocked.");
        PostWorkSubject? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var subject = await ReadPostWorkSubjectAsync(connection, transaction, "idempotency_key", idempotencyKey, ct)
                ?? throw new KeyNotFoundException("Post-work subject was not found.");
            var attempt = await ReadPostWorkAttemptByLeaseAsync(connection, transaction, request.LeaseId, ct);
            if (attempt is null || attempt.Kind != PostWorkExecutorKinds.Backend
                || attempt.InstanceId != request.BackendInstanceId || attempt.Fence != request.Fence)
                throw new TaskServerConflictException("stale-fence", "Backend pass lease does not match current authority.");
            if (attempt.State == "completed" && subject.State is PostWorkStates.Applied)
            {
                result = subject;
                return;
            }
            if (attempt.State != "live" || attempt.Fence != subject.LastFence)
                throw new TaskServerConflictException("post-work-lease-lost",
                    "The backend pass lost post-work authority; its outcome is not recorded.");
            await ApplyPostWorkOutcomeAsync(connection, transaction, subject, attempt.AttemptId,
                request.Outcome, request.Reason, request.RetryAt,
                subject.State == PostWorkStates.Reported ? "server-apply" : "backend", ct);
            if (PostWorkMode != PostWorkModes.Canary)
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET backend_shadow_outcome = $outcome WHERE id = $id;
                    """, ct, transaction, ("$outcome", request.Outcome + ":" + Bound(request.Reason, 160)),
                    ("$id", subject.SubjectId));
            await AuditAsync(connection, transaction, actorId, "post-work.backend-pass-completed",
                "post-work-subject", subject.SubjectId,
                JsonSerializer.Serialize(new { request.Outcome, request.Reason, attempt.Fence }), ct);
            result = await ReadPostWorkSubjectAsync(connection, transaction, "id", subject.SubjectId, ct);
        }, ct);
        return result!;
    }

    public async Task<PostWorkClaimResponse> ClaimPostWorkAsync(
        PostWorkClaimRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        if (PostWorkMode != PostWorkModes.Canary)
            return new PostWorkClaimResponse("empty", Message: "Post-work claims run in shadow mode.",
                RejectedReasons: ["post-work-shadow"]);
        PostWorkClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            string hostId;
            await using (var command = Command(connection, """
                SELECT host_id, instance_id, status, capabilities_json FROM runners WHERE id = $id;
                """, transaction, ("$id", request.ExecutorId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Post-work executor is not registered.");
                if (reader.GetString(1) != request.InstanceId || reader.GetString(2) != "active")
                    throw new TaskServerConflictException("runner-instance-stale", "Post-work executor instance is not active.");
                var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
                if (!capabilities.Contains(PostWorkCapabilities.Executor, StringComparer.Ordinal))
                    throw new TaskServerConflictException("post-work-capability-required", "Runner is not a post-work executor.");
                hostId = reader.GetString(0);
            }
            var now = UtcNow;
            // Lease loss returns the subject to the claim queue and frees the host grant.
            await ExecuteAsync(connection, """
                UPDATE post_work_attempts SET state = 'lost', completed_at = $now
                 WHERE state = 'live' AND kind = 'host' AND expires_at <= $now;
                """, ct, transaction, ("$now", Iso(now)));
            var host = await ReadHostPlacementAsync(connection, transaction, hostId, ct);
            var capacity = await ReadRuntimeCapacitySettingsAsync(connection, transaction, hostId, ct);
            var acknowledged = capacity is not null
                               && request.RuntimeCapacityAppliedVersion == capacity.AllocationVersion;
            var occupied = await CountOccupiedHostSlotsAsync(connection, transaction, hostId, ct);
            var grantFree = capacity is not null && occupied < capacity.MaxParallelism;
            var candidates = new List<PostWorkSubject>();
            await using (var command = Command(connection, """
                SELECT id FROM post_work_subjects
                 WHERE owner = 'host' AND cohort_phase = 'host'
                   AND state IN ('queued', 'waiting', 'running')
                 ORDER BY created_at LIMIT 64;
                """, transaction))
            {
                var ids = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
                foreach (var id in ids)
                    if (await ReadPostWorkSubjectAsync(connection, transaction, "id", id, ct) is { } item)
                        candidates.Add(item);
            }
            var rejected = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var subject in candidates)
            {
                var live = await ReadLivePostWorkAttemptAsync(connection, transaction, subject.SubjectId, ct);
                var admission = PostWorkAuthorityPolicy.AdmitHostClaim(Facts(subject, live), now);
                if (!admission.Admitted)
                {
                    rejected.Add(admission.Reason);
                    continue;
                }
                var placement = HostPlacementPolicy.Decide(host, "post", subject.ProjectId,
                    subject.RequiredCapabilities, subject.CreatedAt, now, TimeSpan.Zero, false,
                    acknowledged, grantFree);
                if (!placement.Eligible)
                {
                    foreach (var reason in placement.Rejected) rejected.Add(reason);
                    continue;
                }
                var lease = await AcquirePostWorkAttemptAsync(connection, transaction, subject,
                    PostWorkExecutorKinds.Host, request.ExecutorId, request.InstanceId, hostId,
                    NormalizeTtl(request.RequestedTtlSeconds), capacity!.AllocationVersion,
                    PostWorkGrantHalfCores, now, ct);
                await SetPostWorkStateAsync(connection, transaction, subject.SubjectId, PostWorkStates.Running, ct);
                await PostWorkEventAsync(connection, transaction, subject.SubjectId, lease.AttemptId, subject.CohortId,
                    "host-claimed", hostId, ct);
                await AuditAsync(connection, transaction, actorId, "post-work.claimed", "post-work-attempt",
                    lease.AttemptId, JsonSerializer.Serialize(new { lease.Fence, hostId, capacity.AllocationVersion }), ct);
                response = new PostWorkClaimResponse("claimed",
                    await ReadPostWorkSubjectAsync(connection, transaction, "id", subject.SubjectId, ct), lease);
                return;
            }
            response = new PostWorkClaimResponse("empty", Message: "No eligible post-work subject.",
                RejectedReasons: rejected.ToArray());
        }, ct);
        return response!;
    }

    public async Task<PostWorkLease> RenewPostWorkAsync(
        string attemptId, PostWorkRenewRequest request, CancellationToken ct)
    {
        RequireWritable();
        PostWorkLease? lease = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (attempt, subject) = await ReadFencedPostWorkAttemptAsync(connection, transaction, attemptId, ct);
            var refusal = PostWorkAuthorityPolicy.RefuseFencedCall(request.Authority, attempt.State,
                attempt.ExecutorId, attempt.InstanceId, attempt.LeaseId, attempt.Fence, subject.LastFence,
                attempt.AuthorityEpoch, attempt.ExpiresAt, UtcNow);
            if (refusal is not null)
                throw new TaskServerConflictException(refusal, "Post-work lease is no longer current.");
            var expiry = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, "UPDATE post_work_attempts SET expires_at = $expiry WHERE id = $id;",
                ct, transaction, ("$expiry", Iso(expiry)), ("$id", attemptId));
            lease = ToLease(attempt with { ExpiresAt = expiry }, subject.SubjectId);
        }, ct);
        return lease!;
    }

    public async Task<PostWorkSubject> ReportPostWorkAsync(
        string attemptId, SubmitPostWorkReportRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ValidatePostWorkReport(request.Report);
        var digest = PostWorkIdentity.ReportDigest(request.Report);
        PostWorkSubject? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (attempt, subject) = await ReadFencedPostWorkAttemptAsync(connection, transaction, attemptId, ct);
            if (attempt.ReportDigest is not null)
            {
                if (attempt.ReportDigest != digest)
                    throw new TaskServerConflictException("post-work-report-conflict",
                        "A different report was already accepted for this attempt.");
                result = subject;
                return;
            }
            var now = UtcNow;
            var refusal = PostWorkAuthorityPolicy.RefuseFencedCall(request.Authority, attempt.State,
                attempt.ExecutorId, attempt.InstanceId, attempt.LeaseId, attempt.Fence, subject.LastFence,
                attempt.AuthorityEpoch, attempt.ExpiresAt, now);
            if (refusal is not null)
            {
                await PostWorkEventAsync(connection, transaction, subject.SubjectId, attemptId, subject.CohortId,
                    "late-report-refused", refusal, ct);
                throw new TaskServerConflictException(refusal, "Post-work lease is no longer current; the report is inert.");
            }
            await ExecuteAsync(connection, """
                UPDATE post_work_attempts SET state = 'reported', report_json = $report, report_digest = $digest,
                    outcome = $outcome, completed_at = $now WHERE id = $id;
                """, ct, transaction, ("$report", JsonSerializer.Serialize(request.Report, PostWorkJson)),
                ("$digest", digest), ("$outcome", request.Report.ProposedOutcome), ("$now", Iso(now)),
                ("$id", attemptId));
            var proposal = request.Report.ProposedOutcome + ":" + Bound(request.Report.Reason, 160);
            if (request.Report.ProposedOutcome == PostWorkOutcomes.Deferred)
            {
                var retryAt = request.Report.RetryAt is { } requested && requested > now
                    ? requested.ToUniversalTime() : now.AddSeconds(60);
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET state = 'waiting', deferred_reason = $reason, retry_at = $retry,
                        deferral_count = deferral_count + 1, host_proposal = $proposal, updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$reason", Bound(request.Report.Reason, 160)), ("$retry", Iso(retryAt)),
                    ("$proposal", proposal), ("$now", Iso(now)), ("$id", subject.SubjectId));
            }
            else
            {
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET state = 'reported', host_proposal = $proposal, updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$proposal", proposal), ("$now", Iso(now)), ("$id", subject.SubjectId));
            }
            await PostWorkEventAsync(connection, transaction, subject.SubjectId, attemptId, subject.CohortId,
                "host-reported", proposal, ct);
            await AuditAsync(connection, transaction, actorId, "post-work.reported", "post-work-attempt", attemptId,
                JsonSerializer.Serialize(new { request.Report.ProposedOutcome, request.Report.ExecutorKind, attempt.Fence }), ct);
            result = await ReadPostWorkSubjectAsync(connection, transaction, "id", subject.SubjectId, ct);
        }, ct);
        return result!;
    }

    public async Task<ReconcilePostWorkResponse> ReconcilePostWorkAsync(
        ReconcilePostWorkRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.BackendInstanceId))
            throw new ArgumentException("Backend instance id is required.");
        var actions = new List<PostWorkReconcileAction>();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            // The monolith is the only backend client. A live pass of an earlier
            // process instance died with that process.
            await ExecuteAsync(connection, """
                UPDATE post_work_attempts SET state = 'lost', completed_at = $now
                 WHERE state = 'live' AND kind = 'backend' AND instance_id <> $instance;
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$instance", request.BackendInstanceId));
            foreach (var projectId in request.ProjectIds.Distinct(StringComparer.Ordinal))
            {
                var facts = request.LaneFacts.Where(fact => fact.ProjectId == projectId).ToArray();
                actions.AddRange(await ReconcileSubjectsAsync(connection, transaction,
                    "project_id = $scope", projectId, facts, null, ct));
            }
            await AuditAsync(connection, transaction, actorId, "post-work.reconciled", "post-work", "backend",
                JsonSerializer.Serialize(new { request.BackendInstanceId, actions = actions.Count }), ct);
        }, ct);
        return new ReconcilePostWorkResponse(actions);
    }

    public async Task<PostWorkCohortStatus> StartPostWorkCohortAsync(
        StartPostWorkCohortRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (PostWorkMode != PostWorkModes.Canary)
            throw new TaskServerConflictException("post-work-canary-required", "Cohorts require TaskServer:PostWorkMode=canary.");
        if (string.IsNullOrWhiteSpace(request.CohortId) || request.CohortId.Length > 80
            || string.IsNullOrWhiteSpace(request.ProjectId)
            || request.TaskIds.Count is < 1 or > 50
            || request.TaskIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A cohort needs an id, one project and 1 to 50 task ids.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            var taskIds = request.TaskIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var existing = await ScalarAsync(connection, "SELECT task_ids_json FROM post_work_cohorts WHERE id = $id;",
                ct, transaction, ("$id", request.CohortId)) as string;
            if (existing is not null)
            {
                if (existing != JsonSerializer.Serialize(taskIds))
                    throw new TaskServerConflictException("post-work-cohort-conflict", "Cohort replay differs.");
                return;
            }
            foreach (var taskId in taskIds)
            {
                var other = await ScalarAsync(connection, """
                    SELECT c.id FROM post_work_cohorts c, json_each(c.task_ids_json) t
                     WHERE c.project_id = $project AND t.value = $task AND c.phase <> 'rolled-back';
                    """, ct, transaction, ("$project", request.ProjectId), ("$task", taskId)) as string;
                if (other is not null)
                    throw new TaskServerConflictException("post-work-cohort-overlap", $"Task {taskId} is in cohort {other}.");
            }
            await ExecuteAsync(connection, """
                INSERT INTO post_work_cohorts(id, project_id, task_ids_json, phase, created_at, updated_at)
                VALUES ($id, $project, $tasks, 'draining', $now, $now);
                UPDATE post_work_subjects SET cohort_id = $id, cohort_phase = 'draining', updated_at = $now
                 WHERE project_id = $project AND task_id IN (SELECT value FROM json_each($tasks))
                   AND state NOT IN ('applied', 'retired');
                """, ct, transaction, ("$id", request.CohortId), ("$project", request.ProjectId),
                ("$tasks", JsonSerializer.Serialize(taskIds)), ("$now", now));
            await PostWorkEventAsync(connection, transaction, null, null, request.CohortId,
                "cohort-admissions-stopped", string.Join(",", taskIds), ct);
            await AuditAsync(connection, transaction, actorId, "post-work.cohort-started", "post-work-cohort",
                request.CohortId, JsonSerializer.Serialize(new { request.ProjectId, taskIds }), ct);
        }, ct);
        return (await GetPostWorkCohortAsync(request.CohortId, ct))!;
    }

    /// <summary>
    /// Converts drained cohort subjects to host ownership. A subject whose
    /// backend pass is still live stays draining; the cohort admits host claims
    /// only after every subject is reconciled.
    /// </summary>
    public async Task<PostWorkCohortStatus> AdvancePostWorkCohortAsync(
        string cohortId, AdvancePostWorkCohortRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var phase = await ScalarAsync(connection, "SELECT phase FROM post_work_cohorts WHERE id = $id;",
                ct, transaction, ("$id", cohortId)) as string
                ?? throw new KeyNotFoundException("Post-work cohort was not found.");
            if (phase != PostWorkCohortPhases.Draining) return;
            var now = UtcNow;
            var draining = 0;
            foreach (var subject in await ReadCohortSubjectsAsync(connection, transaction, cohortId, ct))
            {
                if (subject.CohortPhase != PostWorkCohortPhases.Draining) continue;
                var live = await ReadLivePostWorkAttemptAsync(connection, transaction, subject.SubjectId, ct);
                var lane = request.LaneFacts.FirstOrDefault(fact =>
                    fact.ProjectId == subject.ProjectId && fact.TaskId == subject.TaskId);
                var conversion = PostWorkAuthorityPolicy.CohortConversion(Facts(subject, live), lane, now);
                switch (conversion)
                {
                    case "draining":
                        draining++;
                        break;
                    case "retire":
                        await SetPostWorkStateAsync(connection, transaction, subject.SubjectId, PostWorkStates.Retired, ct);
                        await PostWorkEventAsync(connection, transaction, subject.SubjectId, null, cohortId, "retired", "card-left-lane", ct);
                        break;
                    case "mark-applied":
                        await MarkPostWorkAppliedFromLaneAsync(connection, transaction, subject.SubjectId, cohortId, ct);
                        break;
                    case "host":
                        await ExpirePostWorkAttemptsAsync(connection, transaction, subject.SubjectId, "lost", ct);
                        await ExecuteAsync(connection, """
                            UPDATE post_work_subjects SET owner = 'host', cohort_phase = 'host',
                                state = CASE WHEN state IN ('running', 'blocked') THEN 'queued' ELSE state END,
                                updated_at = $now
                             WHERE id = $id;
                            """, ct, transaction, ("$now", Iso(now)), ("$id", subject.SubjectId));
                        await PostWorkEventAsync(connection, transaction, subject.SubjectId, null, cohortId,
                            "converted-to-host", subject.DeferredReason, ct);
                        break;
                }
            }
            if (draining == 0)
            {
                await ExecuteAsync(connection, """
                    UPDATE post_work_cohorts SET phase = 'host', updated_at = $now WHERE id = $id;
                    """, ct, transaction, ("$now", Iso(now)), ("$id", cohortId));
                await PostWorkEventAsync(connection, transaction, null, null, cohortId, "host-claims-admitted", null, ct);
                await AuditAsync(connection, transaction, actorId, "post-work.cohort-admitted", "post-work-cohort",
                    cohortId, "{}", ct);
            }
        }, ct);
        return (await GetPostWorkCohortAsync(cohortId, ct))!;
    }

    /// <summary>
    /// Returns cohort subjects to the backend queue. A live host lease is
    /// revoked and its fence advanced, so a late host report is inert; the
    /// returned actions name which cards the backend must re-enqueue.
    /// </summary>
    public async Task<PostWorkCohortStatus> RollbackPostWorkCohortAsync(
        string cohortId, RollbackPostWorkCohortRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var actions = new List<PostWorkReconcileAction>();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var projectId = await ScalarAsync(connection, "SELECT project_id FROM post_work_cohorts WHERE id = $id;",
                ct, transaction, ("$id", cohortId)) as string
                ?? throw new KeyNotFoundException("Post-work cohort was not found.");
            var now = Iso(UtcNow);
            foreach (var subject in await ReadCohortSubjectsAsync(connection, transaction, cohortId, ct))
            {
                if (PostWorkStates.IsClosed(subject.State)) continue;
                var live = await ReadLivePostWorkAttemptAsync(connection, transaction, subject.SubjectId, ct);
                if (live is not null && live.Kind != PostWorkExecutorKinds.Backend)
                    await PostWorkEventAsync(connection, transaction, subject.SubjectId, live.AttemptId, cohortId,
                        "host-lease-revoked", request.Reason, ct);
                if (live is not null && live.Kind != PostWorkExecutorKinds.Backend)
                    await ExpirePostWorkAttemptsAsync(connection, transaction, subject.SubjectId, "revoked", ct);
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET owner = 'backend', cohort_phase = 'rolled-back',
                        last_fence = last_fence + 1, authority_epoch = authority_epoch + 1,
                        state = CASE WHEN state IN ('running', 'reported') THEN 'queued' ELSE state END,
                        updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$now", now), ("$id", subject.SubjectId));
            }
            actions.AddRange(await ReconcileSubjectsAsync(connection, transaction, "cohort_id = $scope",
                cohortId, request.LaneFacts.Where(fact => fact.ProjectId == projectId).ToArray(), cohortId, ct));
            await ExecuteAsync(connection, """
                UPDATE post_work_cohorts SET phase = 'rolled-back', updated_at = $now WHERE id = $id;
                """, ct, transaction, ("$now", now), ("$id", cohortId));
            await PostWorkEventAsync(connection, transaction, null, null, cohortId, "rolled-back",
                Bound(request.Reason, 200) + ";requeue=" + actions.Count(action =>
                    action.Action == PostWorkReconcileActions.RequeueBackend), ct);
            await AuditAsync(connection, transaction, actorId, "post-work.cohort-rolled-back", "post-work-cohort",
                cohortId, JsonSerializer.Serialize(new { request.Reason, actions = actions.Count }), ct);
        }, ct);
        var status = (await GetPostWorkCohortAsync(cohortId, ct))!;
        return status with { Actions = actions };
    }

    public async Task<PostWorkCohortStatus?> GetPostWorkCohortAsync(string cohortId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        string projectId, phase, taskIdsJson;
        await using (var command = Command(connection,
            "SELECT project_id, phase, task_ids_json FROM post_work_cohorts WHERE id = $id;", ("$id", cohortId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            (projectId, phase, taskIdsJson) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        var subjects = await ReadCohortSubjectsAsync(connection, null, cohortId, ct);
        var record = new List<PostWorkCohortEvent>();
        await using (var command = Command(connection, """
            SELECT at, event, subject_id, detail FROM post_work_events WHERE cohort_id = $id ORDER BY id LIMIT 500;
            """, ("$id", cohortId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                record.Add(new PostWorkCohortEvent(Parse(reader.GetString(0)), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return new PostWorkCohortStatus(cohortId, projectId,
            JsonSerializer.Deserialize<string[]>(taskIdsJson) ?? [], phase,
            subjects.Count(subject => subject.CohortPhase == PostWorkCohortPhases.Draining && !PostWorkStates.IsClosed(subject.State)),
            subjects.Count(subject => subject.Owner == PostWorkOwners.Host && !PostWorkStates.IsClosed(subject.State)),
            subjects.Count(subject => PostWorkStates.IsClosed(subject.State)),
            [], record);
    }

    public async Task<PostWorkShadowSummary> GetPostWorkShadowSummaryAsync(int limit, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        int total = 0, eligible = 0, rejectedCount = 0, decided = 0, deferred = 0, differences = 0;
        await using (var command = Command(connection, """
            SELECT shadow_status, backend_shadow_outcome FROM post_work_subjects;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                total++;
                var shadow = reader.GetString(0);
                var outcome = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (shadow == "eligible") eligible++; else rejectedCount++;
                if (outcome?.StartsWith(PostWorkOutcomes.Decided + ":", StringComparison.Ordinal) == true) decided++;
                if (outcome?.StartsWith(PostWorkOutcomes.Deferred + ":", StringComparison.Ordinal) == true) deferred++;
                // A card the backend processed that no advertised host could
                // have claimed is the difference canary must close first.
                if (outcome is not null && shadow != "eligible") differences++;
            }
        }
        var recent = new List<PostWorkSubject>();
        var ids = new List<string>();
        await using (var command = Command(connection,
            "SELECT id FROM post_work_subjects ORDER BY updated_at DESC LIMIT $limit;", ("$limit", Math.Clamp(limit, 1, 200))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        foreach (var id in ids)
            if (await ReadPostWorkSubjectAsync(connection, null, "id", id, ct) is { } subject) recent.Add(subject);
        return new PostWorkShadowSummary(total, eligible, rejectedCount, decided, deferred, differences, recent);
    }

    private async Task<List<PostWorkReconcileAction>> ReconcileSubjectsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string scopeClause, string scope,
        IReadOnlyList<PostWorkLaneFact> facts, string? cohortId, CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var command = Command(connection, $"""
            SELECT id FROM post_work_subjects WHERE {scopeClause} AND state NOT IN ('applied', 'retired')
             ORDER BY created_at;
            """, transaction, ("$scope", scope)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        var now = UtcNow;
        var actions = new List<PostWorkReconcileAction>();
        foreach (var id in ids)
        {
            var subject = (await ReadPostWorkSubjectAsync(connection, transaction, "id", id, ct))!;
            var live = await ReadLivePostWorkAttemptAsync(connection, transaction, id, ct);
            if (live is not null && live.ExpiresAt <= now)
            {
                await ExpirePostWorkAttemptsAsync(connection, transaction, id, "lost", ct);
                await PostWorkEventAsync(connection, transaction, id, live.AttemptId, subject.CohortId,
                    "lease-expired", live.Kind, ct);
                live = null;
            }
            var lane = facts.FirstOrDefault(fact => fact.TaskId == subject.TaskId);
            var (action, reason) = PostWorkAuthorityPolicy.Reconcile(Facts(subject, live), lane, now);
            if (action == PostWorkReconcileActions.Retire)
            {
                await SetPostWorkStateAsync(connection, transaction, id, PostWorkStates.Retired, ct);
                await PostWorkEventAsync(connection, transaction, id, null, cohortId ?? subject.CohortId, "retired", reason, ct);
            }
            else if (action == PostWorkReconcileActions.MarkApplied)
                await MarkPostWorkAppliedFromLaneAsync(connection, transaction, id, cohortId ?? subject.CohortId, ct);
            else if (action == PostWorkReconcileActions.RequeueBackend && subject.State == PostWorkStates.Running)
                await SetPostWorkStateAsync(connection, transaction, id, PostWorkStates.Queued, ct);
            if (action != PostWorkReconcileActions.None)
                actions.Add(new PostWorkReconcileAction(id, subject.IdempotencyKey, subject.ProjectId,
                    subject.TaskId, action, reason, subject.RetryAt));
        }
        return actions;
    }

    private async Task MarkPostWorkAppliedFromLaneAsync(
        SqliteConnection connection, SqliteTransaction transaction, string subjectId, string? cohortId, CancellationToken ct)
    {
        await ExpirePostWorkAttemptsAsync(connection, transaction, subjectId, "lost", ct);
        await ExecuteAsync(connection, """
            UPDATE post_work_subjects SET state = 'applied',
                decision = COALESCE(decision, 'decided'),
                decision_reason = COALESCE(decision_reason, 'lane-decision-recorded'),
                decided_by = COALESCE(decided_by, 'lane-reconcile'),
                decided_at = COALESCE(decided_at, $now), updated_at = $now
             WHERE id = $id;
            """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", subjectId));
        await PostWorkEventAsync(connection, transaction, subjectId, null, cohortId, "applied", "lane-decision-recorded", ct);
    }

    private async Task ApplyPostWorkOutcomeAsync(
        SqliteConnection connection, SqliteTransaction transaction, PostWorkSubject subject, string attemptId,
        string outcome, string reason, DateTime? retryAt, string decidedBy, CancellationToken ct)
    {
        var now = UtcNow;
        await ExecuteAsync(connection, """
            UPDATE post_work_attempts SET state = 'completed', outcome = $outcome, completed_at = $now WHERE id = $id;
            """, ct, transaction, ("$outcome", outcome), ("$now", Iso(now)), ("$id", attemptId));
        switch (outcome)
        {
            case PostWorkOutcomes.Decided:
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET state = 'applied', decision = 'decided', decision_reason = $reason,
                        decided_by = $by, decided_at = $now, retry_at = NULL, updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$reason", Bound(reason, 160)), ("$by", decidedBy),
                    ("$now", Iso(now)), ("$id", subject.SubjectId));
                break;
            case PostWorkOutcomes.Deferred:
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET state = 'waiting', deferred_reason = $reason, retry_at = $retry,
                        deferral_count = deferral_count + 1, updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$reason", Bound(reason, 160)),
                    ("$retry", Iso((retryAt ?? now.AddSeconds(30)).ToUniversalTime())),
                    ("$now", Iso(now)), ("$id", subject.SubjectId));
                break;
            default:
                await ExecuteAsync(connection, """
                    UPDATE post_work_subjects SET state = 'blocked', deferred_reason = $reason, retry_at = NULL,
                        updated_at = $now
                     WHERE id = $id;
                    """, ct, transaction, ("$reason", Bound(reason, 160)), ("$now", Iso(now)),
                    ("$id", subject.SubjectId));
                break;
        }
        await PostWorkEventAsync(connection, transaction, subject.SubjectId, attemptId, subject.CohortId,
            "outcome-" + outcome, decidedBy + ":" + Bound(reason, 160), ct);
    }

    private async Task<PostWorkLease> AcquirePostWorkAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction, PostWorkSubject subject, string kind,
        string executorId, string instanceId, string? hostId, int ttlSeconds, long allocationVersion,
        int grantHalfCores, DateTime now, CancellationToken ct)
    {
        await ExpirePostWorkAttemptsAsync(connection, transaction, subject.SubjectId, "lost", ct);
        var fence = subject.LastFence + 1;
        var attemptId = $"pwa_{Guid.NewGuid():N}";
        var leaseId = $"pwl_{Guid.NewGuid():N}";
        var expiry = now.AddSeconds(ttlSeconds);
        await ExecuteAsync(connection, """
            UPDATE post_work_subjects SET last_fence = $fence, updated_at = $now WHERE id = $subject;
            INSERT INTO post_work_attempts(id, subject_id, kind, executor_id, instance_id, host_id, lease_id,
                fence, authority_epoch, state, acquired_at, expires_at, allocation_version, grant_half_cores)
            VALUES ($attempt, $subject, $kind, $executor, $instance, $host, $lease, $fence, $epoch, 'live',
                $now, $expiry, $allocation, $grant);
            """, ct, transaction, ("$fence", fence), ("$now", Iso(now)), ("$subject", subject.SubjectId),
            ("$attempt", attemptId), ("$kind", kind), ("$executor", executorId), ("$instance", instanceId),
            ("$host", hostId), ("$lease", leaseId), ("$epoch", subject.AuthorityEpoch),
            ("$expiry", Iso(expiry)), ("$allocation", allocationVersion), ("$grant", grantHalfCores));
        return new PostWorkLease(attemptId, subject.SubjectId, leaseId, kind, executorId, instanceId, hostId,
            fence, subject.AuthorityEpoch, now, expiry, allocationVersion, grantHalfCores);
    }

    private async Task ExpirePostWorkAttemptsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string subjectId, string state, CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE post_work_attempts SET state = $state, completed_at = COALESCE(completed_at, $now)
             WHERE subject_id = $subject AND state = 'live';
            """, ct, transaction, ("$state", state), ("$now", Iso(UtcNow)), ("$subject", subjectId));

    private async Task SetPostWorkStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, string subjectId, string state, CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE post_work_subjects SET state = $state, updated_at = $now WHERE id = $id;
            """, ct, transaction, ("$state", state), ("$now", Iso(UtcNow)), ("$id", subjectId));

    private async Task PostWorkEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, string? subjectId, string? attemptId,
        string? cohortId, string name, string? detail, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO post_work_events(subject_id, attempt_id, cohort_id, event, at, detail)
            VALUES ($subject, $attempt, $cohort, $event, $at, $detail);
            """, ct, transaction, ("$subject", subjectId), ("$attempt", attemptId), ("$cohort", cohortId),
            ("$event", name), ("$at", Iso(UtcNow)), ("$detail", detail is null ? null : Bound(detail, 400)));

    private async Task<(string Status, IReadOnlyList<string> Reasons)> EvaluatePostWorkShadowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId,
        IReadOnlyList<string> requirements, DateTime now, CancellationToken ct)
    {
        var hosts = new List<(HostPlacementAdvertisement Host, bool Acknowledged, int Ceiling)>();
        await using (var command = Command(connection, """
            SELECT h.payload_json, r.runtime_capacity_applied_version, c.allocation_version, c.max_parallelism
              FROM host_placement_advertisements h
              JOIN runners r ON r.id = h.runner_id AND r.instance_id = h.instance_id
              JOIN runtime_capacity_settings c ON c.host_id = h.host_id
             WHERE r.status = 'active'
             LIMIT 100;
            """, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var host = JsonSerializer.Deserialize<HostPlacementAdvertisement>(reader.GetString(0));
                if (host is not null)
                    hosts.Add((host, !reader.IsDBNull(1) && reader.GetInt64(1) == reader.GetInt64(2), reader.GetInt32(3)));
            }
        }
        if (hosts.Count == 0) return ("rejected", ["no-post-host-advertised"]);
        IReadOnlyList<string> firstRejection = [];
        foreach (var host in hosts)
        {
            var occupied = await CountOccupiedHostSlotsAsync(connection, transaction, host.Host.HostId, ct);
            var decision = HostPlacementPolicy.Decide(host.Host, "post", projectId, requirements, now, now,
                TimeSpan.Zero, false, host.Acknowledged, occupied < host.Ceiling);
            if (decision.Eligible) return ("eligible", ["host:" + host.Host.HostId]);
            if (firstRejection.Count == 0) firstRejection = decision.Rejected;
        }
        return ("rejected", firstRejection);
    }

    private static async Task<(string Owner, string Phase, string? CohortId)> CohortPlacementForAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT c.id, c.phase FROM post_work_cohorts c, json_each(c.task_ids_json) t
             WHERE c.project_id = $project AND t.value = $task AND c.phase IN ('draining', 'host')
             LIMIT 1;
            """, transaction, ("$project", projectId), ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (PostWorkOwners.Backend, PostWorkCohortPhases.None, null);
        var phase = reader.GetString(1);
        // A new lane entry of an admitted cohort card has no backend pass to drain.
        return phase == PostWorkCohortPhases.Host
            ? (PostWorkOwners.Host, PostWorkCohortPhases.Host, reader.GetString(0))
            : (PostWorkOwners.Backend, PostWorkCohortPhases.Draining, reader.GetString(0));
    }

    private async Task<List<PostWorkSubject>> ReadCohortSubjectsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string cohortId, CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var command = Command(connection,
            "SELECT id FROM post_work_subjects WHERE cohort_id = $cohort ORDER BY created_at;",
            transaction, ("$cohort", cohortId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        var subjects = new List<PostWorkSubject>();
        foreach (var id in ids)
            if (await ReadPostWorkSubjectAsync(connection, transaction, "id", id, ct) is { } subject)
                subjects.Add(subject);
        return subjects;
    }

    private async Task<(PostWorkLiveAttempt Attempt, PostWorkSubject Subject)> ReadFencedPostWorkAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction, string attemptId, CancellationToken ct)
    {
        var attempt = await ReadPostWorkAttemptAsync(connection, transaction, "id", attemptId, ct)
            ?? throw new KeyNotFoundException("Post-work attempt was not found.");
        if (attempt.Kind == PostWorkExecutorKinds.Backend)
            throw new TaskServerConflictException("post-work-backend-attempt", "Backend passes complete through the backend route.");
        var subjectId = Convert.ToString(await ScalarAsync(connection,
            "SELECT subject_id FROM post_work_attempts WHERE id = $id;", ct, transaction, ("$id", attemptId)),
            CultureInfo.InvariantCulture)!;
        var subject = await ReadPostWorkSubjectAsync(connection, transaction, "id", subjectId, ct)
            ?? throw new KeyNotFoundException("Post-work subject was not found.");
        return (attempt, subject);
    }

    private static Task<PostWorkLiveAttempt?> ReadLivePostWorkAttemptAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string subjectId, CancellationToken ct)
        => ReadPostWorkAttemptAsync(connection, transaction, "live", subjectId, ct);

    private static Task<PostWorkLiveAttempt?> ReadPostWorkAttemptByLeaseAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string leaseId, CancellationToken ct)
        => ReadPostWorkAttemptAsync(connection, transaction, "lease_id", leaseId, ct);

    private static async Task<PostWorkLiveAttempt?> ReadPostWorkAttemptAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string by, string value, CancellationToken ct)
    {
        var where = by switch
        {
            "live" => "subject_id = $value AND state = 'live'",
            "lease_id" => "lease_id = $value",
            _ => "id = $value",
        };
        await using var command = Command(connection, $"""
            SELECT id, kind, executor_id, instance_id, host_id, lease_id, fence, authority_epoch,
                   acquired_at, expires_at, allocation_version, grant_half_cores, state, report_digest
              FROM post_work_attempts WHERE {where};
            """, transaction, ("$value", value));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PostWorkLiveAttempt(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetInt64(7), Parse(reader.GetString(8)), Parse(reader.GetString(9)),
            reader.GetInt64(10), reader.GetInt32(11), reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static async Task<PostWorkSubject?> ReadPostWorkSubjectAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string column, string value, CancellationToken ct)
    {
        if (column is not ("id" or "idempotency_key")) throw new ArgumentOutOfRangeException(nameof(column));
        await using var command = Command(connection, $"""
            SELECT id, idempotency_key, project_id, task_id, lane_entry_id, source_run_id, subject_json,
                   policy_digest, owner, state, cohort_phase, cohort_id, last_fence, authority_epoch,
                   deferred_reason, retry_at, deferral_count, decision, decision_reason, decided_by,
                   decided_at, host_proposal, shadow_status, shadow_reasons_json, backend_shadow_outcome,
                   created_at, updated_at
              FROM post_work_subjects WHERE {column} = $value;
            """, transaction, ("$value", value));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        var immutable = JsonSerializer.Deserialize<PostWorkImmutable>(reader.GetString(6), PostWorkJson)!;
        return new PostWorkSubject(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), Text(5), immutable.RequiredCapabilities,
            immutable.Artifacts, immutable.Steps, reader.GetString(7), reader.GetString(8), reader.GetString(9),
            reader.GetString(10), Text(11), reader.GetInt64(12), reader.GetInt64(13), Text(14),
            Text(15) is { } retry ? Parse(retry) : null, reader.GetInt32(16), Text(17), Text(18), Text(19),
            Text(20) is { } decided ? Parse(decided) : null, Text(21), reader.GetString(22),
            JsonSerializer.Deserialize<string[]>(reader.GetString(23)) ?? [], Text(24),
            Parse(reader.GetString(25)), Parse(reader.GetString(26)));
    }

    private static PostWorkSubjectFacts Facts(PostWorkSubject subject, PostWorkLiveAttempt? live)
        => new(subject.Owner, subject.State, subject.CohortPhase, subject.LaneEntryId, subject.RetryAt,
            live?.Kind, live?.InstanceId, live?.ExpiresAt);

    private static PostWorkLease ToLease(PostWorkLiveAttempt attempt, string subjectId)
        => new(attempt.AttemptId, subjectId, attempt.LeaseId, attempt.Kind, attempt.ExecutorId,
            attempt.InstanceId, attempt.HostId, attempt.Fence, attempt.AuthorityEpoch, attempt.AcquiredAt,
            attempt.ExpiresAt, attempt.AllocationVersion, attempt.GrantHalfCores);

    private static void ValidatePostWorkRequest(MaterializePostWorkSubjectRequest request)
    {
        if (new[] { request.ProjectId, request.TaskId, request.LaneEntryId, request.Source }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200)
            || request.SourceRunId is { Length: > 200 }
            || request.RequiredCapabilities.Count > 32
            || request.RequiredCapabilities.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 160)
            || request.Artifacts.Count > 64
            || request.Artifacts.Any(item => string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Locator)
                || item.Locator.Length > 400)
            || request.Steps.Count > 16
            || request.Steps.Any(step => string.IsNullOrWhiteSpace(step.StepId) || string.IsNullOrWhiteSpace(step.FileName)
                || step.TimeoutSeconds is < 1 or > 3600 || step.Arguments.Count > 64))
            throw new ArgumentException("Post-work subject is incomplete or exceeds its bounds.");
    }

    private static void ValidatePostWorkReport(PostWorkReport report)
    {
        if (!PostWorkOutcomes.IsKnown(report.ProposedOutcome)
            || report.ExecutorKind is not (PostWorkExecutorKinds.Container or PostWorkExecutorKinds.Process)
            || string.IsNullOrWhiteSpace(report.Reason) || report.Reason.Length > 400
            || report.Evidence.Count > 64
            || report.Evidence.Any(item => item.OutputTail is { Length: > 4000 })
            || report.CleanupStatus is not ("clean" or "dirty"))
            throw new ArgumentException("Post-work report is invalid.");
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
