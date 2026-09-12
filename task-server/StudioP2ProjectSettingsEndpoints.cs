using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "project settings, project CRUD, ownership mappings,
/// project URLs" bundle: 16 routes under <c>/api/v1/studio/projects/**</c>,
/// all mutations. Route param spellings <c>{id}</c> and <c>{projectId}</c>
/// used by different frontend call sites against the same rename/update
/// endpoint are served by a single mapped route.
/// </summary>
public static class StudioP2ProjectSettingsEndpoints
{
    public static void MapStudioP2ProjectSettingsEndpoints(this WebApplication app)
    {
        var projects = app.MapGroup("/api/v1/studio/projects")
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPut("/{project}/auto-commit", async (
            string project, SetAutoCommitRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetAutoCommitAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/auto-push-strategy", async (
            string project, SetAutoPushStrategyRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetAutoPushStrategyAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/cli-context-mode", async (
            string project, SetCliContextModeRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetCliContextModeAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/cli-mode", async (
            string project, SetCliModeRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetCliModeAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/crash-recovery", async (
            string project, SetCrashRecoveryRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetCrashRecoveryAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/lane-sort-strategy", async (
            string project, SetLaneSortStrategyRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetLaneSortStrategyAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/max-parallelism", async (
            string project, SetMaxParallelismRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetMaxParallelismAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/orchestrator-model", async (
            string project, SetOrchestratorModelRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetOrchestratorModelAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPut("/{project}/quota-wait-policy", async (
            string project, SetQuotaWaitPolicyRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetQuotaWaitPolicyAsync(project, request, TaskServerEndpoints.Actor(context), ct)));

        // Both /api/projects/{id} and /api/projects/{projectId} frontend call
        // sites target this identical handler; ASP.NET Core route parameter
        // names need not match the frontend's, so this single mapping serves
        // both call sites (registering the same route pattern twice throws).
        projects.MapPut("/{id}", async (
            string id, UpdateStudioProjectRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateProjectAsync(id, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapDelete("/{projectId}", async (
            string projectId, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProjectAsync(projectId, TaskServerEndpoints.Actor(context), ct);
                return new { deleted = true, projectId };
            }));

        projects.MapPut("/{projectId}/ownership-mappings/{mappingId}", async (
            string projectId,
            string mappingId,
            UpsertProjectOwnershipMappingRequest request,
            TaskServerStore store,
            HttpContext context,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpsertProjectOwnershipMappingAsync(
                    projectId, mappingId, request, TaskServerEndpoints.Actor(context), ct)));

        projects.MapPost("/{projectId}/urls", async (
            string projectId, CreateProjectUrlRequest request, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateProjectUrlAsync(projectId, request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created));

        projects.MapDelete("/{projectId}/urls/{urlId}", async (
            string projectId, string urlId, TaskServerStore store, HttpContext context, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProjectUrlAsync(projectId, urlId, TaskServerEndpoints.Actor(context), ct);
                return new { deleted = true, urlId };
            }));

        projects.MapPut("/{projectId}/urls/{urlId}", async (
            string projectId,
            string urlId,
            UpdateProjectUrlRequest request,
            TaskServerStore store,
            HttpContext context,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateProjectUrlAsync(projectId, urlId, request, TaskServerEndpoints.Actor(context), ct)));
    }
}
