using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Extension point for a P2 feature area whose durable projection is a list
/// of independently addressable rows (for example proposals, one per
/// generated idea) rather than a single "latest result" blob. Register an
/// implementation in DI; <see cref="StudioOperationsEndpoints"/> invokes
/// every matching projector once a studio operation succeeds, after the
/// domain mutation (the operation's own <c>result_json</c>) already
/// committed, the same ordering <see cref="StudioLifecycleCoordinator"/>
/// uses for stream events. Most feature areas do not need this: a route
/// that only ever shows "the latest completed report" can read
/// <c>result_json</c> directly and skip registering a projector.
/// </summary>
public interface IStudioOperationCompletionProjector
{
    bool Handles(string kind);

    Task OnCompletedAsync(StudioOperationDto operation, TaskServerStore store, CancellationToken ct);
}

/// <summary>
/// Runner-facing claim/report surface for fenced studio operations, and the
/// Studio-facing read/cancel surface. This mirrors the shape of
/// <c>/api/v1/runners/{runnerId}/claims</c> and <c>/api/v1/runs/{runId}/**</c>
/// on purpose: a studio operation is claimed, heartbeats, reports events and
/// artifacts, and completes under the same fenced-lease discipline as a
/// coding or review attempt, without ever becoming a task run.
/// </summary>
public static class StudioOperationsEndpoints
{
    public static void MapStudioOperationsEndpoints(this WebApplication app)
    {
        var runners = app.MapGroup("/api/v1/runners/{runnerId}")
            .RequireTaskServerScope(TaskServerScopes.RunsClaim);
        runners.MapPost("/studio-operation-claims", async (
            string runnerId, ClaimStudioOperationRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("invalid-request", "Route runnerId does not match the request body."));
            return await TaskServerEndpoints.InvokeAsync(() => store.ClaimStudioOperationAsync(request, ct));
        });

        var operations = app.MapGroup("/api/v1/studio-operations")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        operations.MapGet("/{operationId}", async (string operationId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetStudioOperationAsync(operationId, ct)));

        operations.MapPost("/{operationId}/cancel", async (string operationId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.CancelStudioOperationAsync(operationId, ct);
                return new { canceled = true, operationId };
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var report = operations.MapGroup("/{operationId}")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        report.MapPost("/heartbeat", async (
            string operationId, StudioOperationLeaseRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.HeartbeatStudioOperationAsync(operationId, request, ct);
                return new { renewed = true };
            }));

        report.MapPost("/events", async (
            string operationId, AppendStudioOperationEventRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AppendStudioOperationEventAsync(operationId, request, ct), StatusCodes.Status201Created));

        report.MapPost("/artifacts", async (
            string operationId, AppendStudioOperationArtifactRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AppendStudioOperationArtifactAsync(operationId, request, ct), StatusCodes.Status201Created));

        report.MapPost("/completion", async (
            string operationId,
            CompleteStudioOperationRequest request,
            TaskServerStore store,
            IEnumerable<IStudioOperationCompletionProjector> projectors,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var operation = await store.CompleteStudioOperationAsync(operationId, request, ct);
                if (string.Equals(operation.Status, StudioOperationStatuses.Succeeded, StringComparison.Ordinal))
                    foreach (var projector in projectors.Where(projector => projector.Handles(operation.Kind)))
                        await projector.OnCompletedAsync(operation, store, ct);
                return operation;
            }));
    }
}
