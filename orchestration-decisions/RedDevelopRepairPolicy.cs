namespace AgentStudio.OrchestrationDecisions;

/// <summary>Frozen facts for one post-merge failure. The host gathers these facts before asking for a repair.</summary>
public sealed record RedDevelopRepairFacts(
    int SuspectCount,
    bool RevertAppliesCleanly,
    bool LaterMergeDependsOnSuspect,
    TimeSpan RedAge);

public enum RedDevelopRepairAction { Revert, FixForward, Escalate }

public sealed record RedDevelopRepairDecision(RedDevelopRepairAction Action, string Reason);

/// <summary>D2 A: one clean, independent merge is reverted; a long red interval reaches the operator.</summary>
public static class RedDevelopRepairPolicy
{
    public static readonly TimeSpan EscalationAge = TimeSpan.FromHours(2);

    public static RedDevelopRepairDecision Decide(RedDevelopRepairFacts facts)
    {
        if (facts.RedAge >= EscalationAge)
            return new(RedDevelopRepairAction.Escalate,
                "Develop has remained red for two hours; offer the pre-merge batch gate.");
        if (facts.SuspectCount == 1 && facts.RevertAppliesCleanly && !facts.LaterMergeDependsOnSuspect)
            return new(RedDevelopRepairAction.Revert,
                "One merge is attributed and an independent revert applies cleanly.");
        return new(RedDevelopRepairAction.FixForward,
            "The failure needs a coding repair because a safe independent revert is unavailable.");
    }

    public static bool MayMerge(IReadOnlyCollection<string> changedPaths,
        IReadOnlyCollection<string> failingArea)
        => !changedPaths.Any(path => failingArea.Contains(path, StringComparer.OrdinalIgnoreCase));

    public static bool MayCreateRelease(bool developRed) => !developRed;
}
