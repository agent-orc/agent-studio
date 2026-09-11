using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The P1 "task metadata" bundle: single-field task mutations. Mirrors
/// <c>StudioEndpoints.MapTaskLifecycleEndpoints</c>'s group-plus-per-route-scope
/// pattern exactly, including the unscoped-project routing rule: every route
/// here is reachable as <c>/api/v1/projects/-/tasks/{taskId}/...</c> when the
/// connector has no project id to forward for a legacy single-parameter
/// frontend call, and resolves the task by id alone in that case.
/// </summary>
internal static class StudioTaskMetadataEndpoints
{
    internal static void MapStudioTaskMetadataEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapPost("/change-project", async (
            string projectId, string taskIdentity, HttpContext context, ChangeTaskProjectRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ChangeTaskProjectAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/cli-type", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskCliTypeRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskCliTypeAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/epic", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskEpicRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskEpicAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/model", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskModelRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskModelAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/references", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskReferencesRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskReferencesAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/release", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskReleaseRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskReleaseAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/tags", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskTagsRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskTagsAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/task-type", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskTaskTypeRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskTaskTypeAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/thinking-level", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskThinkingLevelRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetTaskThinkingLevelAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        // title is a real column on tasks and already has an ExpectedVersion
        // update path (UpdateTaskAsync); this route reuses it unchanged with
        // only Title set, so Body/State are left null and stay unchanged.
        tasks.MapPut("/title", async (
            string projectId, string taskIdentity, HttpContext context, SetTaskTitleRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(
                () => store.UpdateTaskAsync(
                    projectId,
                    taskIdentity,
                    new UpdateTaskRequest(request.Title, null, null, request.ExpectedVersion),
                    TaskServerEndpoints.Actor(context),
                    ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }
}
