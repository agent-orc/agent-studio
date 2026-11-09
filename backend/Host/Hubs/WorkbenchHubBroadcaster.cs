using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// Projects repository, decision, and review changes onto the existing TaskHub route.
/// Repository-authored Workbenches have no create/update API, so the docs
/// watcher is the authoritative source for created and updated events; the
/// mutation notifier supplies the synchronous write-path events.
/// </summary>
public sealed class WorkbenchHubBroadcaster
{
    private readonly IHubContext<TaskHub> _hub;
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly ILogger<WorkbenchHubBroadcaster> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, WorkbenchListItem>> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkbenchHubBroadcaster(
        IHubContext<TaskHub> hub,
        WorkbenchCatalogueService catalogue,
        AgentStudio.Registry.ProjectRegistry projects,
        ILogger<WorkbenchHubBroadcaster> logger)
    {
        _hub = hub;
        _catalogue = catalogue;
        _projects = projects;
        _logger = logger;
    }

    public void Attach(TaskWatcherService watcher, WorkbenchChangeNotifier notifier)
    {
        watcher.OnWikiChanged += OnWikiChanged;
        notifier.DecisionRecorded += OnDecisionRecorded;
        notifier.ReviewRecorded += OnReviewRecorded;
    }

    public void InitializeSnapshotsAfterBoot(TaskIndexCache index)
    {
        // A restored board can answer while its first scan is still running.
        // Catalogue discovery needs the reference index from that scan, so
        // await it without occupying a worker or delaying hosted services.
        _ = InitializeSnapshotsAsync(index, index.RefreshInFlight);
    }

    private async Task InitializeSnapshotsAsync(TaskIndexCache index, Task? refresh)
    {
        if (refresh != null)
        {
            try { await refresh.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Initial Dossier snapshots await a successful task scan");
            }
        }
        if (index.TryGetPublished() is null) return;
        await Task.Run(InitializeSnapshots).ConfigureAwait(false);
    }

    private void InitializeSnapshots()
    {
        try
        {
            foreach (var projectName in _catalogue.ListProjectNames())
            {
                var snapshot = ReadProject(projectName);
                lock (_gate)
                    _snapshots.TryAdd(projectName, snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial Dossier hub snapshots could not be loaded");
        }
    }

    private void OnWikiChanged(string projectName, string changedPath)
    {
        var current = ReadProject(projectName);
        Dictionary<string, WorkbenchListItem> previous;
        lock (_gate)
        {
            previous = _snapshots.TryGetValue(projectName, out var snapshot)
                ? new Dictionary<string, WorkbenchListItem>(snapshot, StringComparer.Ordinal)
                : new Dictionary<string, WorkbenchListItem>(StringComparer.Ordinal);
            _snapshots[projectName] = current;
        }

        var changedId = ResolveWorkbenchId(changedPath);
        foreach (var item in current.Values.Where(item => !previous.ContainsKey(item.Id)))
            Send(projectName, "workbenchCreated", "created", item, null);

        if (changedId == null || !current.TryGetValue(changedId, out var changed)) return;
        if (!previous.TryGetValue(changedId, out var before)) return;

        Send(projectName, "workbenchUpdated", "updated", changed, before.Status);
        if (!string.Equals(before.Status, changed.Status, StringComparison.Ordinal))
            Send(projectName, "workbenchStatusChanged", "statusChanged", changed, before.Status);
    }

    private void OnDecisionRecorded(WorkbenchDecisionRecordedEvent evt)
    {
        var current = ReadProject(evt.ProjectName);
        current.TryGetValue(evt.WorkbenchId, out var item);
        lock (_gate) _snapshots[evt.ProjectName] = current;
        Send(evt.ProjectName, "workbenchDecisionRecorded", "decisionRecorded", item, evt.PreviousStatus,
            evt.WorkbenchId);
        if (!string.Equals(evt.PreviousStatus, evt.CurrentStatus, StringComparison.Ordinal))
            Send(evt.ProjectName, "workbenchStatusChanged", "statusChanged", item, evt.PreviousStatus,
                evt.WorkbenchId);
    }

    private void OnReviewRecorded(WorkbenchReviewRecordedEvent evt)
    {
        var current = ReadProject(evt.ProjectName);
        current.TryGetValue(evt.WorkbenchId, out var item);
        lock (_gate) _snapshots[evt.ProjectName] = current;
        Send(evt.ProjectName, "workbenchReviewRecorded", "reviewRecorded", item, null,
            evt.WorkbenchId);
    }

    private Dictionary<string, WorkbenchListItem> ReadProject(string projectName) =>
        (_catalogue.List(projectName, includeHistory: true)?.Items ?? [])
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private void Send(
        string projectName,
        string method,
        string eventType,
        WorkbenchListItem? item,
        string? previousStatus,
        string? workbenchId = null)
    {
        var payload = new WorkbenchHubEvent(
            eventType,
            projectName,
            item?.Id ?? workbenchId ?? "",
            item,
            previousStatus,
            DateTime.UtcNow);
        try
        {
            _ = _hub.Clients
                .Group(TaskHub.ProjectGroup(projectName, _projects))
                .SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TaskHub Dossier broadcast of {Method} failed for {Project}",
                method, projectName);
        }
    }

    private static string? ResolveWorkbenchId(string path)
    {
        try
        {
            var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            for (var depth = 0; depth < 12 && current != null; depth++)
            {
                if (File.Exists(Path.Combine(current, "workbench.json")))
                    return Path.GetFileName(current);
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        return null;
    }
}

public sealed record WorkbenchHubEvent(
    string Type,
    string ProjectName,
    string WorkbenchId,
    WorkbenchListItem? Workbench,
    string? PreviousStatus,
    DateTime OccurredAtUtc);
