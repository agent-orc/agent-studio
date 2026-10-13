namespace AgentStudio.Orchestration.Decisions;

public sealed record CodingCompletionFacts(string CardId, string AttemptId, string Outcome,
    bool ProviderRejected = false, bool EpicPlanning = false, bool UnverifiedDelivery = false, bool ReportOnly = false);
public sealed record CodingCompletionDecision(TransitionAction Action, string TargetLane, bool CreatesReview,
    string Reason, DecisionReceipt Receipt, ActionCandidate Candidate);

public static class CodingCompletionPolicy
{
    public static CodingCompletionDecision Decide(CodingCompletionFacts facts)
    {
        var lane = TransitionDecisionPolicy.CodingLane(facts.Outcome, facts.ProviderRejected);
        var createsReview = TransitionDecisionPolicy.CreatesReview(facts.EpicPlanning, facts.Outcome,
            facts.UnverifiedDelivery, facts.ReportOnly);
        var action = createsReview ? TransitionAction.CreateReview : lane switch
        {
            "5e-escalated" => TransitionAction.Escalate,
            "2-ready" => TransitionAction.ReturnToReady,
            "4-auto-review" => TransitionAction.Continue,
            _ => TransitionAction.Ignore,
        };
        var reason = $"Coding completion '{facts.Outcome}' selects {action}.";
        return new(action, lane, createsReview, reason,
            new(facts.CardId, facts.AttemptId, "coding-completion", 0, 0, [], false),
            TransitionDecisionPolicy.Candidate(action));
    }
}
