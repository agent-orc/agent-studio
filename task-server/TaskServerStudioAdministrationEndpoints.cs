using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The 16 "administration and long tail" (P3) routes resolved to a real v1
/// contract: orchestrator/prompt admin config, auto-review status, CLI
/// quota/model-routing policy, per-project CLI/lane-sort settings, the
/// pipeline catalogue, and the task-results half of global search. See
/// <c>docs/studio-route-ownership/index.html</c> section 07 for the full
/// per-route disposition (the other 8 P3 routes were reclassified dev-seat or
/// retired instead of ported here).
/// </summary>
public static class TaskServerStudioAdministrationEndpoints
{
    public static void MapStudioAdministrationEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        var admin = studio.MapGroup("/admin");
        admin.MapGet("/config/orchestrator", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetOrchestratorConfigSnapshotAsync(ct)));
        admin.MapGet("/prompts", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPromptCatalogAsync(ct)));
        admin.MapGet("/prompts/{name}", async (string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPromptDetailAsync(name, ct)));

        studio.MapGet("/auto-review/status", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAutoReviewStatusAsync(ct)));

        var cli = studio.MapGroup("/cli");
        cli.MapGet("/model-routing/policy", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetModelRoutingPolicyAsync(ct)));
        cli.MapGet("/model-routing/recommendation", async (
            string taskType, string cliType, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetModelRoutingRecommendationAsync(taskType, cliType, ct)));
        cli.MapGet("/quota/caps", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliQuotaCapsAsync(ct)));
        cli.MapGet("/quota/model-routes", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliModelRoutesAsync(ct)));
        cli.MapGet("/quota/wait-policy", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliQuotaWaitPolicyAsync(ct)));

        var projects = studio.MapGroup("/projects");
        projects.MapGet("/pipeline-catalogue", async (
            string? projectName, string? pipelineType, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPipelineCatalogueAsync(projectName, pipelineType, ct)));
        projects.MapGet("/settings", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAllProjectSettingsAsync(ct)));
        projects.MapGet("/{project}/cli-context-modes", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCliContextModesAsync(project, ct)));
        projects.MapGet("/{project}/cli-modes", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCliModesAsync(project, ct)));
        projects.MapGet("/{project}/lane-sort-strategies", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetLaneSortStrategiesAsync(project, ct)));
        projects.MapGet("/{project}/quota-wait-policy", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectQuotaWaitPolicyAsync(project, ct)));

        studio.MapGet("/search", async (string? q, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.SearchTasksAsync(q, limit ?? 20, ct)));
    }
}
