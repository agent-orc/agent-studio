using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Dispatch;

/// <summary>Explicit Owner controls and deterministic evidence keys, separate from the switched background decider.</summary>
internal static class DispatchLeaseControls
{
    public static string Window(string? wave, DateTimeOffset now) => MonolithTransitionAdapter.DispatchWindow(wave, now);
    public static DispatchLeaseDecision Decide(DispatchLeaseState state, DispatchLeaseFacts facts)
        => MonolithTransitionAdapter.DispatchLease(state, facts);
}
