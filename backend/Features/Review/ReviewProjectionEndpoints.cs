using System.Collections.Concurrent;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Review;

/// <summary>
/// Computes and serves <see cref="ReviewProjectionView"/> (AGT-2717): reads the
/// task's timeline and parked-blocker marker, then delegates the artifact
/// merge to the pure <see cref="ReviewProjectionReader"/>.
/// </summary>
public sealed class ReviewProjectionService
{
    // The board's Git projection reads this for every board card on every
    // index run. Parsing each timeline again was one of the API's largest
    // allocators (AGT-3369), so a parsed timeline is kept against its file's
    // length and write time. The full review view is also kept by card and
    // sidecar generation. Both memos stay bounded at their capacity.
    internal const int TimelineMemoCapacity = 4096;

    private readonly AgentStudio.Tasks.TimelineLog _timeline;
    private readonly AgentStudio.Projects.ProjectSettingsService? _settings;
    private readonly ConcurrentDictionary<string, ParsedTimeline> _timelines = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedProjection> _projections = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedProjectionFolders = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _projectionRepairQueue = new();
    private readonly object _projectionQueueGate = new();
    private int _queuedProjectionCount;

    private sealed record ParsedTimeline(long Length, DateTime WriteUtc, IReadOnlyList<TimelineEvent> Events);
    private sealed record CachedProjection(long FolderVersion, int MaximumRounds,
        DateTime ReadUtc, ReviewProjectionView View);
    internal Action<string>? LedgerReadObserver { get; set; }

    public ReviewProjectionService(
        AgentStudio.Tasks.TimelineLog timeline,
        AgentStudio.Projects.ProjectSettingsService? settings = null)
    {
        _timeline = timeline;
        _settings = settings;
    }

    public ReviewProjectionView Read(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath)) return ReviewProjectionView.Empty;
        var timeline = ReadTimeline(task.FolderPath);
        var parkedBlocker = AgentStudio.Tasks.ParkedBlockerMarker.TryRead(task.FolderPath);
        var projection = ReviewProjectionReader.Read(task, timeline, parkedBlocker);
        LedgerReadObserver?.Invoke(task.FolderPath);
        var ledger = AgentStudio.Runner.ReviewRoundBudgetStore.Read(task.FolderPath, projection.Attempts);
        return projection with
        {
            RoundBudget = new ReviewRoundBudgetView(
                ledger.Delivered,
                Math.Clamp(_settings?.Get(task.ProjectName).MaxDeliveredReviewRounds
                           ?? AgentStudio.OrchestrationDecisions.ReviewRoundBudgetPolicy.DefaultMaximumRounds, 1, 20),
                ledger.SpentBy),
        };
    }

    private IReadOnlyList<TimelineEvent> ReadTimeline(string folder)
    {
        var file = new FileInfo(AgentStudio.Tasks.TaskPaths.TimelineLog(folder));
        if (!file.Exists) return [];
        // Stat before reading: a write in between leaves a newer file than the
        // remembered stamp, so the next read parses it again.
        var length = file.Length;
        var writeUtc = file.LastWriteTimeUtc;
        if (_timelines.TryGetValue(folder, out var known) && known.Length == length && known.WriteUtc == writeUtc)
            return known.Events;
        var events = _timeline.ReadAll(folder);
        if (_timelines.Count >= TimelineMemoCapacity) _timelines.Clear();
        _timelines[folder] = new ParsedTimeline(length, writeUtc, events);
        return events;
    }

    public Dictionary<string, ReviewProjectionView> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        // Repair a few old views per pass. This also covers a missed watcher
        // event for review artifacts other than the ledger.
        lock (_projectionQueueGate)
        {
            var repairCount = Math.Min(32, _queuedProjectionCount);
            for (var i = 0; i < repairCount && _projectionRepairQueue.TryDequeue(out var folder); i++)
            {
                _projectionRepairQueue.Enqueue(folder);
                if (_projections.TryGetValue(folder, out var old)
                    && DateTime.UtcNow - old.ReadUtc > TimeSpan.FromMinutes(5))
                    _projections.TryRemove(folder, out _);
            }
        }
        var result = new Dictionary<string, ReviewProjectionView>(StringComparer.Ordinal);
        var maximumByProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            var version = SidecarFileGeneration.FolderVersion(job.FolderPath);
            if (!maximumByProject.TryGetValue(job.ProjectName, out var maximum))
            {
                maximum = Math.Clamp(_settings?.Get(job.ProjectName).MaxDeliveredReviewRounds
                    ?? AgentStudio.OrchestrationDecisions.ReviewRoundBudgetPolicy.DefaultMaximumRounds, 1, 20);
                maximumByProject[job.ProjectName] = maximum;
            }
            if (_projections.TryGetValue(job.FolderPath, out var cached)
                && cached.FolderVersion == version
                && cached.MaximumRounds == maximum)
            {
                result[job.TaskKey] = cached.View;
                continue;
            }
            var view = Read(job);
            lock (_projectionQueueGate)
            {
                if (_queuedProjectionFolders.TryAdd(job.FolderPath, 0))
                {
                    if (_queuedProjectionCount >= TimelineMemoCapacity
                        && _projectionRepairQueue.TryDequeue(out var oldest))
                    {
                        _queuedProjectionFolders.TryRemove(oldest, out _);
                        _projections.TryRemove(oldest, out _);
                        _queuedProjectionCount--;
                    }
                    _projectionRepairQueue.Enqueue(job.FolderPath);
                    _queuedProjectionCount++;
                }
                _projections[job.FolderPath] = new CachedProjection(version, maximum, DateTime.UtcNow, view);
            }
            result[job.TaskKey] = view;
        }
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
