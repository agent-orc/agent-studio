using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-W73 OD-S1 routes. The engine reads frozen grounds input, creates the
/// shadow model job and stores the receipt under the orchestration scope; a
/// runner host claims and completes the job with its own provider credential;
/// Studio reads receipts. Nothing here decides a transition.
/// </summary>
public static class OrchestratorDecisionEndpoints
{
    private static readonly JsonSerializerOptions WireJson = DecisionGroundsPolicy.Json;

    public static void MapOrchestratorDecisionEndpoints(this WebApplication app, bool mapsEngineRoutes)
    {
        if (mapsEngineRoutes)
        {
            var engine = app.MapGroup("/api/v1/orchestration");
            engine.MapGet("/runs/{runId}/grounds-input", (HttpContext context, string runId,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : store.GetInputs(runId) is { } input ? Wire(input) : Results.NotFound());
            engine.MapGet("/decision-grounds/pending", (HttpContext context, int? limit,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : Wire(store.PendingReceipts(limit ?? 10)));
            engine.MapPost("/decision-jobs", (HttpContext context, OrchestratorDecisionJobRequest request,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Execute(() =>
                    MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                        : Wire(store.CreateJob(request))));
            engine.MapGet("/decision-jobs/{jobId}", (HttpContext context, string jobId,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : store.GetJob(jobId) is { } job ? Wire(job) : Results.NotFound());
            engine.MapPut("/runs/{runId}/decision-receipt", async (HttpContext context, string runId,
                OrchestratorDecisionReceiptV1 receipt, AccessSecurityStore security,
                OrchestratorDecisionStore store, AgentMessageBusBridge bus, CancellationToken ct) =>
            {
                var existed = store.GetReceipt(receipt.ReceiptId) is not null;
                var result = MonolithOrchestrationEndpoints.Execute(() =>
                {
                    if (MonolithOrchestrationEndpoints.Authorize(context, security) is null)
                        return Results.Unauthorized();
                    if (receipt.OccurrenceId != runId)
                        return Results.BadRequest(new ApiError("invalid-request", "Receipt occurrence does not match the route."));
                    return Wire(store.PutReceipt(receipt));
                });
                if (!existed && store.GetReceipt(receipt.ReceiptId) is { } stored)
                    await bus.EmitDecisionReceiptAsync(stored, ct);
                return result;
            });
        }

        app.MapPost("/api/runner/orchestrator-decisions/claim", (HttpContext context,
            OrchestratorDecisionJobClaimRequest request, OrchestratorDecisionStore store) =>
            RunnerMatches(context, request.RunnerId) ? Wire(store.Claim(request)) : Results.Unauthorized())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        app.MapPost("/api/runner/orchestrator-decisions/complete", (HttpContext context,
            OrchestratorDecisionJobCompletion completion, OrchestratorDecisionStore store) =>
            !RunnerMatches(context, completion.RunnerId) ? Results.Unauthorized()
                : store.Complete(completion) ? Results.Ok(new { accepted = true })
                : Results.Conflict(new { accepted = false, error = "stale decision-job claim" }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        var studio = app.MapGroup("/api/orchestration/decision-receipts");
        studio.MapGet("", (string? taskId, string? projectId, int? limit, OrchestratorDecisionStore store) =>
            Wire(store.ListReceipts(string.IsNullOrWhiteSpace(taskId) ? null : taskId, limit ?? 20,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId)));
        studio.MapGet("/spend", (string? taskId, string? projectId, OrchestratorDecisionStore store) =>
            Wire(store.SpendSummary(string.IsNullOrWhiteSpace(taskId) ? null : taskId,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId)));
        studio.MapGet("/{receiptId}", (string receiptId, OrchestratorDecisionStore store) =>
            store.GetReceipt(receiptId) is { } receipt ? Wire(receipt) : Results.NotFound());
        studio.MapGet("/{receiptId}/operator-intent", (string receiptId, OrchestratorDecisionStore store) =>
            store.GetOperatorIntent(receiptId) is { } intent ? Wire(intent) : Results.NotFound());
        studio.MapPost("/{receiptId}/operator-intent", (string receiptId, DecisionOperatorIntentRequest request,
            OrchestratorDecisionStore store) => MonolithOrchestrationEndpoints.Execute(() =>
                Wire(store.PutOperatorIntent(receiptId, request))));
        studio.MapGet("/{receiptId}/outcome", (string receiptId, OrchestratorDecisionStore store,
            MonolithOrchestrationStore orchestration) =>
        {
            if (store.GetReceipt(receiptId) is not { } receipt) return Results.NotFound();
            var occurrence = orchestration.GetOccurrence(receipt.OccurrenceId);
            var later = receipt.Scope.TaskId is { } taskId
                ? store.ReceiptsForTask(taskId).Where(item => item.EndedAt > receipt.EndedAt)
                    .Select(store.Summary).ToArray()
                : [];
            return Wire(new
            {
                receipt.ReceiptId,
                receipt.OccurrenceId,
                occurrenceStatus = occurrence?.Run.Status,
                appliedAction = occurrence?.MonolithReceipt?.Action,
                appliedReason = occurrence?.MonolithReceipt?.Reason,
                engineAction = occurrence?.EngineReceipt?.Action,
                spend = store.Summary(receipt),
                lateCalls = store.LateCalls(receipt.OccurrenceId),
                laterDecisions = later,
            });
        });
    }

    private static IResult Wire(object value) => Results.Json(value, WireJson);

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
            || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);
}
