using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Task-folder artefacts in the Task Server (pipeline delivery target,
/// section 05, AGT-3320): the workspace mirror of task files while the
/// workspace is still the write authority, and the integrated-card query that
/// release card attribution reads instead of scanning task folders.
/// </summary>
internal static class TaskArtifactsEndpoints
{
    public static void MapTaskArtifactsEndpoints(this IEndpointRouteBuilder app)
    {
        var project = app.MapGroup("/api/v1/projects/{projectId}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        project.MapPut("/task-files/workspace-import", async (
                HttpContext context, string projectId, TaskFileMirrorRequest request,
                TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.MirrorWorkspaceTaskFilesAsync(
                projectId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        project.MapGet("/integrated-cards", async (string projectId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListIntegratedCardsAsync(projectId, ct)));

        app.MapGet("/api/v1/workspace-export/status", (WorkspaceExportStatus status) => Results.Ok(status.Current))
            .RequireTaskServerScope(TaskServerScopes.OperationsInspect);
    }
}
