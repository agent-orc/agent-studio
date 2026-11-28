using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The P1 "task lifecycle extras" bundle: task creation from the unscoped
/// legacy body shape, the reverse task-reference lookup, bulk
/// reorder/batch-move, the archive listing, and the task-lifecycle side
/// actions (concept dossier, context-usage refresh, integration rebase,
/// planning closure, promote-concept, promote-to-coding). Task-scoped routes
/// follow the same unscoped-project-token resolution as
/// <c>StudioEndpoints.MapTaskLifecycleEndpoints</c>; the rest live under
/// <c>/api/v1/studio/tasks</c>.
/// </summary>
internal static class StudioTaskLifecycleExtrasEndpoints
{
    internal static void MapStudioTaskLifecycleExtrasEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapGet("/dependents", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTaskDependentsAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/promote-concept", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPromoteConceptStatusAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/promote-to-coding", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPromoteToCodingStatusAsync(projectId, taskIdentity, ct)));

        tasks.MapPost("/concept-dossier", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            ConceptDossierRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RequestConceptDossierAsync(
                projectId, taskIdentity, request ?? new ConceptDossierRequest(), TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/context-usage/refresh", async (
            string projectId, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RefreshContextUsageAsync(
                projectId, taskIdentity, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/integration/rebase", async (
            string projectId, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RequestIntegrationRebaseAsync(
                projectId, taskIdentity, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/planning-closure", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            PlanningClosureRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.SetPlanningClosureAsync(
                projectId, taskIdentity, request ?? new PlanningClosureRequest(false), TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/promote-concept", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            PromoteConceptRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RequestPromoteConceptAsync(
                projectId, taskIdentity, request ?? new PromoteConceptRequest(), TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/magazine-deliverable/preview", (HttpContext context, string projectId, string taskIdentity,
            AttachMagazineDeliverableRequest request, TaskServerStore store, TaskServerBootstrapOptions options, CancellationToken ct)
            => AttachMagazine(context, projectId, taskIdentity, request, store, options, ct, previewOnly: true))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/magazine-deliverable", (HttpContext context, string projectId, string taskIdentity,
            AttachMagazineDeliverableRequest request, TaskServerStore store, TaskServerBootstrapOptions options, CancellationToken ct)
            => AttachMagazine(context, projectId, taskIdentity, request, store, options, ct, previewOnly: false))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var studioTasks = app.MapGroup("/api/v1/studio/tasks")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        studioTasks.MapPost("", async (
            HttpContext context, StudioCreateTaskRequest request, TaskServerStore store,
            TaskServerBootstrapOptions options, CancellationToken ct)
            => await CreateStudioCard(context, request, store, options, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        studioTasks.MapGet("/archive", async (
            string? project, int? offset, int? limit, string? search, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetArchivedTasksAsync(project, offset ?? 0, limit ?? 50, search, ct)));

        studioTasks.MapPost("/batch-move", async (
            HttpContext context,
            BatchMoveRequest request,
            TaskServerStore store,
            StudioLifecycleCoordinator coordinator,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.StartBatchMoveAsync(request, TaskServerEndpoints.Actor(context), coordinator, ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        studioTasks.MapGet("/batch-move/{batchId}", async (
            string batchId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetBatchMoveAsync(batchId, ct)));

        studioTasks.MapPost("/reference-status", async (
            ReferenceStatusRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetReferenceStatusesAsync(request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        studioTasks.MapPost("/reorder", async (
            HttpContext context, ReorderTasksRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ReorderTasksAsync(request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    internal static Task<IResult> CreateStudioCard(HttpContext context, StudioCreateTaskRequest request,
        TaskServerStore store, TaskServerBootstrapOptions options, CancellationToken ct)
    {
        var actor = request.MagazineDeliverable is null ? TaskServerEndpoints.Actor(context) : MagazineAuthor(context, options);
        return actor is null
            ? Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden))
            : TaskServerEndpoints.CreateTaskAsync(context, request.CompatibilityFields,
                async () =>
                {
                    var created = await store.CreateStudioTaskAsync(request, actor, MagazineUser(context), ct);
                    return created with { Id = created.TaskId };
                });
    }

    private static Task<IResult> AttachMagazine(HttpContext context, string projectId, string taskIdentity,
        AttachMagazineDeliverableRequest request, TaskServerStore store, TaskServerBootstrapOptions options,
        CancellationToken ct, bool previewOnly)
        => MagazineAuthor(context, options) is { } actor
            ? TaskServerEndpoints.InvokeAsync(() => store.AttachMagazineDeliverableAsync(
                projectId, taskIdentity, request, actor, MagazineUser(context), previewOnly, ct))
            : Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden));

    private static StudioAuthUserDto? MagazineUser(HttpContext context)
        => context.Items[typeof(StudioAuthUserDto)] as StudioAuthUserDto;

    private static string? MagazineAuthor(HttpContext context, TaskServerBootstrapOptions options)
    {
        if (MagazineUser(context) is { } user)
            return user.Role is StudioUserRoles.Owner or StudioUserRoles.Operator && !user.MustChangePassword
                ? user.UserId : null;
        if (context.TaskServerPrincipal() is { Kind: TaskServerPrincipalKinds.Studio } principal
            && principal.Scopes.Contains(TaskServerScopes.Management))
        {
            // The management credential is the workspace-wide authority. A trusted Studio
            // edge may attest the narrower MagazineAccess check and its authenticated author.
            var delegated = context.Request.Headers[MagazineDeliverableContract.EdgeAuthorHeader].FirstOrDefault();
            return string.IsNullOrWhiteSpace(delegated) ? principal.PrincipalId : delegated;
        }
        return options.RequiresAuthentication ? null : "local-operator";
    }
}
