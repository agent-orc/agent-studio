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

    /// <summary>The AI Operator decision while the <c>OperatorDecision</c> switch selects Monolith.</summary>
    public static AiOperatorDecision Operator(AiOperatorDecisionPayload payload) => AiOperatorDecisionPolicy.Decide(payload);

    /// <summary>The transition an admitted active coding-completion choice maps to (AGT-3064).</summary>
    public static TransitionDecision? ActiveCodingTransition(DecisionChoiceV1 choice, string cardKey, string attemptId)
        => ActiveDecisionPolicy.CodingTransition(choice, cardKey, attemptId);
}
