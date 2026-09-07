using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Serves the card's review head on its own route so a surface that does not
/// already hold the full task detail (the board chip, a polling banner) can read
/// the same projection without re-deriving it from artifacts.
///
/// <para>
/// The identical value is embedded in <c>GET /api/tasks/{jobId}</c> as
/// <see cref="TaskDetail.ReviewProjection"/>; this route exists so the four
/// surfaces share one contract rather than one payload.
/// </para>
/// </summary>
public static class TaskReviewProjectionEndpoints
{
    public static void MapTaskReviewProjectionEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/tasks/{jobId}/review-projection
        group.MapGet("/{jobId}/review-projection", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            TaskIntegrationStatusService integrationStatus,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Review.ReviewProjectionService reviewProjection) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();

            // The delivery line of the projection needs the computed integration
            // verdict; without it a card that is already in develop would read as
            // "not attempted" here and "integrated" on the task detail.
            var lookup = integrationStatus.BuildLookup(new[] { info });
            if (lookup.TryGetValue(info.TaskKey, out var integration))
                info = info with { Integration = integration };

            return Results.Ok(reviewProjection.Build(info));
        });
    }
}
