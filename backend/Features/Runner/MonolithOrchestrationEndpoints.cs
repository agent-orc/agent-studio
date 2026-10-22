using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Runner;

public static class MonolithOrchestrationEndpoints
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static void MapMonolithOrchestrationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/orchestration");
        api.MapGet("/runs/{runId}", (HttpContext context, string runId,
            AccessSecurityStore security, MonolithOrchestrationStore store) =>
            Authorize(context, security) is null
                ? Results.Unauthorized()
                : store.Get(runId) is { } run ? Wire(run) : Results.NotFound());
        api.MapGet("/runs", (HttpContext context, string? projectId, string? status,
            AccessSecurityStore security, MonolithOrchestrationStore store) =>
            Authorize(context, security) is null
                ? Results.Unauthorized() : Wire(store.List(projectId, status)));
        api.MapPost("/projects/{projectId}/runs", (HttpContext context, string projectId,
            CreateOrchestrationRunRequest request, AccessSecurityStore security,
            MonolithOrchestrationStore store, TaskScannerService scanner) =>
            Execute(() =>
            {
                if (Authorize(context, security) is null) return Results.Unauthorized();
                var task = scanner.ScanAllJobs().FirstOrDefault(item =>
                    item.ProjectName == projectId && (item.Id == request.TaskId || item.TaskKey == request.TaskId));
                if (task is null) return Results.NotFound();
                var source = request.ExpectedSourceState ?? task.State;
                if (task.State != source) return Results.Conflict(new ApiError(
                    "orchestration-source-state-mismatch", "The card has left the expected source lane."));
                var run = store.Create(
                    projectId, task.Id, request.Stage, request.PayloadJson, request.IdempotencyKey,
                    source, request.AuthorityAttemptId ?? task.Id, request.AuthorityFence,
                    request.AuthorityEpoch);
                if (run.Status is "pending" or "leased" or "applying")
                    EngineDecisionWaitMarker.Write(task.FolderPath, run.RunId);
                context.Response.Headers.Location = $"/api/v1/orchestration/runs/{run.RunId}";
                return Wire(run, StatusCodes.Status201Created);
            }));
        api.MapPost("/claims", (HttpContext context, OrchestrationClaimRequest request,
            AccessSecurityStore security, MonolithOrchestrationStore store) =>
            Execute(() => Matches(context, security, request.EngineId)
                ? Wire(store.Claim(request)) : Results.Unauthorized()));
        api.MapPost("/runs/{runId}/lease/renew", (HttpContext context, string runId,
            OrchestrationLeaseRenewRequest request, AccessSecurityStore security,
            MonolithOrchestrationStore store) =>
            Execute(() => Matches(context, security, request.EngineId)
                ? Wire(store.Renew(runId, request)) : Results.Unauthorized()));
        api.MapPost("/runs/{runId}/lease/release", (HttpContext context, string runId,
            ReleaseOrchestrationLeaseRequest request, AccessSecurityStore security,
            MonolithOrchestrationStore store) =>
            Execute(() => Matches(context, security, request.EngineId)
                ? Wire(store.Release(runId, request)) : Results.Unauthorized()));
        api.MapPost("/runs/{runId}/stages/complete", async (HttpContext context, string runId,
            CompleteOrchestrationStageRequest request, AccessSecurityStore security,
            MonolithOrchestrationStore store, CancellationToken ct) =>
            await ExecuteAsync(async () => Matches(context, security, request.EngineId)
                ? Wire(await store.CompleteAsync(runId, request, ct)) : Results.Unauthorized()));
    }

    private static IResult Wire(object value, int statusCode = StatusCodes.Status200OK)
        => Results.Json(value, WireJson, statusCode: statusCode);

    private static bool Matches(HttpContext context, AccessSecurityStore security, string engineId)
    {
        if (Authorize(context, security) is not { } principal
            || !string.Equals(principal.RunnerId, engineId, StringComparison.Ordinal)) return false;
        // AGT-3060: every authenticated engine request is the engine's readiness signal.
        context.RequestServices.GetService<OrchestrationEngineReadiness>()?.Observe(engineId);
        return true;
    }

    internal static RunnerPrincipal? Authorize(HttpContext context, AccessSecurityStore security)
    {
        var principal = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
        if (principal is null)
        {
            var value = context.Request.Headers.Authorization.FirstOrDefault();
            if (value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true) return null;
            principal = security.AuthenticateRunner(value[7..].Trim());
        }
        return principal is { RunnerId: var id }
               && id.StartsWith("engine_", StringComparison.Ordinal)
               && principal.Scopes.Contains(RunnerScopes.Orchestration)
            ? principal : null;
    }

    internal static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new ApiError("not-found", exception.Message)); }
        catch (ArgumentException exception) { return Results.BadRequest(new ApiError("invalid-request", exception.Message)); }
        catch (InvalidOperationException exception) { return Results.Conflict(new ApiError(exception.Message, exception.Message)); }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new ApiError("not-found", exception.Message)); }
        catch (ArgumentException exception) { return Results.BadRequest(new ApiError("invalid-request", exception.Message)); }
        catch (InvalidOperationException exception) { return Results.Conflict(new ApiError(exception.Message, exception.Message)); }
    }
}

public sealed class MonolithOrchestrationRecoveryService(MonolithOrchestrationStore store,
    TaskScannerService scanner,
    AttemptAuthorityService authority,
    OrchestratorDecisionStore decisionInputs,
    OrchestrationRolloutStore rollout,
    IAiOperatorDecisionQueue aiOperator,
    OrchestratorLog operatorLog,
    EngineBatchAdmission batchAdmission,
    OrchestrationEngineReadiness readiness,
    TimeProvider time,
    ILogger<MonolithOrchestrationRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan PreparationGrace = TimeSpan.FromSeconds(30);

    /// <summary>Batch admissions one re-arm pass may run; the rest wait for the next pass.</summary>
    internal const int RearmBudgetPerPass = 4;

    /// <summary>Wait before a failed re-arm admission is tried again; it doubles per failure.</summary>
    internal static readonly TimeSpan RearmRetryBase = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RearmRetryCap = TimeSpan.FromMinutes(30);

    // A failed admission waits before its next attempt, so occurrences that keep
    // failing cannot take the per-pass budget from the ones behind them.
    private readonly Dictionary<string, (int Failures, DateTime RetryAt)> _rearmRetries =
        new(StringComparer.Ordinal);

    // An admission can run a per-task gate, so a re-arm pass runs beside the
    // loop and a loop starts one only when none is running (as AGT-3091).
    private readonly object _rearmGate = new();
    private Task _rearmPass = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                aiOperator.RecoverPending();
                RecoverOnce();
                try { await store.RecoverApplyingAsync(stoppingToken, (item, exception) =>
                    logger.LogWarning(exception,
                        "orchestration-application-retry-pending run={RunId} task={TaskId}",
                        item.Run.RunId, item.Run.TaskId), item =>
                    logger.LogInformation("orchestration-application-superseded run={RunId} task={TaskId}",
                        item.Run.RunId, item.Run.TaskId), (item, exception) =>
                    logger.LogError(exception,
                        "orchestration-application-malformed run={RunId} task={TaskId}",
                        item.Run.RunId, item.Run.TaskId)); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-application-retry-pending");
                }
                try { AlertFailedApplications(); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-application-alert-pending");
                }
                try
                {
                    RecoverShadowOutcomes();
                    store.RecoverShadowComparisons();
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-shadow-comparison-retry-pending");
                }
                await RecoverRolledBackAsync(stoppingToken);
                StartRearmPass(stoppingToken);
                AlertLongWaits();
                await Task.Delay(PreparationGrace, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogError(exception, "orchestration-recovery-incomplete"); }
        finally
        {
            // A stop waits for a running re-arm pass. The pass ends with the
            // admission in progress, which the host stop cancels.
            Task pass;
            lock (_rearmGate) pass = _rearmPass;
            await pass;
        }
    }

    private async Task RecoverRolledBackAsync(CancellationToken ct)
    {
        foreach (var pending in store.WaitingOccurrences().Where(item =>
            item.Run.Status is ("pending" or "leased") && !item.Shadow
            && rollout.IsRolledBack(item.Profile)))
        {
            try
            {
                var claimed = store.ClaimRolledBack(pending.Run.RunId);
                if (claimed.Run is null || claimed.Lease is null) continue;
                var decision = DecideRolledBack(claimed.Run);
                await store.CompleteAsync(claimed.Run.RunId,
                    new CompleteOrchestrationStageRequest("engine_rollback", "monolith",
                        claimed.Lease.LeaseId, claimed.Lease.Fence, claimed.Run.CurrentStage,
                        decision.Action, decision.OutputJson,
                        $"rollback:{claimed.Run.RunId}:{claimed.Lease.Fence}"), ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "orchestration-rollback-pending run={RunId}", pending.Run.RunId);
            }
        }
    }

    private static (OrchestrationAction Action, string OutputJson) DecideRolledBack(OrchestrationRunDto run)
    {
        if (run.CurrentStage == OrchestrationStage.BatchPlan)
        {
            // Rollback decides without the advisor; the monolith owns the next plan anyway.
            var facts = JsonSerializer.Deserialize<BatchPlanOccurrencePayload>(run.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Facts;
            var plan = MonolithTransitionAdapter.BatchPlan(facts with { AdvisorMode = "off" }).Decision;
            return (OrchestrationAction.Continue, JsonSerializer.Serialize(new
            {
                batchPlan = plan,
                decision = MonolithTransitionAdapter.BatchPlanDecisionPolicySignature(plan),
                decider = $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith-rollback",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        using var payload = JsonDocument.Parse(run.PayloadJson);
        TransitionDecision transition;
        if (run.CurrentStage == OrchestrationStage.GateDispatch)
        {
            var root = payload.RootElement;
            var failed = root.TryGetProperty("gates", out var gates)
                && gates.ValueKind == JsonValueKind.Array
                && gates.EnumerateArray().Any(gate => gate.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    && status.GetString() is ("failed" or "fail")) ? 1 : 0;
            var attempt = root.TryGetProperty("gateAttemptId", out var gateAttempt)
                ? gateAttempt.GetString() ?? run.RunId : run.RunId;
            var used = root.TryGetProperty("automaticRoundsUsed", out var rounds)
                ? rounds.GetInt32() : 0;
            var allowed = root.TryGetProperty("maximumCardRounds", out var maximum)
                ? maximum.GetInt32() : 4;
            var infrastructureFailure = root.TryGetProperty("infrastructureFailure", out var infrastructure)
                && infrastructure.ValueKind == JsonValueKind.True;
            var interventionRequired = root.TryGetProperty("interventionRequired", out var intervention)
                && intervention.ValueKind == JsonValueKind.True;
            transition = MonolithTransitionAdapter.StageGateTransition(run.TaskId, attempt,
                failed, used, allowed, infrastructureFailure, interventionRequired).Transition!;
        }
        else if (payload.RootElement.TryGetProperty("reviewOutcome", out _))
        {
            var stage = JsonSerializer.Deserialize<StagePayload>(run.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            transition = MonolithTransitionAdapter.Decide(MonolithTransitionAdapter.StageDecisionPolicyFacts(
                run.TaskId, run.ReissueAttempts, stage));
        }
        else
        {
            var root = payload.RootElement;
            transition = MonolithTransitionAdapter.StageCoding(run.TaskId,
                root.TryGetProperty("runAttemptId", out var attempt)
                    ? attempt.GetString() ?? run.RunId : run.RunId,
                root.TryGetProperty("targetState", out var target) ? target.GetString() : null);
        }
        var action = MonolithTransitionAdapter.StageDecisionPolicyToStageAction(transition.Action);
        return (action, JsonSerializer.Serialize(new
        {
            transition,
            decision = action.ToString(),
            decider = $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith-rollback",
        }));
    }

    private void StartRearmPass(CancellationToken stoppingToken)
    {
        lock (_rearmGate)
        {
            if (!_rearmPass.IsCompleted) return;
            _rearmPass = Task.Run(async () =>
            {
                try { await RearmDeferredBatchAdmissionsAsync(stoppingToken); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "engine-batch-admission-rearm-incomplete");
                }
            });
        }
    }

    /// <summary>
    /// Until 17.10.2026 the applier completed every engine DeferToBatch decision
    /// without its batch admission, and a completed occurrence is final, so
    /// nothing drove those cards on. A completed, non-shadow ReviewReport
    /// decision to defer to the batch that is still its card's newest re-arms the
    /// same admission while the card waits in Auto Review on the current review
    /// and the batch gate has not taken that review past admission. The facts
    /// are in memory or in two small files, so a pass runs no Git before it
    /// admits; a repeat finds the admission and does nothing. A stop ends the
    /// pass after the admission in progress.
    /// </summary>
    internal async Task<int> RearmDeferredBatchAdmissionsAsync(CancellationToken stoppingToken)
    {
        var tasks = scanner.ScanAllJobs();
        var admissions = 0;
        var rearmed = 0;
        foreach (var occurrence in store.NewestPerTask("ReviewReport").Where(item =>
            !item.Shadow && item.Run.Status == "completed"
            && item.EngineReceipt?.Action == nameof(TransitionAction.DeferToBatch)))
        {
            if (admissions >= RearmBudgetPerPass || stoppingToken.IsCancellationRequested) break;
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId);
            if (card is null || card.State != occurrence.ExpectedSourceState) continue;
            if (RetryPending(occurrence.Run.RunId)) continue;
            try
            {
                if (!batchAdmission.AwaitsAdmission(card, occurrence))
                {
                    ForgetRetry(occurrence.Run.RunId);
                    continue;
                }
                admissions++;
                var outcome = await batchAdmission.AdmitAsync(card, occurrence, stoppingToken);
                scanner.PublishCoreFromKnownFolder(card.FolderPath);
                ForgetRetry(occurrence.Run.RunId);
                if (outcome != EngineBatchAdmissionOutcome.Skipped) rearmed++;
                logger.LogWarning(
                    "engine-batch-admission-rearmed task={TaskKey} run={RunId} attempt={AttemptId} outcome={Outcome}",
                    card.Key ?? card.Id, occurrence.Run.RunId, occurrence.AuthorityAttemptId, outcome);
            }
            catch (OperationCanceledException exception)
            {
                // Only a host stop cancels an admission; the next start re-arms the rest.
                logger.LogInformation(exception, "engine-batch-admission-rearm-stopped task={TaskKey} run={RunId}",
                    card.Key ?? card.Id, occurrence.Run.RunId);
                break;
            }
            catch (Exception exception)
            {
                var (failures, retryAt) = RecordRetry(occurrence.Run.RunId);
                logger.LogWarning(exception,
                    "engine-batch-admission-rearm-pending task={TaskKey} run={RunId} failures={Failures} retryAt={RetryAt:o}",
                    card.Key ?? card.Id, occurrence.Run.RunId, failures, retryAt);
            }
        }
        if (admissions > 0) scanner.InvalidateCache();
        return rearmed;
    }

    private bool RetryPending(string runId)
    {
        lock (_rearmRetries)
            return _rearmRetries.TryGetValue(runId, out var retry)
                && retry.RetryAt > time.GetUtcNow().UtcDateTime;
    }

    private void ForgetRetry(string runId)
    {
        lock (_rearmRetries) _rearmRetries.Remove(runId);
    }

    private (int Failures, DateTime RetryAt) RecordRetry(string runId)
    {
        lock (_rearmRetries)
        {
            var failures = (_rearmRetries.TryGetValue(runId, out var previous) ? previous.Failures : 0) + 1;
            var wait = TimeSpan.FromTicks(Math.Min(RearmRetryCap.Ticks,
                RearmRetryBase.Ticks << Math.Min(failures - 1, 16)));
            var retry = (failures, time.GetUtcNow().UtcDateTime + wait);
            _rearmRetries[runId] = retry;
            return retry;
        }
    }

    private void AlertLongWaits()
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var occurrence in store.WaitingOccurrences().Where(item =>
            !rollout.IsRolledBack(item.Profile)
            && ShouldAlert(item, now)))
        {
            var card = scanner.ScanAllJobs().FirstOrDefault(item =>
                item.Id == occurrence.Run.TaskId && item.ProjectName == occurrence.Run.ProjectId);
            if (card is null || card.State != occurrence.ExpectedSourceState) continue;
            var topic = "engine-decision-unavailable:" + occurrence.Run.RunId;
            if (operatorLog.Read(card.WatchPath).Any(item => item.Topic == topic)) continue;
            operatorLog.Append(card.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Alert,
                Topic = topic,
                JobId = card.Id,
                Summary = $"{card.Key ?? card.Id} is waiting for the Orchestrator Engine.",
                Reasoning = "The occurrence remains uncompleted and will resume after an engine instance claims or reclaims it.",
            });
        }
        if (readiness.State != OrchestrationEngineReadiness.Ready) return;
        foreach (var occurrence in store.WaitingOccurrences().Where(item =>
            ShouldAlertHealthyLongWait(item, now)))
        {
            var card = scanner.ScanAllJobs().FirstOrDefault(item =>
                item.Id == occurrence.Run.TaskId && item.ProjectName == occurrence.Run.ProjectId);
            if (card is null || card.State != occurrence.ExpectedSourceState) continue;
            var topic = "engine-decision-long-wait:" + occurrence.Run.RunId;
            if (operatorLog.Read(card.WatchPath).Any(item => item.Topic == topic)) continue;
            operatorLog.Append(card.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Alert,
                Topic = topic,
                JobId = card.Id,
                Summary = $"{card.Key ?? card.Id} has waited over 15 minutes for an engine decision while the engine is live.",
                Reasoning = $"Occurrence {occurrence.Run.RunId} remains {occurrence.Run.Status}.",
            });
        }
    }

    internal void AlertFailedApplications()
    {
        var failed = store.List(null, "failed");
        if (failed.Count == 0) return;
        var cards = scanner.ScanAllJobs();
        foreach (var run in failed)
        {
            var occurrence = store.GetOccurrence(run.RunId);
            var card = cards.FirstOrDefault(item => item.Id == run.TaskId
                && item.ProjectName == run.ProjectId);
            if (occurrence?.ApplicationFailure is null || card is null) continue;
            var topic = "engine-decision-application-refused:" + run.RunId;
            if (!operatorLog.Read(card.WatchPath).Any(item => item.Topic == topic))
                operatorLog.Append(card.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Alert,
                    Topic = topic,
                    JobId = card.Id,
                    Summary = $"{card.Key ?? card.Id} needs operator attention after an engine decision could not be applied.",
                    Reasoning = $"The card remains in {card.State}. {occurrence.ApplicationFailure} "
                        + "Resolve the lane refusal and decide the next card action.",
                });
            if (EngineDecisionWaitMarker.Clear(card.FolderPath, run.RunId))
            {
                scanner.InvalidateCache();
                scanner.PublishCoreFromKnownFolder(card.FolderPath);
            }
        }
    }

    internal static bool ShouldAlert(MonolithOrchestrationOccurrence occurrence, DateTime now)
        => occurrence.Run.Status == "pending"
            && now - occurrence.Run.CreatedAt > TimeSpan.FromMinutes(1)
            || occurrence.Run.Status == "leased"
                && occurrence.Lease is { ExpiresAt: var expiry } && expiry <= now;

    internal static bool ShouldAlertHealthyLongWait(MonolithOrchestrationOccurrence occurrence,
        DateTime now)
        => occurrence.Run.Status is ("pending" or "leased" or "applying")
            && now - occurrence.Run.CreatedAt >= TimeSpan.FromMinutes(15);

    /// <summary>
    /// A process can stop after the monolith applied a shadow transition but
    /// before it recorded the receipt; the card has then left the lane that
    /// replays the transition. The current lane resolves such an occurrence.
    /// </summary>
    internal void RecoverShadowOutcomes()
    {
        var unrecorded = store.UnrecordedShadowOutcomes();
        if (unrecorded.Count == 0) return;
        // A cached lane can predate the move; resolve only from a fresh scan.
        scanner.InvalidateCache();
        var tasks = scanner.ScanAllJobs();
        foreach (var occurrence in unrecorded)
        {
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId);
            store.ResolveShadowOutcome(occurrence.Run.RunId, card?.State);
        }
    }

    internal void RecoverOnce(Action? afterTaskSnapshot = null)
    {
        var tasks = scanner.ScanAllJobs();
        afterTaskSnapshot?.Invoke();
        // Of the card state this pass writes, the scanner projects only the wait
        // marker. A pass that wrote or removed none must not invalidate the task
        // index: a mutation invalidation makes the next freshness reader, task
        // detail reads among them, wait for a full workspace walk every pass.
        var markersChanged = false;
        var changedFolders = new HashSet<string>(StringComparer.Ordinal);
        bool RecordMarker(string folder, bool changed)
        {
            if (changed) changedFolders.Add(folder);
            return changed;
        }
        foreach (var occurrence in store.WaitingOccurrences().Where(item =>
            item.Run.Status is "pending" or "leased"))
        {
            try
            {
                store.SupersedeStaleLaneDecisionAsync(occurrence.Run.RunId, () =>
                {
                    var card = scanner.FindPublishedJob(occurrence.Run.TaskId);
                    return card?.ProjectName == occurrence.Run.ProjectId ? card.State : null;
                }).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "orchestration-supersession-pending run={RunId}",
                    occurrence.Run.RunId);
            }
        }
        foreach (var occurrence in store.AwaitingActivation())
        {
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId)
                ?? scanner.ScanAllJobsWithArchive().FirstOrDefault(item =>
                    item.Id == occurrence.Run.TaskId && item.ProjectName == occurrence.Run.ProjectId);
            try
            {
                if (card?.State != occurrence.ExpectedSourceState)
                {
                    store.Supersede(occurrence.Run.RunId, card?.State, includeApplying: false);
                    continue;
                }
                if (occurrence.Run.CurrentStage == OrchestrationStage.ReviewInputDecision)
                {
                    if (decisionInputs.GetInputs(occurrence.Run.RunId) is not null)
                    {
                        store.Activate(occurrence.Run.RunId);
                        if (card is not null && card.State == occurrence.ExpectedSourceState)
                            markersChanged |= RecordMarker(card.FolderPath,
                                MarkWaiting(card.FolderPath, occurrence.Run.RunId));
                    }
                    else if (time.GetUtcNow().UtcDateTime - occurrence.Run.UpdatedAt >= PreparationGrace)
                        store.CancelUnactivated(occurrence.Run.RunId, "review-input-grounds-not-frozen");
                    continue;
                }
                if (card is not null)
                    markersChanged |= RecordMarker(card.FolderPath,
                        EngineDecisionWaitMarker.Clear(card.FolderPath, occurrence.Run.RunId));
                var accepted = authority.GetReview(occurrence.AuthorityAttemptId)?.Reports.Any(report =>
                    report.AuthorityStatus == AttemptWriteStatus.Accepted
                    && occurrence.IdempotencyKey ==
                        $"review-report:{occurrence.AuthorityAttemptId}:{report.IdempotencyKey}") == true;
                if (accepted)
                {
                    if (card is not null && occurrence.Run.CurrentStage == OrchestrationStage.ReviewDecision
                        && !occurrence.Shadow)
                    {
                        var payload = JsonSerializer.Deserialize<StagePayload>(occurrence.Run.PayloadJson,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            ?? throw new InvalidDataException("The prepared review decision is missing its payload.");
                        ReviewRoundBudgetStore.RecordAcceptedEngineReview(card.FolderPath, payload);
                    }
                    store.Activate(occurrence.Run.RunId);
                    if (!occurrence.Shadow && card is not null && card.State == occurrence.ExpectedSourceState)
                        markersChanged |= RecordMarker(card.FolderPath,
                            MarkWaiting(card.FolderPath, occurrence.Run.RunId));
                }
                else
                {
                    if (time.GetUtcNow().UtcDateTime - occurrence.Run.UpdatedAt >= PreparationGrace)
                        store.CancelUnactivated(occurrence.Run.RunId, "review-settlement-not-accepted");
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "orchestration-review-activation-pending run={RunId}",
                    occurrence.Run.RunId);
            }
        }
        // AI Operator occurrences carry no card wait marker: one marker file
        // names one run, and a lane occurrence on the same card owns it.
        foreach (var occurrence in store.WaitingOccurrences().Where(item =>
            item.Run.CurrentStage != OrchestrationStage.OperatorDecision))
        {
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId);
            if (card is not null && card.State == occurrence.ExpectedSourceState)
                markersChanged |= RecordMarker(card.FolderPath,
                    MarkWaiting(card.FolderPath, occurrence.Run.RunId));
        }
        // A marker that names a cancelled occurrence, or a completed one that
        // did not hold its card, waits for nothing; the card would show an
        // engine wait until it leaves the lane.
        foreach (var card in tasks.Where(item => EngineDecisionWaitMarker.Read(item.FolderPath) is not null))
        {
            // RecoverOnce runs outside the loop's per-step catches, so a lane move
            // or a concurrent clear that races this read must not end the loop.
            try
            {
                if (EngineDecisionWaitMarker.Read(card.FolderPath) is { } runId
                    && store.GetOccurrence(runId) is { } named
                    && (named.ExpectedSourceState != card.State
                        || named.Run.Status is ("cancelled" or "failed")
                        || named.Run.Status == "completed" && !MayHoldCard(named)))
                    markersChanged |= RecordMarker(card.FolderPath,
                        EngineDecisionWaitMarker.Clear(card.FolderPath, runId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException)
            {
                logger.LogWarning(exception, "engine-decision-wait-marker-cleanup-skipped task={TaskKey}",
                    card.Key ?? card.Id);
            }
        }
        if (markersChanged)
        {
            scanner.InvalidateCache();
            foreach (var folder in changedFolders)
                scanner.PublishCoreFromKnownFolder(folder);
        }
    }

    /// <summary>
    /// Marks the card as waiting on <paramref name="runId"/>. The occurrence can
    /// complete, and its applier clear the marker, between the status read and
    /// this write, so the status is read again and a finished occurrence leaves
    /// no marker behind. Returns whether the card's marker may have changed.
    /// </summary>
    internal bool MarkWaiting(string folder, string runId)
    {
        try
        {
            var previous = EngineDecisionWaitMarker.Read(folder);
            EngineDecisionWaitMarker.Write(folder, runId);
            if (store.Get(runId)?.Status is not ("pending" or "leased" or "applying"))
                EngineDecisionWaitMarker.Clear(folder, runId);
            return EngineDecisionWaitMarker.Read(folder) != previous;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The card moved after the scan; the next pass reads its new folder,
            // which a rescan publishes.
            logger.LogWarning(exception, "engine-decision-wait-marker-skipped run={RunId}", runId);
            return true;
        }
    }

    /// <summary>
    /// An active decision can hold its card for an AI Operator ask or a
    /// checkpoint, and its application keeps the wait marker on purpose.
    /// </summary>
    private static bool MayHoldCard(MonolithOrchestrationOccurrence occurrence)
    {
        if (occurrence.Profile == "CodingCompletion") return true;
        if (occurrence.Completion?.OutputJson is not { } output) return false;
        using var document = JsonDocument.Parse(output);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("activeChoice", out _);
    }
}
