namespace AgentStudio.Watcher;

/// <summary>
/// One model-free detector data source. Each probe reads an existing,
/// already-computed system-of-record and emits normalized observations;
/// none of them make a network call, run a model, or mutate state. The
/// hosted sweep runs every registered probe and hands the union to
/// <see cref="WatcherCaseEngine"/>.
/// </summary>
public interface IWatcherSignalProbe
{
    /// <summary>Short, log-friendly name for the structured sweep summary.</summary>
    string Name { get; }

    IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc);
}
