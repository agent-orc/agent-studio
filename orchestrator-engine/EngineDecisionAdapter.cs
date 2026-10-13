using AgentStudio.Orchestration.Decisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public static class EngineDecisionAdapter
{
    public static CodingCompletionDecision Coding(CodingCompletionFacts facts)
        => CodingCompletionPolicy.Decide(facts);

    public static TransitionDecision Review(TransitionFacts facts)
        => TransitionDecisionPolicy.Review(facts);

    public static TransitionDecision Sweep(SweepTransitionFacts facts)
        => SweepTransitionPolicy.Decide(facts);

    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);

    public static PreparedReviewDecision PrepareReview(ReviewReportRequest report,
        ReviewRoundBudgetLedger seed, CardRoundBudgetState budget, string attemptId,
        int priorReissues, int concernsUsed, ProjectDecisionSettings settings, bool acceptedReplay)
        => ReviewPreparationPolicy.Decide(report, seed, budget, attemptId, priorReissues,
            concernsUsed, settings, acceptedReplay);
}
