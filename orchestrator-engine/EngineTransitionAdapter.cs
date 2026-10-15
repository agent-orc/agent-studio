using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.OrchestratorEngine;

/// <summary>Host boundary for the shared decision library. No transition rules belong here.</summary>
public static class EngineTransitionAdapter
{
    public static TransitionDecision Decide(TransitionFacts facts) => TransitionDecisionPolicy.Decide(facts);
    public static ReviewBudgetDecision ReviewBudget(ReviewBudgetFacts facts) => ReviewBudgetPolicy.Decide(facts);
    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);
    public static AiOperatorDecision Operator(AiOperatorDecisionPayload payload) => AiOperatorDecisionPolicy.Decide(payload);
}
