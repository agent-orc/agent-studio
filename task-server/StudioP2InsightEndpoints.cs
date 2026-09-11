using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "operations and insight" bus, token usage, runtime event,
/// and token pricing bundle. All twelve frontend-facing routes are project-
/// scoped reads (project-scoped for bus/token-usage/runtime; token pricing
/// is a stateless calculation) over durable tables fed by three additional
/// fenced ingestion routes that are not part of the frontend route bundle:
/// they exist only so a Runner/engine principal has somewhere to report
/// these events, the same way <c>/api/v1/runs/{runId}/events</c> already
/// exists as plumbing behind the board. Ingestion is scoped
/// <see cref="TaskServerScopes.RunsWrite"/>, matching that existing route;
/// every read route is scoped <see cref="TaskServerScopes.TasksRead"/>
/// except the pricing calculator, which is a POST and therefore scoped
/// <see cref="TaskServerScopes.TasksWrite"/> per the P0 convention that POST
/// routes require a write scope even without a persisted side effect.
/// </summary>
public static class StudioP2InsightEndpoints
{
    public static void MapStudioP2InsightEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapBusEndpoints(studio);
        MapTokenUsageEndpoints(studio);
        MapRuntimeEndpoints(studio);
        MapTokenPricingEndpoints(studio);
        MapIngestionEndpoints(app);
    }

    private static void MapBusEndpoints(RouteGroupBuilder studio)
    {
        var bus = studio.MapGroup("/bus/{project}");

        bus.MapGet("/messages", async (
            string project,
            string? cli,
            string? correlationId,
            string? jobId,
            string? kind,
            int? limit,
            string? participantId,
            string? runId,
            string? severity,
            string? since,
            string? tag,
            string? until,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListBusMessagesAsync(
                project, cli, correlationId, jobId, kind, limit, participantId, runId, severity, since, tag, until, ct)));

        bus.MapGet("/messages/{id}", async (string project, string id, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetBusMessageAsync(project, id, ct)));

        bus.MapGet("/recent", async (string project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListRecentBusMessagesAsync(project, limit, ct)));

        bus.MapGet("/summary", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBusSummaryAsync(project, ct)));

        bus.MapGet("/token-aggregate", async (
            string project, string? since, string? until, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBusTokenAggregateAsync(project, since, until, ct)));
    }

    private static void MapTokenUsageEndpoints(RouteGroupBuilder studio)
    {
        var tokenUsage = studio.MapGroup("/projects/{project}/token-usage");

        tokenUsage.MapGet("/expensive", async (string project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetExpensiveTaskTokenUsageAsync(project, limit, ct)));

        tokenUsage.MapGet("/heatmap", async (string project, int? days, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageHeatmapAsync(project, days, ct)));

        tokenUsage.MapGet("/job/{taskId}", async (
            string project, string taskId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageForTaskAsync(project, taskId, ct)));

        tokenUsage.MapGet("/pipeline-cost", async (string project, int? days, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsagePipelineCostAsync(project, days, ct)));

        tokenUsage.MapGet("/summary", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageSummaryAsync(project, ct)));
    }

    private static void MapRuntimeEndpoints(RouteGroupBuilder studio)
    {
        // `refresh` has no cache to bypass in this projection; it is bound
        // and accepted so the frontend's existing query contract still
        // round-trips, but its value is otherwise ignored.
        studio.MapGet("/runtime/{project}/events", async (
            string project, bool? refresh, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListRuntimeEventsAsync(project, ct)));
    }

    private static void MapTokenPricingEndpoints(RouteGroupBuilder studio)
    {
        studio.MapPost("/token-pricing/calculate", (CalculateTokenPricingRequest request, TaskServerStore store) =>
        {
            try
            {
                return Results.Ok(store.CalculateTokenPricing(request));
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapIngestionEndpoints(WebApplication app)
    {
        var ingest = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        ingest.MapPost("/bus/{project}/messages/ingest", async (
            string project, BusMessageIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.IngestBusMessageAsync(project, request, ct), StatusCodes.Status201Created));

        ingest.MapPost("/token-usage/{project}/ingest", async (
            string project, TokenUsageIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.IngestTokenUsageAsync(project, request, ct), StatusCodes.Status201Created));

        ingest.MapPost("/runtime/{project}/events/ingest", async (
            string project, RuntimeEventIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.IngestRuntimeEventAsync(project, request, ct), StatusCodes.Status201Created));
    }
}
