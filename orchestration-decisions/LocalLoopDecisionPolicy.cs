namespace AgentStudio.Orchestration.Decisions;

/// <summary>Local loop facts are measured by the host; these predicates own the stop conditions.</summary>
public static class LocalLoopDecisionPolicy
{
    public static bool UnusablePrompt(bool usable) => !usable;
    public static bool BudgetSpent(int used, int allowed) => used >= allowed;
    public static bool RepeatedGateFailure(int priorFailures, bool cardCeilingReached)
        => priorFailures >= 1 || cardCeilingReached;
    public static bool RepeatedNoProgress(int priorReissues, bool sawRecovery, int toolCalls,
        int fileChanges, int substantiveCharacters)
        => priorReissues > 0 && sawRecovery && toolCalls == 0 && fileChanges == 0 && substantiveCharacters == 0;
}
