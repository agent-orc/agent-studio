using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestration.Decisions;

public sealed record SweepTransitionFacts(string CardId, string Sweep,
    OperatorSweepTrigger? Trigger, OperatorSweepGuardFacts Guards,
    ContinuationBaseFact? ContinuationBase = null,
    IReadOnlyList<ReviewFollowUpFinding>? Findings = null);

public static class SweepTransitionPolicy
{
    public static TransitionDecision Decide(SweepTransitionFacts facts)
    {
        var decision = OperatorSweepPolicy.Decide(facts.Sweep, facts.Trigger, facts.Guards);
        var action = decision.Action switch
        {
            OperatorSweepAction.Ignore => TransitionAction.Ignore,
            OperatorSweepAction.Hold => TransitionAction.Hold,
            OperatorSweepAction.WaitForPerson => TransitionAction.Escalate,
            _ => facts.Sweep switch
            {
                OperatorSweepKinds.FixRounds => TransitionAction.SweepFixRound,
                OperatorSweepKinds.GateTriage => TransitionAction.SweepGateRound,
                _ => TransitionAction.SalvageContinuation,
            },
        };
        return new(action, decision.Reason,
            new(facts.CardId, facts.Trigger?.SubjectKey ?? string.Empty, facts.Sweep,
                facts.Guards.Budget.Used, facts.Guards.Budget.Allowed,
                facts.Findings?.Select(finding => finding.Aspect).ToArray() ?? [], false),
            facts.Findings ?? [], decision.Action == OperatorSweepAction.Act ? facts.ContinuationBase : null,
            [TransitionDecisionPolicy.Candidate(action)]);
    }
}
