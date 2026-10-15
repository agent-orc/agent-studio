using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Runner;

/// <summary>Host boundary for the shared decision library. No transition rules belong here.</summary>
public static class MonolithTransitionAdapter
{
    public static TransitionDecision Decide(TransitionFacts facts) => TransitionDecisionPolicy.Decide(facts);
    public static ReviewBudgetDecision ReviewBudget(ReviewBudgetFacts facts) => ReviewBudgetPolicy.Decide(facts);
    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);

    /// <summary>Test-host Monolith decision mode only; a production host is engine-only.</summary>
    public static AiOperatorDecision Operator(AiOperatorDecisionPayload payload) => AiOperatorDecisionPolicy.Decide(payload);
}
