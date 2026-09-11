using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Review;

/// <summary>
/// Computes and serves <see cref="ReviewProjectionView"/> (AGT-2717): reads the
/// task's timeline and parked-blocker marker, then delegates the artifact
/// merge to the pure <see cref="ReviewProjectionReader"/>.
/// </summary>
public sealed class ReviewProjectionService
{
    private readonly AgentStudio.Tasks.TimelineLog _timeline;

    public ReviewProjectionService(AgentStudio.Tasks.TimelineLog timeline)
    {
        _timeline = timeline;
    }

    public ReviewProjectionView Read(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath)) return ReviewProjectionView.Empty;
        var timeline = _timeline.ReadAll(task.FolderPath);
        var parkedBlocker = AgentStudio.Tasks.ParkedBlockerMarker.TryRead(task.FolderPath);
        return ReviewProjectionReader.Read(task, timeline, parkedBlocker);
    }

    public Dictionary<string, ReviewProjectionView> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, ReviewProjectionView>(StringComparer.Ordinal);
        foreach (var job in jobs) result[job.TaskKey] = Read(job);
        return result;
    }
}

/// <summary>Folds a batched <see cref="ReviewProjectionView"/> lookup onto <see cref="TaskInfo"/>.</summary>
public static class ReviewProjectionTaskFolding
{
    public static IEnumerable<TaskInfo> WithReviewProjection(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, ReviewProjectionView> lookup)
    {
        foreach (var job in jobs)
            yield return lookup.TryGetValue(job.TaskKey, out var projection)
                ? job with { ReviewProjection = projection }
                : job;
    }
}

/// <summary>Read-side HTTP surface for <see cref="ReviewProjectionView"/>.</summary>
public static class ReviewProjectionEndpoints
{
    public static void MapReviewProjectionEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/tasks/{jobId}/review-projection
        // Same projection already folded onto GET /api/tasks/{jobId} as
        // TaskInfo.reviewProjection; this dedicated route lets a caller that
        // only needs the review head skip the full task-detail payload.
        group.MapGet("/{jobId}/review-projection",
            (string jobId,
             string? project,
             string? watchPath,
             AgentStudio.Tasks.TaskScannerService scanner,
             ReviewProjectionService projections,
             AgentStudio.Registry.ProjectRegistry projectRegistry) =>
        {
            watchPath = ResolveWatchPath(projectRegistry, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });
            return Results.Ok(projections.Read(info));
        });
    }
}
