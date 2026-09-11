using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "admin/prompts/utility" bundle: the orchestrator's global
/// config singleton, named prompt overrides (upsert/delete/preview/
/// rebaseline/review), the static component-routing resolver, and the two
/// fenced Runner-dispatch actions (prompt enhancement and title
/// generation). None of these routes are project-scoped. Every route here
/// has a side effect (a write, a persisted review annotation, or a fenced
/// dispatch), so the whole group requires <see cref="TaskServerScopes.TasksWrite"/>,
/// matching the P0 convention that <c>TasksRead</c> is reserved for routes
/// with no side effect.
/// </summary>
public static class StudioP2AdminEndpoints
{
    public static void MapStudioP2AdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        admin.MapPut("/admin/config/orchestrator", async (
            HttpContext context, UpdateOrchestratorConfigRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpsertOrchestratorConfigAsync(request, TaskServerEndpoints.Actor(context), ct)));

        admin.MapDelete("/admin/prompts/{name}", async (
            HttpContext context, string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeletePromptOverrideAsync(name, TaskServerEndpoints.Actor(context), ct);
                return new { deleted = true, name };
            }));

        admin.MapPut("/admin/prompts/{name}", async (
            HttpContext context, string name, UpdatePromptOverrideRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpsertPromptOverrideAsync(name, request, TaskServerEndpoints.Actor(context), ct)));

        admin.MapPost("/admin/prompts/{name}/preview", async (
            string name, PreviewPromptOverrideRequest? request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PreviewPromptOverrideAsync(name, request ?? new PreviewPromptOverrideRequest(), ct)));

        admin.MapPost("/admin/prompts/{name}/rebaseline", async (
            HttpContext context, string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RebaselinePromptOverrideAsync(name, TaskServerEndpoints.Actor(context), ct)));

        admin.MapPost("/admin/prompts/{name}/review", async (
            HttpContext context, string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ReviewPromptOverrideAsync(name, TaskServerEndpoints.Actor(context), ct)));

        admin.MapPost("/admin/prompts/review-all", async (
            HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ReviewAllPromptOverridesAsync(TaskServerEndpoints.Actor(context), ct)));

        admin.MapPost("/component-routing/resolve", (
            ResolveComponentRoutingRequest request, TaskServerStore store)
            => Results.Ok(store.ResolveComponentRouting(request)));

        admin.MapPost("/prompt/enhance", async (
            EnhancePromptRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var operation = await store.CreateStudioOperationAsync(
                    StudioOperationKinds.PromptEnhance, null, null, request, null, null, null, ct);
                return new StudioOperationAcceptedResponse(
                    operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
            }, StatusCodes.Status202Accepted));

        admin.MapPost("/title/generate", async (
            GenerateTitleRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var operation = await store.CreateStudioOperationAsync(
                    StudioOperationKinds.TitleGenerate, null, null, request, null, null, null, ct);
                return new StudioOperationAcceptedResponse(
                    operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
            }, StatusCodes.Status202Accepted));
    }
}
