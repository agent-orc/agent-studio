using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The P1 "G3_RunnerOrchestrator" route bundle: raw-context-key orchestrator
/// chat, per-project runner mode/start/stop, the orchestrator log and its
/// human override channel, pending decisions, token usage summaries (live
/// and TTL-cached), the cross-project orchestrator activity feed, the
/// auto-review queue projection, and queue starvation detection. Mirrors
/// <see cref="StudioEndpoints"/>'s group/scope conventions exactly: default
/// <see cref="TaskServerScopes.TasksRead"/> on the group, escalated to
/// <see cref="TaskServerScopes.TasksWrite"/> on every non-GET endpoint.
///
/// Does NOT redeclare <c>/api/v1/studio/runner/status</c> or
/// <c>/api/v1/studio/runner/{project}/orchestrator-chat</c> (+ its
/// attachments sub-routes) - those are P0's project-scoped routes, already
/// mapped by <c>StudioEndpoints.MapRunnerEndpoints</c>. The context-key-scoped
/// chat routes below reuse the same literal-prefix-plus-parameter route shape
/// P0's orchestrator digest routes use (<c>/global</c>, <c>/project:{id}</c>,
/// <c>/task:{projectId}/{taskId}</c>, see
/// <c>StudioEndpoints.MapOrchestratorEndpoints</c>) rather than a single
/// generic <c>{contextKey}</c> parameter: a bare single-parameter segment
/// there would collide with (and be ambiguous against) P0's bare
/// <c>{project}/orchestrator-chat</c> route, since both are GET/POST on an
/// identically-shaped one-parameter-plus-literal template. The three
/// literal-prefixed templates are strictly more specific than P0's bare
/// parameter route, so only requests actually shaped like a composite context
/// key reach these handlers; anything else still falls through to P0's
/// project-scoped route unchanged.
/// </summary>
internal static class StudioRunnerOrchestratorEndpoints
{
    internal static void MapStudioRunnerOrchestratorEndpoints(this WebApplication app)
    {
        var runner = app.MapGroup("/api/v1/studio/runner")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapContextKeyOrchestratorChatEndpoints(runner);
        MapModeAndLifecycleEndpoints(runner);
        MapOrchestratorLogEndpoints(runner);
        MapOrchestratorSessionEndpoints(runner);
        MapPendingDecisionsEndpoints(runner);
        MapTokenSummaryEndpoints(runner);
        MapGlobalReadEndpoints(runner);
    }

    private static void MapContextKeyOrchestratorChatEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/global/orchestrator-chat", (HttpContext context, TaskServerStore store, CancellationToken ct)
            => GetContextKeyChatAsync("global", context, store, ct));
        runner.MapGet("/project:{projectIdentity}/orchestrator-chat", (
            string projectIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => GetContextKeyChatAsync($"project:{projectIdentity}", context, store, ct));
        runner.MapGet("/task:{projectIdentity}/{taskIdentity}/orchestrator-chat", (
            string projectIdentity, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => GetContextKeyChatAsync($"task:{projectIdentity}/{taskIdentity}", context, store, ct));

        runner.MapPost("/global/orchestrator-chat", (
            HttpContext context, StudioOrchestratorChatMessageRequest request, TaskServerStore store, CancellationToken ct)
            => PostContextKeyChatAsync("global", request, context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        runner.MapPost("/project:{projectIdentity}/orchestrator-chat", (
            string projectIdentity, HttpContext context, StudioOrchestratorChatMessageRequest request,
            TaskServerStore store, CancellationToken ct)
            => PostContextKeyChatAsync($"project:{projectIdentity}", request, context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        runner.MapPost("/task:{projectIdentity}/{taskIdentity}/orchestrator-chat", (
            string projectIdentity, string taskIdentity, HttpContext context,
            StudioOrchestratorChatMessageRequest request, TaskServerStore store, CancellationToken ct)
            => PostContextKeyChatAsync($"task:{projectIdentity}/{taskIdentity}", request, context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static Task<IResult> GetContextKeyChatAsync(
        string rawContextKey, HttpContext context, TaskServerStore store, CancellationToken ct)
        => TaskServerEndpoints.InvokeAsync(
            () => store.GetRunnerOrchestratorChatByContextKeyAsync(rawContextKey, TaskServerEndpoints.Actor(context), ct));

    private static Task<IResult> PostContextKeyChatAsync(
        string rawContextKey, StudioOrchestratorChatMessageRequest request, HttpContext context, TaskServerStore store,
        CancellationToken ct)
        => TaskServerEndpoints.InvokeAsync(
            () => store.SendRunnerOrchestratorChatMessageByContextKeyAsync(
                rawContextKey, request, TaskServerEndpoints.Actor(context), ct));

    private static void MapModeAndLifecycleEndpoints(RouteGroupBuilder runner)
    {
        runner.MapPut("/{project}/mode", async (
            string project, HttpContext context, UpdateRunnerModeRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateRunnerModeAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        runner.MapPost("/{project}/start", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.StartRunnerAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        runner.MapPost("/{project}/stop", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.StopRunnerAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapOrchestratorLogEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/{project}/orchestrator-log", async (
            string project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListOrchestratorLogAsync(project, limit, ct)));

        runner.MapPost("/{project}/orchestrator-log/override", async (
            string project, HttpContext context, OrchestratorLogOverrideRequest request, TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AppendOrchestratorLogOverrideAsync(project, request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapOrchestratorSessionEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/global/orchestrator-session", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetGlobalRunnerOrchestratorSessionAsync(ct)));

        runner.MapGet("/{project}/orchestrator-session", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetRunnerOrchestratorSessionAsync(project, ct)));
    }

    private static void MapPendingDecisionsEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/{project}/pending-decisions", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListPendingDecisionsAsync(project, ct)));
    }

    private static void MapTokenSummaryEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/{project}/token-summary", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetRunnerTokenSummaryAsync(project, ct)));

        runner.MapGet("/token-summary-aggregate", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetRunnerTokenSummaryAggregateAsync(ct)));

        runner.MapGet("/token-summary-aggregate/cached", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetRunnerTokenSummaryAggregateCachedAsync(ct)));
    }

    private static void MapGlobalReadEndpoints(RouteGroupBuilder runner)
    {
        runner.MapGet("/auto-review-queue", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAutoReviewQueueAsync(ct)));

        runner.MapGet("/orchestrator-feed", async (int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetOrchestratorFeedAsync(limit, ct)));

        runner.MapGet("/queue-starvation", async (int? windowMinutes, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetQueueStarvationAsync(windowMinutes, ct)));
    }
}
