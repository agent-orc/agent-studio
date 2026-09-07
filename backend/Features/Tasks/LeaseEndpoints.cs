using AgentStudio.Pipeline;
using AgentStudio.Runner;
using System.Text;
using CapabilityProtocol = AgentStudio.TaskServer.Contracts.CapabilityProtocol;

namespace AgentStudio.Tasks;

/// <summary>
/// The fenced Runner ↔ Server run-lease API under <c>/api/runner/lease</c>
/// (parallel-task-execution.md §8.2C; ADR-0060). The server is the single lease
/// authority: <c>acquire</c> mints a lease id + a monotonic fencing token per
/// task, <c>renew</c>/<c>release</c> must present the current token, and a stale
/// token — presented after a TTL takeover raised the fence — is rejected. That
/// rejection is the split-brain guard.
///
/// <para>
/// The endpoints are thin glue over the unit-tested lease authority
/// (<see cref="RunLeaseService"/>): they validate the task exists, stamp the
/// caller's <see cref="RunnerIdentity"/> onto a partial acquire request, and
/// return the service's <see cref="RunLeaseResponse"/> verbatim. This is the
/// productive successor to the disk-backed <c>.pickup-lock.json</c> lease
/// (ADR-0044, <see cref="PickupLockFile"/>), which stays the same-machine pickup
/// guard until the runner split (ADR-0059) cuts over.
/// </para>
/// </summary>
public static class LeaseEndpoints
{
    private static readonly SemaphoreSlim ClaimGate = new(1, 1);

    public static void MapLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/lease");

        group.MapPost("/acquire", async (
            RunLeaseAcquireRequest req,
            HttpContext context,
            ITaskScanner scanner,
            ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects,
            RunLeaseService leases,
            RunnerIdentity identity,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName)) return Results.Unauthorized();
            await ClaimGate.WaitAsync(ct);
            try
            {
                var task = FindTask(scanner, req.TaskKey);
                if (task is null)
                    return Results.NotFound(new RunLeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));
                if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal)
                {
                    var settingsProject = settings.Get(task.ProjectName);
                    if (!ProjectExecutionPolicy.IsAssignedRemote(
                            settingsProject, principal.RunnerId, principal.RunnerName))
                        return Results.Json(new RunLeaseResponse(
                            "ProjectDenied", false, null,
                            "The Runner is not assigned to this project's execution location."), statusCode: StatusCodes.Status403Forbidden);
                }
                var project = projects.FindByStorageLocation(task.WatchPath)
                              ?? projects.FindByIdOrDisplayName(task.ProjectName);
                var repository = RemoteProjectRepositoryResolver.Resolve(
                    project,
                    settings.Get(task.ProjectName).IntegrationBranch);
                var clientId = context.Items["ClientId"] as string
                               ?? context.Request.Headers["X-Client-Id"].ToString();
                var canonical = StampIdentity(req, identity) with
                {
                    RepositoryId = repository?.RepositoryId
                                   ?? (string.IsNullOrWhiteSpace(project?.Id) ? task.ProjectName : project.Id),
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                };
                return Results.Ok(leases.TryAcquire(canonical));
            }
            finally
            {
                ClaimGate.Release();
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        group.MapPost("/renew", (RunLeaseHeartbeatRequest req, HttpContext context, RunLeaseService leases) =>
            !RunnerMatches(context, req.RunnerId)
                ? Results.Unauthorized()
                : CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                    ? Results.Ok(leases.Renew(req))
                    : Results.Conflict(new RunLeaseResponse(
                        "Invalid", false, null,
                        "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease renewal.")))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapPost("/release", (RunLeaseReleaseRequest req, HttpContext context, RunLeaseService leases) =>
            !RunnerMatches(context, req.RunnerId)
                ? Results.Unauthorized()
                : CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                    ? Results.Ok(leases.Release(req))
                    : Results.Conflict(new RunLeaseResponse(
                        "Invalid", false, null,
                        "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease release.")))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapGet("/{taskKey}", (string taskKey, RunLeaseService leases) =>
            Results.Ok(leases.Peek(taskKey)));

        // Interactive project-chat work uses the same remote pull direction as
        // card runs. Studio queues an opaque request for the project's assigned
        // runner; the host claims, renews and completes it with a claim token.
        // No central process reaches into the runner over SSH.
        app.MapPost("/api/runner/project-chat/claim",
            (RemoteChatWorkClaimRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId, req.RunnerName))
                    return Results.Unauthorized();
                return Results.Ok(broker.TryClaim(req));
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/runner/project-chat/renew",
            (RemoteChatWorkRenewRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId))
                    return Results.Unauthorized();
                return broker.Renew(req)
                    ? Results.Ok(new { renewed = true })
                    : Results.Conflict(new { renewed = false, error = "stale project-chat claim" });
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/runner/project-chat/complete",
            (RemoteChatWorkCompletionRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId))
                    return Results.Unauthorized();
                return broker.Complete(req)
                    ? Results.Ok(new { accepted = true })
                    : Results.Conflict(new { accepted = false, error = "stale project-chat claim" });
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);

        // Daemon pickup is selected server-side from the project record. The
        // gate makes scan + fenced lease + ready-to-progress move one claim
        // critical section for all remote contenders. The local runner reads
        // the same resolved executionLocation and therefore never enters this race.
        app.MapPost("/api/runner/claim", async (
            RunnerClaimRequest req,
            TaskScannerService scanner,
            AgentStudio.Projects.ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects,
            TaskTransitionService transitions,
            RunLeaseService leases,
            TaskSessionLog sessions,
            HttpContext context,
            AgentStudio.Clients.ClientIdentityStore clients,
            AgentStudio.Clients.HostTelemetryStore telemetry,
            AccessSecurityStore accessSecurity,
            HumanReviewEscalation humanReviewEscalation,
            AgentStudio.Runner.V1ReviewExecutorRegistry capabilityRegistry,
            AgentStudio.Prompts.RuntimePromptService prompts,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            PromptEnrichmentService promptEnrichment,
            DossierMaintenanceService dossierMaintenance,
            RemoteDispatchRejectionStore dispatchRejections,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerClaim");
            var remoteClaimFailures = new RemoteClaimFailureBudget(
                loggerFactory.CreateLogger<RemoteClaimFailureBudget>());
            var remoteDeliveryFailures = new RemoteDeliveryFailureStore(
                loggerFactory.CreateLogger<RemoteDeliveryFailureStore>());
            void RecordRejection(TaskInfo task, string code, string? reason) =>
                dispatchRejections.Record(
                    task,
                    req.RunnerId,
                    req.RunnerName,
                    code,
                    reason);
            if (string.IsNullOrWhiteSpace(req.RunnerId) || string.IsNullOrWhiteSpace(req.RunnerName))
                return Results.BadRequest(new RunnerClaimResponse(RunnerClaimStatus.Invalid, Message: "runnerId and runnerName are required."));
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName))
                return Results.Unauthorized();

            var runnerPrincipal = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
            var clientId = context.Items["ClientId"] as string ?? context.Request.Headers["X-Client-Id"].ToString();
            if (req.Telemetry is not null && !string.IsNullOrWhiteSpace(clientId))
                telemetry.Append(clientId, req.Telemetry);
            int? activeSlots = req.ActiveSlots is not null
                ? Math.Max(0, req.ActiveSlots.Value)
                : req.Telemetry is null
                    ? null
                    : Math.Max(0, req.Telemetry.ActiveSlots);
            var securedRunner = runnerPrincipal is null
                ? null
                : accessSecurity.RecordRunnerActivity(
                    runnerPrincipal.RunnerId, activeSlots, req.AvailableSlots, claimed: false);
            // Seeds the central ceiling on first contact only, from what the
            // daemon itself declared: RUNNER_MAX_PARALLELISM, or - for daemons
            // too old to send it - the ceiling they report as adopted. The
            // deprecated project maxParallelism may only narrow that value - it
            // is an operator intent to run fewer things at once, while raising
            // the seed above the declaration would hand out slots the host does
            // not have. A host that declares nothing keeps a null ceiling: the
            // server enforces nothing rather than inventing a cap out of a
            // project setting.
            // Once a ceiling is persisted the seed is not recomputed - the
            // project scan must not run on every poll.
            // DEPRECATED COMPAT: drop the project term after 2026-10-01.
            var persistedCeiling = string.IsNullOrWhiteSpace(clientId)
                ? null
                : clients.Find(clientId)?.RunnerDesiredMaxParallelism;
            var daemonDeclaredCeiling = req.BootstrapMaxParallelism is > 0
                ? req.BootstrapMaxParallelism
                : req.EffectiveMaxParallelism;
            var projectCompatCeiling = persistedCeiling is > 0
                ? null
                : DeprecatedProjectCompatCeiling(settings, req.RunnerId, req.RunnerName);
            var seedCeiling = persistedCeiling is > 0
                ? persistedCeiling
                : HostCapacityPolicy.ResolveCeiling(
                    null,
                    projectCompatCeiling,
                    daemonDeclaredCeiling);
            var client = string.IsNullOrWhiteSpace(clientId)
                ? null
                : clients.RecordRunnerActivity(
                    clientId,
                    activeSlots,
                    req.AvailableSlots,
                    claimed: false,
                    seedMaxParallelism: seedCeiling,
                    effectiveMaxParallelism: req.EffectiveMaxParallelism,
                    effectiveMaxParallelismAppliedAt: req.EffectiveMaxParallelismAppliedAt);
            // The migration is a one-off state change on the host identity and
            // must be readable in the log; it used to happen silently. The guard
            // makes it fire on the single poll that persists the seed.
            if (persistedCeiling is not > 0 && client?.RunnerDesiredMaxParallelism is > 0)
                logger.LogInformation(
                    "host-capacity-seeded client={ClientId} runner={Runner} ceiling={Ceiling} " +
                    "daemonDeclared={DaemonDeclared} projectCompat={ProjectCompat}",
                    clientId, req.RunnerName, client.RunnerDesiredMaxParallelism,
                    daemonDeclaredCeiling, projectCompatCeiling);
            // Null ceiling: no operator target, no daemon report, no project
            // opt-in. The server then enforces nothing - inventing a ceiling
            // would be a silent throttle on a fleet that never asked for one.
            var hostCeiling = HostCapacityPolicy.ResolveCeiling(
                client?.RunnerDesiredMaxParallelism, null, seedCeiling);
            var capacityTargets = hostCeiling is null ? null : new HostCapacityTargets(
                hostCeiling.Value,
                client?.RunnerTargetLoadPercent ?? HostCapacityPolicy.DefaultTargetLoadPercent,
                RunnerRampStrategies.Normalize(client?.RunnerRampStrategy));
            // Every poll answer carries the central policy, granted or not, so a
            // daemon adopts a changed ceiling without waiting for a claim.
            RunnerClaimResponse WithCapacity(RunnerClaimResponse response, string? admissionReason = null)
                => response with
                {
                    DesiredMaxParallelism = capacityTargets?.MaxParallelism,
                    TargetLoadPercent = capacityTargets?.TargetLoadPercent,
                    RampStrategy = capacityTargets?.RampStrategy,
                    AdmissionReason = admissionReason ?? response.AdmissionReason,
                };
            if (securedRunner is not null && !accessSecurity.RunnerAcceptsClaims(securedRunner.Id))
                return Results.Ok(WithCapacity(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: securedRunner.RetiredAt is not null
                        ? "runner is retired"
                        : securedRunner.RetireRequestedAt is not null
                            ? "runner is draining and will retire after active work finishes"
                            : "runner is draining; no new leases are admitted")));
            if (client?.DrainRequestedAt is not null || client?.Kind == ClientIdentityKind.Retired)
                return Results.Ok(WithCapacity(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: client.Kind == ClientIdentityKind.Retired
                        ? "runner is retired"
                        : client.RetireRequestedAt is not null
                            ? "runner is draining and will retire after active work finishes"
                            : "runner is draining; no new leases are admitted")));
            await ClaimGate.WaitAsync(ct);
            try
            {
                // A successful claim has already moved its selected card to
                // Progress, so replay must consult durable acquire authority
                // before Ready-task selection. Any known delivery, including a
                // stale or superseded one, is terminal for this request and
                // must never fall through to claim unrelated work.
                var requestedClaimKey = req.IdempotencyKey?.Trim();
                if (!string.IsNullOrWhiteSpace(requestedClaimKey))
                {
                    var replay = leases.TryReplayAcquire(
                        req.RunnerId.Trim(), requestedClaimKey);
                    if (replay is not null)
                    {
                        if (!replay.Granted || replay.Lease is null)
                        {
                            return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: replay.Message ?? replay.Outcome)));
                        }

                        var replayedTask = FindTask(scanner, replay.Lease.TaskKey);
                        if (replayedTask is null)
                        {
                            return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: "The original claim task is no longer available.")));
                        }

                        var replayLane = RemoteClaimReplayLanePolicy.Decide(replayedTask.State);
                        if (replayLane.Action == RemoteClaimReplayLaneAction.Refuse)
                        {
                            return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: replayLane.Message)));
                        }
                        if (replayLane.Action == RemoteClaimReplayLaneAction.RepairToProgress)
                        {
                            var replayMove = await transitions.MoveAsync(
                                replayedTask.Id,
                                TaskStates.Progress,
                                replayedTask.WatchPath,
                                ct,
                                cause: $"remote-runner-replay:{req.RunnerName.Trim()}",
                                authorityWrite: new AttemptWriteReference(
                                    replay.Lease.AttemptId!,
                                    replay.Lease.FencingToken,
                                    replay.Lease.AuthorityEpoch,
                                    $"lane-claim:{requestedClaimKey}"),
                                expectedSourceState: TaskStates.Ready,
                                transitionCause: LaneChangeCauses.Claimed,
                                transitionDetail: "claim-replay");
                            if (replayMove.Status != MoveJobStatus.Success)
                            {
                                // Do not release replayed authority here. The
                                // original process may already be running, and
                                // its live lease is the remaining admission
                                // guard until a later replay converges the lane.
                                logger.LogWarning(
                                    "remote-runner-claim-replay-move-failed task={TaskKey} runner={Runner} status={Status} message={Message}",
                                    replay.Lease.TaskKey,
                                    req.RunnerName,
                                    replayMove.Status,
                                    replayMove.Message);
                                return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                    RunnerClaimStatus.Empty,
                                    Message: $"claim replay move refused: {replayMove.Status} {replayMove.Message}")));
                            }

                            logger.LogInformation(
                                "remote-runner-claim-replay-lane-repaired task={TaskKey} runner={Runner} attempt={AttemptId} from={FromState} to={ToState}",
                                replay.Lease.TaskKey,
                                req.RunnerName,
                                replay.Lease.AttemptId,
                                TaskStates.Ready,
                                TaskStates.Progress);
                        }

                        // Read back task truth after the convergence write. Do
                        // not return Claimed while folder/task.json still expose
                        // the card as claimable, even when acquire authority is
                        // already durable.
                        replayedTask = FindTask(scanner, replay.Lease.TaskKey);
                        if (replayedTask is null
                            || !string.Equals(replayedTask.State, TaskStates.Progress, StringComparison.Ordinal))
                        {
                            return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: "The original claim lane did not converge to Progress.")));
                        }

                        var replayedProject = projects.FindByStorageLocation(replayedTask.WatchPath)
                                              ?? projects.FindByIdOrDisplayName(replayedTask.ProjectName);
                        var replayedRepository = RemoteProjectRepositoryResolver.Resolve(
                            replayedProject,
                            settings.Get(replayedTask.ProjectName).IntegrationBranch);
                        if (replayedRepository is null)
                        {
                            return Results.Ok(WithCapacity(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: "The original claim repository is no longer configured.")));
                        }

                        // A replay re-serves a lease this host already holds, so
                        // occupancy does not grow. Recording the unchanged count
                        // keeps the ledger free of the old "active + 1" drift.
                        var replayedActiveRuns = Math.Max(
                            CountHostLeases(
                                scanner.ScanAllJobs().Where(task => !task.Fixture),
                                leases,
                                clientId,
                                req.RunnerId),
                            activeSlots ?? 0);
                        // Without a ceiling there is nothing to derive from, so the
                        // daemon's own headroom minus the served lease stays the
                        // compatibility answer.
                        var replayedFreeSlots = capacityTargets is null
                            ? Math.Max(0, req.AvailableSlots - 1)
                            : HostCapacityPolicy.FreeSlots(capacityTargets.MaxParallelism, replayedActiveRuns);
                        if (runnerPrincipal is not null)
                            accessSecurity.RecordRunnerActivity(
                                runnerPrincipal.RunnerId,
                                replayedActiveRuns,
                                replayedFreeSlots,
                                claimed: true);
                        if (!string.IsNullOrWhiteSpace(clientId))
                            clients.RecordRunnerActivity(
                                clientId,
                                replayedActiveRuns,
                                replayedFreeSlots,
                                claimed: true,
                                seedMaxParallelism: seedCeiling,
                                effectiveMaxParallelism: req.EffectiveMaxParallelism,
                                effectiveMaxParallelismAppliedAt: req.EffectiveMaxParallelismAppliedAt);
                        return Results.Ok(WithCapacity(new RunnerClaimResponse(
                            RunnerClaimStatus.Claimed,
                            replay.Lease.TaskKey,
                            replayedTask.Id,
                            replayedTask.ProjectName,
                            replay.Lease,
                            ProjectId: replayedRepository.ProjectId,
                            RepositoryUrl: replayedRepository.RepositoryUrl,
                            DefaultBranch: replayedRepository.DefaultBranch,
                            TaskKind: replayedTask.Kind,
                            // A replay must describe the same run as the original
                            // claim, including the persisted enrichment framing.
                            RunSpec: AddPersistedPromptEnrichment(
                                BuildRunSpec(replayedTask, settings, prompts, dossierMaintenance),
                                replayedTask))));
                    }
                }

                var recoveredSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var activeTaskKeys = req.ActiveTaskKeys is null
                    ? null
                    : req.ActiveTaskKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requeueGraceSeconds = Math.Clamp(
                    configuration.GetValue("Runner:RemoteRequeue:GraceSeconds", 120),
                    1,
                    900);
                var now = DateTime.UtcNow;

                // A daemon or server restart may leave the card in Progress while
                // its original CLI still runs. A free lease is therefore not
                // enough to requeue: wait through the authority grace and require
                // this assigned runner poll to answer that the task is absent
                // from its active process set.
                foreach (var interrupted in scanner.ScanAllJobs()
                             .Where(t => !t.Fixture && t.State == TaskStates.Progress))
                {
                    var project = settings.Get(interrupted.ProjectName);
                    if (!ProjectExecutionPolicy.AllowsAutomaticPickup(project)
                        || !ProjectExecutionPolicy.IsAssignedRemote(project, req.RunnerId, req.RunnerName))
                        continue;
                    var interruptedKey = interrupted.Key ?? interrupted.TaskKey ?? interrupted.Id;
                    if (leases.Peek(interruptedKey).Outcome != "Free") continue;
                    var inspection = leases.Inspect(interruptedKey);
                    var lastAuthorityActivity = inspection.Lease?.LastHeartbeatAt
                                                ?? interrupted.EnteredLaneAt;
                    var requeueDecision = RemoteRunRequeuePolicy.Decide(
                        new RemoteRunRequeueFacts(
                            Math.Max(0, (now - lastAuthorityActivity.ToUniversalTime()).TotalSeconds),
                            requeueGraceSeconds,
                            RunnerRespondedWithActiveSet: activeTaskKeys is not null,
                            RunnerReportsTaskActive: activeTaskKeys?.Contains(interruptedKey) == true));
                    if (requeueDecision.Action != RemoteRunRequeueAction.Requeue)
                    {
                        logger.LogInformation(
                            "remote-runner-requeue-deferred task={TaskKey} runner={Runner} reason={Reason} detail={Detail}",
                            interruptedKey, req.RunnerName, requeueDecision.ReasonCode, requeueDecision.Detail);
                        continue;
                    }
                    var recoveryWrite = leases.CurrentWriteReference(
                        interruptedKey,
                        $"lane-recovery:{interruptedKey}:{req.RunnerId.Trim()}");
                    var preparationFailure = remoteClaimFailures.GetState(interrupted);
                    if (preparationFailure?.Attempts >= RemoteClaimFailureBudget.MaxAttempts)
                    {
                        var reason =
                            $"Remote claim repository/environment preparation failed " +
                            $"({preparationFailure.Attempts}/{RemoteClaimFailureBudget.MaxAttempts}): " +
                            preparationFailure.Reason;
                        var escalated = await humanReviewEscalation.EscalateAsync(
                            interrupted.Id,
                            interrupted.WatchPath,
                            interrupted.ProjectName,
                            HumanReviewEscalationCategories.RemoteClaimEnvironment,
                            reason,
                            ct,
                            recoveryWrite);
                        logger.Log(
                            escalated.Status == MoveJobStatus.Success
                                ? LogLevel.Error
                                : LogLevel.Critical,
                            "remote-claim-exhausted-recovery project={Project} task={TaskKey} status={Status} reason={Reason}",
                            interrupted.ProjectName,
                            interruptedKey,
                            escalated.Status,
                            reason);
                        continue;
                    }
                    await transitions.MoveAsync(
                        interrupted.Id, TaskStates.Ready, interrupted.WatchPath, ct,
                        cause: $"remote-runner-lease-recovery:{req.RunnerName.Trim()}",
                        authorityWrite: recoveryWrite,
                        suppressProductExecution: true,
                        transitionCause: LaneChangeCauses.LeaseRecovery,
                        transitionDetail: requeueDecision.ReasonCode);
                    if (recoveryWrite is not null)
                        recoveredSources[interruptedKey] = recoveryWrite.AttemptId;
                }

                var claimSnapshot = scanner.GetLiveSnapshotWithReferenceIndex();
                var liveSnapshot = claimSnapshot.Live;
                var waitsOn = claimSnapshot.References;

                // Server-owned occupancy: leases this host currently holds. The
                // daemon's own count is only a floor, so a second daemon process
                // on the same host cannot spend the ceiling twice.
                var hostActiveRuns = Math.Max(
                    CountHostLeases(liveSnapshot, leases, clientId, req.RunnerId),
                    activeSlots ?? 0);

                // The ramp timestamp is read here for the same reason as the
                // lease count: it was captured before this request queued on the
                // gate, so a claim granted while we waited would be invisible and
                // the conservative one-per-60s would degrade to one per waiting
                // request. RecordRunnerActivity only advances it on a granted
                // claim, so re-reading sees exactly the last admission.
                var lastAdmissionAt = (string.IsNullOrWhiteSpace(clientId)
                    ? null
                    : clients.Find(clientId)?.RunnerLastClaimAt) ?? client?.RunnerLastClaimAt;

                if (req.AvailableSlots <= 0)
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty,
                        Message: "runner status recorded; the daemon reports no free host slots")));

                var admission = capacityTargets is null
                    ? new HostAdmissionVerdict(
                        true, HostAdmissionReasons.NoCentralCeiling, "no central host ceiling is configured")
                    : HostCapacityPolicy.Decide(
                        capacityTargets,
                        new HostAdmissionFacts(
                            hostActiveRuns,
                            now,
                            lastAdmissionAt,
                            req.Telemetry?.CpuPercent));
                if (!admission.Admitted)
                {
                    logger.LogDebug(
                        "remote-runner-capacity-hold runner={Runner} reason={Reason} detail={Detail} active={Active} ceiling={Ceiling}",
                        req.RunnerName, admission.ReasonCode, admission.Detail,
                        hostActiveRuns, capacityTargets?.MaxParallelism);
                    return Results.Ok(WithCapacity(
                        new RunnerClaimResponse(
                            RunnerClaimStatus.Empty,
                            Message: admission.Detail),
                        admission.ReasonCode));
                }

                var eligible = liveSnapshot
                    .Where(t => !t.Fixture && t.State == TaskStates.Ready)
                    .Where(t =>
                    {
                        var project = settings.Get(t.ProjectName);
                        return RemoteDispatchEligibility.IsAssignedAndRoutable(
                            t, project, req.RunnerId, req.RunnerName, waitsOn);
                    })
                    .OrderBy(t => t.Order)
                    .ThenBy(t => t.CreatedAt);

                TaskInfo? candidate = null;
                RemoteProjectRepository? repository = null;
                TaskInfo? failedPreflightCandidate = null;
                RemoteProjectRepository? failedPreflightRepository = null;
                RunnerProjectPreflight? failedProjectPreflight = null;
                string? nonRemoteCapableProject = null;
                string? capabilityMismatch = null;
                foreach (var task in eligible)
                {
                    var taskProjectSettings = settings.Get(task.ProjectName);
                    var buildProfileGate = BuildProfileGate.Evaluate(taskProjectSettings.BuildProfile);
                    if (!buildProfileGate.AllowsPickup)
                    {
                        RecordRejection(task, "build-profile-gate", buildProfileGate.Reason);
                        logger.LogWarning(
                            "remote-runner-coding-claim-skipped-build-profile-gate runner={Runner} task={TaskKey} reason={Reason}",
                            req.RunnerName,
                            task.Key ?? task.TaskKey ?? task.Id,
                            buildProfileGate.Reason);
                        continue;
                    }
                    var cliType = CliTypes.Normalize(task.CliType);
                    var requiredCapabilities = (req.RequiredCapabilities ?? [])
                        .Append(CapabilityProtocol.CodingExecutor)
                        .Append(CapabilityProtocol.CliExecution(cliType))
                        .Append(CapabilityProtocol.ProviderAuthentication(cliType))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var capabilityAdmission = capabilityRegistry.EvaluateCodingAdmission(
                        req.RunnerId.Trim(),
                        req.CapabilityInstanceId,
                        requiredCapabilities);
                    if (!capabilityAdmission.Eligible)
                    {
                        capabilityMismatch ??= capabilityAdmission.Message;
                        RecordRejection(task, "capability-mismatch", capabilityAdmission.Message);
                        logger.LogInformation(
                            "remote-runner-coding-claim-skipped-capability runner={Runner} task={TaskKey} cli={CliType} reason={Reason}",
                            req.RunnerName,
                            task.Key ?? task.TaskKey ?? task.Id,
                            cliType,
                            capabilityAdmission.Message);
                        continue;
                    }
                    var registryProject = projects.FindByStorageLocation(task.WatchPath)
                                          ?? projects.FindByIdOrDisplayName(task.ProjectName);
                    var targetBranch = taskProjectSettings.IntegrationBranch;
                    repository = RemoteProjectRepositoryResolver.Resolve(
                        registryProject,
                        targetBranch);
                    if (repository is not null)
                    {
                        var cached = string.IsNullOrWhiteSpace(clientId)
                            ? null
                            : clients.FindRunnerProjectPreflight(clientId, repository.ProjectId);
                        if (cached is not null
                            && string.Equals(cached.RegistrationFingerprint,
                                ProjectDeliveryPreflightFingerprint.Create(repository), StringComparison.Ordinal)
                            && ProjectDeliveryPreflightPolicy.IsFresh(cached, now)
                            && !string.Equals(cached.Status, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            RecordRejection(
                                task,
                                "project-preflight-failed",
                                $"project delivery preflight failed: {cached.Detail}");
                            failedPreflightCandidate ??= task;
                            failedPreflightRepository ??= repository;
                            failedProjectPreflight ??= cached;
                            repository = null;
                            continue;
                        }
                        candidate = task;
                        break;
                    }

                    nonRemoteCapableProject = task.ProjectName;
                    RecordRejection(
                        task,
                        "repository-url-missing",
                        "project has no repositoryUrl");
                    if (registryProject is not null && !string.IsNullOrWhiteSpace(clientId))
                    {
                        var missingRepositoryDetail =
                            "repository URL is not configured; add the project's Repository URL before remote delivery";
                        var missingFingerprint = ProjectDeliveryPreflightFingerprint.CreateUnconfigured(
                            registryProject.Id,
                            targetBranch);
                        var existingMissing = clients.FindRunnerProjectPreflight(clientId, registryProject.Id);
                        if (existingMissing is null
                            || !string.Equals(existingMissing.RegistrationFingerprint, missingFingerprint, StringComparison.Ordinal)
                            || !string.Equals(existingMissing.Detail, missingRepositoryDetail, StringComparison.Ordinal))
                        {
                            clients.SetRunnerProjectPreflight(clientId, new RunnerProjectPreflight
                            {
                                ProjectId = registryProject.Id,
                                ProjectName = task.ProjectName,
                                RegistrationFingerprint = missingFingerprint,
                                TargetBranch = targetBranch,
                                Status = "failed",
                                Detail = missingRepositoryDetail,
                                CheckedAt = now,
                            });
                        }
                    }
                    logger.LogWarning(
                        "remote-runner-project-not-remote-capable project={Project} task={TaskKey} reason=repository-url-not-configured",
                        task.ProjectName,
                        task.Key ?? task.TaskKey ?? task.Id);
                }

                if ((candidate is null || repository is null)
                    && failedPreflightCandidate is not null
                    && failedPreflightRepository is not null
                    && failedProjectPreflight is not null)
                {
                    RecordRejection(
                        failedPreflightCandidate,
                        "project-preflight-failed",
                        $"project delivery preflight failed: {failedProjectPreflight.Detail}");
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightFailed,
                        ProjectName: failedPreflightCandidate.ProjectName,
                        Message: $"Project delivery preflight failed: {failedProjectPreflight.Detail}",
                        ProjectId: failedPreflightRepository.ProjectId,
                        RepositoryUrl: failedPreflightRepository.RepositoryUrl,
                        DefaultBranch: failedPreflightRepository.DefaultBranch,
                        TaskKind: failedPreflightCandidate.Kind,
                        RegistrationFingerprint: ProjectDeliveryPreflightFingerprint.Create(failedPreflightRepository))));
                }

                if (candidate is null || repository is null)
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty,
                        Message: nonRemoteCapableProject is not null
                            ? $"project '{nonRemoteCapableProject}' is not remote-capable: repository URL is not configured"
                            : capabilityMismatch)));

                if (string.IsNullOrWhiteSpace(clientId))
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.Invalid,
                        Message: "A registered host client identity is required for project delivery preflight.")));

                var registrationFingerprint = ProjectDeliveryPreflightFingerprint.Create(repository);
                if (req.ProjectPreflight is not null)
                {
                    if (!string.Equals(req.ProjectPreflight.ProjectId, repository.ProjectId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(req.ProjectPreflight.RegistrationFingerprint, registrationFingerprint, StringComparison.Ordinal))
                    {
                        return Results.Ok(WithCapacity(new RunnerClaimResponse(
                            RunnerClaimStatus.Invalid,
                            ProjectName: candidate.ProjectName,
                            Message: "The project registration changed while delivery preflight was running. Retry the claim.",
                            ProjectId: repository.ProjectId,
                            RepositoryUrl: repository.RepositoryUrl,
                            DefaultBranch: repository.DefaultBranch,
                            RegistrationFingerprint: registrationFingerprint)));
                    }

                    var urlsMatch = SameRepositoryUrl(req.ProjectPreflight.FetchUrl, repository.RepositoryUrl)
                                    && SameRepositoryUrl(req.ProjectPreflight.PushUrl, repository.RepositoryUrl);
                    var succeeded = req.ProjectPreflight.Succeeded && urlsMatch;
                    var detail = urlsMatch
                        ? OneLine(req.ProjectPreflight.Detail)
                        : $"fetch and push URL must both match registered repository '{repository.RepositoryUrl}'";
                    clients.SetRunnerProjectPreflight(clientId, new RunnerProjectPreflight
                    {
                        ProjectId = repository.ProjectId,
                        ProjectName = candidate.ProjectName,
                        RegistrationFingerprint = registrationFingerprint,
                        RepositoryUrl = repository.RepositoryUrl,
                        FetchUrl = req.ProjectPreflight.FetchUrl?.Trim() ?? "",
                        PushUrl = req.ProjectPreflight.PushUrl?.Trim() ?? "",
                        TargetBranch = repository.DefaultBranch,
                        Status = succeeded ? "ready" : "failed",
                        Detail = detail,
                        // Server receipt time is the cache authority. A host
                        // clock cannot make a stale proof live indefinitely.
                        CheckedAt = DateTime.UtcNow,
                    });
                    logger.Log(succeeded ? LogLevel.Information : LogLevel.Warning,
                        "remote-runner-project-preflight project={Project} projectId={ProjectId} runner={Runner} status={Status} detail={Detail}",
                        candidate.ProjectName, repository.ProjectId, req.RunnerName, succeeded ? "ready" : "failed", detail);
                }

                var projectPreflight = clients.FindRunnerProjectPreflight(clientId, repository.ProjectId);
                if (projectPreflight is null
                    || !string.Equals(projectPreflight.RegistrationFingerprint, registrationFingerprint, StringComparison.Ordinal)
                    || !ProjectDeliveryPreflightPolicy.IsFresh(projectPreflight, DateTime.UtcNow))
                {
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightRequired,
                        ProjectName: candidate.ProjectName,
                        Message: "A fresh project delivery preflight is required before this claim.",
                        ProjectId: repository.ProjectId,
                        RepositoryUrl: repository.RepositoryUrl,
                        DefaultBranch: repository.DefaultBranch,
                        TaskKind: candidate.Kind,
                        RegistrationFingerprint: registrationFingerprint)));
                }

                if (!string.Equals(projectPreflight.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    RecordRejection(
                        candidate,
                        "project-preflight-failed",
                        $"project delivery preflight failed: {projectPreflight.Detail}");
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightFailed,
                        ProjectName: candidate.ProjectName,
                        Message: $"Project delivery preflight failed: {projectPreflight.Detail}",
                        ProjectId: repository.ProjectId,
                        RepositoryUrl: repository.RepositoryUrl,
                        DefaultBranch: repository.DefaultBranch,
                        TaskKind: candidate.Kind,
                        RegistrationFingerprint: registrationFingerprint)));
                }

                var taskKey = candidate.Key ?? candidate.TaskKey;
                if (string.IsNullOrWhiteSpace(taskKey)) taskKey = candidate.Id;
                var runSpec = BuildRunSpec(candidate, settings, prompts, dossierMaintenance);
                PromptEnrichmentPreparation? enrichmentPreparation = null;
                try
                {
                    // Epics use the separately rendered decomposition prompt,
                    // not the authored coding prompt consumed by this step.
                    if (!string.Equals(candidate.Kind, TaskKinds.Epic, StringComparison.OrdinalIgnoreCase))
                    {
                        var promptPath = Path.Combine(candidate.FolderPath, "prompt.md");
                        var authoredPrompt = File.Exists(promptPath)
                            ? await File.ReadAllTextAsync(promptPath, ct)
                            : string.Empty;
                        enrichmentPreparation =
                            promptEnrichment.Prepare(candidate, authoredPrompt, runSpec.Model);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "remote-runner-prompt-enrichment-blocked project={Project} task={TaskKey}",
                        candidate.ProjectName,
                        taskKey);
                    RecordRejection(
                        candidate,
                        "dispatch-preparation-failed",
                        $"prompt enrichment blocked dispatch: {ex.Message}");
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty,
                        Message: $"Prompt enrichment blocked dispatch: {ex.Message}"));
                }
                runSpec = AddPromptEnrichment(runSpec, enrichmentPreparation?.ContextMarkdown);
                remoteClaimFailures.PrepareForClaim(candidate);
                remoteDeliveryFailures.PrepareForClaim(candidate);
                var claimKey = string.IsNullOrWhiteSpace(req.IdempotencyKey)
                    ? $"claim:{taskKey}:{req.RunnerId.Trim()}:{Guid.NewGuid():N}"
                    : req.IdempotencyKey.Trim();
                var acquire = leases.TryAcquire(new RunLeaseAcquireRequest(
                    taskKey, req.RunnerId.Trim(), req.RunnerName.Trim(), req.Hostname,
                    req.Pid, req.BackendName, req.RequestedTtlSeconds,
                    repository.RepositoryId,
                    SourceRunAttemptId: recoveredSources.GetValueOrDefault(taskKey),
                    IdempotencyKey: claimKey)
                {
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                    LeaseInstanceId = req.CapabilityInstanceId,
                });
                if (!acquire.Granted || acquire.Lease is null)
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty, Message: acquire.Message ?? acquire.Outcome)));

                dispatchRejections.Clear(candidate);
                var move = await transitions.MoveAsync(
                    candidate.Id, TaskStates.Progress, candidate.WatchPath, ct,
                    cause: $"remote-runner:{req.RunnerName.Trim()}",
                    authorityWrite: new AttemptWriteReference(
                        acquire.Lease.AttemptId!,
                        acquire.Lease.FencingToken,
                        acquire.Lease.AuthorityEpoch,
                        $"lane-claim:{claimKey}"),
                    transitionCause: LaneChangeCauses.Claimed);
                if (move.Status != MoveJobStatus.Success)
                {
                    leases.Release(new RunLeaseReleaseRequest(
                        taskKey, acquire.Lease.LeaseId, acquire.Lease.FencingToken, req.RunnerId.Trim(),
                        acquire.Lease.AttemptId, acquire.Lease.AuthorityEpoch,
                        $"claim-rollback:{taskKey}:{acquire.Lease.LeaseId}"));
                    logger.LogWarning(
                        "remote-runner-claim-move-failed project={Project} task={TaskKey} runner={Runner} status={Status} message={Message}",
                        candidate.ProjectName, taskKey, req.RunnerName, move.Status, move.Message);
                    RecordRejection(
                        candidate,
                        "dispatch-transition-failed",
                        $"claim move refused: {move.Status} {move.Message}");
                    return Results.Ok(WithCapacity(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty, Message: $"claim move refused: {move.Status} {move.Message}")));
                }
                logger.LogInformation(
                    "remote-runner-task-claimed project={Project} projectId={ProjectId} task={TaskKey} runner={Runner} lease={LeaseId} token={FencingToken} repositorySource={RepositorySource} defaultBranch={DefaultBranch}",
                    candidate.ProjectName, repository.ProjectId, taskKey, req.RunnerName, acquire.Lease.LeaseId,
                    acquire.Lease.FencingToken, repository.Source, repository.DefaultBranch);
                var graceRunsRemaining = settings.ConsumeBuildProfileRevalidationGraceRun(candidate.ProjectName);
                if (graceRunsRemaining is not null)
                {
                    logger.LogWarning(
                        "build-profile-revalidation-grace-consumed project={Project} task={TaskKey} remainingRuns={RemainingRuns}",
                        candidate.ProjectName, taskKey, graceRunsRemaining);
                }
                sessions.AppendSessionEvent(candidate.Id, new SessionEvent
                {
                    Ts = acquire.Lease.AcquiredAt,
                    Kind = "start",
                    Cli = "remote-runner",
                    RunAttemptId = acquire.Lease.AttemptId,
                    Model = candidate.Model,
                    ThinkingLevel = candidate.ThinkingLevel,
                    Cwd = candidate.FolderPath,
                    ExecutionLocation = new TaskExecutionLocation
                    {
                        State = TaskExecutionStates.RemoteRunning,
                        ExecutionKind = "remote",
                        RunnerId = acquire.Lease.RunnerId,
                        ClientId = acquire.Lease.ClientId ?? acquire.Lease.RunnerId,
                        HostDisplayName = string.IsNullOrWhiteSpace(acquire.Lease.RunnerName) ? acquire.Lease.Hostname : acquire.Lease.RunnerName,
                        ConfiguredRunnerId = ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(candidate.ProjectName)),
                        StartedAt = acquire.Lease.AcquiredAt,
                        LastHeartbeat = acquire.Lease.LastHeartbeatAt,
                        LastActivityAt = acquire.Lease.LastHeartbeatAt,
                        ProcessId = acquire.Lease.Pid > 0 ? acquire.Lease.Pid : null,
                        Branch = candidate.Provenance?.Branch,
                        WorktreePath = candidate.FolderPath,
                        ConnectionState = "connected",
                        LeaseState = "active",
                        TrustReason = "Captured from the fenced run lease granted by the task server.",
                    },
                }, candidate.WatchPath);
                // One more lease is now held by this host. Free slots follow from
                // the central ceiling, not from the daemon's reported headroom.
                var occupiedAfterClaim = hostActiveRuns + 1;
                var freeAfterClaim = capacityTargets is null
                    ? Math.Max(0, req.AvailableSlots - 1)
                    : HostCapacityPolicy.FreeSlots(capacityTargets.MaxParallelism, occupiedAfterClaim);
                if (runnerPrincipal is not null)
                    accessSecurity.RecordRunnerActivity(
                        runnerPrincipal.RunnerId,
                        occupiedAfterClaim,
                        freeAfterClaim,
                        claimed: true);
                if (!string.IsNullOrWhiteSpace(clientId))
                    clients.RecordRunnerActivity(
                        clientId,
                        occupiedAfterClaim,
                        freeAfterClaim,
                        claimed: true,
                        seedMaxParallelism: seedCeiling,
                        effectiveMaxParallelism: req.EffectiveMaxParallelism,
                        effectiveMaxParallelismAppliedAt: req.EffectiveMaxParallelismAppliedAt);
                if (runnerPrincipal is not null)
                {
                    accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                        DateTime.UtcNow, "claim", taskKey, candidate.ProjectName,
                        InitiatingPrincipal(candidate.OwnerClientId), runnerPrincipal.RunnerId, runnerPrincipal.CredentialId,
                        acquire.Lease.FencingToken));
                }
                logger.LogInformation(
                    "remote-runner-run-spec task={TaskKey} cli={CliType} model={Model} thinking={ThinkingLevel} permission={PermissionMode} context={ContextMode}",
                    taskKey,
                    runSpec.CliType,
                    runSpec.Model ?? "<cli-default>",
                    runSpec.ThinkingLevel ?? "<cli-default>",
                    runSpec.PermissionMode,
                    runSpec.ContextMode);
                return Results.Ok(WithCapacity(new RunnerClaimResponse(
                    RunnerClaimStatus.Claimed,
                    taskKey,
                    candidate.Id,
                    candidate.ProjectName,
                    acquire.Lease,
                    ProjectId: repository.ProjectId,
                    RepositoryUrl: repository.RepositoryUrl,
                    DefaultBranch: repository.DefaultBranch,
                    TaskKind: candidate.Kind,
                    LeaseInstanceId: req.CapabilityInstanceId,
                    RunSpec: runSpec), admission.ReasonCode));
            }
            finally
            {
                ClaimGate.Release();
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/runner/epic-planning-prompt", (
            RemoteEpicPlanningPromptRequest req,
            TaskScannerService scanner,
            RunLeaseService leases,
            RuntimePromptService prompts,
            AgentStudio.Projects.ProjectSettingsService settings) =>
        {
            if (!leases.IsCurrent(req.TaskKey, req.LeaseId, req.FencingToken, req.RunnerId))
                return Results.Conflict(new { error = "Lease id, fencing token, or runner id does not match the current holder." });
            var epic = scanner.ScanAllJobs().FirstOrDefault(t =>
                string.Equals(t.TaskKey, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, req.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (epic is null) return Results.NotFound();
            if (!TaskKinds.IsEpic(epic.Kind))
                return Results.BadRequest(new { error = "The claimed task is not an Epic planning run." });

            var goal = File.Exists(Path.Combine(epic.FolderPath, "prompt.md"))
                ? File.ReadAllText(Path.Combine(epic.FolderPath, "prompt.md"))
                : string.Empty;
            var rendered = prompts.Render(RuntimePromptService.EpicDecomposition, new Dictionary<string, string?>
            {
                ["title"] = string.IsNullOrWhiteSpace(epic.Title) ? "(untitled)" : epic.Title,
                ["prompt_text"] = goal,
                ["working_directory"] = req.WorkingDirectory,
                ["repository_path"] = req.WorkingDirectory,
                ["job_folder"] = "server-managed task folder",
                ["prompt_path"] = "prompt.md via the runner API",
                ["attachments_list"] = "",
                ["mode_framing"] = prompts.RenderModeFraming(TaskModes.Planning, epic.AllowWebAccess),
            });
            var project = settings.Get(epic.ProjectName);
            return Results.Ok(new RemoteEpicPlanningPromptResponse(
                rendered,
                epic.CliType,
                project.EpicPlanningModel ?? epic.Model,
                project.EpicPlanningThinkingLevel ?? epic.ThinkingLevel));
        });

        app.MapPost("/api/runner/completion", async (
            RemoteRunCompletionRequest req,
            HttpContext context,
            TaskScannerService scanner,
            TaskTransitionService transitions,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            ReviewAttemptTaskLifecycleService reviewAttemptLifecycle,
            TaskSessionLog sessions,
            TimelineLog timeline,
            AccessSecurityStore accessSecurity,
            WorkspaceArtifactCommitService artifactCommits,
            AgentStudio.Projects.ProjectSettingsService projectSettings,
            RemoteReviewPlanBuilder remoteReviewPlans,
            TaskMutationService mutations,
            RemoteTokenReceiptService tokenReceipts,
            GitService git,
            TaskStateMachine states,
            OrchestratorChatLog chatLog,
            HumanReviewEscalation humanReviewEscalation,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, req.RunnerId)) return Results.Unauthorized();
            await ClaimGate.WaitAsync(ct);
            try
            {
            var remoteClaimFailures = new RemoteClaimFailureBudget(
                loggerFactory.CreateLogger<RemoteClaimFailureBudget>());
            var remoteDeliveryFailures = new RemoteDeliveryFailureStore(
                loggerFactory.CreateLogger<RemoteDeliveryFailureStore>());
            var reportedOutcome = req.Outcome ?? string.Empty;
            var task = scanner.ScanAllJobs().FirstOrDefault(t =>
                string.Equals(t.TaskKey, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, req.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (task is null)
                return Results.NotFound(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress, $"No task '{req.TaskKey}'."));

            var outcome = reportedOutcome.Trim().ToLowerInvariant();
            var isEpicPlanning = TaskKinds.IsEpic(task.Kind);
            var targetState = outcome switch
            {
                "done" or "noop" => TaskStates.AutoReview,
                "blocked" or "needsinput" or "unknown" => TaskStates.Escalated,
                "environmentfailure" => TaskStates.Ready,
                _ => string.Empty,
            };
            if (targetState.Length == 0)
                return Results.BadRequest(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Outcome must be Done, NoOp, Blocked, NeedsInput, Unknown, or EnvironmentFailure."));

            // AGT-2178: Epic planning is source-read-only - it produces no commit
            // and therefore no fenced ResultSha. The 2177 ResultSha gate only
            // applies to coding completions; epic planning is finalized through
            // the isEpicPlanning branch below (which needs no ResultSha).
            if (targetState == TaskStates.AutoReview
                && !isEpicPlanning
                && (!ReviewSubjectStore.IsValidResultSha(req.ResultSha)
                    || string.IsNullOrWhiteSpace(req.AttemptChainId)
                    || !string.Equals(req.AttemptChainId, req.LeaseId, StringComparison.Ordinal)))
            {
                return Results.BadRequest(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Done/NoOp completion requires a full fenced ResultSha and AttemptChainId equal to the current lease id.",
                    RunAttemptId: req.AttemptId,
                    FailureClassification: AttemptWriteStatus.Invalid.ToString()));
            }

            if (string.IsNullOrWhiteSpace(req.AttemptId)
                || !req.AuthorityEpoch.HasValue
                || string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                return Results.Conflict(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Attempt ID, fence, authority epoch, and idempotency key are required for Remote completion.",
                    RunAttemptId: req.AttemptId,
                    FailureClassification: AttemptWriteStatus.Invalid.ToString()));
            }

            var attemptId = req.AttemptId.Trim();
            var epoch = req.AuthorityEpoch.Value;
            if (!string.IsNullOrWhiteSpace(req.ImmutableResultRef)
                && !string.IsNullOrWhiteSpace(req.ResultSha))
            {
                string expectedResultRef;
                try
                {
                    expectedResultRef =
                        AgentStudio.TaskServer.Contracts.FencedGitRefs.ImmutableResult(
                            attemptId,
                            req.FencingToken,
                            req.ResultSha);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        TaskStates.Progress,
                        ex.Message,
                        RunAttemptId: attemptId,
                        FailureClassification: AttemptWriteStatus.Invalid.ToString()));
                }
                if (!string.Equals(
                        req.ImmutableResultRef,
                        expectedResultRef,
                        StringComparison.Ordinal))
                {
                    return Results.BadRequest(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        TaskStates.Progress,
                        $"Immutable result ref must be '{expectedResultRef}' for the current fenced attempt.",
                        RunAttemptId: attemptId,
                        FailureClassification: AttemptWriteStatus.SubjectMismatch.ToString()));
                }
            }
            // Result-SHA is independent authority. A salvage commit is useful
            // evidence, but it must never be promoted into the review subject.
            var resultSha = req.ResultSha;
            var completionKey = req.IdempotencyKey.Trim();
            AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope? resultEnvelope = null;
            string? resultEnvelopeDigest = null;
            var leasedRun = authority.GetRun(attemptId);
            var worktreeBlocked = (req.GateItems ?? []).Any(item =>
                item.TrimStart().StartsWith(
                    HumanReviewEscalationCategories.WorktreeBlocked + ":",
                    StringComparison.OrdinalIgnoreCase));
            var envelopeDecision = RemoteCompletionEnvelopePolicy.Decide(
                requiresEnvelope: !isEpicPlanning
                                  && !TaskModes.IsReportOnly(task.Mode)
                                  && !worktreeBlocked,
                outcome,
                runAttemptKnown: leasedRun is not null,
                hasResultSha: ReviewSubjectStore.IsValidResultSha(req.ResultSha),
                hasBaseSha: ReviewSubjectStore.IsValidResultSha(req.BaseSha),
                hasImmutableResultRef: !string.IsNullOrWhiteSpace(req.ImmutableResultRef),
                hasArtifactManifestDigest: IsSha256(req.ArtifactManifestDigest));
            var responseOutcome = reportedOutcome;
            string? unverifiedDelivery = null;
            if (envelopeDecision.ShouldPersist)
            {
                resultEnvelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
                    leasedRun!.RepositoryId,
                    attemptId,
                    req.BaseSha!,
                    resultSha!,
                    req.ImmutableResultRef!,
                    null,
                    req.ArtifactManifestDigest!,
                    RepositoryUrl: req.Repository);
                resultEnvelopeDigest =
                    AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(resultEnvelope);
            }
            else if (envelopeDecision.ShouldFailDelivery)
            {
                outcome = envelopeDecision.AuthorityOutcome;
                responseOutcome = RemoteDeliveryFailurePolicy.DeliveryFailed;
                targetState = TaskStates.Ready;
                unverifiedDelivery = envelopeDecision.Reason;
            }
            var settled = authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(
                    attemptId,
                    req.FencingToken,
                    epoch,
                    completionKey),
                Outcome = outcome,
                ResultSha = resultSha,
                Reason = unverifiedDelivery ?? req.Reason,
                ExecutorId = req.RunnerId,
                LeaseId = req.LeaseId,
                ExpectedTaskKey = req.TaskKey,
                RequireResultSha = !isEpicPlanning && outcome is ("done" or "noop"),
                ResultEnvelope = resultEnvelope,
                ResultEnvelopeDigest = resultEnvelopeDigest,
            });
            if (!settled.Accepted)
            {
                var response = new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, task.State, settled.Message,
                    RunAttemptId: attemptId,
                    FailureClassification: settled.Status.ToString());
                return settled.Status == AttemptWriteStatus.Invalid
                    ? Results.BadRequest(response)
                    : Results.Conflict(response);
            }
            var settledRun = settled.RunAttempt ?? authority.GetRun(attemptId);
            var terminalAt = settledRun?.TerminalAt ?? DateTime.UtcNow;
            var terminalResult = settledRun?.TerminalOutcome ?? outcome;
            if (!sessions.CloseSessionEvent(task.Id, new RunSessionCloseout
                {
                    RunAttemptId = attemptId,
                    FinishedAt = terminalAt,
                    Result = terminalResult,
                    Status = RunCloseoutPolicy.StatusFor(terminalResult, recordedStatus: null),
                    ExitCode = req.ExitCode
                }, task.WatchPath))
            {
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                    "remote-run-closeout-missing task={TaskKey} attempt={AttemptId}",
                    req.TaskKey,
                    attemptId);
            }
            var tokenReceipt = tokenReceipts.PersistFromLog(task, attemptId, req.RunnerId);
            if (!tokenReceipt.Persisted && !string.IsNullOrWhiteSpace(tokenReceipt.Warning))
            {
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                    "remote-token-receipt task={TaskKey} attempt={AttemptId} warning={Warning}",
                    req.TaskKey,
                    attemptId,
                    tokenReceipt.Warning);
            }
            RemoteDeliveryFailureDecision? deliveryFailure = null;
            if (envelopeDecision.ShouldFailDelivery)
            {
                deliveryFailure = settled.Status == AttemptWriteStatus.Duplicate
                    ? remoteDeliveryFailures.GetDecision(task)
                    : null;
                deliveryFailure ??= remoteDeliveryFailures.Record(
                    task,
                    unverifiedDelivery!,
                    req.SalvageRecoveryBranch ?? req.SalvageBranch,
                    req.SalvageRecoveryCommitSha ?? req.SalvageCommitSha);
                targetState = deliveryFailure.Escalate
                    ? TaskStates.Escalated
                    : TaskStates.Ready;
                if (deliveryFailure.Escalate)
                {
                    mutations.AddJobTag(
                        task.Id, OutOfBandStampPolicy.UnverifiedDeliveryTag, task.WatchPath);
                }
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                    "remote-completion-envelope-missing task={TaskKey} attempt={AttemptId} runner={RunnerId} deliveryAttempt={DeliveryAttempt}/{MaximumAttempts} targetState={TargetState} fence={FenceBranch} reason={Reason}",
                    req.TaskKey,
                    attemptId,
                    req.RunnerId,
                    deliveryFailure.Attempt,
                    deliveryFailure.MaximumAttempts,
                    targetState,
                    req.SalvageRecoveryBranch ?? req.SalvageBranch ?? "none",
                    unverifiedDelivery);
            }
            else if (settled.Status != AttemptWriteStatus.Duplicate)
            {
                remoteDeliveryFailures.Reset(task);
            }

            // The immutable ResultEnvelope ref is the reviewed delivery. A
            // salvage branch is recovery evidence only and must never outrank
            // the immutable result when both are present. AGT-2494: a divergent
            // salvage parks the run's result on its collision branch, so the
            // canonical branch is ranked behind that recovery branch.
            var deliveryCandidates = RemoteDeliveryRefPolicy.Candidates(
                req.ImmutableResultRef,
                req.SalvageResolution,
                req.SalvageBranch,
                req.SalvageRecoveryBranch,
                req.SalvageRecoveryCommitSha,
                resultSha);
            var deliveryBranch = deliveryCandidates.Count > 0
                ? deliveryCandidates[0].Ref
                : null;
            RemoteDeliveryCommitRange? deliveryRange = null;
            RemoteCommitAttributionResult? remoteAttribution = null;
            string? attributionWarning = null;
            // Set when the completion cannot produce a verified, materializable
            // coding subject: either its immutable envelope is absent or the
            // target repository actively contradicts the claimed delivery.
            if (!isEpicPlanning && outcome is ("done" or "noop"))
            {
                var reportedIntegrationBranch =
                    TaskIntegrationBranch.NormalizeRef(req.IntegrationBranch);
                if (reportedIntegrationBranch is not null)
                {
                    mutations.SetRunIntegrationBranchOnFolder(
                        task.FolderPath,
                        reportedIntegrationBranch);
                }
                var repoRoot = git.ResolveRepoRootForWatchPath(task.WatchPath);
                if (string.IsNullOrWhiteSpace(repoRoot)
                    || string.IsNullOrWhiteSpace(deliveryBranch)
                    || string.IsNullOrWhiteSpace(resultSha))
                {
                    attributionWarning =
                        "Remote commit attribution skipped because repository, delivery branch, or result SHA was unavailable.";
                }
                else
                {
                    deliveryRange = git.InspectRemoteDeliveryCommitRange(
                        repoRoot,
                        deliveryBranch,
                        resultSha,
                        req.IntegrationBranch,
                        req.BaseSha,
                        ct);
                    // AGT-2494: the top-ranked claim is contradicted by the
                    // repository, so let the repository name the ref instead of
                    // the ranking. A divergent salvage published this run's
                    // result to its collision branch; reviewing it there beats
                    // escalating a delivery that demonstrably exists.
                    if (deliveryRange.IsDisproved && deliveryCandidates.Count > 1)
                    {
                        var reselected = RemoteDeliveryRefPolicy.Select(
                            deliveryCandidates,
                            candidate => git.VerifyDeliveredCommit(repoRoot, candidate, resultSha));
                        if (reselected.CarriesResult
                            && !string.Equals(reselected.Ref, deliveryBranch, StringComparison.Ordinal))
                        {
                            loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                                "remote-delivery-ref-reselected task={TaskKey} claimed={Claimed} selected={Selected} origin={Origin} verification={Verification}",
                                req.TaskKey,
                                deliveryBranch,
                                reselected.Ref,
                                reselected.Origin,
                                reselected.Verification);
                            deliveryBranch = reselected.Ref!;
                            deliveryRange = git.InspectRemoteDeliveryCommitRange(
                                repoRoot,
                                deliveryBranch,
                                resultSha,
                                req.IntegrationBranch,
                                req.BaseSha,
                                ct);
                        }
                    }
                    if (!deliveryRange.Success)
                    {
                        attributionWarning = deliveryRange.Warning;
                    }
                    else
                    {
                        remoteAttribution = RemoteCommitAttributionGuard.Attribute(
                            req.TaskKey,
                            deliveryBranch,
                            deliveryRange.Commits);
                        attributionWarning = remoteAttribution.Warning;
                        mutations.SetRunIntegrationBranchOnFolder(
                            task.FolderPath,
                            deliveryRange.IntegrationBranch!);
                        mutations.SetRemoteCommitAttributionOnFolder(
                            task.FolderPath,
                            attemptId,
                            req.RunnerId,
                            resultSha,
                            remoteAttribution.Commits);
                    }
                }

                if (!string.IsNullOrWhiteSpace(attributionWarning))
                {
                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                        "remote-commit-attribution task={TaskKey} branch={Branch} warning={Warning}",
                        req.TaskKey,
                        deliveryBranch,
                        attributionWarning);
                }

                // AGT-2220: a delivery the repository actively contradicts must
                // not ride on into 4-auto-review as if it were clean. That is
                // exactly what happened to AGT-2220 itself on 28.07.: origin held
                // 744deb89 while the completion claimed f538f896, the mismatch
                // was logged as a warning only, commits[] stayed empty - and the
                // card was stamped Done anyway. A *disproved* delivery now routes
                // to the honest escalated state. "Could not check" (no origin, no
                // branch, no SHA) is deliberately NOT treated as disproof - it is
                // recorded, never upgraded to proof.
                if (deliveryRange is not null && deliveryRange.IsDisproved)
                {
                    unverifiedDelivery =
                        $"Delivery not verified against the target repository: {attributionWarning} "
                        + "No completion stamp was written (AGT-2220).";
                    targetState = TaskStates.Escalated;
                    mutations.AddJobTag(
                        task.Id, OutOfBandStampPolicy.UnverifiedDeliveryTag, task.WatchPath);
                }
            }

            ReviewAttemptDto? reviewAttempt = null;
            CreateReviewAttemptRequest? reviewAttemptRequest = null;
            // Report-only modes (planning / research) deliver a document into the
            // task folder on THIS server; there is no code subject to materialize
            // on a review-executor host. Their completion is validated
            // deterministically by the lightweight report pipeline
            // (ReviewDecisionOrchestrator.ProcessReportOnlyDoneAsync), so no
            // remote ReviewAttempt is minted for them.
            // No review subject is minted for an envelope-less or repository-
            // disproved delivery. There is nothing materializable to review,
            // and minting one is how the AGT-2220 incident acquired a subject
            // pinned to a SHA that was never on its recorded ref.
            if (!isEpicPlanning
                && outcome is ("done" or "noop")
                && unverifiedDelivery is null
                && !TaskModes.IsReportOnly(task.Mode))
            {
                var requirementsPath = Path.Combine(task.FolderPath, "prompt.md");
                var requirements = File.Exists(requirementsPath) ? File.ReadAllText(requirementsPath) : task.Id;
                var run = settled.RunAttempt!;
                var taskProjectSettings = projectSettings.Get(task.ProjectName);
                var repositoryPath = git.ResolveRepoRootForWatchPath(task.WatchPath);
                var integrationRef = ReviewBaselineBranchPolicy.Decide(
                    task.IntegrationBranch ?? req.IntegrationBranch,
                    taskProjectSettings.IntegrationBranch,
                    repositoryDefaultBranch: null).IntegrationRef;
                reviewAttemptRequest = new CreateReviewAttemptRequest(
                    req.TaskKey,
                    run.RepositoryId,
                    run.ResultSha!,
                    run.AttemptId,
                    AttemptAuthorityService.Hash(requirements),
                    AttemptAuthorityService.Hash("remote-review-policy:v1"),
                    run.EvidenceDigests,
                    $"review-subject:{run.AttemptId}:{run.ResultSha}",
                    RepositoryUrl: req.Repository,
                    ResultRef: deliveryBranch,
                    Plan: remoteReviewPlans.Build(
                        task,
                        repositoryPath,
                        taskProjectSettings,
                        integrationRef));
            }

            if (!isEpicPlanning
                && settled.Status == AttemptWriteStatus.Duplicate
                && string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                var existingReview = authority
                    .GetTaskProjection(req.TaskKey)
                    .CurrentReviewAttempt;
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey, responseOutcome, targetState, "duplicate delivery",
                    RunAttemptId: attemptId,
                    ReviewAttemptId: existingReview?.AttemptId,
                    ReviewSubjectId: existingReview?.Subject.SubjectId));
            }

            var source = CredentialRedactor.Redact(
                string.IsNullOrWhiteSpace(req.Source) ? req.RunnerId : req.Source.Trim());
            var salvageBranch = CredentialRedactor.Redact(req.SalvageBranch);
            var salvageCommitSha = CredentialRedactor.Redact(req.SalvageCommitSha);
            var salvageBranchUrl = CredentialRedactor.Redact(req.SalvageBranchUrl);
            var salvageRecoveryBranchUrl = CredentialRedactor.Redact(req.SalvageRecoveryBranchUrl);
            var reportedReason = CredentialRedactor.Redact(req.Reason);
            var details = new Dictionary<string, string>
            {
                ["cli"] = "remote-runner",
                ["status"] = outcome,
                ["runner"] = source,
                ["runAttemptId"] = attemptId,
                ["fence"] = req.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["authorityEpoch"] = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["idempotencyKey"] = completionKey,
                ["sentinel"] = outcome switch
                {
                    "needsinput" => "TASK_NEEDS_INPUT",
                    "unknown" or "unverified" => string.Empty,
                    _ => $"TASK_{outcome.ToUpperInvariant()}",
                },
            };
            if (!string.IsNullOrWhiteSpace(salvageBranch))
                details["salvageBranch"] = salvageBranch;
            if (!string.IsNullOrWhiteSpace(salvageCommitSha))
                details["salvageCommitSha"] = salvageCommitSha;
            if (!string.IsNullOrWhiteSpace(salvageBranchUrl))
                details["salvageBranchUrl"] = salvageBranchUrl;
            if (!string.IsNullOrWhiteSpace(req.ResultSha))
                details["resultSha"] = req.ResultSha;
            if (!string.IsNullOrWhiteSpace(req.AttemptChainId))
                details["attemptChainId"] = req.AttemptChainId;
            if (!string.IsNullOrWhiteSpace(req.SalvageResolution))
                details["salvageResolution"] = req.SalvageResolution;
            if (!string.IsNullOrWhiteSpace(req.SalvageLocalCommitSha))
                details["salvageLocalCommitSha"] = req.SalvageLocalCommitSha;
            if (!string.IsNullOrWhiteSpace(req.SalvageRecoveryBranch))
                details["salvageRecoveryBranch"] = req.SalvageRecoveryBranch;
            if (!string.IsNullOrWhiteSpace(req.SalvageRecoveryCommitSha))
                details["salvageRecoveryCommitSha"] = req.SalvageRecoveryCommitSha;
            if (!string.IsNullOrWhiteSpace(salvageRecoveryBranchUrl))
                details["salvageRecoveryBranchUrl"] = salvageRecoveryBranchUrl;
            if (!string.IsNullOrWhiteSpace(req.SalvageAuthoritativeBaseBranch))
                details["salvageAuthoritativeBaseBranch"] = req.SalvageAuthoritativeBaseBranch;
            if (!string.IsNullOrWhiteSpace(req.SalvageAuthoritativeBaseSha))
                details["salvageAuthoritativeBaseSha"] = req.SalvageAuthoritativeBaseSha;
            if (!string.IsNullOrWhiteSpace(deliveryRange?.IntegrationBranch))
                details["integrationBranch"] = deliveryRange.IntegrationBranch;
            else if (!string.IsNullOrWhiteSpace(req.IntegrationBranch))
                details["integrationBranch"] = TaskIntegrationBranch.NormalizeRef(req.IntegrationBranch)!;
            if (remoteAttribution is not null)
                details["attributedCommitCount"] = remoteAttribution.Commits.Count.ToString();
            if (!string.IsNullOrWhiteSpace(attributionWarning))
                details["commitAttributionWarning"] = attributionWarning;
            if (!string.IsNullOrWhiteSpace(reportedReason))
                details["reason"] = reportedReason;
            if (deliveryFailure is not null)
            {
                details["deliveryStatus"] = RemoteDeliveryFailurePolicy.DeliveryFailed;
                details["deliveryAttempt"] = deliveryFailure.Attempt.ToString();
                details["maximumDeliveryAttempts"] = deliveryFailure.MaximumAttempts.ToString();
                details["missingEnvelopeFacts"] = string.Join(",", envelopeDecision.MissingFacts ?? []);
                details["deliveryAction"] = deliveryFailure.Escalate ? "escalate" : "requeue";
            }
            RemoteClaimFailureDecision? claimFailure = null;
            if (outcome == "environmentfailure")
            {
                claimFailure = remoteClaimFailures.Record(task, reportedReason);
                details["attempt"] = claimFailure.Attempt.ToString();
                details["maximumAttempts"] = claimFailure.MaximumAttempts.ToString();
            }
            if (reviewAttempt is not null)
            {
                details["reviewAttemptId"] = reviewAttempt.AttemptId;
                details["reviewSubjectId"] = reviewAttempt.Subject.SubjectId;
                details["expectedResultSha"] = reviewAttempt.Subject.ExpectedResultSha;
            }
            var gateFile = WriteGateItems(task.FolderPath, req.GateItems);
            if (gateFile is not null)
            {
                details["gateItems"] = string.Join(" | ", req.GateItems!);
                artifactCommits.TryCommitArtifactUpload(null, task.Id, task.FolderPath, [gateFile]);
            }
            if (!string.IsNullOrWhiteSpace(salvageBranch)
                && !string.IsNullOrWhiteSpace(salvageCommitSha))
            {
                var resultsDir = TaskPaths.ResultsDir(task.FolderPath);
                Directory.CreateDirectory(resultsDir);
                var deliverablesPath = Path.Combine(resultsDir, "deliverables.md");
                var branchRef = !string.IsNullOrWhiteSpace(salvageBranchUrl)
                    ? $"[{salvageBranch}]({salvageBranchUrl})"
                    : $"`{salvageBranch}`";
                var recoveryLine = !string.IsNullOrWhiteSpace(req.SalvageRecoveryBranch)
                    && !string.IsNullOrWhiteSpace(req.SalvageRecoveryCommitSha)
                    ? $"- Divergent local history preserved on " +
                      (!string.IsNullOrWhiteSpace(salvageRecoveryBranchUrl)
                          ? $"[{req.SalvageRecoveryBranch}]({salvageRecoveryBranchUrl})"
                          : $"`{req.SalvageRecoveryBranch}`") +
                      $" at `{req.SalvageRecoveryCommitSha}`; " +
                      $"`{req.SalvageAuthoritativeBaseBranch ?? salvageBranch}` at " +
                      $"`{req.SalvageAuthoritativeBaseSha ?? salvageCommitSha}` was the authoritative pickup base.{Environment.NewLine}"
                    : string.Empty;
                File.WriteAllText(
                    deliverablesPath,
                    $"# Remote runner deliverables{Environment.NewLine}{Environment.NewLine}" +
                    $"- Salvage branch {branchRef} at `{salvageCommitSha}`.{Environment.NewLine}" +
                    recoveryLine,
                    System.Text.Encoding.UTF8);
                artifactCommits.TryCommitArtifactUpload(
                    null, task.Id, task.FolderPath, ["results/deliverables.md"]);
            }
            if (deliveryFailure is not null)
            {
                RemoteDeliveryFailureNote.Append(
                    task.FolderPath,
                    attemptId,
                    deliveryFailure,
                    unverifiedDelivery!,
                    CredentialRedactor.Redact(req.SalvageRecoveryBranch ?? req.SalvageBranch),
                    CredentialRedactor.Redact(req.SalvageRecoveryCommitSha ?? req.SalvageCommitSha),
                    CredentialRedactor.Redact(req.SalvageRecoveryBranchUrl ?? req.SalvageBranchUrl));
                artifactCommits.TryCommitArtifactUpload(
                    null, task.Id, task.FolderPath, ["status.md", "prompt.md"]);
            }
            var timelineAlreadyRecorded = timeline.ReadAll(task.FolderPath).Any(evt =>
                evt.Details is not null
                && evt.Details.TryGetValue("idempotencyKey", out var recordedKey)
                && string.Equals(recordedKey, completionKey, StringComparison.Ordinal));
            if (!timelineAlreadyRecorded && !timeline.Append(
                    task.FolderPath,
                    TimelineEventKinds.AgentRunFinished,
                    TimelineActors.Agent,
                    summary: $"remote run {outcome} on {source}",
                    runId: attemptId,
                    details: details))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Remote completion timeline persistence failed",
                    detail: $"RunAttempt '{attemptId}' is settled, but its idempotent timeline fact was not persisted. Retry the same completion delivery.");
            }

            var laneWrite = new AttemptWriteReference(
                attemptId,
                req.FencingToken,
                epoch,
                $"lane-completion:{completionKey}");

            if (claimFailure is not null)
            {
                var reason =
                    $"Remote claim repository/environment preparation failed " +
                    $"({claimFailure.Attempt}/{claimFailure.MaximumAttempts}): {claimFailure.Reason}";
                if (!claimFailure.Escalate)
                {
                    var retryMove = await transitions.MoveAsync(
                        task.Id,
                        TaskStates.Ready,
                        task.WatchPath,
                        ct,
                        cause: $"remote-claim-environment-retry:{claimFailure.Attempt}/{claimFailure.MaximumAttempts}",
                        authorityWrite: laneWrite,
                        suppressProductExecution: true,
                        transitionCause: LaneChangeCauses.ClaimEnvironmentRetry,
                        transitionDetail: $"{claimFailure.Attempt}/{claimFailure.MaximumAttempts}");
                    if (retryMove.Status != MoveJobStatus.Success)
                        return Results.Conflict(new RemoteRunCompletionResponse(
                            req.TaskKey,
                            reportedOutcome,
                            task.State,
                            $"Environment retry lane move refused: {retryMove.Status} {retryMove.Message}",
                            RunAttemptId: attemptId));

                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                        "remote-claim-environment-retry project={Project} task={TaskKey} runner={Runner} attempt={Attempt}/{MaximumAttempts} reason={Reason}",
                        task.ProjectName,
                        req.TaskKey,
                        source,
                        claimFailure.Attempt,
                        claimFailure.MaximumAttempts,
                        claimFailure.Reason);
                    return Results.Ok(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        TaskStates.Ready,
                        reason,
                        RunAttemptId: attemptId));
                }

                var escalated = await humanReviewEscalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    HumanReviewEscalationCategories.RemoteClaimEnvironment,
                    reason,
                    ct,
                    laneWrite);
                if (escalated.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        task.State,
                        $"Environment escalation lane move refused: {escalated.Status} {escalated.Message}",
                        RunAttemptId: attemptId));

                timeline.Append(
                    escalated.NewFolderPath ?? task.FolderPath,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.System,
                    reason,
                    runId: attemptId,
                    details: new Dictionary<string, string>
                    {
                        ["category"] = HumanReviewEscalationCategories.RemoteClaimEnvironment,
                        ["attempt"] = claimFailure.Attempt.ToString(),
                        ["maximumAttempts"] = claimFailure.MaximumAttempts.ToString(),
                        ["reason"] = claimFailure.Reason,
                    });
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogError(
                    "remote-claim-environment-escalated project={Project} task={TaskKey} runner={Runner} attempt={Attempt}/{MaximumAttempts} reason={Reason}",
                    task.ProjectName,
                    req.TaskKey,
                    source,
                    claimFailure.Attempt,
                    claimFailure.MaximumAttempts,
                    claimFailure.Reason);
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey,
                    reportedOutcome,
                    TaskStates.Escalated,
                    reason,
                    RunAttemptId: attemptId));
            }

            remoteClaimFailures.Reset(task);

            if (isEpicPlanning)
            {
                // Epic planning is source-read-only and owns no ReviewSubject.
                // It still uses the canonical RunAttempt for its lane write and
                // completion evidence, then delegates child creation to the
                // shared idempotent decomposition lifecycle.
                //
                // The lane is the one a valid plan earns: never 4-auto-review,
                // which would park a run with no Result-SHA in a canonical
                // attempt wait that can never be satisfied (see
                // EpicRunPolicy.PlanningCompletionLane). The move stays ahead of
                // Finalize so spawn evidence is written on the post-move folder;
                // an invalid plan is recovered from there to 0-backlog below.
                var planningLane = EpicRunPolicy.PlanningCompletionLane(decompositionValid: true);
                if (!string.Equals(task.State, planningLane, StringComparison.OrdinalIgnoreCase))
                {
                    var planningMove = await transitions.MoveAsync(
                        task.Id,
                        planningLane,
                        task.WatchPath,
                        ct,
                        cause: $"remote-epic-planning-completion:{source}",
                        authorityWrite: laneWrite,
                        suppressProductExecution: true,
                        transitionCause: LaneChangeCauses.Delivered,
                        transitionDetail: "epic-planning");
                    if (planningMove.Status != MoveJobStatus.Success)
                        return Results.Conflict(new RemoteRunCompletionResponse(
                            req.TaskKey, reportedOutcome, task.State,
                            $"Epic planning lane move refused: {planningMove.Status} {planningMove.Message}",
                            RunAttemptId: attemptId));
                }

                task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
                var invalidationReason = req.SourceMutated
                    ? "Epic planning mutated the read-only product checkout"
                    : outcome is not ("done" or "noop")
                        ? $"Epic planning ended with non-success outcome '{outcome}'"
                        : null;
                var finalized = EpicDecompositionLifecycle.Finalize(
                    task, req.OutputLines, req.LeaseId, projectSettings, mutations, scanner,
                    states, timeline, chatLog,
                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteEpicPlanning"),
                    invalidationReason);
                if (!finalized.Valid)
                {
                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                        "remote-epic-planning-invalid task={TaskKey} runner={Runner} sourceMutated={SourceMutated} reason={Reason}",
                        req.TaskKey, source, req.SourceMutated, finalized.Error);
                    return Results.Ok(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome,
                        EpicRunPolicy.PlanningCompletionLane(decompositionValid: false),
                        req.SourceMutated
                            ? "Epic planning attempted to mutate the read-only checkout; no children were created."
                            : finalized.Error,
                        RunAttemptId: attemptId));
                }
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogInformation(
                    "remote-epic-planning-completion project={Project} task={TaskKey} runner={Runner} children={Children} token={FencingToken}",
                    task.ProjectName, req.TaskKey, source, finalized.CreatedTaskIds.Count, req.FencingToken);
                if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal epicPrincipal)
                {
                    accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                        DateTime.UtcNow, "completion", req.TaskKey, task.ProjectName,
                        InitiatingPrincipal(task.OwnerClientId), epicPrincipal.RunnerId, epicPrincipal.CredentialId,
                        req.FencingToken, outcome));
                }
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, planningLane,
                    RunAttemptId: attemptId));
            }

            if (targetState == TaskStates.Escalated)
            {
                // An unverified completion carries its own category and exact
                // boundary reason instead of a generic agent-outcome text.
                var (category, reason) = unverifiedDelivery is not null
                    ? (HumanReviewEscalationCategories.UnverifiedDelivery, unverifiedDelivery)
                    : RemoteEscalation(outcome, req.Reason, req.GateItems);
                var escalated = await humanReviewEscalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    category,
                    reason,
                    ct,
                    laneWrite);
                if (escalated.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        responseOutcome,
                        task.State,
                        $"Escalation lane move refused: {escalated.Status} {escalated.Message}",
                        RunAttemptId: attemptId));
                timeline.Append(
                    escalated.NewFolderPath ?? task.FolderPath,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.System,
                    reason,
                    runId: attemptId,
                    details: new Dictionary<string, string>
                    {
                        ["category"] = category,
                        ["reason"] = reason,
                    });
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey,
                    responseOutcome,
                    TaskStates.Escalated,
                    reason,
                    RunAttemptId: attemptId));
            }

            if (!string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                var move = await transitions.MoveAsync(
                    task.Id, targetState, task.WatchPath, ct,
                    cause: deliveryFailure is null
                        ? $"remote-runner-completion:{source}"
                        : $"remote-delivery-envelope-retry:{deliveryFailure.Attempt}/{deliveryFailure.MaximumAttempts}",
                    authorityWrite: laneWrite,
                    suppressProductExecution: true,
                    // A verified completion is the delivery hand-off; an unverified
                    // one is requeued for another delivery round by the runner.
                    transitionCause: deliveryFailure is null
                        ? LaneChangeCauses.Delivered
                        : LaneChangeCauses.RunnerRequeue,
                    transitionDetail: deliveryFailure is null
                        ? outcome
                        : $"delivery-envelope-retry {deliveryFailure.Attempt}/{deliveryFailure.MaximumAttempts}");
                if (move.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome, task.State, $"Lane move refused: {move.Status} {move.Message}",
                        RunAttemptId: attemptId,
                        ReviewAttemptId: reviewAttempt?.AttemptId,
                        ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }

            // The claim guard and this mint share ReviewAttemptTaskLifecycleService's
            // lifecycle lock. The persisted lane transition above therefore closes
            // the former Progress-window race: a poll sees either no attempt or an
            // attempt whose task is already in Auto Review.
            if (reviewAttemptRequest is not null)
            {
                task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
                var review = reviewAttemptLifecycle.CreateReviewAttemptInAutoReview(task, reviewAttemptRequest);
                if (!review.Accepted || review.ReviewAttempt is null)
                {
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome, task.State, review.Message,
                        RunAttemptId: attemptId,
                        FailureClassification: review.Status.ToString()));
                }
                reviewAttempt = review.ReviewAttempt;
                var run = settled.RunAttempt!;
                ReviewSubjectStore.Write(task.FolderPath, new ReviewSubjectRecord
                {
                    TaskKey = req.TaskKey,
                    RunAttemptId = run.AttemptId,
                    Project = task.ProjectName,
                    Repository = req.Repository
                                 ?? git.ResolveRepoRootForWatchPath(task.WatchPath)
                                 ?? string.Empty,
                    ResultSha = run.ResultSha!,
                    BaseSha = run.ResultEnvelope?.BaseSha ?? req.BaseSha,
                    AttemptChainId = req.AttemptChainId!,
                    Executor = req.RunnerId,
                    LeaseId = req.LeaseId,
                    FencingToken = req.FencingToken,
                    ImmutableResultRef = run.ResultEnvelope?.ImmutableRemoteRef,
                    ResultRef = reviewAttemptRequest.ResultRef,
                    IntegrationBranch = deliveryRange?.IntegrationBranch
                                        ?? TaskIntegrationBranch.NormalizeRef(req.IntegrationBranch),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                });
            }

            loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogInformation(
                "remote-runner-completion project={Project} task={TaskKey} runner={Runner} outcome={Outcome} targetState={TargetState} token={FencingToken}",
                task.ProjectName, req.TaskKey, source, outcome, targetState, req.FencingToken);
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal runnerPrincipal)
            {
                accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                    DateTime.UtcNow, "completion", req.TaskKey, task.ProjectName,
                    InitiatingPrincipal(task.OwnerClientId), runnerPrincipal.RunnerId, runnerPrincipal.CredentialId,
                    req.FencingToken, outcome));
            }
            return Results.Ok(new RemoteRunCompletionResponse(
                req.TaskKey, responseOutcome, targetState,
                Message: deliveryFailure is null ? null : unverifiedDelivery,
                RunAttemptId: attemptId,
                ReviewAttemptId: reviewAttempt?.AttemptId,
                ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }
            finally
            {
                ClaimGate.Release();
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
    }

    private static string? WriteGateItems(string folderPath, IReadOnlyList<string>? gateItems)
    {
        var items = (gateItems ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Replace('\r', ' ').Replace('\n', ' ').Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0) return null;

        const string relativePath = "orchestrator-follow-up.md";
        var path = Path.Combine(folderPath, relativePath);
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var sb = new StringBuilder(existing);
        if (sb.Length == 0)
            sb.Append("# Orchestrator follow-up\n\n");
        else if (!existing.EndsWith("\n\n", StringComparison.Ordinal))
            sb.Append(existing.EndsWith('\n') ? "\n" : "\n\n");
        foreach (var item in items)
        {
            var row = $"- [ ] {item}";
            if (!existing.Contains(row, StringComparison.Ordinal))
                sb.Append(row).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return relativePath;
    }

    private static bool CanonicalLeaseWritePresent(
        string? attemptId,
        long? authorityEpoch,
        string? idempotencyKey) =>
        !string.IsNullOrWhiteSpace(attemptId)
        && authorityEpoch is > 0
        && !string.IsNullOrWhiteSpace(idempotencyKey);

    private static bool SameRepositoryUrl(string? actual, string expected) =>
        string.Equals(actual?.Trim().TrimEnd('/'), expected.Trim().TrimEnd('/'), StringComparison.Ordinal);

    private static string OneLine(string? value)
    {
        var clean = (value ?? "preflight failed").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 1000 ? clean : clean[..1000];
    }

    /// <summary>
    /// Fill a partial acquire request with this backend's runner identity so a
    /// local caller need only name the task; a remote runner supplies its own
    /// identity and those values win. Keeps the previously-unused lease API
    /// productive for the in-process runner without forcing every caller to
    /// re-derive host/pid/backend.
    /// </summary>
    private static RunLeaseAcquireRequest StampIdentity(RunLeaseAcquireRequest req, RunnerIdentity identity) => req with
    {
        RunnerId = string.IsNullOrWhiteSpace(req.RunnerId) ? identity.RunnerId : req.RunnerId,
        RunnerName = string.IsNullOrWhiteSpace(req.RunnerName) ? identity.RunnerName : req.RunnerName,
        Hostname = string.IsNullOrWhiteSpace(req.Hostname) ? identity.Hostname : req.Hostname,
        BackendName = string.IsNullOrWhiteSpace(req.BackendName) ? identity.BackendName : req.BackendName,
        Pid = req.Pid == 0 ? Environment.ProcessId : req.Pid,
    };

    /// <summary>
    /// Server-side occupancy of one execution host: the tasks in Progress whose
    /// current run lease belongs to this host. This is the count host capacity
    /// admission spends, so a daemon that under-reports (or a second daemon
    /// process on the same machine) cannot exceed the central ceiling.
    /// </summary>
    private static int CountHostLeases(
        IEnumerable<TaskInfo> snapshot,
        RunLeaseService leases,
        string? clientId,
        string runnerId)
    {
        var count = 0;
        foreach (var task in snapshot)
        {
            if (task.Fixture) continue;
            if (task.State != TaskStates.Progress) continue;
            var key = task.Key ?? task.TaskKey ?? task.Id;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var inspection = leases.Inspect(key);
            if (inspection.State != "active" || inspection.Lease is null) continue;
            var lease = inspection.Lease;
            if ((!string.IsNullOrWhiteSpace(clientId)
                 && string.Equals(lease.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(lease.RunnerId, runnerId, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// DEPRECATED COMPATIBILITY READ (remove after 2026-10-01). Capacity now
    /// lives on the host, but a host that has never been given a central target
    /// must not lose the parallelism its projects were configured with. The
    /// largest <c>maxParallelism</c> among the projects assigned to this runner
    /// seeds the host ceiling once; after that the host target is authoritative
    /// and this value is ignored.
    /// </summary>
    private static int? DeprecatedProjectCompatCeiling(
        ProjectSettingsService settings,
        string runnerId,
        string runnerName)
    {
        var ceiling = 0;
        foreach (var project in settings.GetAll().Values)
        {
            if (!ProjectExecutionPolicy.IsAssignedRemote(project, runnerId, runnerName)) continue;
            ceiling = Math.Max(ceiling, project.MaxParallelism);
        }
        return ceiling > 0 ? ceiling : null;
    }

    /// T0b (CAR migration plan §3 T0b / §7 AP3) — resolve the claimed card's
    /// execution specification for the wire. This is deliberately the <b>same</b>
    /// source the local path reads at spawn time, so a card runs the same way
    /// wherever it lands:
    /// <list type="bullet">
    ///   <item>CLI: <c>task.cliType</c>, normalized exactly as <c>CliRouter.Get</c>
    ///     resolves it (unknown / unset ⇒ claude, the project default).</item>
    ///   <item>Model / thinking level: the card's pins, with the project's Epic
    ///     planning overrides applied for an Epic — mirroring
    ///     <c>ProjectRunner</c>'s epic branch and the
    ///     <c>/api/runner/epic-planning-prompt</c> endpoint.</item>
    ///   <item>Permission / context mode: <c>ProjectSettingsService.ResolveCliMode</c>
    ///     and <c>ResolveContextMode</c> — the identical live lookups the local
    ///     spawn performs, so a project toggle takes effect on the next claim.</item>
    /// </list>
    ///
    /// <para>
    /// Two deliberate differences from the local path, both conservative:
    /// (1) model <b>qualification</b> is not run here — it needs the rendered
    /// prompt, the CLI's model catalogue and the project history, none of which
    /// the claim has; the card's own pin is transported instead. (2) A card that
    /// pins no thinking level yields <c>null</c> rather than the CLI's default
    /// rung, so the claim never invents a reasoning flag the operator did not ask
    /// for; the CLI's own default applies remotely, as it does today.
    /// </para>
    /// </summary>
    private static RunSpecDto BuildRunSpec(
        TaskInfo task,
        ProjectSettingsService settings,
        AgentStudio.Prompts.RuntimePromptService prompts,
        DossierMaintenanceService? dossierMaintenance)
    {
        var cliType = CliTypes.Normalize(task.CliType);
        var projectSettings = settings.Get(task.ProjectName);
        var isEpicPlanning = TaskKinds.IsEpic(task.Kind);

        var model = isEpicPlanning && !string.IsNullOrWhiteSpace(projectSettings.EpicPlanningModel)
            ? projectSettings.EpicPlanningModel
            : task.Model;
        var thinkingLevel = isEpicPlanning && projectSettings.EpicPlanningThinkingLevel is not null
            ? projectSettings.EpicPlanningThinkingLevel
            : task.ThinkingLevel;

        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        // Resolve the requested rung against what this CLI + model can actually
        // select. An unsupported value would otherwise reach the CLI verbatim and
        // fail the spawn; the registry-aware resolver falls back to the supported default.
        thinkingLevel = string.IsNullOrWhiteSpace(thinkingLevel)
            ? null
            : ModelMetadataRegistry.ResolveThinkingLevel(cliType, model, thinkingLevel);

        var modeFraming = BuildModeFraming(task, prompts, dossierMaintenance);

        return new RunSpecDto(
            cliType,
            model,
            thinkingLevel,
            settings.ResolveCliMode(task.ProjectName, cliType).Mode,
            settings.ResolveContextMode(task.ProjectName, cliType, task.ContextMode).Mode,
            modeFraming);
    }

    internal static string? BuildModeFraming(
        TaskInfo task,
        AgentStudio.Prompts.RuntimePromptService prompts,
        DossierMaintenanceService? dossierMaintenance)
    {
        // The standalone runner fetches prompt.md verbatim, so mode and Dossier
        // contracts must travel with the claim. Best-effort: discovery or a
        // framing render failure must never block a claim.
        string framing;
        try
        {
            framing = prompts.RenderModeFraming(task.Mode, task.AllowWebAccess);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "BuildRunSpec: mode framing is best-effort");
            return null;
        }

        try
        {
            if (!TaskModes.IsReportOnly(task.Mode)
                && !TaskModes.IsConcept(task.Mode)
                && dossierMaintenance is not null)
            {
                var targets = dossierMaintenance.ResolveTargets(task.ProjectName, task);
                if (targets.Count > 0)
                {
                    var taskKey = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key;
                    framing += prompts.RenderDossierMaintenanceFraming(
                        taskKey,
                        DossierMaintenanceService.RenderTargetList(targets),
                        new PromptCallContext(
                            task.ProjectName,
                            PipelineCatalogue.DossierMaintenanceStepId,
                        task.Model));
                }
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "BuildRunSpec: Dossier framing is best-effort");
        }
        return string.IsNullOrWhiteSpace(framing) ? null : framing;
    }

    private static RunSpecDto AddPromptEnrichment(RunSpecDto runSpec, string? enrichmentContext)
        => runSpec with
        {
            ModeFraming = PromptEnrichmentService.ComposeModeFraming(
                runSpec.ModeFraming,
                enrichmentContext),
        };

    private static RunSpecDto AddPersistedPromptEnrichment(RunSpecDto runSpec, TaskInfo task)
    {
        if (TaskKinds.IsEpic(task.Kind)) return runSpec;
        try
        {
            var report = PromptEnrichmentService.ReadReport(task.FolderPath);
            if (report is null || report.Status != PromptEnrichmentStatuses.Enriched)
                return runSpec;
            var contextPath = Path.Combine(
                task.FolderPath,
                IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(contextPath)
                ? AddPromptEnrichment(runSpec, File.ReadAllText(contextPath))
                : runSpec;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "AddPersistedPromptEnrichment: replay uses base mode framing");
            return runSpec;
        }
    }

    private static TaskInfo? FindTask(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        return scanner.ScanAllJobs().FirstOrDefault(t =>
            !t.Fixture
            && (string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool RunnerMatches(HttpContext context, string runnerId, string? runnerName = null)
    {
        if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal) return true;
        return string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal)
               && (runnerName is null || string.Equals(principal.RunnerName, runnerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string InitiatingPrincipal(string? ownerClientId)
        => string.IsNullOrWhiteSpace(ownerClientId) ? "automation:unknown" : ownerClientId;

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static (string Category, string Reason) RemoteEscalation(
        string outcome,
        string? reportedReason,
        IReadOnlyList<string>? gateItems)
    {
        var reason = CredentialRedactor.Redact(reportedReason)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if ((gateItems ?? []).Any(item =>
                item.TrimStart().StartsWith(
                    HumanReviewEscalationCategories.WorktreeBlocked + ":",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return (
                HumanReviewEscalationCategories.WorktreeBlocked,
                reason.Length == 0
                    ? "The remote runner retained an unsecured worktree because its salvage fence could not be published."
                    : reason);
        }
        return outcome switch
        {
            "blocked" when reason.StartsWith(
                "Remote environment preparation failed after ",
                StringComparison.OrdinalIgnoreCase)
                => (
                    HumanReviewEscalationCategories.RemoteEnvironmentPreparation,
                    reason),
            "blocked" => (
                HumanReviewEscalationCategories.AgentBlocked,
                reason.Length == 0
                    ? "The remote agent reported that it could not continue."
                    : $"The remote agent reported a blocker: {reason}"),
            "needsinput" => (
                HumanReviewEscalationCategories.NeedsHumanInput,
                reason.Length == 0
                    ? "The remote agent requires operator input before it can continue."
                    : $"The remote agent requires operator input: {reason}"),
            _ => (
                HumanReviewEscalationCategories.RemoteOutcomeUnknown,
                reason.Length == 0
                    ? "The remote runner ended without a recognized terminal outcome."
                    : $"The remote runner ended without a recognized terminal outcome: {reason}"),
        };
    }
}
