using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3060: the delivery-chain and acceptance-rail rules live in the decision
/// library. These helpers derive the facts the way the monolith does and run
/// the same AI Operator decision and vocabulary mapping as the services, so
/// the existing rule tables keep their card-level inputs.
/// </summary>
internal static class OperatorDecisionHarness
{
    public static string? ReconciliationTarget(TaskInfo card, TaskIntegrationStatus? status)
    {
        var decision = AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(
            AiOperatorDeciders.DeliveryChain, "delivery:test",
            DeliveryChain: DeliveryChainReconciler.Facts(card, status, completedReopenLimitReached: false)));
        return decision.Effect switch
        {
            AiOperatorEffects.PromoteToHumanReview => TaskStates.HumanReview,
            AiOperatorEffects.ReturnToAutoReview => TaskStates.AutoReview,
            AiOperatorEffects.Escalate => TaskStates.Escalated,
            _ => null,
        };
    }

    public static AcceptanceRailDecision RailDecide(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        int conflictRequeues,
        AcceptanceRailOptions options,
        DateTimeOffset now,
        int infrastructureRequeues = 0,
        DateTimeOffset? lastInfrastructureRequeueAt = null,
        DateTimeOffset? quotaResetAt = null)
        => AcceptanceRailHostedService.RailDecision(AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(
            AiOperatorDeciders.AcceptanceRail, "acceptance-rail:test",
            AcceptanceRail: AcceptanceRailFacts.From(task, integration, conflictRequeues, options, now,
                infrastructureRequeues, lastInfrastructureRequeueAt, quotaResetAt))));
}
