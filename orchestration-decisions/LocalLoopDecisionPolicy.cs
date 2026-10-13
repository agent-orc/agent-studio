namespace AgentStudio.OrchestrationDecisions;

/// <summary>Stops an automatic retry when a prior recovery produced no progress.</summary>
public static class LocalLoopDecisionPolicy
{
    public static bool RepeatedNoProgress(int priorReissues, bool sawRecovery, int toolCalls,
        int fileChanges, int substantiveCharacters)
        => priorReissues > 0 && sawRecovery && toolCalls == 0 && fileChanges == 0 && substantiveCharacters == 0;
}
