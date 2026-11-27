using Contract = AgentStudio.TaskServer.Contracts;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.TaskServer;

/// <summary>Host boundary for the shared decision library. No transition rules belong here.</summary>
public static class TaskServerTransitionAdapter
{
    public static Contract.ReviewReportRequest ReviewMeasurements(
        Contract.ReviewReportRequest request, Contract.ReviewPlanDto? plan)
        => ReviewMeasurementPolicy.NormalizeReport(request, plan);

    public static TransitionDecision Decide(TransitionFacts facts) => TransitionDecisionPolicy.Decide(facts);
    public static ReviewBudgetDecision ReviewBudget(ReviewBudgetFacts facts) => ReviewBudgetPolicy.Decide(facts);
    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);
}
