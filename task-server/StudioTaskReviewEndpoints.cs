using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The P1 "G8_TaskReview" Studio route bundle: task-level code review
/// findings, pipeline status/step-run requests, the regression radar, review
/// evidence acknowledgement/follow-up, and durable task summaries. Every
/// task-scoped route lives under <c>/api/v1/projects/{projectId}/tasks/{taskIdentity}</c>,
/// mirroring <c>StudioEndpoints.MapTaskLifecycleEndpoints</c>; the code-review
/// defaults route is the one global exception (see its own mapping below).
/// </summary>
internal static class StudioTaskReviewEndpoints
{
    /// <summary>
    /// Required public entry point (see this group's file-ownership rules).
    /// Delegates to the <see cref="IEndpointRouteBuilder"/> overload below --
    /// every call inside only ever uses <c>MapGroup</c>/<c>MapGet</c>/<c>MapPost</c>,
    /// all of which are defined against <see cref="IEndpointRouteBuilder"/>,
    /// so nothing here is actually <see cref="WebApplication"/>-specific. The
    /// second overload exists so tests can map these routes onto a
    /// <c>WebApplicationFactory</c>-hosted <see cref="Program"/> app (which
    /// does not yet call this method itself -- see <c>StudioTaskReviewTests.cs</c>)
    /// via <c>app.UseEndpoints(endpoints => endpoints.MapStudioTaskReviewEndpoints())</c>,
    /// without needing a direct reference to the real <see cref="WebApplication"/> instance.
    /// </summary>
    internal static void MapStudioTaskReviewEndpoints(this WebApplication app)
        => MapStudioTaskReviewEndpoints((IEndpointRouteBuilder)app);

    internal static void MapStudioTaskReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapPost("/code-review", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            SubmitCodeReviewFindingRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SubmitCodeReviewFindingAsync(
                    projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        // "list" must be registered ahead of "{fileName}" only in source
        // order for readability; ASP.NET routing already prefers the more
        // specific literal segment over the parameterized one regardless of
        // declaration order.
        tasks.MapGet("/code-review/list", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ListCodeReviewFindingsAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/code-review/{fileName}", async (
            string projectId, string taskIdentity, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetCodeReviewFindingsForFileAsync(projectId, taskIdentity, fileName, ct)));

        tasks.MapGet("/pipeline", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetTaskPipelineAsync(projectId, taskIdentity, ct)));

        tasks.MapPost("/pipeline/steps/{stepId}/run", async (
            string projectId,
            string taskIdentity,
            string stepId,
            HttpContext context,
            RunPipelineStepRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RunPipelineStepAsync(
                    projectId, taskIdentity, stepId, request ?? new RunPipelineStepRequest(),
                    TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapGet("/regression-radar", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetRegressionRadarAsync(projectId, taskIdentity, ct)));

        tasks.MapPost("/review-evidence/{evidenceId}/acknowledge", async (
            string projectId,
            string taskIdentity,
            string evidenceId,
            HttpContext context,
            ReviewEvidenceActionRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AcknowledgeReviewEvidenceAsync(
                    projectId, taskIdentity, evidenceId, request ?? new ReviewEvidenceActionRequest(),
                    TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/review-evidence/{evidenceId}/follow-up", async (
            string projectId,
            string taskIdentity,
            string evidenceId,
            HttpContext context,
            ReviewEvidenceActionRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.FollowUpReviewEvidenceAsync(
                    projectId, taskIdentity, evidenceId, request ?? new ReviewEvidenceActionRequest(),
                    TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/summary/interim", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            InterimSummaryRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SubmitInterimSummaryAsync(
                    projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/summary/regenerate", async (
            string projectId,
            string taskIdentity,
            HttpContext context,
            RegenerateSummaryRequest? request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RegenerateSummaryAsync(
                    projectId, taskIdentity, request ?? new RegenerateSummaryRequest(),
                    TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        // Global, not task-scoped: a default review policy template.
        var codeReviewDefaults = app.MapGroup("/api/v1/studio/tasks/code-review")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        codeReviewDefaults.MapGet("/defaults", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCodeReviewDefaultsAsync(ct)));
    }
}
