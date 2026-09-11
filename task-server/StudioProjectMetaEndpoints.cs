using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The G2 "project meta" P1 bundle: epics, tags, project autonomy and
/// execution-runner settings, and pipeline projections. Domain logic lives
/// on the <see cref="TaskServerStore"/> partial in
/// <c>TaskServerStudioProjectMetaStore.cs</c>; this file only maps routes,
/// mirroring the style of <c>StudioEndpoints.cs</c> from the P0 bundle.
/// </summary>
internal static class StudioProjectMetaEndpoints
{
    public static void MapStudioProjectMetaEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapEpicEndpoints(studio);
        MapTagEndpoints(studio);
        MapProjectMetaEndpoints(studio);
    }

    private static void MapEpicEndpoints(RouteGroupBuilder studio)
    {
        var epics = studio.MapGroup("/epics");

        // Declared before "/{epicId}" for readability; ASP.NET's endpoint
        // routing already prefers the more specific literal segments over a
        // route parameter regardless of declaration order.
        epics.MapGet("/completed/count", async (
            string? project, bool? includeFixtures, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async ()
                => new EpicCompletedCountResponse(await store.CountCompletedEpicsAsync(project, ct))));

        epics.MapGet("", async (
            string? project, string? status, bool? includeFixtures, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async ()
                => new EpicListResponse(await store.ListEpicsAsync(project, status, ct))));

        epics.MapGet("/{epicId}", async (string epicId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetEpicAsync(epicId, ct)));

        epics.MapPost("/{epicId}/sub-tasks", async (
            string epicId, HttpContext context, CreateTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateEpicSubTaskAsync(epicId, request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapTagEndpoints(RouteGroupBuilder studio)
    {
        var tags = studio.MapGroup("/tags");

        tags.MapGet("", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () => new TagListResponse(await store.ListTagsAsync(ct))));

        tags.MapPost("", async (HttpContext context, CreateTagRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateTagAsync(request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tags.MapDelete("/{id}", async (string id, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteTagAsync(id, TaskServerEndpoints.Actor(context), ct);
                return new DeleteTagResponse(true, id);
            }))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapProjectMetaEndpoints(RouteGroupBuilder studio)
    {
        var projects = studio.MapGroup("/projects/{project}");

        projects.MapGet("/autonomy", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectAutonomyAsync(project, ct)));

        projects.MapPut("/autonomy", async (
            string project, HttpContext context, UpdateProjectAutonomyRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateProjectAutonomyAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPut("/execution-runner", async (
            string project, HttpContext context, UpdateProjectExecutionRunnerRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateProjectExecutionRunnerAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapGet("/pipeline-health", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPipelineHealthAsync(project, ct)));

        projects.MapPut("/pipeline-step", async (
            string project, HttpContext context, UpsertPipelineStepRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpsertPipelineStepAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPut("/pipeline-step-order", async (
            string project, HttpContext context, UpdatePipelineStepOrderRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdatePipelineStepOrderAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPost("/pipeline-steps/{stepId}/probe", async (
            string project, string stepId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ProbePipelineStepAsync(project, stepId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }
}
