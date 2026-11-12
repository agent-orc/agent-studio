using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Docs;

public enum SpawnedCardRemoval
{
    Removed,
    AlreadyGone,
    Started,
    Failed,
}

/// <summary>
/// Removes a card that a caller created for a Dossier decision which then
/// could not be recorded, so the decision and its card stay one operation
/// (AGT-3654). Only cards that no run has picked up are removed.
/// </summary>
public interface IWorkbenchSpawnedCardCleanup
{
    SpawnedCardRemoval Remove(string projectName, string taskId);
}

public sealed class WorkbenchSpawnedCardCleanup : IWorkbenchSpawnedCardCleanup
{
    private static readonly HashSet<string> UnstartedLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Backlog,
        TaskStates.Preparation,
        TaskStates.OrchestratorPrep,
        TaskStates.Ready,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly ProjectRegistry _projects;

    public WorkbenchSpawnedCardCleanup(
        TaskScannerService scanner, TaskStateMachine states, ProjectRegistry projects)
    {
        _scanner = scanner;
        _states = states;
        _projects = projects;
    }

    public SpawnedCardRemoval Remove(string projectName, string taskId)
    {
        var watchPath = TaskEndpointHelpers.ResolveWatchPath(_projects, projectName, watchPath: null);
        var info = _scanner.FindJob(taskId, watchPath);
        if (info == null) return SpawnedCardRemoval.AlreadyGone;
        if (!UnstartedLanes.Contains(info.State)) return SpawnedCardRemoval.Started;
        return _states.DeleteJob(info.Id, info.WatchPath)
            ? SpawnedCardRemoval.Removed
            : SpawnedCardRemoval.Failed;
    }
}
