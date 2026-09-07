using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// Bridges the in-process <see cref="TaskChangeNotifier"/> (and the
/// <see cref="TaskTransitionService.OnJobMoved"/> event) onto the
/// <see cref="TaskHub"/> SignalR fan-out so connected browser tabs react to
/// job mutations by push instead of waiting for the next board poll.
///
/// <para>Event map (server → client method / payload):</para>
/// <list type="bullet">
///   <item><c>jobCreated</c> → <c>TaskInfo</c> (resolved from the scanner)</item>
///   <item><c>jobUpdated</c> → <c>TaskInfo</c> (a single field changed)</item>
///   <item><c>jobMoved</c>   → <c>{ id, fromState, toState }</c></item>
///   <item><c>jobDeleted</c> → <c>{ id, watchPath }</c></item>
///   <item><c>jobsReordered</c> → <c>{ projectName, lane }</c></item>
///   <item><c>jobsBulkChanged</c> → no payload, "re-pull suggested"</item>
/// </list>
///
/// <para>The coarse <c>jobsChanged</c> broadcast (wired off the
/// FileSystemWatcher in <c>Program.cs</c>) is left untouched and still acts
/// as a catch-all for external folder changes; these fine-grained events are
/// additive and fire synchronously on the API mutation path so they beat the
/// ~250 ms watcher debounce.</para>
///
/// <para>Subscriptions are wired in the constructor and live for the process
/// lifetime; resolve the singleton once at startup so the handlers attach.
/// Every broadcast is best-effort and swallows its own exceptions so a hub
/// transport fault can never poison the mutation that produced the event.</para>
/// </summary>
public sealed class TaskHubBroadcaster
{
    private readonly IHubContext<TaskHub> _hub;
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly ILogger<TaskHubBroadcaster> _logger;
    private readonly CliRouter? _router;
    private readonly TaskRunnerService? _runners;
    private readonly AgentStudio.ModelMigrations.ModelMigrationCoordinator? _modelMigrations;

    public TaskHubBroadcaster(
        IHubContext<TaskHub> hub,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        TaskChangeNotifier notifier,
        ILogger<TaskHubBroadcaster> logger,
        CliRouter? router = null,
        TaskRunnerService? runners = null,
        AgentStudio.ModelMigrations.ModelMigrationCoordinator? modelMigrations = null)
    {
        _hub = hub;
        _scanner = scanner;
        _projects = projects;
        _logger = logger;
        _router = router;
        _runners = runners;
        _modelMigrations = modelMigrations;

        notifier.TaskCreated += OnCreated;
        notifier.TaskUpdated += OnUpdated;
        notifier.TaskDeleted += OnDeleted;
        notifier.JobsReordered += OnReordered;
        notifier.JobsBulkChanged += OnBulkChanged;
    }

    /// <summary>
    /// Wire the move signal from <see cref="TaskTransitionService"/>. Kept off
    /// the constructor so the transition service is not a hard dependency of
    /// the broadcaster (avoids a DI cycle and keeps the unit test that drives
    /// the notifier directly free of the heavier move stack).
    /// </summary>
    public void AttachMoveSource(TaskTransitionService transitions)
    {
        transitions.OnJobMoved += OnMoved;
    }

    private void OnCreated(TaskChangeEvent e) => BroadcastInfo("jobCreated", e);
    private void OnUpdated(TaskChangeEvent e) => BroadcastInfo("jobUpdated", e);

    private void OnDeleted(TaskChangeEvent e) =>
        SendProject(e.WatchPath, "jobDeleted", new { id = e.JobId, watchPath = e.WatchPath });

    private void OnMoved(string projectName, string jobId, string fromState, string toState) =>
        SendProject(projectName, "jobMoved", new { id = jobId, fromState, toState });

    private void OnReordered(JobsReorderedEvent e) =>
        SendProject(e.ProjectName, "jobsReordered", new { projectName = e.ProjectName, lane = e.Lane });

    private void OnBulkChanged() => Send("jobsBulkChanged");

    private void BroadcastInfo(string method, TaskChangeEvent e)
    {
        // Resolve the canonical TaskInfo so the client can merge the row in
        // place. A create/update can race a concurrent move that already
        // relocated the folder; in that case FindJob returns null and we
        // fall back to a bulk re-pull rather than dropping the event.
        var info = _scanner.FindJob(e.JobId, e.WatchPath);
        if (info != null)
        {
            var projected = _router is not null && _runners is not null
                ? AgentStudio.Tasks.TaskEndpointHelpers.WithRuntime(info, _router, _runners)
                : info;
            if (_modelMigrations is not null)
                projected = _modelMigrations.WithProposal(projected);
            SendProject(projected.ProjectName, method, projected);
        }
        else Send("jobsBulkChanged");
    }

    private void SendProject(string projectHandle, string method, params object?[] args)
    {
        try
        {
            _ = _hub.Clients.Group(TaskHub.ProjectGroup(projectHandle, _projects)).SendCoreAsync(method, args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobsHub project broadcast of {Method} failed", method);
        }
    }

    private void Send(string method, params object?[] args)
    {
        try
        {
            // Fire-and-forget: the mutation already succeeded on disk, the
            // push is a courtesy nudge. Awaiting would couple API latency to
            // socket health (acceptance #7: emit must not add measurable
            // latency to the mutation).
            _ = _hub.Clients.All.SendCoreAsync(method, args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobsHub broadcast of {Method} failed", method);
        }
    }
}
