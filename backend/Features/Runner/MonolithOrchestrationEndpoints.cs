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
        => Authorize(context, security) is { } principal
            && string.Equals(principal.RunnerId, engineId, StringComparison.Ordinal);

    private static RunnerPrincipal? Authorize(HttpContext context, AccessSecurityStore security)
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

    private static IResult Execute(Func<IResult> action)
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
    OrchestrationRolloutStore rollout,
    OrchestratorLog operatorLog,
    TimeProvider time,
    ILogger<MonolithOrchestrationRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan PreparationGrace = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RecoverOnce();
                try { await store.RecoverApplyingAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-application-retry-pending");
                }
                await RecoverRolledBackAsync(stoppingToken);
                AlertLongWaits();
                await Task.Delay(PreparationGrace, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogError(exception, "orchestration-recovery-incomplete"); }
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
            var plan = BatchPlanDecisionPolicy.Decide(facts with { AdvisorMode = "off" }).Decision;
            return (OrchestrationAction.Continue, JsonSerializer.Serialize(new
            {
                batchPlan = plan,
                decision = BatchPlanDecisionPolicy.Signature(plan),
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
            transition = StageDecisionPolicy.GateTransition(run.TaskId, attempt,
                failed, used, allowed, infrastructureFailure, interventionRequired).Transition!;
        }
        else if (payload.RootElement.TryGetProperty("reviewOutcome", out _))
        {
            var stage = JsonSerializer.Deserialize<StagePayload>(run.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            transition = MonolithTransitionAdapter.Decide(StageDecisionPolicy.Facts(
                run.TaskId, run.ReissueAttempts, stage));
        }
        else
        {
            var root = payload.RootElement;
            transition = StageDecisionPolicy.Coding(run.TaskId,
                root.TryGetProperty("runAttemptId", out var attempt)
                    ? attempt.GetString() ?? run.RunId : run.RunId,
                root.TryGetProperty("targetState", out var target) ? target.GetString() : null);
        }
        var action = StageDecisionPolicy.ToStageAction(transition.Action);
        return (action, JsonSerializer.Serialize(new
        {
            transition,
            decision = action.ToString(),
            decider = $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith-rollback",
        }));
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
    }

    internal static bool ShouldAlert(MonolithOrchestrationOccurrence occurrence, DateTime now)
        => occurrence.Run.Status == "pending"
            && now - occurrence.Run.CreatedAt > TimeSpan.FromMinutes(1)
            || occurrence.Run.Status == "leased"
                && occurrence.Lease is { ExpiresAt: var expiry } && expiry <= now;

    internal void RecoverOnce()
    {
        var tasks = scanner.ScanAllJobs();
        foreach (var occurrence in store.AwaitingActivation())
        {
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId);
            try
            {
                if (card is not null)
                    EngineDecisionWaitMarker.Clear(card.FolderPath, occurrence.Run.RunId);
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
                        EngineDecisionWaitMarker.Write(card.FolderPath, occurrence.Run.RunId);
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
        foreach (var occurrence in store.WaitingOccurrences())
        {
            var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                && item.ProjectName == occurrence.Run.ProjectId);
            if (card is not null && card.State == occurrence.ExpectedSourceState)
                EngineDecisionWaitMarker.Write(card.FolderPath, occurrence.Run.RunId);
        }
        foreach (var cancelled in store.List(null, "cancelled"))
        {
            var card = tasks.FirstOrDefault(item => item.Id == cancelled.TaskId
                && item.ProjectName == cancelled.ProjectId);
            if (card is not null)
                EngineDecisionWaitMarker.Clear(card.FolderPath, cancelled.RunId);
        }
        scanner.InvalidateCache();
    }
}
