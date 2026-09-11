using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "design council, proposals, visual evidence, skill
/// readiness, project snapshot" route bundle. Every route is project-scoped
/// under <c>/api/v1/studio/projects/{project}</c>; GET routes require
/// <see cref="TaskServerScopes.TasksRead"/> and every route with a side
/// effect (a dispatch, a write, or a delete) requires
/// <see cref="TaskServerScopes.TasksWrite"/>, matching the P0 convention.
/// </summary>
public static class StudioP2DesignEndpoints
{
    public static void MapStudioP2DesignEndpoints(this WebApplication app)
    {
        var projects = app.MapGroup("/api/v1/studio/projects/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapDesignEndpoints(projects);
        MapProposalsEndpoints(projects);
        MapSkillReadinessEndpoints(projects);
        MapSnapshotEndpoints(projects);
        MapVisualEvidenceEndpoints(projects);
    }

    private static void MapDesignEndpoints(RouteGroupBuilder projects)
    {
        projects.MapPost("/design/actions/{action}", async (
            string project, string action, DesignActionDispatchRequest? request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DispatchDesignActionAsync(project, action, request ?? new DesignActionDispatchRequest(), ct),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapGet("/design/council", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignCouncilAsync(project, ct)));

        projects.MapGet("/design/council/{fileName}", async (
            string project, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignCouncilEntryAsync(project, fileName, ct)));

        projects.MapPost("/design/council/{fileName}/accept", async (
            HttpContext context, string project, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AcceptDesignCouncilEntryAsync(project, fileName, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapGet("/design/overview", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignOverviewAsync(project, ct)));

        projects.MapGet("/design/references", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignReferencesAsync(project, ct)));
    }

    private static void MapProposalsEndpoints(RouteGroupBuilder projects)
    {
        projects.MapDelete("/proposals", async (
            string project, long? keepGeneration, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async ()
                => new { deleted = await store.DeleteProposalsAsync(project, keepGeneration, ct) }))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapDelete("/proposals/{proposalId}", async (
            string project, string proposalId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProposalAsync(project, proposalId, ct);
                return new { deleted = true, proposalId };
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPost("/proposals/{proposalId}/decision", async (
            string project, string proposalId, DecideProposalRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.DecideProposalAsync(project, proposalId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPost("/proposals/generate", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GenerateProposalsAsync(project, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapPost("/proposals/refine-feedback", async (
            string project, RefineProposalsFeedbackRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RefineProposalsFeedbackAsync(project, request, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapSkillReadinessEndpoints(RouteGroupBuilder projects)
    {
        projects.MapPost("/skill-readiness/fix-task", async (
            string project, FixSkillReadinessTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.FixSkillReadinessTaskAsync(project, request, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapSnapshotEndpoints(RouteGroupBuilder projects)
    {
        projects.MapGet("/snapshot", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectSnapshotAsync(project, ct)));
    }

    private static void MapVisualEvidenceEndpoints(RouteGroupBuilder projects)
    {
        projects.MapGet("/visual-evidence", async (string project, bool? refresh, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListVisualEvidenceAsync(project, ct)));

        projects.MapPost("/visual-evidence/{itemId}/acknowledge", async (
            string project, string itemId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.AcknowledgeVisualEvidenceAsync(project, itemId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }
}

/// <summary>
/// Materializes proposal rows once a <see cref="StudioOperationKinds.ProposalsGenerate"/>
/// or <see cref="StudioOperationKinds.ProposalsRefineFeedback"/> studio
/// operation succeeds. Registered in DI by
/// <see cref="StudioP2DesignServiceCollectionExtensions.AddStudioP2DesignServices"/>;
/// invoked by the shared studio-operation completion route in
/// <c>StudioOperationsEndpoints.cs</c>. The actual row-materialization logic
/// lives in <see cref="TaskServerStore.ApplyGeneratedProposalsAsync"/> so it
/// can be unit tested and reused without a DI container.
/// </summary>
public sealed class ProposalsCompletionProjector : IStudioOperationCompletionProjector
{
    public bool Handles(string kind)
        => kind == StudioOperationKinds.ProposalsGenerate || kind == StudioOperationKinds.ProposalsRefineFeedback;

    public Task OnCompletedAsync(StudioOperationDto operation, TaskServerStore store, CancellationToken ct)
        => store.ApplyGeneratedProposalsAsync(operation, ct);
}
