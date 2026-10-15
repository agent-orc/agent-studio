using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Host boundary for the shared decision library. No transition rules belong here.</summary>
public static class MonolithTransitionAdapter
{
    public static TransitionDecision Decide(TransitionFacts facts) => TransitionDecisionPolicy.Decide(facts);
    public static ReviewBudgetDecision ReviewBudget(ReviewBudgetFacts facts) => ReviewBudgetPolicy.Decide(facts);
    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);

    public static TransitionDecision StageCoding(string taskId, string attemptId, string? targetState)
        => StageDecisionPolicy.Coding(taskId, attemptId, targetState);

    public static StagePolicyDecision StageGateTransition(string taskId, string attemptId,
        int failed, int roundsUsed, int roundsAllowed,
        bool infrastructureFailure = false, bool interventionRequired = false)
        => StageDecisionPolicy.GateTransition(taskId, attemptId, failed, roundsUsed,
            roundsAllowed, infrastructureFailure, interventionRequired);

    public static ReviewFollowUpDecision ReviewFollowUp(
        IEnumerable<ReviewFollowUpFinding> findings, int concernRoundsUsed, int maxConcernRounds)
        => ReviewFollowUpPolicy.Decide(findings, concernRoundsUsed, maxConcernRounds);

    public static RemoteDeliveryIntegrationDecision RemoteDeliveryIntegration(
        bool hasSettledResultEnvelope, string? reviewOutcome, ReviewPlanDto? reviewPlan,
        IReadOnlyList<ReviewVerdictDto> verdicts)
        => RemoteDeliveryIntegrationPolicy.Decide(hasSettledResultEnvelope, reviewOutcome, reviewPlan, verdicts);

    public static ReviewRoundBudgetDecision ReviewRoundBudget(
        ReviewRoundBudgetLedger ledger, string attemptId, IEnumerable<string> blockedAspects,
        int maximumRounds, int consecutiveBlockRounds,
        int? priorAutomaticReissues = null, int? maximumAutomaticReissues = null)
        => ReviewRoundBudgetPolicy.Decide(ledger, attemptId, blockedAspects, maximumRounds,
            consecutiveBlockRounds, priorAutomaticReissues, maximumAutomaticReissues);

    public static CardRoundBudgetAction CardRoundBudget(CardRoundBudgetState budget,
        IEnumerable<string> blockingAspects)
        => CardRoundBudgetPolicy.Decide(budget, blockingAspects);

    public static CardRoundBudgetAction CardRoundBudgetForReview(CardRoundBudgetState budget,
        IReadOnlyCollection<string> blockingAspects, bool opensConcernRound)
        => CardRoundBudgetPolicy.DecideForReview(budget, blockingAspects, opensConcernRound);

    public static string GateCancellation(string? expectedSha, string? currentSha,
        string? expectedLane, string? currentLane, bool continuePending,
        bool shuttingDown, string? reportedReason = null)
        => GateCancellationPolicy.Decide(expectedSha, currentSha, expectedLane, currentLane,
            continuePending, shuttingDown, reportedReason);

    public static BatchPlanStep BatchPlan(BatchPlanFacts facts)
        => BatchPlanDecisionPolicy.Decide(facts);

    public static RemoteReviewContinuationStep ContinuationReplay(IEnumerable<RoundTimelineFact> ledger,
        string attemptId, string cause, string moveKey, string currentState)
        => ContinuationReplayPolicy.Decide(ledger, attemptId, cause, moveKey, currentState);

    /// <summary>The AI Operator decision while the <c>OperatorDecision</c> switch selects Monolith.</summary>
    public static AiOperatorDecision Operator(AiOperatorDecisionPayload payload) => AiOperatorDecisionPolicy.Decide(payload);

    /// <summary>The transition an admitted active coding-completion choice maps to (AGT-3064).</summary>
    public static TransitionDecision? ActiveCodingTransition(DecisionChoiceV1 choice, string cardKey, string attemptId)
        => ActiveDecisionPolicy.CodingTransition(choice, cardKey, attemptId);
}
