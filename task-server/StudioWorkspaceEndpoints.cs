using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio P1 "workspace" bundle (G9_Workspace): workspace-wide aggregates
/// under <c>/api/v1/studio/workspace</c> (singular) and per-row workspace
/// CRUD/extension-settings routes under <c>/api/v1/studio/workspaces</c>
/// (plural). Wired into <c>Program.cs</c> via <c>app.MapStudioWorkspaceEndpoints()</c>
/// alongside the other P1 bundles.
/// </summary>
internal static class StudioWorkspaceEndpoints
{
    internal static void MapStudioWorkspaceEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapWorkspaceAggregateEndpoints(studio);
        MapWorkspaceCrudEndpoints(studio);
    }

    private static void MapWorkspaceAggregateEndpoints(RouteGroupBuilder studio)
    {
        var workspace = studio.MapGroup("/workspace");

        workspace.MapGet("/screenshots", async (
            string? workspaceId, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetWorkspaceScreenshotsAsync(workspaceId, limit ?? 50, ct)));

        workspace.MapGet("/summary", async (
            string? workspaceId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetWorkspaceSummaryAsync(workspaceId, ct)));

        workspace.MapGet("/tokens/expensive-jobs", async (
            string? workspaceId, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetWorkspaceExpensiveJobsAsync(workspaceId, limit ?? 20, ct)));

        workspace.MapGet("/tokens/expensive-jobs/cached", async (
            string? workspaceId, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetWorkspaceExpensiveJobsCachedAsync(workspaceId, limit ?? 20, ct)));

        workspace.MapGet("/tokens/timeline", async (
            string? workspaceId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetWorkspaceTokenTimelineAsync(workspaceId, ct)));

        workspace.MapGet("/tokens/timeline/cached", async (
            string? workspaceId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetWorkspaceTokenTimelineCachedAsync(workspaceId, ct)));
    }

    private static void MapWorkspaceCrudEndpoints(RouteGroupBuilder studio)
    {
        var workspaces = studio.MapGroup("/workspaces");

        workspaces.MapPut("/{id}", async (
            HttpContext context, string id, UpdateWorkspaceRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(
                () => store.UpdateWorkspaceAsync(id, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        workspaces.MapDelete("/{id}", async (
            HttpContext context, string id, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(
                () => store.DeleteWorkspaceAsync(id, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        workspaces.MapPost("/{id}/reorder", async (
            HttpContext context, string id, ReorderWorkspacesRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ReorderWorkspacesAsync(id, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        workspaces.MapPut("/{workspaceId}/autonomy", async (
            HttpContext context, string workspaceId, UpdateWorkspaceAutonomyRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateWorkspaceAutonomyAsync(workspaceId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        workspaces.MapPut("/{workspaceId}/orchestrator-model", async (
            HttpContext context, string workspaceId, UpdateWorkspaceOrchestratorModelRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateWorkspaceOrchestratorModelAsync(workspaceId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        workspaces.MapGet("/{workspaceId}/settings", async (
            string workspaceId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetWorkspaceStudioSettingsAsync(workspaceId, ct)));
    }
}
