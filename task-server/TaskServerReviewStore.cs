using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web);

    internal async Task ApplyReviewMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, "result_handoffs", "repository_url", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "result_sha", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "repository_id", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "repository_url", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "result_ref", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "source_bundle_artifact_id", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "runs", "source_bundle_sha256", "TEXT", ct);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS review_subjects(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                source_run_id TEXT NOT NULL REFERENCES runs(id),
                repository_id TEXT NOT NULL,
                repository_url TEXT,
                expected_result_sha TEXT NOT NULL,
                result_ref TEXT,
                source_bundle_artifact_id TEXT,
                source_bundle_sha256 TEXT,
                coding_host_id TEXT,
                review_policy_hash TEXT NOT NULL,
                plan_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS review_fence_counters(
                subject_id TEXT PRIMARY KEY REFERENCES review_subjects(id),
                last_fence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS review_attempts(
                id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL REFERENCES review_subjects(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                attempt_number INTEGER NOT NULL,
                status TEXT NOT NULL,
                executor_id TEXT,
                instance_id TEXT,
                host_id TEXT,
                lease_id TEXT,
                fence INTEGER NOT NULL DEFAULT 0,
                acquired_at TEXT,
                expires_at TEXT,
                report_id TEXT,
                report_json TEXT,
                report_sha256 TEXT,
                report_idempotency_key TEXT UNIQUE,
                outcome TEXT,
                failure_classification TEXT,
                summary TEXT,
                reported_at TEXT,
                cleanup_idempotency_key TEXT UNIQUE,
                cleaned_at TEXT,
                port_base INTEGER,
                resource_namespace TEXT,
                created_at TEXT NOT NULL,
                UNIQUE(subject_id, attempt_number)
            );
            CREATE TABLE IF NOT EXISTS review_deliveries(
                delivery_key TEXT PRIMARY KEY,
                attempt_id TEXT NOT NULL REFERENCES review_attempts(id),
                operation TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                response_json TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_review_restarts(
                runner_id TEXT PRIMARY KEY REFERENCES runners(id) ON DELETE CASCADE,
                instance_id TEXT NOT NULL,
                restarted_at TEXT NOT NULL,
                reviews_lost INTEGER NOT NULL CHECK(reviews_lost >= 0)
            );
            CREATE INDEX IF NOT EXISTS ix_review_attempts_status_created
                ON review_attempts(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_review_attempts_subject
                ON review_attempts(subject_id, attempt_number);
            """, ct);
        await AddColumnIfMissingAsync(connection, "review_attempts", "port_base", "INTEGER", ct);
        await AddColumnIfMissingAsync(connection, "review_attempts", "resource_namespace", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "review_deliveries", "response_json", "TEXT", ct);
        await BackfillReviewResourceNamespacesAsync(connection, ct);
    }

    private static async Task BackfillReviewResourceNamespacesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var missing = new List<(string AttemptId, long Fence)>();
        await using (var command = Command(connection, """
            SELECT id, fence
              FROM review_attempts
             WHERE lease_id IS NOT NULL
               AND fence > 0
               AND (resource_namespace IS NULL OR trim(resource_namespace) = '');
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                missing.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        foreach (var attempt in missing)
        {
            await ExecuteAsync(
                connection,
                "UPDATE review_attempts SET resource_namespace = $namespace WHERE id = $attempt;",
                ct,
                ("$namespace", ResourceNamespace(attempt.AttemptId, attempt.Fence)),
                ("$attempt", attempt.AttemptId));
        }
    }

    public async Task<ReviewSubjectDto> CreateReviewSubjectAsync(
        CreateReviewSubjectRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        request = request with
        {
            Plan = ReviewPlanResourcePolicy.Apply(request.Plan),
        };
        ValidateReviewSubjectRequest(request);
        ReviewSubjectDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadReviewSubjectByIdempotencyAsync(
                connection, transaction, request.IdempotencyKey, ct);
            if (replay is not null)
            {
                ValidateReviewSubjectReplay(replay, request);
                result = replay;
                return;
            }

            await using (var command = Command(connection, """
                SELECT r.task_id, r.status, r.result_sha, r.repository_id, r.repository_url,
                       r.result_ref, r.source_bundle_artifact_id, r.source_bundle_sha256, t.state
                  FROM runs r
                  JOIN tasks t ON t.id = r.task_id
                 WHERE r.id = $run;
                """, transaction, ("$run", request.SourceRunId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Source coding run was not found.");
                if (!string.Equals(reader.GetString(0), request.TaskId, StringComparison.Ordinal))
                    throw new TaskServerConflictException("review-subject-mismatch", "Source run belongs to a different task.");
                if (reader.GetString(1) is "running" or "process-unknown")
                    throw new TaskServerConflictException("coding-attempt-not-terminal", "Source coding attempt is not terminal.");
                if (!string.Equals(reader.IsDBNull(2) ? null : reader.GetString(2), request.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
                    throw new TaskServerConflictException("result-sha-mismatch", "Expected review SHA does not match the fenced coding result.");
                if (!string.Equals(reader.IsDBNull(3) ? null : reader.GetString(3), request.RepositoryId, StringComparison.Ordinal))
                    throw new TaskServerConflictException("repository-mismatch", "Review repository identity does not match the fenced coding result.");
                if (!string.Equals(reader.GetString(8), "4-auto-review", StringComparison.Ordinal))
                    throw new TaskServerConflictException("task-not-auto-review", "Review subjects can only be created in Auto Review.");
                ValidateOptionalSourceField(reader, 4, request.RepositoryUrl, "repository URL");
                ValidateOptionalSourceField(reader, 5, request.ResultRef, "result ref");
                ValidateOptionalSourceField(reader, 6, request.SourceBundleArtifactId, "source bundle artifact");
                ValidateOptionalSourceField(reader, 7, request.SourceBundleSha256, "source bundle digest");
            }

            var subjectId = $"rsub_{Guid.NewGuid():N}";
            var attemptId = $"rat_{Guid.NewGuid():N}";
            var now = UtcNow;
            var planJson = JsonSerializer.Serialize(request.Plan, ReviewJson);
            await ExecuteAsync(connection, """
                INSERT INTO review_subjects(
                    id, task_id, source_run_id, repository_id, repository_url,
                    expected_result_sha, result_ref, source_bundle_artifact_id,
                    source_bundle_sha256, coding_host_id, review_policy_hash,
                    plan_json, idempotency_key, created_at)
                VALUES (
                    $id, $task, $run, $repository, $url, $sha, $ref, $bundle,
                    $bundleSha, $codingHost, $policy, $plan, $key, $now);
                INSERT INTO review_attempts(
                    id, subject_id, task_id, attempt_number, status, created_at)
                VALUES ($attempt, $id, $task, 1, 'queued', $now);
                """, ct, transaction,
                ("$id", subjectId), ("$task", request.TaskId), ("$run", request.SourceRunId),
                ("$repository", request.RepositoryId), ("$url", request.RepositoryUrl),
                ("$sha", request.ExpectedResultSha.ToLowerInvariant()), ("$ref", request.ResultRef),
                ("$bundle", request.SourceBundleArtifactId), ("$bundleSha", request.SourceBundleSha256),
                ("$codingHost", request.CodingHostId), ("$policy", request.ReviewPolicyHash),
                ("$plan", planJson), ("$key", request.IdempotencyKey), ("$now", Iso(now)),
                ("$attempt", attemptId));
            await AuditAsync(connection, transaction, actorId, "review.subject-created", "review-subject", subjectId,
                JsonSerializer.Serialize(new
                {
                    request.TaskId,
                    request.SourceRunId,
                    request.RepositoryId,
                    request.ExpectedResultSha,
                    attemptId,
                }), ct);
            result = new ReviewSubjectDto(
                subjectId, request.TaskId, request.SourceRunId, request.RepositoryId,
                request.RepositoryUrl, request.ExpectedResultSha.ToLowerInvariant(), request.ResultRef,
                request.SourceBundleArtifactId, request.SourceBundleSha256, request.CodingHostId,
                request.ReviewPolicyHash, request.Plan, now);
        }, ct);
        return result!;
    }

    public async Task<ReviewClaimResponse> ClaimReviewAsync(
        ReviewClaimRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireAdmission();
        ReviewClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var executor = await ReadReviewExecutorAsync(
                connection, transaction, request.ExecutorId, request.InstanceId, ct);
            await SupersedeUnclaimableReviewAttemptsAsync(
                connection,
                transaction,
                actorId,
                "claim-guard",
                taskId: null,
                ct);
            var capabilityAdmission = await EvaluateCapabilityAdmissionAsync(
                connection,
                transaction,
                request.ExecutorId,
                executor.HostId,
                request.RequiredCapabilities,
                ct);
            if (!capabilityAdmission.Eligible)
            {
                response = new ReviewClaimResponse("empty", Message: capabilityAdmission.Message);
                return;
            }
            if (request.AvailableSlots <= 0)
            {
                response = new ReviewClaimResponse("empty", Message: "Review executor has no available slot.");
                return;
            }

            ReviewAttemptDto? attempt = null;
            ReviewSubjectDto? subject = null;
            string? capabilityBlock = null;
            var candidates = new List<(ReviewAttemptDto Attempt, ReviewSubjectDto Subject)>();
            await using (var command = Command(connection, """
                SELECT a.id, a.subject_id, a.task_id, a.attempt_number, a.status,
                       a.executor_id, a.host_id, a.fence, a.created_at, a.reported_at,
                       a.cleaned_at, a.outcome, a.failure_classification,
                       s.source_run_id, s.repository_id, s.repository_url,
                       s.expected_result_sha, s.result_ref, s.source_bundle_artifact_id,
                       s.source_bundle_sha256, s.coding_host_id, s.review_policy_hash,
                       s.plan_json, s.created_at
                  FROM review_attempts a
                  JOIN review_subjects s ON s.id = a.subject_id
                  JOIN tasks t ON t.id = a.task_id
                 WHERE (
                         a.status = 'queued'
                         OR a.status = 'process-unknown'
                         OR (a.status = 'leased' AND a.expires_at <= $now)
                       )
                   AND t.state = '4-auto-review'
                   AND NOT (
                         json_extract(s.plan_json, '$.requireDifferentHostFailureDomain') = 1
                         AND s.coding_host_id = $host
                       )
                 ORDER BY a.created_at, a.attempt_number
                 LIMIT 32;
                """, transaction, ("$now", Iso(UtcNow)), ("$host", executor.HostId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var candidateAttempt = ReadReviewAttempt(reader);
                    var candidateSubject = ReadReviewSubjectFromClaim(reader);
                    if (SupportsSubject(executor, candidateSubject))
                        candidates.Add((candidateAttempt, candidateSubject));
                }
            }
            foreach (var candidate in candidates)
            {
                var candidateRequirements = RequiredReviewCapabilities(
                    request.RequiredCapabilities,
                    candidate.Subject);
                var candidateAdmission = await EvaluateCapabilityAdmissionAsync(
                    connection,
                    transaction,
                    request.ExecutorId,
                    executor.HostId,
                    candidateRequirements,
                    ct);
                if (!candidateAdmission.Eligible)
                {
                    capabilityBlock = candidateAdmission.Message;
                    continue;
                }
                attempt = candidate.Attempt;
                subject = candidate.Subject;
                capabilityAdmission = candidateAdmission;
                break;
            }

            if (attempt is null || subject is null)
            {
                response = new ReviewClaimResponse(
                    "empty",
                    Message: capabilityBlock
                             ?? "No eligible immutable review subject is queued for this host failure domain.");
                return;
            }

            var fence = Convert.ToInt64(await ScalarAsync(connection, """
                SELECT last_fence FROM review_fence_counters WHERE subject_id = $subject;
                """, ct, transaction, ("$subject", subject.SubjectId)) ?? 0L, CultureInfo.InvariantCulture) + 1;
            var leaseId = $"rls_{Guid.NewGuid():N}";
            var acquired = UtcNow;
            var expires = acquired.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            var portCursor = Convert.ToInt32(
                await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'review_port_cursor';", ct, transaction)
                    ?? 23992,
                CultureInfo.InvariantCulture);
            var portBase = portCursor >= 59992 ? 24000 : portCursor + 8;
            var resourceNamespace = ResourceNamespace(attempt.AttemptId, fence);
            await SetMetaAsync(connection, transaction, "review_port_cursor",
                portBase.ToString(CultureInfo.InvariantCulture), ct);
            await ExecuteAsync(connection, """
                INSERT INTO review_fence_counters(subject_id, last_fence)
                VALUES ($subject, $fence)
                ON CONFLICT(subject_id) DO UPDATE SET last_fence = excluded.last_fence;
                UPDATE review_attempts
                   SET status = 'leased', executor_id = $executor, instance_id = $instance,
                       host_id = $host, lease_id = $lease, fence = $fence,
                       acquired_at = $acquired, expires_at = $expires, port_base = $portBase,
                       resource_namespace = $resourceNamespace,
                       required_capabilities_json = $requiredCapabilities,
                       canary_capabilities_json = $canaryCapabilities
                 WHERE id = $attempt;
                UPDATE runners SET last_seen_at = $acquired WHERE id = $executor;
                """, ct, transaction,
                ("$subject", subject.SubjectId), ("$fence", fence), ("$executor", request.ExecutorId),
                ("$instance", request.InstanceId), ("$host", executor.HostId), ("$lease", leaseId),
                ("$acquired", Iso(acquired)), ("$expires", Iso(expires)),
                ("$portBase", portBase), ("$resourceNamespace", resourceNamespace),
                ("$attempt", attempt.AttemptId),
                ("$requiredCapabilities", JsonSerializer.Serialize(capabilityAdmission.Required)),
                ("$canaryCapabilities", JsonSerializer.Serialize(capabilityAdmission.Canaries)));
            await ReserveCanariesAsync(
                connection,
                transaction,
                request.ExecutorId,
                capabilityAdmission.Canaries,
                attempt.AttemptId,
                ct);
            var claimedAttempt = attempt with
            {
                Status = "leased",
                ExecutorId = request.ExecutorId,
                HostId = executor.HostId,
                Fence = fence,
            };
            var lease = new ReviewLeaseDto(
                leaseId, attempt.AttemptId, subject.SubjectId, request.ExecutorId,
                request.InstanceId, executor.HostId, fence, acquired, expires, "active",
                resourceNamespace, portBase);
            await AuditAsync(connection, transaction, actorId, "review.claimed", "review-attempt", attempt.AttemptId,
                JsonSerializer.Serialize(new
                {
                    subject.SubjectId,
                    subject.ExpectedResultSha,
                    request.ExecutorId,
                    request.InstanceId,
                    executor.HostId,
                    fence,
                    resourceNamespace,
                }), ct);
            response = new ReviewClaimResponse(
                "claimed",
                claimedAttempt,
                subject,
                lease,
                RequiredCapabilities: capabilityAdmission.Required,
                CanaryCapabilities: capabilityAdmission.Canaries);
        }, ct);
        return response!;
    }

    private async Task<int> SupersedeUnclaimableReviewAttemptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorId,
        string source,
        string? taskId,
        CancellationToken ct)
    {
        var stale = new List<(string AttemptId, string TaskId, string? State)>();
        await using (var command = Command(connection, """
            SELECT a.id, a.task_id, t.state
              FROM review_attempts a
              LEFT JOIN tasks t ON t.id = a.task_id
             WHERE a.status IN ('queued', 'leased', 'process-unknown')
               AND (t.id IS NULL OR t.state <> '4-auto-review')
               AND ($task IS NULL OR a.task_id = $task)
             ORDER BY a.created_at, a.attempt_number;
            """, transaction, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                stale.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var superseded = 0;
        foreach (var item in stale)
        {
            var lane = item.State ?? "missing";
            var reason =
                $"Task is in lane '{lane}', not '4-auto-review'; open ReviewAttempt authority was superseded by {source}.";
            var affected = await ExecuteAsync(connection, """
                UPDATE review_attempts
                   SET status = 'superseded',
                       outcome = 'Superseded',
                       summary = $reason,
                       reported_at = COALESCE(reported_at, $now)
                 WHERE id = $attempt
                   AND status IN ('queued', 'leased', 'process-unknown');
                """, ct, transaction,
                ("$reason", reason),
                ("$now", Iso(UtcNow)),
                ("$attempt", item.AttemptId));
            if (affected == 0) continue;

            superseded += affected;
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "review.superseded",
                "review-attempt",
                item.AttemptId,
                JsonSerializer.Serialize(new
                {
                    item.TaskId,
                    lane,
                    source,
                    authority = "Superseded",
                    reason,
                }),
                ct);
        }

        return superseded;
    }

    private static IReadOnlyList<string>? RequiredReviewCapabilities(
        IReadOnlyList<string>? requested,
        ReviewSubjectDto subject)
    {
        // Protocol-v2 callers did not advertise health. Keep their empty claim
        // compatible; protocol-v3 capability-aware callers send the base set
        // and receive subject-derived requirements additively.
        if (requested is null || requested.Count == 0) return requested;
        var requirements = new HashSet<string>(requested, StringComparer.Ordinal)
        {
            CapabilityProtocol.ReviewExecutor,
            CapabilityProtocol.RepositoryAccess,
        };
        if (!string.IsNullOrWhiteSpace(subject.RepositoryUrl))
        {
            requirements.Add(CapabilityProtocol.GitFetch);
            requirements.Add(ReviewCapabilities.GitMaterialization);
        }
        if (!string.IsNullOrWhiteSpace(subject.SourceBundleArtifactId))
            requirements.Add(ReviewCapabilities.SourceBundleMaterialization);
        if (subject.Plan.RequiresVisualReview)
            requirements.Add(CapabilityProtocol.Vision);
        if (subject.Plan.Commands.Any(command => command.CompareToBaseline))
            requirements.Add(ReviewCapabilities.BaselineComparison);
        if (subject.Plan.Preparation is { Count: > 0 })
            requirements.Add(ReviewCapabilities.DependencyPreparation);
        if (subject.Plan.RequiredAspects.Any(aspect =>
                aspect is "completion" or "requirements" or "code-quality" or "documentation" or "evidence"))
            requirements.Add(ReviewCapabilities.SemanticReview);
        foreach (var command in subject.Plan.Commands.Where(command =>
                     ReviewCommandKinds.IsAgent(command.ExecutionKind)))
        {
            requirements.Add(ReviewCapabilities.SemanticReview);
            if (!string.IsNullOrWhiteSpace(command.CliType))
            {
                requirements.Add(CapabilityProtocol.CliExecution(command.CliType));
                requirements.Add(CapabilityProtocol.ProviderAuthentication(command.CliType));
            }
        }
        return requirements.Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<ReviewLeaseDto> RenewReviewLeaseAsync(
        string attemptId,
        ReviewLeaseRenewRequest request,
        string actorId,
        CancellationToken ct)
    {
        // Draining stops new claims, but an already fenced review must retain
        // authority long enough to finish, report, and clean up.
        RequireWritable();
        ReviewLeaseDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadReviewAuthorityAsync(connection, transaction, attemptId, ct);
            ValidateReviewAuthority(attempt, request.ExecutorId, request.InstanceId, request.LeaseId, request.Fence, requireLeased: true);
            var payloadHash = HashJson(request);
            if (await DeliveryExistsAsync(connection, transaction, attemptId, "renew", request.IdempotencyKey, payloadHash, ct))
            {
                result = ToReviewLease(attempt);
                return;
            }
            if (attempt.ExpiresAt <= UtcNow)
                throw new TaskServerConflictException("review-lease-expired", "Review lease expired and its evidence is fenced off.");
            var requestedExpiry = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            var expires = attempt.ExpiresAt is { } currentExpiry
                          && currentExpiry > requestedExpiry
                ? currentExpiry
                : requestedExpiry;
            await ExecuteAsync(connection, "UPDATE review_attempts SET expires_at = $expires WHERE id = $attempt;",
                ct, transaction, ("$expires", Iso(expires)), ("$attempt", attemptId));
            await RecordDeliveryAsync(connection, transaction, attemptId, "renew", request.IdempotencyKey, payloadHash, ct);
            result = ToReviewLease(attempt with { ExpiresAt = expires });
            await AuditAsync(connection, transaction, actorId, "review.lease-renewed", "review-attempt", attemptId,
                JsonSerializer.Serialize(new { request.Fence, expires }), ct);
        }, ct);
        return result!;
    }

    /// <summary>
    /// Re-fences one review attempt for a replacement instance of the executor
    /// that owns its detached worker. This is a continuity operation, not a
    /// queue claim: the previous lease identity must still match exactly, and a
    /// live leased attempt can never be taken over.
    /// </summary>
    public async Task<ReviewClaimResponse> ReClaimReviewAsync(
        string attemptId,
        ReviewReClaimRequest request,
        string actorId,
        CancellationToken ct)
    {
        // Draining closes new queue claims, but must still allow an adopted
        // worker to repair authority and finish its already-running review.
        RequireWritable();
        ValidateReviewReClaimRequest(attemptId, request);
        ReviewClaimResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var executor = await ReadReviewExecutorAsync(
                connection, transaction, request.ExecutorId, request.InstanceId, ct);
            var payloadHash = HashJson(request);
            var replay = await ReadReviewReClaimDeliveryAsync(
                connection,
                transaction,
                attemptId,
                request.IdempotencyKey,
                payloadHash,
                ct);
            if (replay is not null)
            {
                result = replay;
                return;
            }

            var attempt = await ReadReviewAuthorityAsync(connection, transaction, attemptId, ct);
            if (!string.Equals(attempt.ExecutorId, request.ExecutorId, StringComparison.Ordinal)
                || !string.Equals(attempt.HostId, executor.HostId, StringComparison.Ordinal)
                || !string.Equals(attempt.LeaseId, request.PreviousLeaseId, StringComparison.Ordinal)
                || attempt.Fence != request.PreviousFence)
            {
                throw new TaskServerConflictException(
                    "stale-review-fence",
                    "Review executor, host, previous lease, or previous fence does not match current authority.");
            }
            if (attempt.LeaseId is null
                || attempt.AcquiredAt is null
                || attempt.ExpiresAt is null
                || string.IsNullOrWhiteSpace(attempt.ResourceNamespace)
                || attempt.PortBase <= 0
                || attempt.PortBase > ushort.MaxValue - 7)
            {
                throw new TaskServerConflictException(
                    "review-isolation-authority-missing",
                    "Review reclaim requires the handed-off lease and its persisted physical isolation authority.");
            }

            var now = UtcNow;
            if (string.Equals(attempt.Status, "leased", StringComparison.Ordinal)
                && attempt.ExpiresAt > now)
            {
                throw new TaskServerConflictException(
                    "review-lease-active",
                    "An unexpired live review lease cannot be re-claimed.");
            }
            if (!string.Equals(attempt.Status, "process-unknown", StringComparison.Ordinal)
                && !string.Equals(attempt.Status, "leased", StringComparison.Ordinal))
            {
                throw new TaskServerConflictException(
                    "review-reclaim-not-eligible",
                    $"Review attempt status is '{attempt.Status}' and cannot be re-claimed.");
            }

            var subject = await ReadReviewSubjectAsync(
                              connection, transaction, attempt.SubjectId, ct)
                          ?? throw new KeyNotFoundException("Review subject was not found.");
            await EnsureReviewSubjectCurrentAsync(connection, transaction, subject, ct);
            var lastFence = Convert.ToInt64(await ScalarAsync(connection, """
                SELECT last_fence FROM review_fence_counters WHERE subject_id = $subject;
                """, ct, transaction, ("$subject", attempt.SubjectId)) ?? 0L, CultureInfo.InvariantCulture);
            var fence = Math.Max(lastFence, attempt.Fence) + 1;
            var leaseId = $"rls_{Guid.NewGuid():N}";
            var expires = now.AddSeconds(request.RequestedTtlSeconds);
            await ExecuteAsync(connection, """
                INSERT INTO review_fence_counters(subject_id, last_fence)
                VALUES ($subject, $fence)
                ON CONFLICT(subject_id) DO UPDATE SET last_fence = excluded.last_fence;
                UPDATE review_attempts
                   SET status = 'leased', executor_id = $executor, instance_id = $instance,
                       host_id = $host, lease_id = $lease, fence = $fence,
                       acquired_at = $acquired, expires_at = $expires
                 WHERE id = $attempt;
                UPDATE runners SET last_seen_at = $acquired WHERE id = $executor;
                """, ct, transaction,
                ("$subject", attempt.SubjectId), ("$fence", fence),
                ("$executor", request.ExecutorId), ("$instance", request.InstanceId),
                ("$host", executor.HostId), ("$lease", leaseId),
                ("$acquired", Iso(now)), ("$expires", Iso(expires)),
                ("$attempt", attempt.AttemptId));

            var reclaimed = attempt with
            {
                Status = "leased",
                ExecutorId = request.ExecutorId,
                InstanceId = request.InstanceId,
                HostId = executor.HostId,
                LeaseId = leaseId,
                Fence = fence,
                AcquiredAt = now,
                ExpiresAt = expires,
            };
            result = new ReviewClaimResponse(
                "claimed",
                ToReviewAttempt(reclaimed),
                Lease: ToReviewLease(reclaimed));
            var responseJson = JsonSerializer.Serialize(result, ReviewJson);
            await RecordDeliveryAsync(
                connection,
                transaction,
                attemptId,
                "reclaim",
                request.IdempotencyKey,
                payloadHash,
                ct,
                responseJson);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "review.re-claimed",
                "review-attempt",
                attemptId,
                JsonSerializer.Serialize(new
                {
                    request.ExecutorId,
                    request.InstanceId,
                    previousFence = request.PreviousFence,
                    fence,
                    reclaimed.ResourceNamespace,
                    reclaimed.PortBase,
                }),
                ct);
        }, ct);
        return result!;
    }

    public async Task<ReviewReportDto> ReportReviewAsync(
        string attemptId,
        ReviewReportRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ReviewReportDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadReviewAuthorityAsync(connection, transaction, attemptId, ct);
            var subject = await ReadReviewSubjectAsync(connection, transaction, attempt.SubjectId, ct)
                ?? throw new KeyNotFoundException("Review subject was not found.");
            request = ReviewVerdictCitationPolicy.NormalizeReport(
                request,
                subject.Plan.Commands
                    .Where(command => ReviewCommandKinds.IsAgent(command.ExecutionKind))
                    .Select(command => command.Aspect));
            var payloadJson = JsonSerializer.Serialize(request, ReviewJson);
            var payloadHash = Hash(payloadJson);
            if (!string.IsNullOrWhiteSpace(attempt.ReportIdempotencyKey))
            {
                if (!string.Equals(attempt.ReportIdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
                    || !string.Equals(attempt.ReportSha256, payloadHash, StringComparison.Ordinal))
                    throw new TaskServerConflictException("idempotency-conflict", "Review report key is bound to a different payload.");
                result = ToReviewReport(attempt);
                return;
            }

            ValidateReviewAuthority(attempt, request.ExecutorId, request.InstanceId, request.LeaseId, request.Fence, requireLeased: true);
            if (attempt.ExpiresAt <= UtcNow)
                throw new TaskServerConflictException("review-lease-expired", "Review lease expired and its report is fenced off.");
            await EnsureReviewSubjectCurrentAsync(connection, transaction, subject, ct);
            var classified = ClassifyReviewReport(subject, request, attempt);
            var received = UtcNow;
            var reportId = $"rrpt_{Guid.NewGuid():N}";
            var retry = string.Equals(classified.Outcome, "ReviewInfra", StringComparison.Ordinal);
            const string taskState = "4-auto-review";
            await ExecuteAsync(connection, """
                UPDATE review_attempts
                   SET status = 'reported', report_id = $report, report_json = $json,
                       report_sha256 = $hash, report_idempotency_key = $key,
                       outcome = $outcome, failure_classification = $classification,
                       summary = $summary, reported_at = $now
                 WHERE id = $attempt;
                UPDATE tasks
                   SET state = $state, version = version + 1, updated_at = $now
                 WHERE id = $task;
                """, ct, transaction,
                ("$report", reportId), ("$json", payloadJson), ("$hash", payloadHash),
                ("$key", request.IdempotencyKey), ("$outcome", classified.Outcome),
                ("$classification", classified.Classification), ("$summary", request.Summary),
                ("$now", Iso(received)), ("$attempt", attemptId), ("$state", taskState),
                ("$task", attempt.TaskId));
            if (retry)
                await InsertReviewRetryAsync(connection, transaction, attempt, received, ct);
            if (!string.Equals(classified.Outcome, "ReviewInfra", StringComparison.Ordinal))
            {
                await ResolveCanarySuccessAsync(
                    connection,
                    transaction,
                    request.ExecutorId,
                    attemptId,
                    "review canary reported an authoritative non-infrastructure outcome",
                    ct);
            }
            await AuditAsync(connection, transaction, actorId, "review.reported", "review-attempt", attemptId,
                JsonSerializer.Serialize(new
                {
                    subject.SubjectId,
                    subject.RepositoryId,
                    subject.ExpectedResultSha,
                    request.Workspace.ActualHead,
                    request.Workspace.TreeHash,
                    request.Workspace.DirtyBefore,
                    request.Workspace.DirtyAfter,
                    classified.Outcome,
                    classified.Classification,
                    reportId,
                    payloadHash,
                    retry,
                }), ct);
            result = new ReviewReportDto(
                reportId, attemptId, attempt.SubjectId, classified.Outcome,
                classified.Classification, request.Summary, payloadHash, received, retry, taskState);
        }, ct);
        return result!;
    }

    public async Task<ReviewCleanupResponse> CleanupReviewAsync(
        string attemptId,
        ReviewCleanupRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ReviewCleanupResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var attempt = await ReadReviewAuthorityAsync(connection, transaction, attemptId, ct);
            var payloadHash = HashJson(request);
            if (await DeliveryExistsAsync(connection, transaction, attemptId, "cleanup", request.IdempotencyKey, payloadHash, ct))
            {
                result = new ReviewCleanupResponse(
                    "duplicate", attemptId, attempt.CleanedAt ?? UtcNow,
                    string.Equals(attempt.Outcome, "ReviewInfra", StringComparison.Ordinal));
                return;
            }
            ValidateReviewAuthority(attempt, request.ExecutorId, request.InstanceId, request.LeaseId, request.Fence, requireLeased: false);
            if (!request.WorkspaceRemoved)
            {
                var failedAt = UtcNow;
                var classification = request.FailureClassification ?? "WorkspaceCleanupFailed";
                await ExecuteAsync(connection, """
                    UPDATE review_attempts
                       SET status = 'cleanup-failed', outcome = 'ReviewInfra',
                           failure_classification = $classification,
                           cleanup_idempotency_key = $key, cleaned_at = $now
                     WHERE id = $attempt;
                    UPDATE tasks
                       SET state = '4-auto-review', version = version + 1, updated_at = $now
                     WHERE id = $task;
                    """, ct, transaction,
                    ("$classification", classification), ("$key", request.IdempotencyKey),
                    ("$now", Iso(failedAt)), ("$attempt", attemptId), ("$task", attempt.TaskId));
                await InsertReviewRetryAsync(connection, transaction, attempt, failedAt, ct);
                await RecordDeliveryAsync(connection, transaction, attemptId, "cleanup", request.IdempotencyKey, payloadHash, ct);
                await AuditAsync(connection, transaction, actorId, "review.cleanup-failed", "review-attempt", attemptId,
                    JsonSerializer.Serialize(new { classification, request.WorkspaceRemoved }), ct);
                result = new ReviewCleanupResponse("cleanup-failed", attemptId, failedAt, true);
                return;
            }

            var now = UtcNow;
            var retry = false;
            if (string.IsNullOrWhiteSpace(attempt.ReportId))
            {
                retry = true;
                await ExecuteAsync(connection, """
                    UPDATE review_attempts
                       SET outcome = 'ReviewInfra',
                           failure_classification = $classification,
                           reported_at = $now
                     WHERE id = $attempt;
                    UPDATE tasks
                       SET state = '4-auto-review', version = version + 1, updated_at = $now
                     WHERE id = $task;
                    """, ct, transaction,
                    ("$classification", request.FailureClassification ?? "CleanupWithoutReport"),
                    ("$now", Iso(now)), ("$attempt", attemptId), ("$task", attempt.TaskId));
                await InsertReviewRetryAsync(connection, transaction, attempt, now, ct);
            }
            await ExecuteAsync(connection, """
                UPDATE review_attempts
                   SET status = 'cleaned', cleanup_idempotency_key = $key, cleaned_at = $now
                 WHERE id = $attempt;
                """, ct, transaction, ("$key", request.IdempotencyKey), ("$now", Iso(now)), ("$attempt", attemptId));
            await RecordDeliveryAsync(connection, transaction, attemptId, "cleanup", request.IdempotencyKey, payloadHash, ct);
            if (!string.IsNullOrWhiteSpace(attempt.ReportId)
                && !string.Equals(attempt.Outcome, "ReviewInfra", StringComparison.Ordinal))
            {
                await QueueReviewOrchestrationAsync(
                    connection, transaction, attempt, actorId, ct);
            }
            await AuditAsync(connection, transaction, actorId, "review.cleaned", "review-attempt", attemptId,
                JsonSerializer.Serialize(new { request.WorkspaceRemoved, retry, request.FailureClassification }), ct);
            result = new ReviewCleanupResponse("cleaned", attemptId, now, retry);
        }, ct);
        return result!;
    }

    public async Task<ReviewSubjectDto?> GetReviewSubjectAsync(string subjectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        return await ReadReviewSubjectAsync(connection, transaction, subjectId, ct);
    }

    public async Task<ReviewAttemptDto?> GetReviewAttemptAsync(string attemptId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, subject_id, task_id, attempt_number, status, executor_id,
                   host_id, fence, created_at, reported_at, cleaned_at, outcome,
                   failure_classification
              FROM review_attempts WHERE id = $attempt;
            """, ("$attempt", attemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReviewAttempt(reader) : null;
    }

    private static void ValidateReviewSubjectRequest(CreateReviewSubjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.SourceRunId)
            || string.IsNullOrWhiteSpace(request.RepositoryId)
            || string.IsNullOrWhiteSpace(request.ReviewPolicyHash)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Task, coding run, repository, policy, and idempotency identities are required.");
        if (!ValidDigest(request.ExpectedResultSha, 40, 64))
            throw new ArgumentException("Expected result SHA must be a full hexadecimal commit or content digest.");
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl)
            && string.IsNullOrWhiteSpace(request.SourceBundleArtifactId))
            throw new ArgumentException("An immutable repository source or source bundle is required.");
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl)
            && (string.IsNullOrWhiteSpace(request.SourceBundleArtifactId)
                || !ValidDigest(request.SourceBundleSha256, 64)))
            throw new ArgumentException("A source bundle review subject requires its SHA-256 content digest.");
        if (request.Plan.Commands.Count == 0 || request.Plan.RequiredAspects.Count == 0)
            throw new ArgumentException("Review plan commands and required aspects are required.");
        var commandIds = request.Plan.Commands.Select(command => command.StepId).ToHashSet(StringComparer.Ordinal);
        var preparationIds = (request.Plan.Preparation ?? [])
            .Select(command => command.StepId)
            .ToHashSet(StringComparer.Ordinal);
        if (commandIds.Count != request.Plan.Commands.Count
            || preparationIds.Count != (request.Plan.Preparation?.Count ?? 0)
            || commandIds.Overlaps(preparationIds))
            throw new ArgumentException("Review command step ids must be unique.");
        if (request.Plan.Commands.Any(command =>
                command.CompareToBaseline || ReviewCommandKinds.IsAgent(command.ExecutionKind))
            && string.IsNullOrWhiteSpace(request.Plan.IntegrationRef))
            throw new ArgumentException(
                "A baseline-compared or semantic review plan requires an integration ref.");
        if (request.Plan.Commands.Any(command =>
                ReviewCommandKinds.IsAgent(command.ExecutionKind)
                && (string.IsNullOrWhiteSpace(command.Prompt)
                    || string.IsNullOrWhiteSpace(command.CliType)
                    || string.IsNullOrWhiteSpace(command.Model))))
        {
            throw new ArgumentException(
                "An agent review command requires a prompt, CLI type, and model in the frozen plan.");
        }
        if (request.Plan.RequiresVisualReview
            && !request.Plan.RequiredAspects.Contains("visual", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("A visual review plan must require the visual aspect.");
    }

    private void ValidateReviewReClaimRequest(
        string attemptId,
        ReviewReClaimRequest request)
    {
        if (string.IsNullOrWhiteSpace(attemptId)
            || string.IsNullOrWhiteSpace(request.ExecutorId)
            || string.IsNullOrWhiteSpace(request.InstanceId)
            || string.IsNullOrWhiteSpace(request.PreviousLeaseId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException(
                "Attempt, executor, instance, previous lease, and idempotency identities are required.");
        }
        if (request.PreviousFence <= 0)
            throw new ArgumentException("Previous review fence must be positive.");
        if (request.RequestedTtlSeconds < _options.MinimumLeaseSeconds
            || request.RequestedTtlSeconds > _options.MaximumLeaseSeconds)
        {
            throw new ArgumentException(
                $"Review lease TTL must be between {_options.MinimumLeaseSeconds} and {_options.MaximumLeaseSeconds} seconds.");
        }
    }

    private static (string Outcome, string? Classification) ClassifyReviewReport(
        ReviewSubjectDto subject,
        ReviewReportRequest request,
        ReviewAuthorityRow attempt)
    {
        var resourceNamespace = attempt.ResourceNamespace;
        if (!string.Equals(request.Workspace.ResourceNamespace, resourceNamespace, StringComparison.Ordinal)
            || !string.Equals(request.Environment.ExecutorId, attempt.ExecutorId, StringComparison.Ordinal)
            || !string.Equals(request.Environment.InstanceId, attempt.InstanceId, StringComparison.Ordinal)
            || !string.Equals(request.Environment.HostId, attempt.HostId, StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("workspace", out var workspace)
            || !WorkspaceMatchesNamespace(workspace, resourceNamespace)
            || !string.Equals(Hash(workspace), request.Workspace.WorkspaceIdentity, StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("cache", out var cache)
            || !IsContainedPath(workspace, cache)
            || !request.Environment.Isolation.TryGetValue("temp", out var temp)
            || !IsContainedPath(workspace, temp)
            || string.Equals(cache, temp, StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("containers", out var containers)
            || !string.Equals(containers, resourceNamespace, StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("databases", out var databases)
            || !string.Equals(databases, resourceNamespace, StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("ports", out var ports)
            || !string.Equals(
                ports,
                $"{attempt.PortBase}-{attempt.PortBase + 7}",
                StringComparison.Ordinal)
            || !request.Environment.Isolation.TryGetValue("credentials", out var credentials)
            || !string.Equals(credentials, "review-read-only", StringComparison.Ordinal))
            return ("ReviewInfra", "ContainmentMismatch");
        if (!request.Environment.Toolchain.ContainsKey("runtime")
            || !request.Environment.Toolchain.ContainsKey("git")
            || subject.Plan.Commands.Select(command => command.StepId)
                .Concat((subject.Plan.Preparation ?? []).Select(command => command.StepId))
                .Any(stepId =>
                    !request.Environment.Toolchain.ContainsKey($"command:{stepId}")))
            return ("ReviewInfra", "ToolchainIdentityMissing");
        if (request.Commands.Any(command =>
                !string.Equals(command.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(command.ExecutorId)
                    && !string.Equals(command.ExecutorId, attempt.ExecutorId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(command.HostId)
                    && !string.Equals(command.HostId, attempt.HostId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(command.AttemptId)
                    && !string.Equals(command.AttemptId, attempt.AttemptId, StringComparison.Ordinal))))
            return ("ReviewInfra", "CommandAttributionMismatch");
        if (string.Equals(request.Outcome, "ReviewInfra", StringComparison.Ordinal)
            && request.FailureClassification is "SnapshotUnavailable" or "SourceBundleDigestMismatch")
            return ("ReviewInfra", request.FailureClassification);
        if (!string.Equals(subject.RepositoryId, request.Workspace.RepositoryId, StringComparison.Ordinal))
            return ("ReviewInfra", "RepositoryMismatch");
        if (!string.Equals(subject.ExpectedResultSha, request.Workspace.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(subject.ExpectedResultSha, request.Workspace.ActualHead, StringComparison.OrdinalIgnoreCase))
            return ("ReviewInfra", "ShaMismatch");
        if (request.Workspace.DirtyBefore) return ("ReviewInfra", "DirtyBefore");
        if (request.Workspace.DirtyAfter) return ("ReviewInfra", "MutatedAfter");
        if (string.IsNullOrWhiteSpace(request.Workspace.TreeHash))
            return ("ReviewInfra", "TreeHashMissing");
        if (string.Equals(request.Outcome, "ReviewInfra", StringComparison.Ordinal)
            && request.FailureClassification is "ToolUnavailable")
            return ("ReviewInfra", request.FailureClassification);
        if (request.Commands.Any(command =>
                !string.Equals(command.ExpectedResultSha, subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
                || (command.WorkspaceRole.StartsWith("baseline", StringComparison.Ordinal)
                    ? string.IsNullOrWhiteSpace(command.BaselineSha)
                      || !string.Equals(command.HeadBefore, command.BaselineSha, StringComparison.OrdinalIgnoreCase)
                      || string.IsNullOrWhiteSpace(command.TreeBefore)
                    : !string.Equals(command.HeadBefore, subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(
                          command.TreeBefore,
                          request.Workspace.TreeHash,
                          StringComparison.OrdinalIgnoreCase))))
            return ("ReviewInfra", "CommandSubjectMismatch");
        if (ReviewToolchainFailurePolicy.IsUnavailable(request.Commands, request.Artifacts))
            return ("ReviewInfra", "ToolUnavailable");
        if (string.Equals(request.Outcome, "ReviewInfra", StringComparison.Ordinal)
            && request.FailureClassification is "PreparationFailed")
            return ClassifyPreparationFailure(subject, request);
        var verdictAspects = request.Verdicts.Select(verdict => verdict.Aspect).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subject.Plan.RequiredAspects.Any(aspect => !verdictAspects.Contains(aspect)))
            return ("ReviewInfra", "IncompleteReviewReport");
        var commandKeys = request.Commands
            .Select(command => $"{command.Phase}\0{command.WorkspaceRole}\0{command.StepId}")
            .ToHashSet(StringComparer.Ordinal);
        if (commandKeys.Count != request.Commands.Count)
            return ("ReviewInfra", "DuplicateCommandEvidence");
        var candidateSteps = request.Commands
            .Where(command => command.Phase == "verification" && command.WorkspaceRole == "candidate")
            .Select(command => command.StepId)
            .ToHashSet(StringComparer.Ordinal);
        if (subject.Plan.Commands.Where(command => command.Required).Any(command => !candidateSteps.Contains(command.StepId)))
            return ("ReviewInfra", "IncompleteCommandEvidence");
        var candidatePreparation = request.Commands
            .Where(command => command.Phase == "preparation" && command.WorkspaceRole == "candidate")
            .Select(command => command.StepId)
            .ToHashSet(StringComparer.Ordinal);
        if ((subject.Plan.Preparation ?? []).Any(command => !candidatePreparation.Contains(command.StepId)))
            return ("ReviewInfra", "IncompletePreparationEvidence");
        foreach (var command in request.Commands)
        {
            var planned = subject.Plan.Commands.SingleOrDefault(item =>
                string.Equals(item.StepId, command.StepId, StringComparison.Ordinal));
            var plannedPreparation = (subject.Plan.Preparation ?? []).SingleOrDefault(item =>
                string.Equals(item.StepId, command.StepId, StringComparison.Ordinal));
            if (!CommandMatchesPlan(command, planned, plannedPreparation))
                return ("ReviewInfra", "CommandPlanMismatch");
            if (planned?.CompareToBaseline == true
                && command.BaselineSha is not null
                && (!ValidDigest(command.BaselineSha, 40, 64)
                    || command.NewFailures is null
                    || command.PreExistingFailures is null
                    || (command.NewFailures.Count > 0 && !command.RetryPerformed)
                    || (command.FlakyQuarantinedFailures is { Count: > 0 }
                        && (!command.RetryPerformed
                            || command.FlakyQuarantinedFailures.Any(command.NewFailures.Contains)))))
                return ("ReviewInfra", "BaselineEvidenceInvalid");
        }
        if (request.Artifacts.Any(artifact => !ValidArtifact(artifact)))
            return ("ReviewInfra", "ArtifactEvidenceInvalid");
        if (request.Commands.Any(command =>
                command.FinishedAt < command.StartedAt
                || !ValidDigest(command.StdoutSha256, 64)
                || !ValidDigest(command.StderrSha256, 64)))
            return ("ReviewInfra", "CommandEvidenceInvalid");
        var artifactDigests = request.Artifacts
            .Select(artifact => artifact.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Commands.Any(command =>
                !artifactDigests.Contains(command.StdoutSha256)
                || !artifactDigests.Contains(command.StderrSha256)))
            return ("ReviewInfra", "ArtifactEvidenceIncomplete");
        if (request.Commands.Any(command => command.Signal is not null || command.ExitCode is null or < 0))
            return ("ReviewInfra", "CommandTerminated");
        if (request.Verdicts.Any(verdict =>
                verdict.Status is not ("pass" or "concerns" or "block" or "fail")))
            return ("ReviewInfra", "InvalidAspectVerdict");
        var commandFailures = request.Commands.Any(command =>
        {
            if (command.Phase != "verification" || command.WorkspaceRole != "candidate")
                return false;
            var planned = subject.Plan.Commands.Single(item =>
                string.Equals(item.StepId, command.StepId, StringComparison.Ordinal));
            if (planned.CompareToBaseline && command.NewFailures is { Count: > 0 })
                return true;
            if (command.ExitCode == 0) return false;
            return !planned.CompareToBaseline
                   || command.BaselineSha is null
                   || command.NewFailures is null
                   || command.NewFailures.Count > 0;
        });
        if (commandFailures
            || ReviewGradingPolicy.Grade(request.Verdicts.Select(verdict => verdict.Status))
                == ReviewGrade.ProductFailure)
            return ("ProductFailure", request.FailureClassification ?? "ReviewFinding");
        if (string.Equals(request.Outcome, "ReviewInfra", StringComparison.Ordinal))
            return ("ReviewInfra", string.IsNullOrWhiteSpace(request.FailureClassification)
                ? "UnclassifiedReviewInfrastructure"
                : request.FailureClassification);
        if (string.Equals(request.Outcome, "Pass", StringComparison.Ordinal)
            || string.Equals(request.Outcome, "ProductFailure", StringComparison.Ordinal))
            return ("Pass", request.FailureClassification);
        return ("ReviewInfra", "InvalidReviewOutcome");
    }

    private static (string Outcome, string? Classification) ClassifyPreparationFailure(
        ReviewSubjectDto subject,
        ReviewReportRequest request)
    {
        var preparation = request.Commands
            .Where(command => command.Phase == "preparation")
            .ToArray();
        var failed = preparation.LastOrDefault(command =>
            command.ExitCode != 0 || command.Signal is not null);
        if (failed is null)
            return ("ReviewInfra", "IncompletePreparationEvidence");
        var planned = (subject.Plan.Preparation ?? []).SingleOrDefault(command =>
            string.Equals(command.StepId, failed.StepId, StringComparison.Ordinal));
        if (!CommandMatchesPlan(
                failed,
                plannedCommand: null,
                plannedPreparation: planned))
            return ("ReviewInfra", "CommandPlanMismatch");
        if (failed.Budget is null
            || failed.Budget.LimitMs <= 0
            || failed.Budget.ConsumedMs < 0)
            return ("ReviewInfra", "CommandBudgetEvidenceInvalid");
        if (!HasUnabridgedArtifact(request.Artifacts, failed.StdoutSha256)
            || !HasUnabridgedArtifact(request.Artifacts, failed.StderrSha256))
            return ("ReviewInfra", "ArtifactEvidenceIncomplete");
        return ("ReviewInfra", "PreparationFailed");
    }

    private static bool CommandMatchesPlan(
        ReviewCommandEvidenceDto command,
        ReviewCommandDto? plannedCommand,
        ReviewPreparationCommandDto? plannedPreparation)
    {
        if (command.Phase == "preparation")
        {
            return plannedPreparation is not null
                   && string.Equals(command.Aspect, "preparation", StringComparison.Ordinal)
                   && string.Equals(plannedPreparation.FileName, command.FileName, StringComparison.Ordinal)
                   && plannedPreparation.Arguments.SequenceEqual(command.Arguments, StringComparer.Ordinal)
                   && (command.WorkspaceRole == "candidate"
                       || command.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal));
        }
        return command.Phase == "verification"
               && plannedCommand is not null
               && string.Equals(plannedCommand.Aspect, command.Aspect, StringComparison.Ordinal)
               && string.Equals(plannedCommand.FileName, command.FileName, StringComparison.Ordinal)
               && plannedCommand.Arguments.SequenceEqual(command.Arguments, StringComparer.Ordinal)
               && string.Equals(plannedCommand.ExecutionKind, command.ExecutionKind, StringComparison.OrdinalIgnoreCase)
               && (!ReviewCommandKinds.IsAgent(plannedCommand.ExecutionKind)
                   || (string.Equals(plannedCommand.Model, command.Model, StringComparison.Ordinal)
                       && string.Equals(plannedCommand.ThinkingLevel, command.ThinkingLevel, StringComparison.Ordinal)))
               && (command.WorkspaceRole == "candidate"
                   || command.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal));
    }

    private static bool ValidArtifact(ReviewArtifactEvidenceDto artifact)
    {
        if (!ValidDigest(artifact.Sha256, 64) || artifact.SizeBytes < 0) return false;
        if (artifact.ContentBase64 is null) return true;
        try
        {
            var bytes = Convert.FromBase64String(artifact.ContentBase64);
            return bytes.LongLength == artifact.SizeBytes
                   && string.Equals(
                       Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                       artifact.Sha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasUnabridgedArtifact(
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
        string digest)
        => artifacts.Any(artifact =>
            string.Equals(artifact.Sha256, digest, StringComparison.OrdinalIgnoreCase)
            && artifact.ContentBase64 is not null
            && ValidArtifact(artifact));

    private async Task InsertReviewRetryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReviewAuthorityRow attempt,
        DateTime now,
        CancellationToken ct)
    {
        var exists = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT count(*) FROM review_attempts
             WHERE subject_id = $subject AND attempt_number > $number;
            """, ct, transaction, ("$subject", attempt.SubjectId), ("$number", attempt.AttemptNumber)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (exists > 0) return;
        await ExecuteAsync(connection, """
            INSERT INTO review_attempts(
                id, subject_id, task_id, attempt_number, status, created_at)
            VALUES ($id, $subject, $task, $number, 'queued', $now);
            """, ct, transaction,
            ("$id", $"rat_{Guid.NewGuid():N}"), ("$subject", attempt.SubjectId),
            ("$task", attempt.TaskId), ("$number", attempt.AttemptNumber + 1), ("$now", Iso(now)));
    }

    private static async Task EnsureReviewSubjectCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReviewSubjectDto subject,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT t.state,
                   (
                       SELECT r.id
                         FROM runs r
                        WHERE r.task_id = t.id
                          AND r.result_sha IS NOT NULL
                        ORDER BY coalesce(r.finished_at, r.created_at) DESC, r.rowid DESC
                        LIMIT 1
                   )
              FROM tasks t
             WHERE t.id = $task;
            """, transaction, ("$task", subject.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException("Review task was not found.");
        var state = reader.GetString(0);
        var latestResultRun = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.Equals(state, "4-auto-review", StringComparison.Ordinal)
            || !string.Equals(latestResultRun, subject.SourceRunId, StringComparison.Ordinal))
            throw new TaskServerConflictException(
                "review-subject-not-current",
                "Review evidence is bound to a result that no longer owns the task's Auto Review lifecycle.");
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string type,
        CancellationToken ct)
    {
        await using var command = Command(connection, $"PRAGMA table_info({table});");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        await reader.DisposeAsync();
        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type};", ct);
    }

    private static void ValidateOptionalSourceField(SqliteDataReader reader, int ordinal, string? expected, string name)
    {
        var actual = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new TaskServerConflictException("review-source-mismatch", $"Review {name} does not match the fenced coding result.");
    }

    private static async Task<ReviewSubjectDto?> ReadReviewSubjectByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, task_id, source_run_id, repository_id, repository_url,
                   expected_result_sha, result_ref, source_bundle_artifact_id,
                   source_bundle_sha256, coding_host_id, review_policy_hash,
                   plan_json, created_at
              FROM review_subjects WHERE idempotency_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReviewSubject(reader) : null;
    }

    private static async Task<ReviewSubjectDto?> ReadReviewSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subjectId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, task_id, source_run_id, repository_id, repository_url,
                   expected_result_sha, result_ref, source_bundle_artifact_id,
                   source_bundle_sha256, coding_host_id, review_policy_hash,
                   plan_json, created_at
              FROM review_subjects WHERE id = $id;
            """, transaction, ("$id", subjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReviewSubject(reader) : null;
    }

    private static ReviewSubjectDto ReadReviewSubject(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            JsonSerializer.Deserialize<ReviewPlanDto>(reader.GetString(11), ReviewJson)
                ?? throw new InvalidOperationException("Stored review plan is invalid."),
            Parse(reader.GetString(12)));

    private static ReviewSubjectDto ReadReviewSubjectFromClaim(SqliteDataReader reader)
        => new(
            reader.GetString(1), reader.GetString(2), reader.GetString(13), reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20), reader.GetString(21),
            JsonSerializer.Deserialize<ReviewPlanDto>(reader.GetString(22), ReviewJson)
                ?? throw new InvalidOperationException("Stored review plan is invalid."),
            Parse(reader.GetString(23)));

    private static ReviewAttemptDto ReadReviewAttempt(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
            Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    private static async Task<ReviewExecutorRow> ReadReviewExecutorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executorId,
        string instanceId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT host_id, instance_id, capabilities_json, status
              FROM runners WHERE id = $id;
            """, transaction, ("$id", executorId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Review executor is not registered.");
        if (!string.Equals(reader.GetString(1), instanceId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-instance-stale", "Review executor instance id is not current.");
        if (!string.Equals(reader.GetString(3), "active", StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-not-active", "Review executor is not active.");
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(2), ReviewJson) ?? [];
        if (!capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException("review-capability-required", "Runner did not advertise the Remote Review Executor capability.");
        if (capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "review-capability-required",
                "A coding service identity cannot claim separately fenced review work.");
        return new ReviewExecutorRow(
            reader.GetString(0),
            capabilities.ToHashSet(StringComparer.Ordinal));
    }

    private static async Task<ReviewAuthorityRow> ReadReviewAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, subject_id, task_id, attempt_number, status, executor_id,
                   instance_id, host_id, lease_id, fence, acquired_at, expires_at,
                   report_id, report_sha256, report_idempotency_key, outcome,
                   failure_classification, summary, reported_at, cleaned_at, port_base,
                   resource_namespace, created_at
              FROM review_attempts WHERE id = $attempt;
            """, transaction, ("$attempt", attemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Review attempt was not found.");
        return new ReviewAuthorityRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : Parse(reader.GetString(18)),
            reader.IsDBNull(19) ? null : Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
            reader.IsDBNull(21)
                ? ResourceNamespace(reader.GetString(0), reader.GetInt64(9))
                : reader.GetString(21),
            Parse(reader.GetString(22)));
    }

    private static void ValidateReviewAuthority(
        ReviewAuthorityRow attempt,
        string executorId,
        string instanceId,
        string leaseId,
        long fence,
        bool requireLeased)
    {
        if (!string.Equals(attempt.ExecutorId, executorId, StringComparison.Ordinal)
            || !string.Equals(attempt.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(attempt.LeaseId, leaseId, StringComparison.Ordinal)
            || attempt.Fence != fence)
            throw new TaskServerConflictException("stale-review-fence", "Review lease, executor instance, or fence is stale.");
        if (requireLeased && !string.Equals(attempt.Status, "leased", StringComparison.Ordinal))
            throw new TaskServerConflictException("review-lease-not-active", $"Review attempt status is '{attempt.Status}'.");
        if (!requireLeased && attempt.Status is not ("leased" or "reported"))
            throw new TaskServerConflictException("review-cleanup-not-current", $"Review attempt status is '{attempt.Status}'.");
    }

    private static ReviewLeaseDto ToReviewLease(ReviewAuthorityRow row)
        => new(
            row.LeaseId!, row.AttemptId, row.SubjectId, row.ExecutorId!, row.InstanceId!,
            row.HostId!, row.Fence, row.AcquiredAt!.Value, row.ExpiresAt!.Value,
            row.Status == "leased" ? "active" : row.Status, row.ResourceNamespace,
            row.PortBase);

    private static ReviewAttemptDto ToReviewAttempt(ReviewAuthorityRow row)
        => new(
            row.AttemptId,
            row.SubjectId,
            row.TaskId,
            row.AttemptNumber,
            row.Status,
            row.ExecutorId,
            row.HostId,
            row.Fence,
            row.CreatedAt,
            row.ReportedAt,
            row.CleanedAt,
            row.Outcome,
            row.FailureClassification);

    private static ReviewReportDto ToReviewReport(ReviewAuthorityRow row)
        => new(
            row.ReportId!, row.AttemptId, row.SubjectId, row.Outcome!,
            row.FailureClassification, row.Summary, row.ReportSha256!, row.ReportedAt!.Value,
            string.Equals(row.Outcome, "ReviewInfra", StringComparison.Ordinal),
            "4-auto-review");

    private async Task QueueReviewOrchestrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReviewAuthorityRow attempt,
        string actorId,
        CancellationToken ct)
    {
        string? projectId = null;
        string? reportJson = null;
        string? taskState = null;
        string? latestResultRunId = null;
        await using (var command = Command(connection, """
            SELECT task.project_id,
                   review.report_json,
                   task.state,
                   (
                       SELECT run.id
                         FROM runs run
                        WHERE run.task_id = task.id
                          AND run.result_sha IS NOT NULL
                        ORDER BY coalesce(run.finished_at, run.created_at) DESC, run.rowid DESC
                        LIMIT 1
                   )
              FROM review_attempts review
              JOIN tasks task ON task.id = review.task_id
             WHERE review.id = $attempt;
            """, transaction, ("$attempt", attempt.AttemptId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                projectId = reader.GetString(0);
                reportJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                taskState = reader.GetString(2);
                latestResultRunId = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(reportJson)
            || string.IsNullOrWhiteSpace(attempt.Outcome)
            || string.IsNullOrWhiteSpace(attempt.ReportSha256))
        {
            throw new TaskServerConflictException(
                "review-report-incomplete",
                "A cleaned review needs a complete stored report before orchestration can start.");
        }

        var report = JsonSerializer.Deserialize<ReviewReportRequest>(reportJson, ReviewJson)
                     ?? throw new TaskServerConflictException(
                         "review-report-incomplete",
                         "The stored review report cannot be read for orchestration.");
        var subject = await ReadReviewSubjectAsync(connection, transaction, attempt.SubjectId, ct)
                      ?? throw new KeyNotFoundException("Review subject was not found.");
        if (!string.Equals(taskState, "4-auto-review", StringComparison.Ordinal)
            || !string.Equals(latestResultRunId, subject.SourceRunId, StringComparison.Ordinal))
        {
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "review.orchestration-superseded",
                "review-attempt",
                attempt.AttemptId,
                JsonSerializer.Serialize(new
                {
                    taskState,
                    subject.SourceRunId,
                    latestResultRunId,
                }),
                ct);
            return;
        }
        var gates = report.Commands.Select(command =>
        {
            var planned = subject.Plan.Commands.Single(item =>
                string.Equals(item.StepId, command.StepId, StringComparison.Ordinal));
            var failed = command.Signal is not null
                         || command.ExitCode is null or < 0
                         || (planned.CompareToBaseline
                             && command.NewFailures is { Count: > 0 })
                         || (command.ExitCode != 0
                             && (!planned.CompareToBaseline
                                 || command.BaselineSha is null
                                 || command.NewFailures is null
                                 || command.NewFailures is { Count: > 0 }));
            return new ReviewOrchestrationGateDto(
                command.StepId,
                command.Aspect,
                failed ? "failed" : "passed",
                failed ? attempt.FailureClassification ?? "ReviewCommandFailed" : null);
        }).ToArray();
        var payload = new ReviewOrchestrationPayloadDto(
            subject.SourceRunId,
            subject.SubjectId,
            attempt.AttemptId,
            subject.ExpectedResultSha,
            subject.ReviewPolicyHash,
            attempt.ReportSha256,
            attempt.Outcome,
            attempt.FailureClassification,
            attempt.Summary,
            report.Verdicts,
            gates);
        await CreateOrchestrationRunCoreAsync(
            connection,
            transaction,
            projectId,
            new CreateOrchestrationRunRequest(
                attempt.TaskId,
                JsonSerializer.Serialize(payload, ReviewJson),
                $"review-orchestration:{attempt.AttemptId}"),
            actorId,
            ct);
    }

    private static async Task<bool> DeliveryExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        string operation,
        string idempotencyKey,
        string payloadHash,
        CancellationToken ct)
    {
        var deliveryKey = $"{operation}:{attemptId}:{idempotencyKey}";
        await using var command = Command(connection, """
            SELECT attempt_id, operation, payload_sha256
              FROM review_deliveries WHERE delivery_key = $key;
            """, transaction, ("$key", deliveryKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        if (!string.Equals(reader.GetString(0), attemptId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), operation, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), payloadHash, StringComparison.Ordinal))
            throw new TaskServerConflictException("idempotency-conflict", "Review delivery key is bound to a different payload.");
        return true;
    }

    private static async Task<ReviewClaimResponse?> ReadReviewReClaimDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        string idempotencyKey,
        string payloadHash,
        CancellationToken ct)
    {
        var deliveryKey = $"reclaim:{attemptId}:{idempotencyKey}";
        await using var command = Command(connection, """
            SELECT attempt_id, operation, payload_sha256, response_json
              FROM review_deliveries WHERE delivery_key = $key;
            """, transaction, ("$key", deliveryKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (!string.Equals(reader.GetString(0), attemptId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), "reclaim", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), payloadHash, StringComparison.Ordinal))
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "Review reclaim idempotency key is bound to a different payload.");
        }
        if (reader.IsDBNull(3))
            throw new InvalidOperationException("Stored review reclaim delivery has no durable response.");
        return JsonSerializer.Deserialize<ReviewClaimResponse>(reader.GetString(3), ReviewJson)
               ?? throw new InvalidOperationException("Stored review reclaim response is invalid.");
    }

    private async Task RecordDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        string operation,
        string idempotencyKey,
        string payloadHash,
        CancellationToken ct,
        string? responseJson = null)
        => await ExecuteAsync(connection, """
            INSERT INTO review_deliveries(
                delivery_key, attempt_id, operation, payload_sha256, response_json, created_at)
            VALUES ($key, $attempt, $operation, $hash, $response, $now);
            """, ct, transaction,
            ("$key", $"{operation}:{attemptId}:{idempotencyKey}"), ("$attempt", attemptId),
            ("$operation", operation), ("$hash", payloadHash), ("$response", responseJson),
            ("$now", Iso(UtcNow)));

    private static void ValidateReviewSubjectReplay(
        ReviewSubjectDto existing,
        CreateReviewSubjectRequest request)
    {
        if (!string.Equals(existing.TaskId, request.TaskId, StringComparison.Ordinal)
            || !string.Equals(existing.SourceRunId, request.SourceRunId, StringComparison.Ordinal)
            || !string.Equals(existing.RepositoryId, request.RepositoryId, StringComparison.Ordinal)
            || !string.Equals(existing.ExpectedResultSha, request.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.ReviewPolicyHash, request.ReviewPolicyHash, StringComparison.Ordinal)
            || HashJson(existing.Plan) != HashJson(request.Plan))
            throw new TaskServerConflictException("idempotency-conflict", "Review subject idempotency key is bound to different immutable facts.");
    }

    private static bool ValidDigest(string? value, params int[] lengths)
        => value is not null
           && lengths.Contains(value.Length)
           && value.All(Uri.IsHexDigit);

    private static string HashJson<T>(T value)
        => Hash(JsonSerializer.Serialize(value, ReviewJson));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsContainedPath(string root, string path)
    {
        var normalizedRoot = NormalizeReportedPath(root);
        var normalizedPath = NormalizeReportedPath(path);
        return normalizedRoot is not null
               && normalizedPath is not null
               && normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    private static bool WorkspaceMatchesNamespace(string workspace, string resourceNamespace)
    {
        var normalized = NormalizeReportedPath(workspace);
        return normalized is not null
               && string.Equals(
                   normalized.Split('/')[^1],
                   resourceNamespace,
                   StringComparison.Ordinal);
    }

    private static string? NormalizeReportedPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return null;
        var segments = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            return null;
        return string.Join('/', segments);
    }

    private static string ResourceNamespace(string attemptId, long fence)
        => "review-" + new string(attemptId.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
           + "-f" + fence.ToString(CultureInfo.InvariantCulture);

    private static bool SupportsSubject(ReviewExecutorRow executor, ReviewSubjectDto subject)
    {
        if (!string.IsNullOrWhiteSpace(subject.RepositoryUrl)
            && !executor.Capabilities.Contains(ReviewCapabilities.GitMaterialization))
            return false;
        if (!string.IsNullOrWhiteSpace(subject.SourceBundleArtifactId)
            && !executor.Capabilities.Contains(ReviewCapabilities.SourceBundleMaterialization))
            return false;
        if (subject.Plan.RequiresVisualReview
            && !executor.Capabilities.Contains(ReviewCapabilities.VisionReview))
            return false;
        if (subject.Plan.Commands.Any(command => command.CompareToBaseline)
            && !executor.Capabilities.Contains(ReviewCapabilities.BaselineComparison))
            return false;
        if (subject.Plan.Preparation is { Count: > 0 }
            && !executor.Capabilities.Contains(ReviewCapabilities.DependencyPreparation))
            return false;
        if (subject.Plan.RequiredAspects.Any(aspect =>
                aspect is "completion" or "requirements" or "code-quality" or "documentation" or "evidence")
            && !executor.Capabilities.Contains(ReviewCapabilities.SemanticReview))
            return false;
        foreach (var command in subject.Plan.Commands.Where(command =>
                     ReviewCommandKinds.IsAgent(command.ExecutionKind)))
        {
            if (!executor.Capabilities.Contains(ReviewCapabilities.SemanticReview))
                return false;
            if (!string.IsNullOrWhiteSpace(command.CliType)
                && (!executor.Capabilities.Contains(CapabilityProtocol.CliExecution(command.CliType))
                    || !executor.Capabilities.Contains(CapabilityProtocol.ProviderAuthentication(command.CliType))))
                return false;
        }
        return true;
    }

    private sealed record ReviewExecutorRow(string HostId, IReadOnlySet<string> Capabilities);

    private sealed record ReviewAuthorityRow(
        string AttemptId,
        string SubjectId,
        string TaskId,
        int AttemptNumber,
        string Status,
        string? ExecutorId,
        string? InstanceId,
        string? HostId,
        string? LeaseId,
        long Fence,
        DateTime? AcquiredAt,
        DateTime? ExpiresAt,
        string? ReportId,
        string? ReportSha256,
        string? ReportIdempotencyKey,
        string? Outcome,
        string? FailureClassification,
        string? Summary,
        DateTime? ReportedAt,
        DateTime? CleanedAt,
        int PortBase,
        string ResourceNamespace,
        DateTime CreatedAt);
}
