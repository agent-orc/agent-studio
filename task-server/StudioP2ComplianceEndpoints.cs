using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "security review, deployment, publish, and wiki grading"
/// bundle: 11 project-scoped routes under
/// <c>/api/v1/studio/projects/{project}/**</c>. Security audits, deployment
/// compiles, and publish package/website runs dispatch a fenced studio
/// operation and return <see cref="StudioOperationAcceptedResponse"/> with
/// 202; publish automation is a plain settings toggle; wiki grading pairs a
/// dispatch action with an idempotent abort.
/// </summary>
public static class StudioP2ComplianceEndpoints
{
    public static void MapStudioP2ComplianceEndpoints(this WebApplication app)
    {
        var projects = app.MapGroup("/api/v1/studio/projects/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        var security = projects.MapGroup("/security");
        security.MapPost("/audit", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => AcceptedAsync(store.RequestSecurityAuditAsync(project, TaskServerEndpoints.Actor(context), ct)),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        security.MapGet("/baseline", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSecurityBaselineAsync(project, ct)));

        security.MapGet("/reviews", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListSecurityReviewsAsync(project, ct)));

        security.MapGet("/reviews/{fileName}", async (
            string project, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSecurityReviewByFileNameAsync(project, fileName, ct)));

        var deployment = projects.MapGroup("/deployment");
        deployment.MapPost("/compile", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => AcceptedAsync(store.RequestDeploymentCompileAsync(project, TaskServerEndpoints.Actor(context), ct)),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        deployment.MapGet("/summary", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDeploymentSummaryAsync(project, ct)));

        var publish = projects.MapGroup("/publish");
        publish.MapPut("/automation", async (
            string project, HttpContext context, SetPublishAutomationRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetPublishAutomationAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        publish.MapPost("/package", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => AcceptedAsync(store.RequestPublishPackageAsync(project, TaskServerEndpoints.Actor(context), ct)),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        publish.MapPost("/website", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => AcceptedAsync(store.RequestPublishWebsiteAsync(project, TaskServerEndpoints.Actor(context), ct)),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var wiki = projects.MapGroup("/wiki/grading");
        wiki.MapPost("/run", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => AcceptedAsync(store.RequestWikiGradingRunAsync(project, TaskServerEndpoints.Actor(context), ct)),
                StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        wiki.MapPost("/abort", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.AbortWikiGradingRunAsync(project, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static async Task<StudioOperationAcceptedResponse> AcceptedAsync(Task<StudioOperationDto> operationTask)
    {
        var operation = await operationTask;
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }
}
