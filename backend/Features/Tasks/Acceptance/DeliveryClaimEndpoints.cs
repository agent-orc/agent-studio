using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2817 - project-scoped routes for the delivery-claim sweep.
///
/// <list type="bullet">
/// <item><c>GET  /api/projects/{id}/delivery-claims</c> - read-only report of
///   every delivered and archived card's class and contradictions. Run it
///   after a promotion to see the divergence between "delivered" and
///   "deployed" without asking.</item>
/// <item><c>POST /api/projects/{id}/delivery-claims/reconcile</c> - the same
///   sweep, additionally repairing the caches that contradict containment:
///   the missing integration record and the stale <c>next-attempt</c>
///   placeholder. It reports what it changed and rewrites nothing it cannot
///   prove.</item>
/// </list>
///
/// The project segment accepts the canonical <c>PROJ-NNN</c> id, the display
/// name, or a watch path; <c>*</c> sweeps every registered project.
/// </summary>
public static class DeliveryClaimEndpoints
{
    public static void MapDeliveryClaimEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId}/delivery-claims",
            (string projectId, DeliveryClaimSweep sweep) =>
                Results.Ok(sweep.Run(Scope(projectId), repair: false)));

        app.MapPost("/api/projects/{projectId}/delivery-claims/reconcile",
            (string projectId, DeliveryClaimSweep sweep) =>
                Results.Ok(sweep.Run(Scope(projectId), repair: true)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);
    }

    /// <summary>
    /// <c>GET /api/tasks/{jobId}/delivery-claim</c> - the per-card deployment
    /// answer. Used by the card surface and by the archive guard when the
    /// board projection carries no verdict yet, so an absent projection is
    /// resolved rather than reported as "not integrated".
    /// </summary>
    public static void MapTaskDeliveryClaimEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/delivery-claim", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            DeliveryClaimSweep sweep,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var task = scanner.FindJob(jobId, watchPath);
            return task is null
                ? Results.NotFound(new { error = "Task not found." })
                : Results.Ok(sweep.Describe(task));
        });
    }

    private static string? Scope(string projectId)
        => string.IsNullOrWhiteSpace(projectId) || projectId == "*" ? null : projectId;
}
