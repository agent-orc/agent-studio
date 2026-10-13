using AgentStudio.Orchestration.Decisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal static class OrchestrationSettlementPolicy
{
    internal static OrchestrationSettlementDecision Decide(OrchestrationAction action,
        IReadOnlyList<OrchestrationStage> stages, OrchestrationStage currentStage,
        int currentReissueAttempts, int maxReissueAttempts, string currentTaskState,
        long expectedTaskVersion, long currentTaskVersion, RepeatedReviewBlockDiagnosis? repeatedBlock = null, TransitionDecision? reviewDecision = null)
        => OrchestrationSettlementRules.Decide(action, stages, currentStage, currentReissueAttempts,
            maxReissueAttempts, currentTaskState, expectedTaskVersion, currentTaskVersion, repeatedBlock, reviewDecision);
}
