namespace AgentStudio.Orchestration.Decisions;

public sealed record NoOpProgressFacts(bool SawRecovery, int ToolCalls, int FileChanges, int SubstanceCharacters);

public static class LoopTransitionPolicy
{
    public static string ContinuationStep(bool completed, bool moved, string lane)
        => completed ? "Completed" : moved ? "Finish" : lane == "4-auto-review" ? "Move" : "Unapplicable";

    public static GateDisposition BoundedRound(int used, int allowed)
        => used >= allowed ? GateDisposition.Escalate : GateDisposition.Reissue;

    public static bool RetryBuildInfrastructure(bool infrastructureFailure, int unmetRequirements, int used, int allowed)
        => infrastructureFailure && unmetRequirements == 0 && used < allowed;

    public static string StoredVerdictLane(string verdict)
        => verdict == "AcceptAsDone" ? "5-human-review" : "5e-escalated";

    public static bool BudgetSpent(int used, int allowed) => used >= allowed;

    public static bool RepeatedNoOpWithoutProgress(int priorReissues, NoOpProgressFacts progress)
        => priorReissues > 0 && progress.SawRecovery && progress.ToolCalls == 0
           && progress.FileChanges == 0 && progress.SubstanceCharacters == 0;

    public static bool EscalateGate(bool gateEscalates, bool automaticDisabled)
        => gateEscalates || automaticDisabled;

    public static bool EscalateBuildGate(int priorIdenticalFailures, bool taskCeilingReached)
        => priorIdenticalFailures >= 1 || taskCeilingReached;

    public static bool EscalateLint(int priorFailures) => priorFailures >= 1;
}
