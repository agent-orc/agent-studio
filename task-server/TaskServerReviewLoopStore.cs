using System.Globalization;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// The standalone review loop (AGT-3061, AGT-W65 I11): the review subject on
/// coding completion, the effects of an engine fix round, and the integration
/// of an accepted delivery under the project's publication lease. The engine
/// decides every transition; this store collects the facts it needs and
/// applies the returned decision in one transaction.
/// </summary>
public sealed partial class TaskServerStore
{
    private const int MinimumPublicationLeaseSeconds = 60;
    private const int MaximumPublicationLeaseSeconds = 1800;
    private const string FixRoundContinuationReason = "review-finding-round";

    internal async Task ApplyStandaloneReviewLoopMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS project_review_plans(
                project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
                version INTEGER NOT NULL,
                review_policy_hash TEXT NOT NULL,
                plan_json TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS integration_commands(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                orchestration_run_id TEXT NOT NULL UNIQUE,
                source_run_id TEXT NOT NULL,
                review_attempt_id TEXT NOT NULL,
                repository_id TEXT NOT NULL,
                repository_url TEXT NOT NULL,
                integration_ref TEXT NOT NULL,
                result_ref TEXT NOT NULL,
                result_sha TEXT NOT NULL,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                integrated_sha TEXT,
                last_outcome TEXT,
                reason TEXT,
                report_key TEXT,
                report_response_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_integration_commands_queue
                ON integration_commands(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_integration_commands_task
                ON integration_commands(task_id, created_at);
            CREATE TABLE IF NOT EXISTS publication_leases(
                project_id TEXT NOT NULL REFERENCES projects(id),
                integration_ref TEXT NOT NULL,
                lease_id TEXT NOT NULL,
                integration_id TEXT NOT NULL,
                runner_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL,
                PRIMARY KEY(project_id, integration_ref)
            );
            """, ct);
        // D17: the reason the base was recorded travels with the claim.
        await EnsureColumnAsync(connection, "task_starting_points", "reason", "TEXT", ct);
        // The base a permit-accepted attempt was handed, so a replayed
        // acceptance returns the same base after the starting point is spent.
        await EnsureColumnAsync(connection, "runs", "continuation_base_ref", "TEXT", ct);
        await EnsureColumnAsync(connection, "runs", "continuation_base_sha", "TEXT", ct);
        await EnsureColumnAsync(connection, "runs", "continuation_base_reason", "TEXT", ct);
    }

    public async Task<ProjectReviewPlanDto?> GetProjectReviewPlanAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadProjectReviewPlanAsync(connection, null, projectId, ct);
    }

    public async Task<ProjectReviewPlanDto> UpsertProjectReviewPlanAsync(
        string projectId,
        UpsertProjectReviewPlanRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ReviewPolicyHash))
            throw new ArgumentException("A review policy hash is required.");
        if (request.Plan.BriefSha256 is not null)
            throw new ArgumentException("A project review plan cannot pin one task brief.");
        ProjectReviewPlanDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            _ = await ScalarAsync(connection, "SELECT id FROM projects WHERE id = $project;", ct, transaction,
                    ("$project", projectId))
                ?? throw new KeyNotFoundException("Project was not found.");
            var current = await ReadProjectReviewPlanAsync(connection, transaction, projectId, ct);
            if ((current?.Version ?? 0) != (request.ExpectedVersion ?? 0))
                throw new TaskServerConflictException("resource-version-mismatch",
                    $"Expected review plan version {request.ExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, current version is {current?.Version ?? 0}.");
            var repository = await ReadProjectRepositoryAsync(connection, transaction, projectId, ct);
            var plan = request.Plan with { IntegrationRef = request.Plan.IntegrationRef ?? repository?.IntegrationRef };
            // Prove now that every completion can seal this plan; a completion
            // must never fail on a template defect.
            ValidateReviewSubjectRequest(new CreateReviewSubjectRequest(
                "template-task", "template-run", repository?.RepositoryId ?? "template-repository",
                repository?.RepositoryUrl ?? "https://template.invalid/repository.git",
                new string('0', 40), null, null, null, null, request.ReviewPolicyHash,
                ReviewLibraryStepPolicy.Seal(ReviewPlanResourcePolicy.Apply(plan), new string('0', 40)),
                "template"));
            var now = UtcNow;
            var version = (current?.Version ?? 0) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO project_review_plans(project_id, version, review_policy_hash, plan_json, updated_at, updated_by)
                VALUES ($project, $version, $policy, $plan, $now, $actor)
                ON CONFLICT(project_id) DO UPDATE SET
                    version = excluded.version,
                    review_policy_hash = excluded.review_policy_hash,
                    plan_json = excluded.plan_json,
                    updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by;
                """, ct, transaction,
                ("$project", projectId), ("$version", version), ("$policy", request.ReviewPolicyHash.Trim()),
                ("$plan", JsonSerializer.Serialize(plan, ReviewJson)), ("$now", Iso(now)), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, "review.project-plan-updated", "project", projectId,
                JsonSerializer.Serialize(new { version, request.ReviewPolicyHash, commands = plan.Commands.Count }), ct);
            result = new ProjectReviewPlanDto(projectId, version, request.ReviewPolicyHash.Trim(), plan, now, actorId);
        }, ct);
        return result!;
    }

    private static async Task<ProjectReviewPlanDto?> ReadProjectReviewPlanAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT version, review_policy_hash, plan_json, updated_at, updated_by
              FROM project_review_plans WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProjectReviewPlanDto(projectId, reader.GetInt64(0), reader.GetString(1),
            JsonSerializer.Deserialize<ReviewPlanDto>(reader.GetString(2), ReviewJson)!,
            Parse(reader.GetString(3)), reader.GetString(4));
    }

    /// <summary>
    /// F4: the standalone server creates the review subject itself when a
    /// coding attempt completes with a settled result envelope, from the
    /// fenced result and the project's review plan, in the completion
    /// transaction. A project without a review plan keeps the explicit
    /// subject route.
    /// </summary>
    private async Task CreateCompletionReviewSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string taskId,
        string outcome,
        ImmutableResultEnvelope envelope,
        string actorId,
        CancellationToken ct)
    {
        var projectId = Convert.ToString(await ScalarAsync(connection,
            "SELECT project_id FROM tasks WHERE id = $task;", ct, transaction, ("$task", taskId)),
            CultureInfo.InvariantCulture)!;
        var template = await ReadProjectReviewPlanAsync(connection, transaction, projectId, ct);
        if (template is null) return;
        var normalized = outcome.Trim().ToLowerInvariant() is "noop" or "no-op"
            ? CompletionOutcomeKinds.NoOp : CompletionOutcomeKinds.Done;
        if (!TransitionDecisionPolicy.CreateReview(false, normalized, verifiedDelivery: true, reportOnly: false))
            return;
        var codingHost = Convert.ToString(await ScalarAsync(connection, """
            SELECT COALESCE(r.coding_host_id, runner.host_id)
              FROM runs r LEFT JOIN runners runner ON runner.id = r.runner_id
             WHERE r.id = $run;
            """, ct, transaction, ("$run", runId)), CultureInfo.InvariantCulture);
        var request = new CreateReviewSubjectRequest(
            taskId, runId, envelope.RepositoryId, envelope.RepositoryUrl, envelope.ResultSha,
            envelope.ImmutableRemoteRef, null, null, string.IsNullOrWhiteSpace(codingHost) ? null : codingHost,
            template.ReviewPolicyHash,
            ReviewLibraryStepPolicy.Seal(ReviewPlanResourcePolicy.Apply(template.Plan), envelope.ResultSha),
            $"completion-review:{runId}");
        try
        {
            ValidateReviewSubjectRequest(request);
        }
        catch (ArgumentException exception)
        {
            // The card stays visibly in Auto Review without a subject; the
            // audit names the defect instead of failing the completion forever.
            await AuditAsync(connection, transaction, actorId, "review.subject-not-created", "run", runId,
                JsonSerializer.Serialize(new { taskId, reason = exception.Message, template.Version }), ct);
            return;
        }
        if (await ReadReviewSubjectByIdempotencyAsync(connection, transaction, request.IdempotencyKey, ct) is not null)
            return;
        await InsertReviewSubjectAsync(connection, transaction, request, actorId, ct);
    }

    private static async Task<(string? Ref, string? Sha, string? Reason)> ReadTaskStartingPointAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT result_ref, result_sha, reason FROM task_starting_points WHERE task_id = $task;",
            transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : (null, null, null);
    }

    /// <summary>
    /// Hands a permit-accepted attempt the same round a claim would get: the
    /// recorded starting point (spent by this attempt) and the next ordered
    /// continuation round, reserved for this run until its worker starts.
    /// </summary>
    private static async Task BindPermitContinuationAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, string runId, long fence,
        CancellationToken ct)
    {
        var (baseRef, baseSha, baseReason) = await ReadTaskStartingPointAsync(connection, transaction, taskId, ct);
        if (baseRef is not null)
            await ExecuteAsync(connection, """
                UPDATE runs SET continuation_base_ref = $ref, continuation_base_sha = $sha,
                    continuation_base_reason = $reason
                 WHERE id = $run;
                DELETE FROM task_starting_points WHERE task_id = $task;
                """, ct, transaction, ("$ref", baseRef), ("$sha", baseSha), ("$reason", baseReason),
                ("$run", runId), ("$task", taskId));
        if (await ReadNextContinuationAsync(connection, transaction, taskId, ct) is { } intent)
            await ExecuteAsync(connection, """
                UPDATE continuation_intents SET status = 'claimed', run_id = $run, fence = $fence
                 WHERE command_id = $command AND status = 'queued';
                """, ct, transaction, ("$run", runId), ("$fence", fence), ("$command", intent.Receipt.CommandId));
    }

    private static async Task<(string? Ref, string? Sha, string? Reason)> ReadRunContinuationBaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT continuation_base_ref, continuation_base_sha, continuation_base_reason
              FROM runs WHERE id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(0) && !reader.IsDBNull(1)
            ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : (null, null, null);
    }

    private static async Task<FollowUpDeliveryDto?> ReadRunFollowUpAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, string runId, CancellationToken ct)
    {
        var commandId = Convert.ToString(await ScalarAsync(connection, """
            SELECT command_id FROM continuation_intents
             WHERE task_id = $task AND run_id = $run AND status IN ('claimed', 'consumed');
            """, ct, transaction, ("$task", taskId), ("$run", runId)), CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(commandId)) return null;
        var intent = await ReadContinuationAsync(connection, transaction, commandId, ct);
        return intent is null ? null : ToFollowUpDelivery(intent, runId);
    }

    /// <summary>
    /// The engine's typed decision for one stage, when its output carries one.
    /// The Task Server applies it only when it names this card, matches the
    /// stage action and, for a fix round, continues on the reviewed delivery
    /// of this run (D17). An engine output without a decision is compatible
    /// with engines before AGT-3061.
    /// </summary>
    private static TransitionDecision? ReadEngineTransition(
        string outputJson, OrchestrationRunDto run, OrchestrationAction action, StagePayload? payload)
    {
        using var output = JsonDocument.Parse(outputJson);
        if (output.RootElement.ValueKind != JsonValueKind.Object
            || !output.RootElement.TryGetProperty("transition", out var element)
            || element.ValueKind != JsonValueKind.Object)
            return null;
        var transition = element.Deserialize<TransitionDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new TaskServerConflictException("engine-decision-invalid", "The engine decision is unreadable.");
        if (!string.Equals(transition.Receipt.CardKey, run.TaskId, StringComparison.Ordinal))
            throw new TaskServerConflictException("engine-decision-mismatch",
                "The engine decision names a different card.");
        if (StageDecisionPolicy.ToStageAction(transition.Action) != action)
            throw new TaskServerConflictException("engine-decision-mismatch",
                $"The engine decision {transition.Action} does not match the stage action {action}.");
        if (transition.ContinuationBase is { } continuation
            && (!string.Equals(continuation.ResultRef, payload?.ResultRef, StringComparison.Ordinal)
                || !string.Equals(continuation.ResultSha, payload?.ResultSha, StringComparison.OrdinalIgnoreCase)))
            throw new TaskServerConflictException("engine-decision-mismatch",
                "The engine fix round does not continue on the reviewed delivery of this run.");
        return transition;
    }

    /// <summary>
    /// The engine transition this run already accepted at an earlier stage, so
    /// a later stage that hands the card on still knows the delivery was
    /// accepted for integration.
    /// </summary>
    private static async Task<TransitionAction?> ReadAcceptedTransitionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT output_json FROM orchestration_stage_results
             WHERE run_id = $run ORDER BY sequence;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        TransitionAction? accepted = null;
        while (await reader.ReadAsync(ct))
        {
            using var output = JsonDocument.Parse(reader.GetString(0));
            if (output.RootElement.ValueKind == JsonValueKind.Object
                && output.RootElement.TryGetProperty("transition", out var element)
                && element.ValueKind == JsonValueKind.Object
                && element.Deserialize<TransitionDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    is { } decision)
                accepted = decision.Action;
        }
        return accepted;
    }

    private static async Task<bool> IsReviewedPublicationAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, CancellationToken ct)
        => (await ReadProjectRepositoryAsync(connection, transaction, projectId, ct))?.DeliveryPolicy
           == ProjectDeliveryPolicies.ReviewedPublication;

    /// <summary>
    /// The rework prompt of an engine fix round: one ordered continuation
    /// round with the decision's findings, delivered with the next claim and
    /// consumed by its worker start, like an operator continue.
    /// </summary>
    private async Task QueueFixRoundPromptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestrationRunDto run,
        TransitionDecision decision,
        long taskVersion,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var commandId = $"review-round:{run.RunId}";
        if (await ScalarAsync(connection, "SELECT 1 FROM continuation_intents WHERE command_id = $command;", ct,
                transaction, ("$command", commandId)) is not null)
            return;
        var prompt = ReviewFindingRoundPrompt.Build(decision.Findings, decision.Receipt.AttemptId,
            decision.ContinuationBase is not null);
        var round = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT COALESCE(MAX(round), 0) FROM continuation_intents WHERE task_id = $task;",
            ct, transaction, ("$task", run.TaskId)) ?? 0L, CultureInfo.InvariantCulture) + 1;
        var submitted = new ContinuationIntentRequest(1, commandId, taskVersion, prompt, null, null, null,
            "continue", FixRoundContinuationReason);
        await ExecuteAsync(connection, """
            INSERT INTO continuation_intents(command_id, project_id, task_id, round,
                expected_task_version, result_task_version, payload_json, prompt, model, cli_type,
                thinking_level, mode, reason, actor, accepted_at, policy_version, explicit_selection)
            VALUES ($command, $project, $task, $round, $expected, $result, $payload, $prompt,
                NULL, NULL, NULL, 'continue', $reason, $actor, $at, $policy, 0);
            """, ct, transaction,
            ("$command", commandId), ("$project", run.ProjectId), ("$task", run.TaskId), ("$round", round),
            ("$expected", taskVersion), ("$result", taskVersion + 1),
            ("$payload", JsonSerializer.Serialize(submitted)), ("$prompt", prompt),
            ("$reason", FixRoundContinuationReason), ("$actor", actorId), ("$at", Iso(now)),
            ("$policy", ModelRoutingPolicyDocument.Value.Version));
        await AuditAsync(connection, transaction, actorId, "continuation.accepted", "task", run.TaskId,
            JsonSerializer.Serialize(new
            {
                CommandId = commandId,
                round,
                Reason = FixRoundContinuationReason,
                orchestrationRunId = run.RunId,
                decision.Receipt,
                continuationBase = decision.ContinuationBase is not null,
            }), ct);
    }

    /// <summary>
    /// Queues the integration of an accepted delivery. The card waits in Auto
    /// Review; a runner host holding the project's publication lease executes
    /// the command and its report applies the lane.
    /// </summary>
    private async Task QueueIntegrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestrationRunDto run,
        StagePayload payload,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var repository = await ReadProjectRepositoryAsync(connection, transaction, run.ProjectId, ct)
            ?? throw new TaskServerConflictException("integration-repository-missing",
                "Reviewed publication requires a registered project repository.");
        var sourceRunId = payload.RunAttemptId
            ?? throw new TaskServerConflictException("orchestration-envelope-incomplete",
                "The accepted delivery names no coding RunAttempt.");
        string? repositoryUrl = null, resultRef = null, resultSha = null;
        await using (var command = Command(connection, """
            SELECT repository_url, result_ref, result_sha FROM runs WHERE id = $run AND task_id = $task;
            """, transaction, ("$run", sourceRunId), ("$task", run.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                repositoryUrl = reader.IsDBNull(0) ? null : reader.GetString(0);
                resultRef = reader.IsDBNull(1) ? null : reader.GetString(1);
                resultSha = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }
        if (string.IsNullOrWhiteSpace(resultRef) || string.IsNullOrWhiteSpace(resultSha))
            throw new TaskServerConflictException("integration-result-missing",
                "The accepted delivery has no fenced result ref and SHA.");
        var integrationId = $"int_{Guid.NewGuid():N}";
        await ExecuteAsync(connection, """
            INSERT INTO integration_commands(
                id, project_id, task_id, orchestration_run_id, source_run_id, review_attempt_id,
                repository_id, repository_url, integration_ref, result_ref, result_sha,
                status, attempts, created_at, updated_at)
            VALUES ($id, $project, $task, $run, $source, $review, $repository, $url, $ref,
                $result_ref, $sha, 'queued', 0, $now, $now)
            ON CONFLICT(orchestration_run_id) DO NOTHING;
            """, ct, transaction,
            ("$id", integrationId), ("$project", run.ProjectId), ("$task", run.TaskId), ("$run", run.RunId),
            ("$source", sourceRunId), ("$review", payload.ReviewAttemptId ?? string.Empty),
            ("$repository", repository.RepositoryId), ("$url", repositoryUrl ?? repository.RepositoryUrl),
            ("$ref", repository.IntegrationRef), ("$result_ref", resultRef), ("$sha", resultSha.ToLowerInvariant()),
            ("$now", Iso(now)));
        await AuditAsync(connection, transaction, actorId, "integration.queued", "task", run.TaskId,
            JsonSerializer.Serialize(new
            {
                integrationId,
                orchestrationRunId = run.RunId,
                sourceRunId,
                repository.IntegrationRef,
                resultSha,
            }), ct);
    }

    public async Task<IReadOnlyList<IntegrationCommandDto>> ListIntegrationsAsync(string? taskId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, IntegrationSelect + """
             WHERE ($task IS NULL OR task_id = $task) ORDER BY created_at, id;
            """, null, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<IntegrationCommandDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadIntegration(reader));
        return result;
    }

    public async Task<IntegrationClaimResponse> ClaimIntegrationAsync(
        IntegrationClaimRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        IntegrationClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            string hostId;
            await using (var command = Command(connection, """
                SELECT host_id, instance_id, status, capabilities_json FROM runners WHERE id = $id;
                """, transaction, ("$id", request.RunnerId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Integration executor is not registered.");
                if (reader.GetString(1) != request.InstanceId || reader.GetString(2) != "active")
                    throw new TaskServerConflictException("runner-instance-stale", "Integration executor instance is not active.");
                var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
                if (!capabilities.Contains(IntegrationCapabilities.Executor, StringComparer.Ordinal))
                    throw new TaskServerConflictException("integration-capability-required",
                        "Runner is not an integration executor.");
                hostId = reader.GetString(0);
            }
            var now = UtcNow;
            await ExpirePublicationLeasesAsync(connection, transaction, now, actorId, ct);
            // A card that left Auto Review no longer waits for its integration.
            await ExecuteAsync(connection, """
                UPDATE integration_commands
                   SET status = 'superseded', reason = 'The card left Auto Review before integration.', updated_at = $now
                 WHERE status = 'queued'
                   AND NOT EXISTS (SELECT 1 FROM tasks t WHERE t.id = integration_commands.task_id
                                    AND t.state = '4-auto-review');
                """, ct, transaction, ("$now", Iso(now)));
            if (await ScalarAsync(connection, """
                    SELECT 1 FROM publication_leases WHERE runner_id = $runner AND status = 'active';
                    """, ct, transaction, ("$runner", request.RunnerId)) is not null)
            {
                response = new IntegrationClaimResponse("empty", Message: "The executor already holds a publication lease.");
                return;
            }
            var policy = await ReadHostProjectPolicyAsync(connection, transaction, hostId, ct);
            IntegrationCommandDto? selected = null;
            await using (var command = Command(connection, IntegrationSelect + """
                 WHERE status = 'queued'
                   AND NOT EXISTS (
                       SELECT 1 FROM publication_leases lease
                        WHERE lease.project_id = integration_commands.project_id
                          AND lease.integration_ref = integration_commands.integration_ref
                          AND lease.status = 'active')
                 ORDER BY created_at, id;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var candidate = ReadIntegration(reader);
                    if (policy is { AllowAllProjects: false }
                        && !policy.AllowedProjectIds.Contains(candidate.ProjectId, StringComparer.Ordinal))
                        continue;
                    selected = candidate;
                    break;
                }
            }
            if (selected is null)
            {
                response = new IntegrationClaimResponse("empty", Message: "No accepted delivery waits for integration.");
                return;
            }
            var fence = Convert.ToInt64(await ScalarAsync(connection, """
                SELECT fence FROM publication_leases WHERE project_id = $project AND integration_ref = $ref;
                """, ct, transaction, ("$project", selected.ProjectId), ("$ref", selected.IntegrationRef)) ?? 0L,
                CultureInfo.InvariantCulture) + 1;
            var ttl = Math.Clamp(request.RequestedTtlSeconds, MinimumPublicationLeaseSeconds, MaximumPublicationLeaseSeconds);
            var lease = new PublicationLeaseDto($"pub_{Guid.NewGuid():N}", selected.ProjectId, selected.IntegrationRef,
                selected.IntegrationId, request.RunnerId, request.InstanceId, fence, now, now.AddSeconds(ttl));
            await ExecuteAsync(connection, """
                INSERT INTO publication_leases(project_id, integration_ref, lease_id, integration_id,
                    runner_id, instance_id, fence, acquired_at, expires_at, status)
                VALUES ($project, $ref, $lease, $integration, $runner, $instance, $fence, $now, $expires, 'active')
                ON CONFLICT(project_id, integration_ref) DO UPDATE SET
                    lease_id = excluded.lease_id, integration_id = excluded.integration_id,
                    runner_id = excluded.runner_id, instance_id = excluded.instance_id,
                    fence = excluded.fence, acquired_at = excluded.acquired_at,
                    expires_at = excluded.expires_at, status = 'active';
                UPDATE integration_commands
                   SET status = 'leased', attempts = attempts + 1, updated_at = $now
                 WHERE id = $integration AND status = 'queued';
                """, ct, transaction,
                ("$project", lease.ProjectId), ("$ref", lease.IntegrationRef), ("$lease", lease.LeaseId),
                ("$integration", lease.IntegrationId), ("$runner", lease.RunnerId), ("$instance", lease.InstanceId),
                ("$fence", lease.Fence), ("$now", Iso(now)), ("$expires", Iso(lease.ExpiresAt)));
            await AuditAsync(connection, transaction, actorId, "integration.claimed", "integration", selected.IntegrationId,
                JsonSerializer.Serialize(new { lease.RunnerId, lease.Fence, lease.IntegrationRef, selected.TaskId }), ct);
            response = new IntegrationClaimResponse("claimed",
                selected with { Status = IntegrationCommandStates.Leased, Attempts = selected.Attempts + 1, UpdatedAt = now },
                lease);
        }, ct);
        return response!;
    }

    public async Task<IntegrationReportResponse> ReportIntegrationAsync(
        string integrationId, IntegrationReportRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("An integration report idempotency key is required.");
        var outcome = (request.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (!IntegrationOutcomeKinds.All.Contains(outcome))
            throw new ArgumentException("Unsupported integration outcome.");
        if (outcome == IntegrationOutcomeKinds.Integrated && !ValidDigest(request.IntegratedSha, 40, 64))
            throw new ArgumentException("A published integration names the new integration tip SHA.");
        IntegrationReportResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            IntegrationCommandDto command;
            string? reportKey;
            string? storedResponse;
            await using (var read = Command(connection, IntegrationSelect + " WHERE id = $id;",
                transaction, ("$id", integrationId)))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Integration command was not found.");
                command = ReadIntegration(reader);
                reportKey = reader.IsDBNull(18) ? null : reader.GetString(18);
                storedResponse = reader.IsDBNull(19) ? null : reader.GetString(19);
            }
            if (reportKey == request.IdempotencyKey && storedResponse is not null)
            {
                response = JsonSerializer.Deserialize<IntegrationReportResponse>(storedResponse);
                return;
            }
            string leaseId, runnerId, instanceId, leaseIntegration, status;
            long fence;
            DateTime expiresAt;
            await using (var read = Command(connection, """
                SELECT lease_id, runner_id, instance_id, integration_id, status, fence, expires_at
                  FROM publication_leases WHERE project_id = $project AND integration_ref = $ref;
                """, transaction, ("$project", command.ProjectId), ("$ref", command.IntegrationRef)))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new TaskServerConflictException("publication-lease-missing", "No publication lease is held.");
                (leaseId, runnerId, instanceId, leaseIntegration, status, fence, expiresAt) = (reader.GetString(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5), Parse(reader.GetString(6)));
            }
            if (leaseId != request.LeaseId || fence != request.Fence || runnerId != request.RunnerId
                || instanceId != request.InstanceId || leaseIntegration != integrationId)
                throw new TaskServerConflictException("stale-fence",
                    "The report does not hold the current publication lease for this integration.");
            var now = UtcNow;
            if (status != "active" || expiresAt <= now || command.Status != IntegrationCommandStates.Leased)
                throw new TaskServerConflictException("publication-lease-expired",
                    "The publication lease expired before the report.");

            var decision = IntegrationResultPolicy.Decide(outcome, command.Attempts);
            var nextStatus = decision.Action switch
            {
                IntegrationResultAction.HumanReview => IntegrationCommandStates.Integrated,
                IntegrationResultAction.Requeue => IntegrationCommandStates.Queued,
                _ => IntegrationCommandStates.Escalated,
            };
            string? taskState = null;
            var reason = decision.Reason;
            if (decision.TaskState is not null)
            {
                var moved = await ExecuteAsync(connection, """
                    UPDATE tasks SET state = $state, version = version + 1, updated_at = $now
                     WHERE id = $task AND state = '4-auto-review';
                    """, ct, transaction, ("$state", decision.TaskState), ("$now", Iso(now)), ("$task", command.TaskId));
                if (moved == 1)
                {
                    taskState = decision.TaskState;
                    var sourceFence = Convert.ToInt64(await ScalarAsync(connection,
                        "SELECT coalesce(fence, 0) FROM runs WHERE id = $run;", ct, transaction,
                        ("$run", command.SourceRunId)) ?? 0L, CultureInfo.InvariantCulture);
                    await AppendLifecycleEventAsync(connection, transaction, command.SourceRunId, command.TaskId,
                        sourceFence, LifecycleEventKinds.TerminalHandoff, new
                        {
                            authority = "task-server",
                            integrationId,
                            outcome,
                            integratedSha = request.IntegratedSha,
                            nextState = taskState,
                            reason,
                        }, ct);
                }
                else
                {
                    reason += " The card had already left Auto Review; its lane is unchanged.";
                }
            }
            var updated = command with
            {
                Status = nextStatus,
                UpdatedAt = now,
                IntegratedSha = request.IntegratedSha?.ToLowerInvariant() ?? command.IntegratedSha,
                LastOutcome = outcome,
                Reason = reason,
            };
            response = new IntegrationReportResponse(updated, taskState, reason);
            await ExecuteAsync(connection, """
                UPDATE integration_commands
                   SET status = $status, integrated_sha = $integrated, last_outcome = $outcome,
                       reason = $reason, report_key = $key, report_response_json = $response, updated_at = $now
                 WHERE id = $id;
                UPDATE publication_leases SET status = 'released'
                 WHERE project_id = $project AND integration_ref = $ref AND lease_id = $lease;
                """, ct, transaction,
                ("$status", nextStatus), ("$integrated", updated.IntegratedSha), ("$outcome", outcome),
                ("$reason", reason), ("$key", request.IdempotencyKey),
                ("$response", JsonSerializer.Serialize(response)), ("$now", Iso(now)), ("$id", integrationId),
                ("$project", command.ProjectId), ("$ref", command.IntegrationRef), ("$lease", leaseId));
            await AuditAsync(connection, transaction, actorId, "integration.reported", "integration", integrationId,
                JsonSerializer.Serialize(new
                {
                    command.TaskId,
                    outcome,
                    request.IntegratedSha,
                    request.Fence,
                    decision = decision.Action.ToString(),
                    nextState = taskState,
                    reason,
                    request.Detail,
                }), ct);
        }, ct);
        return response!;
    }

    private async Task ExpirePublicationLeasesAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTime now, string actorId, CancellationToken ct)
    {
        var expired = new List<string>();
        await using (var command = Command(connection, """
            SELECT integration_id FROM publication_leases WHERE status = 'active' AND expires_at <= $now;
            """, transaction, ("$now", Iso(now))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) expired.Add(reader.GetString(0));
        if (expired.Count == 0) return;
        await ExecuteAsync(connection, """
            UPDATE integration_commands SET status = 'queued', updated_at = $now
             WHERE status = 'leased' AND id IN (
                 SELECT integration_id FROM publication_leases WHERE status = 'active' AND expires_at <= $now);
            UPDATE publication_leases SET status = 'expired' WHERE status = 'active' AND expires_at <= $now;
            """, ct, transaction, ("$now", Iso(now)));
        await AuditAsync(connection, transaction, actorId, "integration.lease-expired", "integration",
            string.Join(',', expired), JsonSerializer.Serialize(new { expired }), ct);
    }

    private const string IntegrationSelect = """
        SELECT id, project_id, task_id, orchestration_run_id, source_run_id, review_attempt_id,
               repository_id, repository_url, integration_ref, result_ref, result_sha, status,
               attempts, created_at, updated_at, integrated_sha, last_outcome, reason,
               report_key, report_response_json
          FROM integration_commands
        """;

    private static IntegrationCommandDto ReadIntegration(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetInt32(12), Parse(reader.GetString(13)), Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17));
}
