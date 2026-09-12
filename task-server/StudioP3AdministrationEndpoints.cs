using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P3 "administration and long tail" bundle
/// (docs/studio-route-ownership/index.html): 24 read-only routes closing out
/// every remaining <c>administration-and-tail</c> operation once P0-P2
/// covered the rest of the inventory. Every route here has no side effect,
/// so the whole group requires <see cref="TaskServerScopes.TasksRead"/>,
/// matching the P0 convention. No new table or migration - see
/// <c>TaskServerStudioP3AdministrationStore.cs</c>.
/// </summary>
public static class StudioP3AdministrationEndpoints
{
    public static void MapStudioP3AdministrationEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        studio.MapGet("/admin/config/orchestrator", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetOrchestratorConfigAsync(ct)));

        studio.MapGet("/admin/prompts", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListPromptOverridesAsync(ct)));

        studio.MapGet("/admin/prompts/coverage", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPromptOverrideCoverageAsync(ct)));

        studio.MapGet("/admin/prompts/{name}", async (string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetPromptOverrideAsync(name, ct)));

        studio.MapGet("/auto-review/status", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAutoReviewStatusAsync(ct)));

        studio.MapGet("/cli/model-routing/policy", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetModelRoutingPolicyAsync(ct)));

        studio.MapGet("/cli/model-routing/recommendation", async (
            string? taskType, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetModelRoutingRecommendationAsync(taskType, ct)));

        studio.MapGet("/cli/quota/caps", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliSettingsAsync(ct)));

        studio.MapGet("/cli/quota/model-routes", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliSettingsAsync(ct)));

        studio.MapGet("/cli/quota/wait-policy", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCliSettingsAsync(ct)));

        studio.MapGet("/watch-paths", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListWatchPathsAsync(ct)));

        studio.MapGet("/search", async (string? q, string? project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.SearchTasksAsync(project, q, limit, ct)));

        var projects = studio.MapGroup("/projects");

        projects.MapGet("/pipeline-catalogue", async (
            string? pipelineType, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPipelineCatalogueAsync(pipelineType, ct)));

        projects.MapGet("/settings", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListAllProjectSettingsAsync(ct)));

        var project = projects.MapGroup("/{project}");

        project.MapGet("/cli-context-modes", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCliContextModeAsync(project, ct)));

        project.MapGet("/cli-modes", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCliModeAsync(project, ct)));

        project.MapGet("/lane-sort-strategies", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectLaneSortStrategyAsync(project, ct)));

        project.MapGet("/quota-wait-policy", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectQuotaWaitPolicyAsync(project, ct)));

        project.MapGet("/review-decisions-pending", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetReviewDecisionsPendingAsync(project, ct)));

        project.MapGet("/wiki/grading/status", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetWikiGradingStatusAsync(project, ct)));

        project.MapGet("/proposals", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListStudioProposalsAsync(project, ct)));

        project.MapGet("/proposals/evidence/{**path}", async (
            string project, string path, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetProposalEvidenceAsync(project, path, ct)));

        project.MapGet("/publish/{targetId}/panel", async (
            string project, string targetId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPublishPanelAsync(project, targetId, ct)));

        project.MapGet("/publish/{targetId}/run", async (
            string project, string targetId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPublishRunAsync(project, targetId, ct)));
    }
}
