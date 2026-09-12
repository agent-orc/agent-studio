using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Studio P2 "supervisor intervention and insight" bundle: the
/// accepted-integration pipeline alert, per-project cycle-time and
/// throughput, the regression radar, supervisor run interventions and
/// pickup pause/resume, the meta-cycle and observation summaries, the
/// project-filtered recent-events replay, a bounded queue-health repair
/// pass, and the test-run ledger (plus its own small ingestion route). GET
/// routes read <see cref="TaskServerScopes.TasksRead"/>; every mutation
/// requires <see cref="TaskServerScopes.TasksWrite"/> except the test-run
/// ingestion route, which is plumbing behind the board and requires
/// <see cref="TaskServerScopes.RunsWrite"/> like other runner-facing writes.
/// </summary>
public static class StudioP2SupervisorEndpoints
{
    public static void MapStudioP2SupervisorEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio");

        studio.MapGet("/pipeline/accepted-integration-alert", async (TaskServerStore store, CancellationToken ct)
                => await TaskServerEndpoints.InvokeAsync(() => store.GetAcceptedIntegrationAlertAsync(ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapProjectEndpoints(studio);
        MapSupervisorEndpoints(studio);
        MapTestRunIngestEndpoint(studio);
    }

    private static void MapProjectEndpoints(RouteGroupBuilder studio)
    {
        var projects = studio.MapGroup("/projects/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        projects.MapGet("/cycle-time", async (
            string project, string? window, string? detail, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCycleTimeAsync(project, window, detail, ct)));

        projects.MapGet("/cycle-time/tasks/{taskKey}", async (
            string project, string taskKey, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTaskCycleTimeAsync(project, taskKey, ct)));

        projects.MapGet("/regression-radar", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetRegressionRadarAsync(project, ct)));

        projects.MapGet("/throughput", async (string project, string? window, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetThroughputAsync(project, window, ct)));

        projects.MapPost("/queue-health/repair", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RepairQueueHealthAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        projects.MapGet("/test-runs", async (string project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectTestRunsAsync(project, limit, ct)));
    }

    private static void MapSupervisorEndpoints(RouteGroupBuilder studio)
    {
        var supervisor = studio.MapGroup("/supervisor/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        supervisor.MapPost("/intervene/cancel-run", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CancelActiveRunAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        supervisor.MapPost("/intervene/force-fail", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ForceFailActiveRunAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        supervisor.MapPost("/intervene/pause-pickup", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PausePickupAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        supervisor.MapPost("/intervene/resume", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ResumePickupAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        supervisor.MapGet("/meta-cycle", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorMetaCycleAsync(project, ct)));

        supervisor.MapGet("/observation", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorObservationAsync(project, ct)));

        supervisor.MapGet("/recent-events", async (string project, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorRecentEventsAsync(project, limit, ct)));
    }

    private static void MapTestRunIngestEndpoint(RouteGroupBuilder studio)
    {
        studio.MapPost("/test-runs/{project}/ingest", async (
                string project, HttpContext context, IngestTestRunRequest request, TaskServerStore store, CancellationToken ct)
                => await TaskServerEndpoints.InvokeAsync(
                    () => store.IngestTestRunAsync(project, request, TaskServerEndpoints.Actor(context), ct),
                    StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
    }
}
