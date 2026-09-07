using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Tranche-0 compatibility mount for the versioned Remote Review plane and
/// runner capability advertisements. The monolith remains the single task and
/// AttemptAuthority writer; this adapter translates the published Task Server
/// contracts used by agent-runner.
/// </summary>
public static class V1ReviewPlaneEndpoints
{
    private const string LoggerName = "AgentStudio.Runner.V1ReviewPlaneEndpoints";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapV1ReviewPlaneEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/protocol", () => Results.Ok(Protocol()));
        api.MapPost("/protocol/compatibility", (Contract.ProtocolCompatibilityRequest request) =>
        {
            var supported = Contract.TaskServerProtocol.Supports(request.ProtocolVersion)
                            && request.ClientKind is "runner" or "review-runner";
            var response = new Contract.ProtocolCompatibilityResponse(
                supported,
                Protocol(),
                supported
                    ? null
                    : $"{request.ClientKind} protocol {request.ProtocolVersion} is not supported.");
            return supported
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status426UpgradeRequired);
        });

        api.MapPut("/runners/{runnerId}", (
            HttpContext context,
            string runnerId,
            Contract.RegisterRunnerRequest request,
            V1ReviewExecutorRegistry registry,
            AgentStudio.Clients.ClientIdentityStore clients,
            AttemptAuthorityService authority,
            ILoggerFactory loggerFactory) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            try
            {
                var registered = registry.Register(runnerId, request);
                clients.RecordSeen(runnerId);
                var adoptions = authority.ReAdoptRunnerAttempts(
                    runnerId,
                    request.HostId,
                    request.InstanceId,
                    request.ActiveAttempts,
                    request.AttemptLeaseTtlSeconds);
                if (request.ActiveAttempts is { Count: > 0 })
                {
                    registry.RecordReAdoptedSlots(
                        runnerId,
                        request.InstanceId,
                        adoptions.Count(item => string.Equals(
                            item.Status, "adopted", StringComparison.Ordinal)));
                }
                if (adoptions.Count > 0)
                {
                    loggerFactory.CreateLogger(LoggerName).LogInformation(
                        "runner-attempt-re-adoption runner={RunnerId} instance={InstanceId} reported={Reported} adopted={Adopted} rejected={Rejected}",
                        runnerId,
                        request.InstanceId,
                        adoptions.Count,
                        adoptions.Count(item => string.Equals(item.Status, "adopted", StringComparison.Ordinal)),
                        adoptions.Count(item => !string.Equals(item.Status, "adopted", StringComparison.Ordinal)));
                }
                return Results.Ok(registered with { AttemptAdoptions = adoptions });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError("runner-role-conflict", exception.Message));
            }
        });

        api.MapMethods("/runners/{runnerId}/capabilities", new[] { "POST", "PUT" }, (
            HttpContext context,
            string runnerId,
            Contract.CapabilityAdvertisementRequest request,
            V1ReviewExecutorRegistry registry,
            AgentStudio.Clients.ClientIdentityStore clients) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new Contract.ApiError(
                    "runner-id-mismatch",
                    "Route and capability runner ids differ."));
            try
            {
                var snapshot = registry.AdvertiseCapabilities(runnerId, request);
                clients.RecordSeen(runnerId);
                return Results.Ok(snapshot);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError(
                    "capability-advertisement-conflict",
                    exception.Message));
            }
        });

        // Capability failure reports are the runner's health telemetry for the
        // review plane. The monolith mount keeps the reference semantics that
        // matter here - idempotent delivery, a suspect→draining threshold, and
        // a cooldown that pauses this executor's claims - but holds the state in
        // the registry instead of the standalone server's SQLite tables. The
        // pause is released by the next Register or capability advertisement
        // (the review daemon re-advertises every minute) or by cooldown expiry,
        // so a transient failure can never strand the Review Unit.
        api.MapPost("/runners/{runnerId}/capability-failures", (
            HttpContext context,
            string runnerId,
            Contract.CapabilityFailureRequest request,
            V1ReviewExecutorRegistry registry,
            ILoggerFactory loggerFactory) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new Contract.ApiError(
                    "runner-id-mismatch",
                    "Route and capability runner ids differ."));
            var logger = loggerFactory.CreateLogger(LoggerName);
            try
            {
                var response = registry.ReportCapabilityFailure(runnerId, request);
                logger.LogWarning(
                    "v1 capability failure runner={RunnerId} capability={Capability} "
                    + "classification={Classification} state={HealthState} cooldownUntil={CooldownUntil} "
                    + "wholeHost={WholeHost} claim={ClaimKind}:{ClaimId} reason={Reason}",
                    runnerId,
                    response.CapabilityKey,
                    request.Classification,
                    response.HealthState,
                    response.CooldownUntil,
                    response.WholeHostDraining,
                    request.ClaimKind ?? "none",
                    request.ClaimId ?? "none",
                    request.Reason);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError(
                    "capability-failure-conflict",
                    exception.Message));
            }
        });

        // Outbox status is pure diagnosis: it tells the server how much of the
        // runner's durable handoff is still unacknowledged. The monolith accepts
        // and logs it (with the reference's sanity and monotonicity guards) and
        // keeps only the latest snapshot per run in memory - the authoritative
        // handoff itself travels through the completion and result-handoff
        // routes, so nothing here needs to be durable.
        api.MapPut("/runners/{runnerId}/outbox-status", (
            HttpContext context,
            string runnerId,
            Contract.RunnerOutboxStatusRequest request,
            V1ReviewExecutorRegistry registry,
            ILoggerFactory loggerFactory) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            var logger = loggerFactory.CreateLogger(LoggerName);
            try
            {
                var status = registry.RecordOutboxStatus(runnerId, request);
                logger.LogInformation(
                    "v1 runner outbox status runner={RunnerId} instance={InstanceId} run={RunId} "
                    + "sequence={LastSequence} acknowledged={AcknowledgedSequence} backlog={Backlog} "
                    + "handoff={HandoffState}",
                    status.RunnerId,
                    status.InstanceId,
                    status.RunId,
                    status.LastSequence,
                    status.LastAcknowledgedSequence,
                    status.BacklogCount,
                    status.FinalHandoffState);
                return Results.Ok(status);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError("stale-outbox-status", exception.Message));
            }
        });

        api.MapPost("/runners/{runnerId}/review-claims", async (
            HttpContext context,
            string runnerId,
            Contract.ReviewClaimRequest request,
            V1ReviewExecutorRegistry registry,
            AttemptAuthorityService authority,
            ReviewAttemptTaskLifecycleService reviewAttemptLifecycle,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Projects.ProjectSettingsService settings,
            RemoteReviewPlanBuilder remoteReviewPlans,
            HumanReviewEscalation escalation,
            TaskMutationService mutations,
            TimelineLog timeline,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, runnerId)
                || !string.Equals(runnerId, request.ExecutorId, StringComparison.Ordinal))
                return Results.Unauthorized();
            if (request.AvailableSlots <= 0)
                return Results.Ok(new Contract.ReviewClaimResponse(
                    "empty", Message: "Review executor has no available slot."));
            if (!registry.TryGetReviewExecutor(runnerId, request.InstanceId, out var executor))
                return Results.Conflict(new Contract.ApiError(
                    "review-executor-not-registered",
                    "Register this identity with the review-executor capability before claiming."));
            if (!executor.Capabilities.Contains(Contract.ReviewCapabilities.BaselineComparison))
                return Results.Conflict(new Contract.ApiError(
                    "review-baseline-comparison-required",
                    "Update this Review Executor to one that advertises baseline-comparison support."));
            if (!executor.Capabilities.Contains(Contract.ReviewCapabilities.DependencyPreparation))
                return Results.Conflict(new Contract.ApiError(
                    "review-dependency-preparation-required",
                    "Update this Review Executor to one that advertises dependency-preparation support."));
            // A drained capability pauses this executor rather than feeding it
            // attempts it just reported itself unable to materialize. The pause
            // lifts on cooldown expiry or on the next full registration (a
            // restarted daemon), so no operator action is needed to resume - but a
            // routine capability advertisement does not cut the drain short.
            if (registry.TryGetCapabilityPause(runnerId, out var pause))
                return Results.Ok(new Contract.ReviewClaimResponse(
                    "empty",
                    Message: $"Review executor is paused until {pause.CooldownUntil:O} after a "
                             + $"{pause.Classification} failure of {pause.CapabilityKey}: {pause.Reason}"));

            // Card state owns admission. Revoke stale terminal-card attempts
            // before the legacy-envelope sweep can classify them as an
            // infrastructure failure instead of Superseded authority.
            reviewAttemptLifecycle.SweepUnclaimableAttempts("claim-guard");
            foreach (var legacy in authority.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope())
            {
                var task = FindTask(scanner, legacy.TaskKey);
                if (task is null
                    || !string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                    continue;
                var legacyChain = BuildAttemptChainSummary(authority, legacy.TaskKey);
                var moved = await escalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
                    "The immutable ReviewSubject has no persisted Result-Envelope and cannot be materialized. "
                    + legacyChain.Headline,
                    ct,
                    statusDetail: legacyChain.Detail);
                if (moved.Status != MoveJobStatus.Success)
                {
                    return Results.Json(
                        new Contract.ApiError(
                            "review-subject-escalation-failed",
                            $"Legacy ReviewSubject was terminalized, but its Escalated lane write failed: {moved.Status} {moved.Message}"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var claimed = reviewAttemptLifecycle.ClaimNextReview(
                runnerId,
                executor.HostId,
                request.InstanceId,
                request.RequestedTtlSeconds);
            if (claimed.Status == AttemptWriteStatus.NotFound)
                return Results.Ok(new Contract.ReviewClaimResponse(
                    "empty", Message: "No current immutable ReviewAttempt is queued."));
            if (!claimed.Accepted || claimed.ReviewAttempt is null)
                return AttemptError(claimed);

            var review = claimed.ReviewAttempt;
            var subject = ToSubject(
                review,
                scanner,
                projects,
                settings,
                remoteReviewPlans,
                out var subjectTask,
                out var baseline);
            CorrectOutdatedIntegrationBranch(
                subjectTask,
                baseline,
                review.AttemptId,
                mutations,
                timeline,
                loggerFactory.CreateLogger(LoggerName));
            var lease = ToLease(review);
            return Results.Ok(new Contract.ReviewClaimResponse(
                "claimed",
                ToAttempt(review),
                subject,
                lease));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        api.MapGet("/reviews/attempts/{attemptId}", (
            string attemptId,
            AttemptAuthorityService authority) =>
            authority.GetReview(attemptId) is { } review
                ? Results.Ok(ToAttempt(review))
                : Results.NotFound(new Contract.ApiError("not-found", "ReviewAttempt was not found.")));

        api.MapPost("/reviews/attempts/{attemptId}/lease/renew", (
            HttpContext context,
            string attemptId,
            Contract.ReviewLeaseRenewRequest request,
            AttemptAuthorityService authority) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    out var review,
                    out var error))
                return error!;

            var renewed = authority.RenewReview(
                new AttemptWriteReference(
                    attemptId,
                    request.Fence,
                    request.AuthorityEpoch,
                    request.IdempotencyKey),
                request.ExecutorId,
                request.RequestedTtlSeconds);
            return renewed.Accepted && renewed.ReviewAttempt is not null
                ? Results.Ok(ToLease(renewed.ReviewAttempt))
                : AttemptError(renewed);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        api.MapPost("/reviews/attempts/{attemptId}/report", async (
            HttpContext context,
            string attemptId,
            Contract.ReviewReportRequest request,
            AttemptAuthorityService authority,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Projects.ProjectSettingsService settings,
            RemoteReviewPlanBuilder remoteReviewPlans,
            GitService git,
            TaskTransitionService transitions,
            HumanReviewEscalation escalation,
            RemoteDeliveryIntegrationCoordinator remoteIntegration,
            TimelineLog timeline,
            RemotePipelineReviewEvidenceProjector remotePipelineEvidence,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateReportLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    request.IdempotencyKey,
                    out var current,
                    out var error))
                return error!;
            var currentReview = current!;
            var authoritativeLease = currentReview.Lease!;
            if (!string.Equals(request.Environment.ExecutorId, authoritativeLease.ExecutorId, StringComparison.Ordinal)
                || !string.Equals(request.Environment.InstanceId, authoritativeLease.ClientId, StringComparison.Ordinal)
                || !string.Equals(request.Environment.HostId, authoritativeLease.HostId, StringComparison.Ordinal)
                || request.Commands.Any(command =>
                    !string.Equals(command.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(command.ExecutorId)
                        && !string.Equals(command.ExecutorId, authoritativeLease.ExecutorId, StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(command.HostId)
                        && !string.Equals(command.HostId, authoritativeLease.HostId, StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(command.AttemptId)
                        && !string.Equals(command.AttemptId, currentReview.AttemptId, StringComparison.Ordinal))))
            {
                return Results.Conflict(new Contract.ApiError(
                    "review-execution-attribution-mismatch",
                    "Remote command placement does not match the authoritative ReviewAttempt lease."));
            }
            var materializableRepository = MaterializableRepository(
                currentReview, scanner, projects, settings);
            if (!string.Equals(
                    request.Workspace.RepositoryId,
                    materializableRepository.RepositoryId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.Workspace.ExpectedResultSha,
                    currentReview.Subject.ExpectedResultSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new Contract.ApiError(
                    "review-subject-mismatch",
                    "Review workspace does not identify the immutable ReviewSubject."));
            }

            if (Contract.ReviewToolchainFailurePolicy.IsUnavailable(
                    request.Commands,
                    request.Artifacts))
            {
                request = request with
                {
                    Outcome = "ReviewInfra",
                    FailureClassification = "ToolUnavailable",
                    Summary = request.Summary
                              ?? "A verification command could not use its declared toolchain.",
                };
            }

            if (!TryOutcome(request.Outcome, out var outcome))
                return Results.BadRequest(new Contract.ApiError(
                    "invalid-review-outcome",
                    "Outcome must be Pass, ProductFailure, ReviewInfra, Inconclusive, or Cancellation."));

            var settled = authority.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    attemptId,
                    request.Fence,
                    request.AuthorityEpoch,
                    request.IdempotencyKey),
                request.Workspace.ActualHead,
                outcome,
                request.FailureClassification,
                request.Summary));
            if (!settled.Accepted || settled.ReviewAttempt is null)
                return AttemptError(settled);

            var task = FindTask(scanner, settled.ReviewAttempt.TaskKey);
            if (task is null)
                return Results.Json(
                    new Contract.ApiError("task-not-found", "Review task was not found in the monolith store."),
                    statusCode: StatusCodes.Status404NotFound);

            var receivedAt = settled.ReviewAttempt.Reports
                .LastOrDefault(report => string.Equals(
                    report.IdempotencyKey,
                    request.IdempotencyKey,
                    StringComparison.Ordinal))
                ?.ReceivedAt
                ?? DateTime.UtcNow;
            if (settled.ReviewAttempt.Outcome == ReviewTerminalOutcome.Pass)
            {
                settings.MarkBuildProfileRemotelyValidated(
                    task.ProjectName,
                    settled.ReviewAttempt.Subject.Plan?.BuildProfileFingerprint,
                    settled.ReviewAttempt.AttemptId,
                    request.ExecutorId,
                    request.Workspace.ActualHead,
                    receivedAt);
            }
            var payload = JsonSerializer.Serialize(request, Json);
            var reportHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant();
            string evidenceFile;
            try
            {
                evidenceFile = await RemoteReviewReportEvidence.WriteAsync(
                    task.FolderPath,
                    attemptId,
                    settled.ReviewAttempt.Subject.SubjectId,
                    request,
                    reportHash,
                    receivedAt,
                    ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Json(
                    new Contract.ApiError(
                        "review-evidence-write-failed",
                        $"Review grade is durable, but its task evidence file could not be written: {exception.Message}"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                await remotePipelineEvidence.ProjectAsync(
                    task,
                    settled.ReviewAttempt,
                    request,
                    evidenceFile,
                    receivedAt,
                    ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Json(
                    new Contract.ApiError(
                        "remote-pipeline-evidence-write-failed",
                        $"Review grade is durable, but remote pipeline evidence could not be projected: {exception.Message}"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var infrastructureFailure = string.Equals(
                request.Outcome,
                "ReviewInfra",
                StringComparison.OrdinalIgnoreCase);
            var retry = infrastructureFailure
                        && authority.HasReviewInfrastructureRetryBudget(settled.ReviewAttempt.AttemptId);
            var repeatDiagnosis = infrastructureFailure
                ? RecordInfrastructureRepeatDiagnosis(authority, timeline, task, settled.ReviewAttempt)
                : null;
            var taskState = TaskStates.AutoReview;
            if (retry)
            {
                var review = settled.ReviewAttempt;
                var retryPlan = ReviewPlanForInfrastructureRetry(
                    request.FailureClassification,
                    review.Subject.Plan,
                    () =>
                    {
                        var project = projects.FindByStorageLocation(task.WatchPath)
                                      ?? projects.FindByIdOrDisplayName(task.ProjectName);
                        var taskSettings = settings.Get(task.ProjectName);
                        var integrationRef = ResolveBaselineBranch(
                            task,
                            project,
                            settings).IntegrationRef;
                        var repositoryPath = git.ResolveRepoRootForWatchPath(task.WatchPath)
                                             ?? project?.RepositoryPath;
                        return remoteReviewPlans.Build(
                            task,
                            repositoryPath,
                            taskSettings,
                            integrationRef);
                    });
                var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                    review.TaskKey,
                    review.RepositoryId,
                    review.Subject.ExpectedResultSha,
                    review.SourceRunAttemptId,
                    review.Subject.TaskRequirementsHash,
                    review.Subject.ReviewPolicyHash,
                    review.Subject.EvidenceDigestInputs,
                    $"v1-review-retry:{attemptId}:{request.IdempotencyKey}",
                    review.AttemptId,
                    review.Subject.RepositoryUrl,
                    review.Subject.ResultRef,
                    retryPlan));
                if (!created.Accepted)
                    return AttemptError(created);
            }
            else if (!infrastructureFailure)
            {
                var sourceRun = authority.GetRun(settled.ReviewAttempt.SourceRunAttemptId);
                var settledReviewPlan = settled.ReviewAttempt.Subject.Plan
                                        ?? ToSubject(
                                            settled.ReviewAttempt,
                                            scanner,
                                            projects,
                                            settings,
                                            remoteReviewPlans,
                                            out _,
                                            out _).Plan;
                var integrationDecision = RemoteDeliveryIntegrationPolicy.Decide(
                    HasSettledResultEnvelope(sourceRun),
                    settled.ReviewAttempt.Outcome?.ToString(),
                    settledReviewPlan,
                    request.Verdicts);
                // Carried onto the Human Review lane row as the verdict's
                // qualifier: the integration outcome behind the park.
                string? integrationOutcome = null;
                if (string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    var projectSettings = settings.Get(task.ProjectName);
                    var subject = ReviewSubjectStore.Read(task.FolderPath);
                    var integrationRequest = new RemoteDeliveryIntegrationRequest(
                        task.ProjectName,
                        task.Id,
                        task.FolderPath,
                        task.WatchPath,
                        TaskIntegrationBranch.Resolve(task, projectSettings.IntegrationBranch),
                        projectSettings.IntegrationStrategy,
                        PipelineTypes.Resolve(task),
                        subject?.CompletedAtUtc
                        ?? (sourceRun?.TerminalAt is { } terminalAt
                            ? new DateTimeOffset(DateTime.SpecifyKind(terminalAt, DateTimeKind.Utc))
                            : DateTimeOffset.UtcNow));
                    if (integrationDecision.ShouldIntegrate)
                    {
                        var integrated = await remoteIntegration.EnqueueAsync(integrationRequest).ConfigureAwait(false);
                        integrationOutcome = integrated.Outcome.ToString();
                    }
                    else
                    {
                        remoteIntegration.RecordGateFailure(
                            integrationRequest,
                            integrationDecision.Reason);
                        integrationOutcome = AcceptedIntegrationFailureCodes.DeliveryGateFailed;
                    }
                }

                if (string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    var moved = await transitions.MoveAsync(
                        task.Id,
                        TaskStates.HumanReview,
                        task.WatchPath,
                        ct,
                        cause: $"remote-review:{attemptId}",
                        authorityWrite: new AttemptWriteReference(
                            attemptId,
                            request.Fence,
                            request.AuthorityEpoch,
                            $"lane:{request.IdempotencyKey}"),
                        suppressProductExecution: true,
                        expectedSourceState: TaskStates.AutoReview,
                        transitionCause: LaneChangeCauses.ReviewVerdict,
                        transitionDetail: integrationOutcome ?? request.Outcome);
                    if (moved.Status == MoveJobStatus.SourceStateMismatch)
                    {
                        var racedTask = FindTask(scanner, settled.ReviewAttempt.TaskKey);
                        if (racedTask is null)
                        {
                            return Results.Json(
                                new Contract.ApiError(
                                    "task-not-found",
                                    "Review task disappeared while its report was being recorded."),
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        }
                        taskState = racedTask.State;
                        RecordPostAcceptanceReportIfTerminal(timeline, racedTask, attemptId, request, evidenceFile);
                    }
                    else if (moved.Status != MoveJobStatus.Success)
                    {
                        return Results.Json(
                            new Contract.ApiError(
                                "review-lane-write-failed",
                                $"Review grade is durable, but the Human Review lane write failed: {moved.Status} {moved.Message}"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    else
                    {
                        taskState = TaskStates.HumanReview;
                        // Board contract: the human-review park needs a journal
                        // verdict, or the boot-time verdict-less backfill later
                        // escalates the freshly reviewed card as pre-funnel
                        // legacy (observed 28.07. after a backend restart).
                        // The move relocated the card folder; the epoch sidecar
                        // must be read from the NEW path or it stamps epoch 0.
                        escalation.RecordRemoteReviewParkVerdict(
                            task.ProjectName,
                            task.Id,
                            moved.NewFolderPath ?? task.FolderPath,
                            request.Outcome,
                            request.Summary ?? string.Empty,
                            BuildAttemptChainSummary(authority, settled.ReviewAttempt.TaskKey));
                    }
                }
                else if (string.Equals(task.State, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase))
                {
                    taskState = TaskStates.HumanReview;
                }
                else
                {
                    taskState = task.State;
                    RecordPostAcceptanceReportIfTerminal(timeline, task, attemptId, request, evidenceFile);
                }
            }
            else
            {
                var review = settled.ReviewAttempt;
                if (string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    // The budget constant alone described the chain by its size.
                    // The chain summary describes it by its NEWEST cause and by
                    // every distinct classification it produced, so a late,
                    // harder failure cannot be hidden behind the majority class
                    // (AGT-2220).
                    var chain = BuildAttemptChainSummary(authority, review.TaskKey);
                    var moved = await escalation.EscalateAsync(
                        task.Id,
                        task.WatchPath,
                        task.ProjectName,
                        HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
                        $"The immutable ReviewSubject exhausted its budget of {AttemptAuthorityService.ReviewInfrastructureRetryBudget} infrastructure retries and cannot be materialized. "
                        + chain.Headline,
                        ct,
                        statusDetail: chain.Detail);
                    if (moved.Status != MoveJobStatus.Success)
                    {
                        return Results.Json(
                            new Contract.ApiError(
                                "review-subject-escalation-failed",
                                $"Review result is durable, but the Escalated lane write failed: {moved.Status} {moved.Message}"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    taskState = TaskStates.Escalated;
                }
                else if (string.Equals(task.State, TaskStates.Escalated, StringComparison.OrdinalIgnoreCase))
                {
                    taskState = TaskStates.Escalated;
                }
                else
                {
                    taskState = task.State;
                    RecordPostAcceptanceReportIfTerminal(timeline, task, attemptId, request, evidenceFile);
                }
            }

            return Results.Ok(new Contract.ReviewReportDto(
                "rrpt_" + HashId($"{attemptId}:{request.IdempotencyKey}"),
                attemptId,
                settled.ReviewAttempt.Subject.SubjectId,
                request.Outcome,
                request.FailureClassification,
                request.Summary,
                reportHash,
                receivedAt,
                retry,
                taskState));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);

        api.MapPost("/reviews/attempts/{attemptId}/cleanup", (
            HttpContext context,
            string attemptId,
            Contract.ReviewCleanupRequest request,
            AttemptAuthorityService authority) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    out var review,
                    out var error,
                    allowTerminal: true))
                return error!;
            return Results.Ok(new Contract.ReviewCleanupResponse(
                request.WorkspaceRemoved ? "cleaned" : "cleanup-failed",
                attemptId,
                DateTime.UtcNow,
                !request.WorkspaceRemoved
                && review!.Outcome == ReviewTerminalOutcome.InfrastructureFailure));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        api.MapGet("/runs/{runId}/result-handoff", (
            string runId,
            AttemptAuthorityService authority) =>
            authority.GetResultHandoff(runId) is { } handoff
                ? Results.Ok(handoff)
                : Results.NotFound(new Contract.ApiError(
                    "result-envelope-not-found",
                    "This run has no persisted immutable result envelope.")));
    }

    private static Contract.ProtocolRangeDto Protocol()
    {
        var version = typeof(V1ReviewPlaneEndpoints).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return new Contract.ProtocolRangeDto(
            Contract.TaskServerProtocol.Current,
            Contract.TaskServerProtocol.MinimumSupported,
            Contract.TaskServerProtocol.MaximumSupported,
            version,
            "orchestrator-monolith",
            ["runner", "review-runner"],
            ["review-plane", "capability-advertisement"]);
    }

    private static bool HasSettledResultEnvelope(RunAttemptDto? run)
    {
        if (run is not
            {
                State: AttemptLifecycleState.Completed,
                ResultEnvelope: not null,
                ResultEnvelopeDigest: not null,
            })
        {
            return false;
        }

        try
        {
            Contract.ResultEnvelopeDigest.Validate(run.ResultEnvelope);
            return string.Equals(
                Contract.ResultEnvelopeDigest.Compute(run.ResultEnvelope),
                run.ResultEnvelopeDigest,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RecordPostAcceptanceReportIfTerminal(
        TimelineLog timeline,
        TaskInfo task,
        string attemptId,
        Contract.ReviewReportRequest request,
        string evidenceFile)
    {
        if (task.State is not (TaskStates.Completed or TaskStates.Archive))
            return;
        if (timeline.ReadAll(task.FolderPath).Any(item =>
                item.Kind == TimelineEventKinds.PostAcceptanceReviewReportRecorded
                && string.Equals(item.RunId, attemptId, StringComparison.Ordinal)))
        {
            return;
        }

        timeline.Append(
            task.FolderPath,
            TimelineEventKinds.PostAcceptanceReviewReportRecorded,
            TimelineActors.System,
            "post-acceptance review report recorded",
            runId: attemptId,
            payloadRef: evidenceFile,
            details: new Dictionary<string, string>
            {
                ["attemptId"] = attemptId,
                ["fence"] = request.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["authorityEpoch"] = request.AuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["outcome"] = request.Outcome,
                ["lane"] = task.State,
            });
    }

    private static Contract.ReviewSubjectDto ToSubject(
        ReviewAttemptDto review,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings,
        RemoteReviewPlanBuilder remoteReviewPlans,
        out TaskInfo? subjectTask,
        out ReviewBaselineBranchDecision? baseline)
    {
        var materializableRepository = MaterializableRepository(
            review, scanner, projects, settings);
        var task = FindTask(scanner, review.TaskKey);
        var project = task is null
            ? null
            : projects.FindByStorageLocation(task.WatchPath)
              ?? projects.FindByIdOrDisplayName(task.ProjectName);
        subjectTask = task;
        baseline = task is null ? null : ResolveBaselineBranch(task, project, settings);
        var integrationRef = baseline?.IntegrationRef;
        var taskSettings = task is null ? null : settings.Get(task.ProjectName);
        var plan = review.Subject.Plan
                   ?? remoteReviewPlans.Build(
                       task,
                       project?.RepositoryPath,
                       taskSettings,
                       integrationRef);
        // The plan is frozen with the subject, so a retry inherits whatever ref
        // the first attempt was handed. AGT-2220 replayed a stale
        // refs/heads/main through four attempts that way. Re-stamping the ref at
        // hand-out time is what lets a corrected integration line reach the
        // runner instead of the snapshot taken when the card was created.
        if (integrationRef is not null
            && !string.Equals(plan.IntegrationRef, integrationRef, StringComparison.Ordinal))
        {
            plan = plan with { IntegrationRef = integrationRef };
        }
        plan = Contract.ReviewPlanResourcePolicy.Apply(plan);
        return new Contract.ReviewSubjectDto(
            review.Subject.SubjectId,
            task?.Id ?? review.TaskKey,
            review.SourceRunAttemptId,
            materializableRepository.RepositoryId,
            materializableRepository.RepositoryUrl,
            review.Subject.ExpectedResultSha,
            review.Subject.ResultRef ?? review.Subject.ExpectedResultSha,
            null,
            null,
            null,
            review.Subject.ReviewPolicyHash,
            plan,
            review.Subject.CreatedAt);
    }

    private static (string RepositoryId, string? RepositoryUrl) MaterializableRepository(
        ReviewAttemptDto review,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        var task = FindTask(scanner, review.TaskKey);
        var project = task is null
            ? null
            : projects.FindByStorageLocation(task.WatchPath)
              ?? projects.FindByIdOrDisplayName(task.ProjectName);
        var repository = task is null
            ? null
            : RemoteProjectRepositoryResolver.Resolve(
                project,
                ResolveBaselineBranch(task, project, settings).Branch);
        var repositoryUrl = review.Subject.RepositoryUrl ?? repository?.RepositoryUrl;
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)
                           ?? review.RepositoryId;
        return (repositoryId, repositoryUrl);
    }

    /// <summary>
    /// The integration line this review's baseline is computed against. Project
    /// and repository truth outrank the card's recorded branch, which is only a
    /// snapshot from worktree preparation and goes stale.
    /// </summary>
    private static ReviewBaselineBranchDecision ResolveBaselineBranch(
        TaskInfo task,
        AgentStudio.Shared.ProjectRecord? project,
        AgentStudio.Projects.ProjectSettingsService settings)
        => ReviewBaselineBranchPolicy.Decide(
            task.IntegrationBranch,
            settings.Get(task.ProjectName).IntegrationBranch,
            RemoteProjectRepositoryResolver.ReadRepositoryDefaultBranch(project));

    internal static Contract.ReviewPlanDto FallbackPlan(
        string? repositoryPath,
        string? projectName,
        AgentStudio.Projects.ProjectSettingsService settings,
        string? integrationRef)
    {
        var profile = string.IsNullOrWhiteSpace(projectName)
            ? null
            : settings.Get(projectName).BuildProfile;
        return FallbackPlan(repositoryPath, profile, integrationRef);
    }

    internal static Contract.ReviewPlanDto FallbackPlan(
        string? repositoryPath,
        BuildProfile? profile,
        string? integrationRef)
    {
        var verify = VerifyCommandPlanner.Plan(repositoryPath ?? string.Empty, profile);
        var preparation = GatePreparationPlanner.Plan(
                repositoryPath ?? string.Empty,
                profile,
                verify.Commands)
            .Select((command, index) => new Contract.ReviewPreparationCommandDto(
                $"prepare-{index + 1}",
                "bash",
                ["-lc", command.Command],
                command.WorkingSubdir,
                TimeoutSeconds: 7200,
                command.DependencyScopes
                    .Select(scope => new Contract.ReviewDependencyScopeDto(
                        scope.WorkingSubdir,
                        scope.Lockfiles))
                    .ToArray()))
            .ToArray();
        var commands = verify.Commands
            .Select((command, index) =>
            {
                var shellCommand = string.IsNullOrWhiteSpace(command.WorkingSubdir)
                    ? command.Command
                    : $"cd -- {ShellQuote(command.WorkingSubdir)} && {command.Command}";
                // AGT-2446 root cause: the contract default of 1800s starved
                // dotnet build/test on the review host once several attempts ran
                // in parallel - the killed process surfaced as
                // ReviewInfra/BaselineUnavailable (baseline) or the bogus
                // "<unparsed failure in verify-2>" ProductFailure (subject).
                // Build/test verify commands get the full clamp window instead;
                // the runner-side hard clamp (7200s) stays the ceiling.
                return new Contract.ReviewCommandDto(
                    $"verify-{index + 1}",
                    command.Kind == VerifyCommandKind.Lint ? "lint" : "build-tests",
                    "sh",
                    ["-lc", shellCommand],
                    TimeoutSeconds: 7200,
                    CompareToBaseline: command.Kind == VerifyCommandKind.Test);
            })
            .ToList();
        if (commands.Count == 0)
        {
            commands.Add(new Contract.ReviewCommandDto(
                "verify-subject", "completion", "git", ["rev-parse", "--verify", "HEAD"]));
        }
        return new Contract.ReviewPlanDto(
            commands,
            commands.Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IntegrationRef: integrationRef,
            Preparation: preparation,
            PreserveGlobs: profile?.PreserveGlobs,
            BuildProfileFingerprint: BuildProfileValidationFingerprint.Create(profile));
    }

    internal static Contract.ReviewPlanDto? ReviewPlanForInfrastructureRetry(
        string? failureClassification,
        Contract.ReviewPlanDto? inheritedPlan,
        Func<Contract.ReviewPlanDto> rebuild)
        => ReviewInfrastructureRetryPlanPolicy.RequiresRebuild(failureClassification)
            ? rebuild()
            : inheritedPlan;

    /// <summary>
    /// Writes the resolved integration line back onto a card whose recorded
    /// <c>integrationBranch</c> no longer matches project truth, so every later
    /// consumer (delivery, merge backstops, the next review) reads the same
    /// branch the baseline just used.
    /// </summary>
    private static void CorrectOutdatedIntegrationBranch(
        TaskInfo? task,
        ReviewBaselineBranchDecision? baseline,
        string attemptId,
        TaskMutationService mutations,
        TimelineLog timeline,
        ILogger logger)
    {
        if (task is null || baseline is not { CardOutdated: true }) return;
        if (!mutations.SetRunIntegrationBranchOnFolder(task.FolderPath, baseline.Branch))
        {
            logger.LogWarning(
                "Review baseline: could not correct outdated integrationBranch on {TaskId} ({Rationale}).",
                task.Id,
                baseline.Rationale);
            return;
        }

        logger.LogInformation(
            "Review baseline: corrected integrationBranch on {TaskId} to {Branch} ({Rationale}).",
            task.Id,
            baseline.IntegrationRef,
            baseline.Rationale);
        timeline.Append(
            task.FolderPath,
            TimelineEventKinds.IntegrationBranchCorrected,
            TimelineActors.System,
            $"Integration branch corrected to {baseline.IntegrationRef} before review: {baseline.Rationale}.",
            runId: attemptId,
            details: new Dictionary<string, string>
            {
                ["attemptId"] = attemptId,
                ["previousBranch"] = baseline.CardBranch ?? string.Empty,
                ["integrationRef"] = baseline.IntegrationRef,
                ["source"] = baseline.Source.ToString(),
            });
    }

    /// <summary>
    /// Names a repeating infrastructure cause on the card. Without it a drained
    /// retry budget leaves only N identical classifications and no statement of
    /// which base or command kept failing (AGT-2220).
    /// </summary>
    private static ReviewInfrastructureRepeatDiagnosis? RecordInfrastructureRepeatDiagnosis(
        AttemptAuthorityService authority,
        TimelineLog timeline,
        TaskInfo task,
        ReviewAttemptDto review)
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            authority.ReviewInfrastructureChain(review.AttemptId),
            review.Subject.Plan?.IntegrationRef);
        if (diagnosis is null) return null;
        if (timeline.ReadAll(task.FolderPath).Any(item =>
                item.Kind == TimelineEventKinds.ReviewInfrastructureRepeatDiagnosed
                && string.Equals(item.RunId, review.AttemptId, StringComparison.Ordinal)))
        {
            return diagnosis;
        }

        timeline.Append(
            task.FolderPath,
            TimelineEventKinds.ReviewInfrastructureRepeatDiagnosed,
            TimelineActors.System,
            diagnosis.Summary,
            runId: review.AttemptId,
            details: new Dictionary<string, string>
            {
                ["classification"] = diagnosis.Classification,
                ["repeatCount"] = diagnosis.RepeatCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["attemptIds"] = string.Join(",", diagnosis.AttemptIds),
                ["integrationRef"] = diagnosis.IntegrationRef ?? string.Empty,
                ["baselineSha"] = diagnosis.BaselineSha ?? string.Empty,
                ["step"] = diagnosis.Step ?? string.Empty,
                ["command"] = diagnosis.Command ?? string.Empty,
            });
        return diagnosis;
    }

    private static Contract.ReviewAttemptDto ToAttempt(ReviewAttemptDto review)
    {
        var attemptNumber = 1;
        if (!string.IsNullOrWhiteSpace(review.SourceReviewAttemptId)) attemptNumber = 2;
        return new Contract.ReviewAttemptDto(
            review.AttemptId,
            review.Subject.SubjectId,
            review.TaskKey,
            attemptNumber,
            review.State.ToString().ToLowerInvariant(),
            review.Lease?.ExecutorId,
            review.Lease?.HostId,
            review.LastFence,
            review.CreatedAt,
            review.TerminalAt,
            null,
            review.Outcome?.ToString(),
            review.FailureClassification);
    }

    private static Contract.ReviewLeaseDto ToLease(ReviewAttemptDto review)
    {
        var lease = review.Lease
                    ?? throw new InvalidOperationException("Claimed ReviewAttempt has no lease.");
        var resourceNamespace =
            $"review-{SafeSegment(review.AttemptId).ToLowerInvariant()}-f{review.LastFence}";
        return new Contract.ReviewLeaseDto(
            lease.LeaseId,
            review.AttemptId,
            review.Subject.SubjectId,
            lease.ExecutorId,
            lease.ClientId ?? lease.ExecutorId,
            lease.HostId,
            review.LastFence,
            lease.AcquiredAt,
            lease.ExpiresAt,
            "active",
            resourceNamespace,
            PortBase(review.AttemptId, review.LastFence),
            review.AuthorityEpoch);
    }

    private static bool TryValidateLease(
        AttemptAuthorityService authority,
        string attemptId,
        string executorId,
        string instanceId,
        string leaseId,
        long fence,
        long authorityEpoch,
        out ReviewAttemptDto? review,
        out IResult? error,
        bool allowTerminal = false)
    {
        review = authority.GetReview(attemptId);
        if (review is null)
        {
            error = Results.NotFound(new Contract.ApiError("not-found", "ReviewAttempt was not found."));
            return false;
        }
        var lease = review.Lease;
        if (lease is null
            || !string.Equals(lease.ExecutorId, executorId, StringComparison.Ordinal)
            || !string.Equals(lease.ClientId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || review.LastFence != fence
            || review.AuthorityEpoch != authorityEpoch)
        {
            error = Results.Conflict(new Contract.ApiError(
                "stale-review-authority",
                "AttemptId, lease, executor, instance, fence, or authority epoch is stale."));
            return false;
        }
        if (!allowTerminal && review.State != AttemptLifecycleState.Leased)
        {
            error = Results.Conflict(new Contract.ApiError(
                "review-attempt-not-leased",
                "ReviewAttempt is not currently leased."));
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryValidateReportLease(
        AttemptAuthorityService authority,
        string attemptId,
        string executorId,
        string instanceId,
        string leaseId,
        long fence,
        long authorityEpoch,
        string idempotencyKey,
        out ReviewAttemptDto? review,
        out IResult? error)
    {
        review = authority.GetReview(attemptId);
        if (review is null)
        {
            error = ReportLeaseExpired(
                "ReviewAttempt authority is no longer available for this report.");
            return false;
        }

        var isSettlementReplay = review.Reports.Any(report => string.Equals(
            report.IdempotencyKey,
            idempotencyKey,
            StringComparison.Ordinal));
        if (review.Lease is null
            || review.State != AttemptLifecycleState.Leased && !isSettlementReplay)
        {
            error = ReportLeaseExpired(
                "ReviewAttempt no longer has report authority; its lease expired.");
            return false;
        }

        var lease = review.Lease;
        if (!string.Equals(lease.ExecutorId, executorId, StringComparison.Ordinal)
            || !string.Equals(lease.ClientId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || review.LastFence != fence
            || review.AuthorityEpoch != authorityEpoch)
        {
            error = Results.Conflict(new Contract.ApiError(
                "stale-review-authority",
                "AttemptId, lease, executor, instance, fence, or authority epoch is stale."));
            return false;
        }

        if (!isSettlementReplay && lease.ExpiresAt <= DateTime.UtcNow)
        {
            error = ReportLeaseExpired("ReviewAttempt report authority expired.");
            return false;
        }

        error = null;
        return true;
    }

    private static IResult ReportLeaseExpired(string message)
        => Results.Conflict(new Contract.ApiError(
            AttemptWriteStatus.LeaseExpired.ToString(),
            message));

    /// <summary>
    /// Collects the task's full ReviewAttempt history (archived epochs included,
    /// so a chain that outlived a compaction is still described completely) and
    /// reduces it to the operator summary. Park and escalation are rare, so the
    /// archive read is affordable here; completeness is the point.
    /// </summary>
    private static ReviewAttemptChainSummary BuildAttemptChainSummary(
        AttemptAuthorityService authority, string taskKey)
        => ReviewAttemptChainSummary.Build(authority
            .GetTaskProjection(taskKey, includeArchived: true)
            .ReviewAttempts
            .Select(ReviewAttemptChainEntry.From));

    private static bool TryOutcome(string value, out ReviewTerminalOutcome outcome)
    {
        outcome = value.Trim().ToLowerInvariant() switch
        {
            "pass" => ReviewTerminalOutcome.Pass,
            "productfailure" => ReviewTerminalOutcome.ProductFailure,
            "reviewinfra" => ReviewTerminalOutcome.InfrastructureFailure,
            "inconclusive" => ReviewTerminalOutcome.Inconclusive,
            "cancellation" => ReviewTerminalOutcome.Cancellation,
            _ => (ReviewTerminalOutcome)(-1),
        };
        return Enum.IsDefined(outcome);
    }

    private static IResult AttemptError(AttemptWriteResult result)
    {
        var error = new Contract.ApiError(
            result.Status.ToString(),
            result.Message ?? $"Review authority rejected the write as {result.Status}.");
        return result.Status switch
        {
            AttemptWriteStatus.NotFound => Results.NotFound(error),
            AttemptWriteStatus.Invalid => Results.BadRequest(error),
            _ => Results.Conflict(error),
        };
    }

    private static TaskInfo? FindTask(TaskScannerService scanner, string taskKey)
        => scanner.ScanAllJobsWithArchive().FirstOrDefault(task =>
            string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Key, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Id, taskKey, StringComparison.OrdinalIgnoreCase));

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);

    private static int PortBase(string attemptId, long fence)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{attemptId}:{fence}"));
        var slot = BitConverter.ToUInt16(bytes, 0) % 4000;
        return 24000 + slot * 8;
    }

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string HashId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];
}

public sealed class V1ReviewExecutorRegistry
{
    /// <summary>Consecutive failures that turn a suspect capability into a drained one.</summary>
    private const int CapabilityFailureThreshold = 2;
    private const int CapabilityBaseCooldownSeconds = 120;
    private const int DiagnosticRetention = 256;

    /// <summary>
    /// Capabilities whose loss takes the whole host down rather than a single
    /// capability - one report drains immediately (Task Server reference set).
    /// </summary>
    private static readonly HashSet<string> WholeHostCapabilities = new(StringComparer.Ordinal)
    {
        Contract.CapabilityProtocol.Disk,
        Contract.CapabilityProtocol.LeaseAuthority,
        Contract.CapabilityProtocol.HostNetwork,
        Contract.CapabilityProtocol.RepositoryFileSystem,
        Contract.CapabilityProtocol.TaskServerAuthority,
    };

    private readonly ConcurrentDictionary<string, Registration> _registrations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityState> _capabilityStates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, CapabilityFailureState>> _capabilityFailures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityFailureDelivery> _capabilityFailureDeliveries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboxStatusEntry> _outboxStatuses =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Contract.RunnerDto Register(string runnerId, Contract.RegisterRunnerRequest request)
    {
        if (!Contract.TaskServerProtocol.Supports(request.ProtocolVersion))
            throw new ArgumentException("Runner protocol is not supported.");
        if (string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(request.HostId)
            || string.IsNullOrWhiteSpace(request.InstanceId))
            throw new ArgumentException("Runner, host, and instance ids are required.");
        var capabilities = request.Capabilities ?? [];
        var reviewExecutor =
            capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal);
        var codingExecutor =
            capabilities.Contains(Contract.ReviewCapabilities.CodingExecutor, StringComparer.Ordinal);
        if (reviewExecutor == codingExecutor)
            throw new InvalidOperationException(
                "A runner identity must advertise exactly one coding or review executor role.");
        var activeAttempts = request.ActiveAttempts ?? [];
        if (activeAttempts.Count > 256)
            throw new ArgumentException("At most 256 active attempts may be reported during registration.");
        var expectedAttemptKind = reviewExecutor
            ? Contract.RunnerAttemptKinds.Review
            : Contract.RunnerAttemptKinds.Coding;
        if (activeAttempts.Any(attempt => !string.Equals(
                attempt.Kind, expectedAttemptKind, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"A {expectedAttemptKind} runner may report only {expectedAttemptKind} attempts.");
        }
        if (activeAttempts
            .Select(attempt => attempt.AttemptId)
            .Distinct(StringComparer.Ordinal)
            .Count() != activeAttempts.Count)
        {
            throw new ArgumentException("Active attempt ids must be unique within a registration.");
        }

        var now = DateTime.UtcNow;
        Registration registration;
        lock (_gate)
        {
            registration = _registrations.AddOrUpdate(
                runnerId,
                _ => new Registration(
                    request.Name,
                    request.HostId,
                    request.InstanceId,
                    request.RunnerVersion,
                    request.ProtocolVersion,
                    capabilities.ToHashSet(StringComparer.Ordinal),
                    request.BootstrapMaxParallelism,
                    now,
                    now),
                (_, existing) =>
                {
                    var existingReview =
                        existing.Capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor);
                    if (existingReview != reviewExecutor)
                        throw new InvalidOperationException(
                            "A coding or review identity cannot change its executor role.");
                    return existing with
                    {
                        Name = request.Name,
                        HostId = request.HostId,
                        InstanceId = request.InstanceId,
                        RunnerVersion = request.RunnerVersion,
                        ProtocolVersion = request.ProtocolVersion,
                        Capabilities = capabilities.ToHashSet(StringComparer.Ordinal),
                        RoleMaxParallelism = request.BootstrapMaxParallelism,
                        LastSeenAt = now,
                    };
                });
            if (_capabilityStates.TryGetValue(runnerId, out var capabilityState)
                && !string.Equals(
                    capabilityState.InstanceId,
                    registration.InstanceId,
                    StringComparison.Ordinal))
            {
                _capabilityStates.Remove(runnerId);
            }
            // A registration re-declares this identity's health, so a restarted
            // executor is never born paused by a previous instance's failures.
            ClearCapabilityFailures(runnerId);
        }
        return new Contract.RunnerDto(
            runnerId,
            registration.Name,
            registration.HostId,
            registration.InstanceId,
            registration.RunnerVersion,
            registration.ProtocolVersion,
            "active",
            registration.RegisteredAt,
            registration.LastSeenAt);
    }

    public Contract.RunnerCapabilitySnapshotDto AdvertiseCapabilities(
        string runnerId,
        Contract.CapabilityAdvertisementRequest request)
    {
        if (request.SchemaVersion != Contract.CapabilityProtocol.CurrentSchemaVersion)
            throw new ArgumentException(
                $"Capability schema {request.SchemaVersion} is unsupported; expected " +
                $"{Contract.CapabilityProtocol.CurrentSchemaVersion}.");
        if (request.FreshForSeconds is < 30 or > 900)
            throw new ArgumentException("Capability freshness must be between 30 and 900 seconds.");
        if (request.Generation <= 0 || request.Capabilities.Count == 0)
            throw new ArgumentException(
                "Capability generation and at least one capability are required.");

        var advertisedAt = request.AdvertisedAt.ToUniversalTime();
        var now = DateTime.UtcNow;
        if (advertisedAt > now.AddMinutes(2))
            throw new ArgumentException("Capability advertisement time is too far in the future.");
        var freshUntil = advertisedAt.AddSeconds(request.FreshForSeconds);
        var capabilities = request.Capabilities
            .Select(capability =>
            {
                var key = capability.Key.Trim().ToLowerInvariant();
                if (key.Length == 0 || string.IsNullOrWhiteSpace(capability.Category))
                    throw new ArgumentException("Capability key and category are required.");
                return new Contract.CapabilityHealthDto(
                    key,
                    capability.Category.Trim().ToLowerInvariant(),
                    capability.Status.Trim().ToLowerInvariant(),
                    Contract.CapabilityHealthStates.Healthy,
                    null,
                    advertisedAt,
                    freshUntil,
                    freshUntil > now,
                    null,
                    null,
                    null,
                    null,
                    0,
                    capability.Version,
                    capability.Identity,
                    capability.Detail,
                    [],
                    [],
                    capability.Signal,
                    capability.ExpiresAt,
                    capability.LimitedUntil,
                    capability.CredentialModifiedAt);
            })
            .GroupBy(capability => capability.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(capability => capability.Category, StringComparer.Ordinal)
            .ThenBy(capability => capability.Key, StringComparer.Ordinal)
            .ToArray();

        lock (_gate)
        {
            if (!_registrations.TryGetValue(runnerId, out var registration))
                throw new InvalidOperationException(
                    "Register this runner identity before advertising capabilities.");
            if (!string.Equals(registration.InstanceId, request.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Capability advertisement instance does not match the registered runner.");
            if (_capabilityStates.TryGetValue(runnerId, out var existing)
                && request.Generation < existing.Generation)
            {
                throw new InvalidOperationException(
                    $"Capability generation {request.Generation} is older than {existing.Generation}.");
            }

            if (existing is not null)
            {
                capabilities = capabilities
                    .Select(capability =>
                    {
                        if (!capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal))
                            return capability;
                        var previous = existing.Capabilities.FirstOrDefault(item =>
                            string.Equals(item.Key, capability.Key, StringComparison.Ordinal));
                        if (previous is null) return capability;
                        var history = previous.RecoveryHistory;
                        if (!string.Equals(
                                previous.AdvertisedStatus,
                                capability.AdvertisedStatus,
                                StringComparison.Ordinal))
                        {
                            history = history
                                .Append(new Contract.CapabilityRecoveryEventDto(
                                    advertisedAt,
                                    previous.AdvertisedStatus,
                                    capability.AdvertisedStatus,
                                    $"Provider authentication probe changed from {previous.AdvertisedStatus} to {capability.AdvertisedStatus}."))
                                .TakeLast(20)
                                .ToArray();
                        }
                        var failure = _capabilityFailures
                            .GetValueOrDefault(runnerId)?
                            .GetValueOrDefault(capability.Key);
                        if (failure is not null && IsPositiveProviderAuthRecovery(capability))
                        {
                            history = history
                                .Append(new Contract.CapabilityRecoveryEventDto(
                                    advertisedAt,
                                    failure.HealthState,
                                    Contract.CapabilityHealthStates.Healthy,
                                    "Provider authentication probe confirmed recovery."))
                                .TakeLast(20)
                                .ToArray();
                        }
                        return capability with { RecoveryHistory = history };
                    })
                    .ToArray();
            }

            registration = registration with { LastSeenAt = now };
            _registrations[runnerId] = registration;
            _capabilityStates[runnerId] = new CapabilityState(
                request.InstanceId,
                request.Generation,
                capabilities,
                request.Telemetry);
            // A generic advertisement is not a recovery verdict. Provider auth
            // is the deliberate exception: an explicit successful status probe
            // is stronger evidence than the earlier run failure and clears only
            // that provider's circuit without a daemon restart. Retained
            // last-good and transient probe states do not clear a cooldown.
            ClearRecoveredProviderAuthFailuresLocked(runnerId, capabilities);
            if (!HasActiveCapabilityCooldownLocked(runnerId, now))
                ClearCapabilityFailures(runnerId);
            return new Contract.RunnerCapabilitySnapshotDto(
                runnerId,
                registration.Name,
                registration.HostId,
                registration.InstanceId,
                registration.RunnerVersion,
                registration.ProtocolVersion,
                "active",
                registration.RegisteredAt,
                registration.LastSeenAt,
                new Contract.RemoteHostAdmissionDto(
                    registration.HostId,
                    "open",
                    null,
                    null,
                    null,
                    null),
                capabilities,
                request.Telemetry);
        }
    }

    public void RecordReAdoptedSlots(string runnerId, string instanceId, int activeSlots)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(runnerId, out var registration)
                || !string.Equals(registration.InstanceId, instanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Attempt slot telemetry requires the current registered runner instance.");
            }
            _capabilityStates.TryGetValue(runnerId, out var existing);
            var now = DateTime.UtcNow;
            _capabilityStates[runnerId] = new CapabilityState(
                instanceId,
                existing?.Generation ?? 0,
                existing?.Capabilities ?? [],
                new Contract.HostTelemetrySnapshotDto(
                    now,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    activeSlots,
                    TaskServerConnectionStatus: "connected",
                    TaskServerConnectionObservedAt: now));
        }
    }

    /// <summary>
    /// Records one capability failure against this runner and returns the
    /// resulting health verdict. Delivery is idempotent per idempotency key; a
    /// replayed key returns its first verdict without advancing the state
    /// machine, and the same key bound to a different payload is a conflict.
    /// </summary>
    public Contract.CapabilityFailureResponse ReportCapabilityFailure(
        string runnerId,
        Contract.CapabilityFailureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CapabilityKey)
            || string.IsNullOrWhiteSpace(request.Classification)
            || string.IsNullOrWhiteSpace(request.Reason)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException(
                "Capability key, classification, reason, and idempotency key are required.");
        }

        var now = DateTime.UtcNow;
        var occurredAt = request.OccurredAt.ToUniversalTime();
        if (occurredAt > now.AddMinutes(2))
            throw new ArgumentException("Capability failure time is too far in the future.");

        var capabilityKey = request.CapabilityKey.Trim().ToLowerInvariant();
        var deliveryKey = DiagnosticKey(runnerId, request.IdempotencyKey);
        var payloadHash = HashPayload(request);
        lock (_gate)
        {
            // An unregistered id is still accepted and recorded for compatibility;
            // only a stale instance of a known id is rejected, because that report
            // describes a runner that no longer exists.
            if (_registrations.TryGetValue(runnerId, out var registration)
                && !string.Equals(registration.InstanceId, request.InstanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Capability failure instance does not match the registered runner.");
            }
            if (_capabilityFailureDeliveries.TryGetValue(deliveryKey, out var delivered))
            {
                if (!string.Equals(delivered.PayloadHash, payloadHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Capability failure idempotency key is bound to another payload.");
                return delivered.Response;
            }

            if (!_capabilityFailures.TryGetValue(runnerId, out var failures))
            {
                failures = new Dictionary<string, CapabilityFailureState>(StringComparer.Ordinal);
                _capabilityFailures[runnerId] = failures;
            }
            failures.TryGetValue(capabilityKey, out var previous);
            if (previous is not null && occurredAt < previous.LastFailureAt)
                throw new InvalidOperationException(
                    "Capability failure is older than the current failure state.");

            var wholeHost = WholeHostCapabilities.Contains(capabilityKey);
            var consecutive = (previous?.ConsecutiveFailures ?? 0) + 1;
            var state = previous?.HealthState == Contract.CapabilityHealthStates.Draining
                        || wholeHost
                        || consecutive >= CapabilityFailureThreshold
                ? Contract.CapabilityHealthStates.Draining
                : Contract.CapabilityHealthStates.Suspect;
            DateTime? cooldownUntil = state == Contract.CapabilityHealthStates.Draining
                ? now.AddSeconds(
                    CapabilityBaseCooldownSeconds * (1 << Math.Min(Math.Max(0, consecutive - 2), 4)))
                : null;
            failures[capabilityKey] = new CapabilityFailureState(
                capabilityKey,
                state,
                request.Classification,
                request.Reason,
                previous?.FirstFailureAt ?? occurredAt,
                occurredAt,
                cooldownUntil,
                consecutive,
                wholeHost);

            var response = new Contract.CapabilityFailureResponse(
                "accepted",
                capabilityKey,
                state,
                cooldownUntil,
                wholeHost,
                state == Contract.CapabilityHealthStates.Draining
                    ? "Claims for this runner are paused until the cooldown expires or it "
                      + "registers again."
                    : null);
            _capabilityFailureDeliveries[deliveryKey] = new CapabilityFailureDelivery(
                payloadHash, response, now);
            TrimOldest(_capabilityFailureDeliveries, entry => entry.ReceivedAt);
            return response;
        }
    }

    /// <summary>
    /// True while a drained capability holds this runner's claims. The pause is
    /// self-healing but not free: it lifts when the cooldown expires, or when the
    /// runner re-registers (a restarted daemon). A capability advertisement alone
    /// does not lift it - the daemon repeats that every minute and would otherwise
    /// never actually be drained.
    /// </summary>
    public bool TryGetCapabilityPause(string runnerId, out CapabilityPause pause)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_capabilityFailures.TryGetValue(runnerId, out var failures))
            {
                var drained = failures.Values
                    .Where(state => state.CooldownUntil > now)
                    .OrderByDescending(state => state.CooldownUntil)
                    .FirstOrDefault();
                if (drained is not null)
                {
                    pause = new CapabilityPause(
                        drained.Key,
                        drained.Classification,
                        drained.Reason,
                        drained.CooldownUntil!.Value,
                        drained.WholeHost);
                    return true;
                }
            }
        }
        pause = default!;
        return false;
    }

    /// <summary>
    /// Accepts one runner outbox snapshot for diagnosis. Sanity and monotonicity
    /// are enforced as in the Task Server reference; only the latest snapshot per
    /// run is kept, in memory, because the authoritative handoff travels through
    /// the completion and result-handoff routes.
    /// </summary>
    public Contract.RunnerOutboxStatusDto RecordOutboxStatus(
        string runnerId,
        Contract.RunnerOutboxStatusRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InstanceId))
            throw new ArgumentException("Outbox status instance id is required.");
        if (string.IsNullOrWhiteSpace(request.RunId))
            throw new ArgumentException("Outbox status runId is required.");
        if (request.LastAcknowledgedSequence > request.LastSequence)
            throw new ArgumentException("Outbox acknowledgement cannot exceed its last sequence.");
        if (request.BacklogCount < 0)
            throw new ArgumentException("Outbox backlog cannot be negative.");
        if ((request.BacklogCount == 0) != (request.OldestUnacknowledgedSequence is null))
            throw new ArgumentException(
                "Oldest unacknowledged sequence must be present exactly when backlog is non-zero.");

        var status = new Contract.RunnerOutboxStatusDto(
            runnerId,
            request.InstanceId,
            request.LastSequence,
            request.LastAcknowledgedSequence,
            request.BacklogCount,
            request.OldestUnacknowledgedSequence,
            request.FinalHandoffState,
            request.RunId,
            request.EnvelopeDigest,
            request.ObservedAt.ToUniversalTime());
        var key = DiagnosticKey(runnerId, request.RunId);
        lock (_gate)
        {
            if (_outboxStatuses.TryGetValue(key, out var existing)
                && request.LastSequence < existing.Status.LastSequence)
            {
                throw new InvalidOperationException(
                    $"Outbox status sequence {request.LastSequence} is older than "
                    + $"{existing.Status.LastSequence}.");
            }
            _outboxStatuses[key] = new OutboxStatusEntry(status, DateTime.UtcNow);
            TrimOldest(_outboxStatuses, entry => entry.ReceivedAt);
        }
        return status;
    }

    /// <summary>Latest recorded outbox snapshot for one run, for diagnosis.</summary>
    public Contract.RunnerOutboxStatusDto? GetOutboxStatus(string runnerId, string runId)
    {
        lock (_gate)
        {
            return _outboxStatuses.TryGetValue(DiagnosticKey(runnerId, runId), out var entry)
                ? entry.Status
                : null;
        }
    }

    /// <summary>
    /// Returns the latest in-memory capability advertisement for every local-v1
    /// runner identity. The standalone Task Server exposes the same wire shape
    /// from its durable store at GET /api/v1/management/remote-hosts.
    /// </summary>
    public IReadOnlyList<Contract.RunnerCapabilitySnapshotDto> ListCapabilitySnapshots()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            return _registrations
                .OrderBy(entry => entry.Value.HostId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Value.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry =>
                {
                    var runnerId = entry.Key;
                    var registration = entry.Value;
                    _capabilityStates.TryGetValue(runnerId, out var state);
                    _capabilityFailures.TryGetValue(runnerId, out var failures);
                    var capabilities = (state?.Capabilities ?? [])
                        .Select(capability =>
                        {
                            var failure = failures?.GetValueOrDefault(capability.Key);
                            return capability with
                            {
                                HealthState = failure?.HealthState
                                              ?? Contract.CapabilityHealthStates.Healthy,
                                Reason = failure?.Reason,
                                IsFresh = capability.FreshUntil > now,
                                FirstFailureAt = failure?.FirstFailureAt,
                                LastFailureAt = failure?.LastFailureAt,
                                CooldownUntil = failure?.CooldownUntil,
                                ConsecutiveFailures = failure?.ConsecutiveFailures ?? 0,
                            };
                        })
                        .ToArray();
                    var automaticDrain = failures?.Values
                        .Where(failure => failure.WholeHost && failure.CooldownUntil > now)
                        .OrderByDescending(failure => failure.CooldownUntil)
                        .FirstOrDefault();
                    return new Contract.RunnerCapabilitySnapshotDto(
                        runnerId,
                        registration.Name,
                        registration.HostId,
                        registration.InstanceId,
                        registration.RunnerVersion,
                        registration.ProtocolVersion,
                        "active",
                        registration.RegisteredAt,
                        registration.LastSeenAt,
                        new Contract.RemoteHostAdmissionDto(
                            registration.HostId,
                            automaticDrain is null ? "open" : "automatic-drain",
                            automaticDrain?.Reason,
                            automaticDrain?.LastFailureAt,
                            null,
                            null),
                        capabilities,
                        state?.Telemetry,
                        RoleMaxParallelism: registration.RoleMaxParallelism);
                })
                .ToArray();
        }
    }

    public bool TryGetReviewExecutor(string runnerId, string instanceId, out ReviewExecutor executor)
    {
        if (_registrations.TryGetValue(runnerId, out var registration)
            && string.Equals(registration.InstanceId, instanceId, StringComparison.Ordinal)
            && registration.Capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor))
        {
            executor = new ReviewExecutor(registration.HostId, registration.Capabilities);
            return true;
        }
        executor = default!;
        return false;
    }

    /// <summary>
    /// Applies the published capability-admission contract to a legacy coding
    /// claim. The monolith still owns card selection, but it consumes the same
    /// fresh, ready advertisement used by the separated Task Server instead of
    /// maintaining a second scheduler-specific host inventory.
    /// </summary>
    public CodingCapabilityAdmission EvaluateCodingAdmission(
        string runnerId,
        string? instanceId,
        IReadOnlyList<string> requestedCapabilities)
    {
        var required = requestedCapabilities
            .Select(key => key.Trim().ToLowerInvariant())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!_registrations.TryGetValue(runnerId, out var registration)
                || !registration.Capabilities.Contains(Contract.ReviewCapabilities.CodingExecutor))
            {
                return CodingCapabilityAdmission.Blocked(
                    required,
                    "Runner has not registered a coding-executor capability identity.");
            }
            if (string.IsNullOrWhiteSpace(instanceId)
                || !string.Equals(registration.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return CodingCapabilityAdmission.Blocked(
                    required,
                    "Claim capability instance does not match the registered coding runner.");
            }
            if (!_capabilityStates.TryGetValue(runnerId, out var state)
                || !string.Equals(state.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return CodingCapabilityAdmission.Blocked(
                    required,
                    "Runner has no capability advertisement for this coding instance.");
            }

            foreach (var key in required)
            {
                var capability = state.Capabilities.FirstOrDefault(item =>
                    string.Equals(item.Key, key, StringComparison.Ordinal));
                if (capability is null)
                    return CodingCapabilityAdmission.Blocked(
                        required,
                        $"Required capability '{key}' was not advertised.");
                if (capability.FreshUntil <= now)
                    return CodingCapabilityAdmission.Blocked(
                        required,
                        $"Required capability '{key}' is stale since {capability.FreshUntil:O}.");
                if (!string.Equals(capability.AdvertisedStatus, "ready", StringComparison.Ordinal))
                    return CodingCapabilityAdmission.Blocked(
                        required,
                        $"Required capability '{key}' is advertised as {capability.AdvertisedStatus}.");
            }

            if (_capabilityFailures.TryGetValue(runnerId, out var failures))
            {
                var draining = failures.Values
                    .Where(failure =>
                        failure.CooldownUntil > now
                        && (failure.WholeHost || required.Contains(failure.Key, StringComparer.Ordinal)))
                    .OrderByDescending(failure => failure.CooldownUntil)
                    .FirstOrDefault();
                if (draining is not null)
                {
                    return CodingCapabilityAdmission.Blocked(
                        required,
                        $"Required capability '{draining.Key}' is draining until {draining.CooldownUntil:O}.");
                }
            }
        }
        return new CodingCapabilityAdmission(true, null, required);
    }

    /// <summary>
    /// Test seam: backdates this runner's recorded capability failures so the
    /// cooldown-expiry path can be exercised without waiting out the real
    /// two-minute backoff. Never called in production.
    /// </summary>
    internal void AgeCapabilityFailuresForTests(string runnerId, TimeSpan age)
    {
        lock (_gate)
        {
            if (!_capabilityFailures.TryGetValue(runnerId, out var failures)) return;
            foreach (var key in failures.Keys.ToList())
            {
                var state = failures[key];
                failures[key] = state with
                {
                    LastFailureAt = state.LastFailureAt - age,
                    CooldownUntil = state.CooldownUntil - age,
                };
            }
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private bool HasActiveCapabilityCooldownLocked(string runnerId, DateTime now)
        => _capabilityFailures.TryGetValue(runnerId, out var failures)
           && failures.Values.Any(state => state.CooldownUntil > now);

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void ClearRecoveredProviderAuthFailuresLocked(
        string runnerId,
        IReadOnlyList<Contract.CapabilityHealthDto> capabilities)
    {
        if (!_capabilityFailures.TryGetValue(runnerId, out var failures)) return;
        foreach (var capability in capabilities.Where(IsPositiveProviderAuthRecovery))
            failures.Remove(capability.Key);
        if (failures.Count == 0) _capabilityFailures.Remove(runnerId);
    }

    private static bool IsPositiveProviderAuthRecovery(Contract.CapabilityHealthDto capability)
        => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)
           && string.Equals(capability.AdvertisedStatus, "ready", StringComparison.Ordinal)
           && capability.Signal is "ok" or "credentials-expiring";

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void ClearCapabilityFailures(string runnerId)
    {
        _capabilityFailures.Remove(runnerId);
        var prefix = DiagnosticKey(runnerId, string.Empty);
        foreach (var key in _capabilityFailureDeliveries.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            _capabilityFailureDeliveries.Remove(key);
        }
    }

    private static void TrimOldest<TValue>(
        Dictionary<string, TValue> entries,
        Func<TValue, DateTime> receivedAt)
    {
        if (entries.Count <= DiagnosticRetention) return;
        foreach (var key in entries
                     .OrderBy(entry => receivedAt(entry.Value))
                     .Take(entries.Count - DiagnosticRetention)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            entries.Remove(key);
        }
    }

    /// <summary>
    /// Composite diagnostic key. The unit separator cannot occur in a runner id,
    /// idempotency key, or run id, so keys never collide across runners.
    /// </summary>
    private static string DiagnosticKey(string runnerId, string suffix)
        => $"{runnerId}{suffix}";

    private static string HashPayload(Contract.CapabilityFailureRequest request)
        => Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(request, PayloadJson)))
            .ToLowerInvariant();

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private sealed record Registration(
        string Name,
        string HostId,
        string InstanceId,
        string RunnerVersion,
        int ProtocolVersion,
        IReadOnlySet<string> Capabilities,
        int RoleMaxParallelism,
        DateTime RegisteredAt,
        DateTime LastSeenAt);

    private sealed record CapabilityState(
        string InstanceId,
        long Generation,
        IReadOnlyList<Contract.CapabilityHealthDto> Capabilities,
        Contract.HostTelemetrySnapshotDto? Telemetry);

    private sealed record CapabilityFailureState(
        string Key,
        string HealthState,
        string Classification,
        string Reason,
        DateTime FirstFailureAt,
        DateTime LastFailureAt,
        DateTime? CooldownUntil,
        int ConsecutiveFailures,
        bool WholeHost);

    private sealed record CapabilityFailureDelivery(
        string PayloadHash,
        Contract.CapabilityFailureResponse Response,
        DateTime ReceivedAt);

    private sealed record OutboxStatusEntry(
        Contract.RunnerOutboxStatusDto Status,
        DateTime ReceivedAt);

    public sealed record CapabilityPause(
        string CapabilityKey,
        string Classification,
        string Reason,
        DateTime CooldownUntil,
        bool WholeHost);

    public sealed record CodingCapabilityAdmission(
        bool Eligible,
        string? Message,
        IReadOnlyList<string> Required)
    {
        public static CodingCapabilityAdmission Blocked(
            IReadOnlyList<string> required,
            string message)
            => new(false, message, required);
    }

    public sealed record ReviewExecutor(string HostId, IReadOnlySet<string> Capabilities);
}
