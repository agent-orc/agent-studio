using System.Collections.Concurrent;
using AgentStudio.Pipeline;

namespace AgentStudio.Projects;

public sealed record ProjectCompletionItem(
    string TaskId,
    string TaskKey,
    string Title,
    DateTime CompletedAt);

public sealed record ProjectThroughputSummary(
    string Project,
    DateTime CapturedAt,
    int CompletedLast24h,
    int CompletedLast7d,
    IReadOnlyList<ProjectCompletionItem> RecentCompletions)
{
    public int ReAcceptanceArtefactsLast24h { get; init; }
    public int ReAcceptanceArtefactsLast7d { get; init; }
    public GateSlotUsage? GateSlots { get; init; }
}

/// <summary>
/// Archive-inclusive read model for project delivery throughput. Completion time
/// comes from real completion entries in the append-only lane-change ledger. A legacy task
/// without that event is counted only while it still resides in 6-completed,
/// where EnteredLaneAt remains an honest completion anchor.
/// </summary>
public sealed class ProjectThroughputService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly ILogger<ProjectThroughputService> _logger;
    private readonly GateSlotUsageTracker? _gateSlots;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectThroughputSummary Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectThroughputService(
        TaskScannerService scanner,
        TimelineLog timeline,
        ILogger<ProjectThroughputService> logger,
        GateSlotUsageTracker? gateSlots = null)
    {
        _scanner = scanner;
        _timeline = timeline;
        _logger = logger;
        _gateSlots = gateSlots;
    }

    public ProjectThroughputSummary? Build(string projectName, DateTime? nowUtc = null)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        if (_cache.TryGetValue(projectName, out var cached)
            && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value with { GateSlots = _gateSlots?.Snapshot() };

        var tasks = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, entry.Path))
            .ToList();
        var value = BuildSummary(projectName, tasks, ReadTimeline, nowUtc) with
        {
            GateSlots = _gateSlots?.Snapshot(),
        };
        _cache[projectName] = (DateTime.UtcNow, value);
        return value;
    }

    internal static ProjectThroughputSummary BuildSummary(
        string projectName,
        IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, IReadOnlyList<TimelineEvent>> readTimeline,
        DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var since24h = now.AddHours(-24);
        var since7d = now.AddDays(-7);
        var completions = new List<ProjectCompletionItem>();
        var artefacts24h = 0;
        var artefacts7d = 0;

        foreach (var task in tasks)
        {
            if (RetiredContainerTask.IsRetired(task)) continue;
            if (!string.Equals(task.State, TaskStates.Completed, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(task.State, TaskStates.Archive, StringComparison.OrdinalIgnoreCase))
                continue;

            var anchors = CompletionAnchors.Find(readTimeline(task));
            var completedAt = anchors.LastReal?.At ?? default;
            artefacts24h += anchors.Artefacts.Count(at => at >= since24h && at <= now);
            artefacts7d += anchors.Artefacts.Count(at => at >= since7d && at <= now);

            if (completedAt == default
                && task.EnteredLaneAt != default
                && string.Equals(task.State, TaskStates.Completed, StringComparison.OrdinalIgnoreCase))
            {
                completedAt = task.EnteredLaneAt.ToUniversalTime();
            }

            if (completedAt == default || completedAt < since7d || completedAt > now) continue;
            var taskKey = !string.IsNullOrWhiteSpace(task.Key)
                ? task.Key!
                : !string.IsNullOrWhiteSpace(task.TaskKey) ? task.TaskKey : task.Id;
            completions.Add(new ProjectCompletionItem(task.Id, taskKey, task.Title, completedAt));
        }

        var recent = completions
            .OrderByDescending(item => item.CompletedAt)
            .ThenBy(item => item.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProjectThroughputSummary(
            projectName,
            now,
            recent.Count(item => item.CompletedAt >= since24h),
            recent.Count,
            recent)
        {
            ReAcceptanceArtefactsLast24h = artefacts24h,
            ReAcceptanceArtefactsLast7d = artefacts7d,
        };
    }

    private IReadOnlyList<TimelineEvent> ReadTimeline(TaskInfo task)
    {
        try
        {
            return _timeline.ReadAll(task.FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "project-throughput-timeline-read-failed projectTask={TaskId} folder={Folder}",
                task.Id,
                task.FolderPath);
            return [];
        }
    }
}
