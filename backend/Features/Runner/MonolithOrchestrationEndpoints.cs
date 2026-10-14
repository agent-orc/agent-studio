using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;

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
    ILogger<MonolithOrchestrationRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var tasks = scanner.ScanAllJobs();
            ActivateSettledReviews();
            foreach (var occurrence in store.WaitingOccurrences())
            {
                var card = tasks.FirstOrDefault(item => item.Id == occurrence.Run.TaskId
                    && item.ProjectName == occurrence.Run.ProjectId);
                if (card is not null && card.State == occurrence.ExpectedSourceState)
                    EngineDecisionWaitMarker.Write(card.FolderPath, occurrence.Run.RunId);
            }
            scanner.InvalidateCache();
            while (!stoppingToken.IsCancellationRequested)
            {
                ActivateSettledReviews();
                try { await store.RecoverApplyingAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-application-retry-pending");
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogError(exception, "orchestration-recovery-incomplete"); }
    }

    private void ActivateSettledReviews()
    {
        foreach (var occurrence in store.AwaitingActivation())
        {
            try
            {
                var accepted = authority.GetReview(occurrence.AuthorityAttemptId)?.Reports.Any(report =>
                    report.AuthorityStatus == AttemptWriteStatus.Accepted
                    && occurrence.IdempotencyKey ==
                        $"review-report:{occurrence.AuthorityAttemptId}:{report.IdempotencyKey}") == true;
                if (accepted) store.Activate(occurrence.Run.RunId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "orchestration-review-activation-pending run={RunId}",
                    occurrence.Run.RunId);
            }
        }
    }
}
