using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3218: the durable trace of a passed engine gate decision reaching the
/// monolith gate handler. The applier only keeps the lane and clears the wait
/// marker; the handler's next pass dispatches the aspect review and records
/// this receipt, which the applier canary reads as the gate dispatch.
/// </summary>
public static class EngineGateDispatchReceipt
{
    public const string RunIdKey = "orchestrationRunId";

    /// <summary>Records the dispatch once per occurrence; a repeated pass leaves the first receipt.</summary>
    public static void Record(TimelineLog timeline, string folderPath, string runId)
    {
        if (Recorded(timeline, folderPath, runId)) return;
        timeline.Append(folderPath, TimelineEventKinds.EngineGateDispatched, TimelineActors.System,
            "The passed engine gate decision dispatched the aspect review.",
            details: new Dictionary<string, string> { [RunIdKey] = runId });
    }

    public static bool Recorded(TimelineLog timeline, string folderPath, string runId)
        => timeline.ReadAll(folderPath).Any(entry => entry.Kind == TimelineEventKinds.EngineGateDispatched
            && entry.Details?.GetValueOrDefault(RunIdKey) == runId);
}
