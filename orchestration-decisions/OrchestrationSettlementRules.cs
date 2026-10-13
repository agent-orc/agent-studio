using AgentStudio.Orchestration.Decisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed record OrchestrationSettlementDecision(
    string RunStatus,
    OrchestrationStage NextStage,
    int ReissueAttempts,
    string? TaskState,
    bool IsTerminal,
    string? SupersededReason = null,
    string? SettlementReason = null,
    bool RequiresLinkedFollowUp = false);

public static class OrchestrationSettlementRules
{
    public static OrchestrationSettlementDecision Decide(
        OrchestrationAction action,
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        string currentTaskState,
        long expectedTaskVersion,
        long currentTaskVersion,
        RepeatedReviewBlockDiagnosis? repeatedBlock = null,
        TransitionDecision? reviewDecision = null)
    {
        if (!string.Equals(currentTaskState, "4-auto-review", StringComparison.Ordinal))
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task moved to '{currentTaskState}' before settlement.");
        }

        if (expectedTaskVersion > 0 && currentTaskVersion != expectedTaskVersion)
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task version changed from {expectedTaskVersion} to {currentTaskVersion} before settlement.");
        }

        if (currentStage == OrchestrationStage.ReviewDecision && reviewDecision is not null)
        {
            if (reviewDecision.Action == TransitionAction.DegradeWithFollowUp)
                return new("completed", currentStage, currentReissueAttempts, "5-human-review", true,
                    SettlementReason: reviewDecision.Reason, RequiresLinkedFollowUp: true);
            if (reviewDecision.Action == TransitionAction.DeferToBatch)
                return Terminal("deferred", currentStage, currentReissueAttempts, "4-auto-review", reviewDecision.Reason);
            if (reviewDecision.Action == TransitionAction.Escalate)
                return Terminal("escalated", currentStage, currentReissueAttempts, "5e-escalated", reviewDecision.Reason);
            if (reviewDecision.Action is TransitionAction.FindingRound or TransitionAction.ConcernRound)
                return Terminal("reissued", currentStage, currentReissueAttempts + 1, "2-ready", reviewDecision.Reason);
        }

        return action switch
        {
            OrchestrationAction.Continue => Continue(stages, currentStage, currentReissueAttempts),
            OrchestrationAction.Reissue => Reissue(
                currentStage,
                currentReissueAttempts,
                maxReissueAttempts,
                repeatedBlock),
            OrchestrationAction.Escalate => Terminal(
                "escalated", currentStage, currentReissueAttempts, "5e-escalated"),
            OrchestrationAction.Complete => Terminal(
                "completed", currentStage, currentReissueAttempts, "5-human-review"),
            OrchestrationAction.Fail => Terminal(
                "failed", currentStage, currentReissueAttempts, "5e-escalated"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static OrchestrationSettlementDecision Continue(
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int reissueAttempts)
    {
        var index = stages.IndexOf(currentStage);
        if (index >= 0 && index + 1 < stages.Count)
        {
            return new OrchestrationSettlementDecision(
                "pending",
                stages[index + 1],
                reissueAttempts,
                null,
                false);
        }

        return Terminal("completed", currentStage, reissueAttempts, "5-human-review");
    }

    private static OrchestrationSettlementDecision Reissue(
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        RepeatedReviewBlockDiagnosis? repeatedBlock)
    {
        var reissueAttempts = currentReissueAttempts + 1;
        var spent = currentReissueAttempts >= maxReissueAttempts;
        if (repeatedBlock is { IsTestBlock: false } && (spent || repeatedBlock.BudgetSpent))
            return new("completed", currentStage, currentReissueAttempts, "5-human-review", true,
                SettlementReason: $"Open findings moved to a linked follow-up: {repeatedBlock.Finding}.",
                RequiresLinkedFollowUp: true);
        return !spent
            ? Terminal("reissued", currentStage, reissueAttempts, "2-ready")
            : Terminal("escalated", currentStage, reissueAttempts, "5e-escalated",
                $"The task-wide review reissue budget is exhausted ({currentReissueAttempts}/{maxReissueAttempts} retries used).");
    }

    private static OrchestrationSettlementDecision Terminal(
        string status,
        OrchestrationStage currentStage,
        int reissueAttempts,
        string taskState,
        string? settlementReason = null)
        => new(status, currentStage, reissueAttempts, taskState, true, SettlementReason: settlementReason);
}

public static class OrchestrationStageListExtensions
{
    public static int IndexOf(
        this IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage stage)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            if (stages[index] == stage) return index;
        }

        return -1;
    }
}
