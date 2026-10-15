namespace AgentStudio.OrchestrationDecisions;

/// <summary>Maps an admitted coding-completion choice to the existing transition vocabulary.</summary>
public static class ActiveDecisionPolicy
{
    public static TransitionDecision? CodingTransition(DecisionChoiceV1 choice, string cardKey,
        string attemptId)
    {
        var action = (choice.Action, choice.Target) switch
        {
            (DecisionActionKinds.Prepare, "review-subject") => TransitionAction.CreateReview,
            (DecisionActionKinds.Retry, "card:ready") => TransitionAction.ReturnToReady,
            (DecisionActionKinds.Adapt, "salvage-handoff") => TransitionAction.SalvageContinuation,
            (DecisionActionKinds.Checkpoint, "verified-tree") => (TransitionAction?)null,
            (DecisionActionKinds.Ask, "operator:ai") => null,
            (DecisionActionKinds.Defer, "card:bounded-pause") => null,
            _ => throw new InvalidOperationException("decision-action-not-dispatchable"),
        };
        return action is null ? null : TransitionDecisionPolicy.Create(action.Value,
            $"Model selected {choice.Action}({choice.Target}) for the settled coding attempt.",
            cardKey, attemptId, 0, 0);
    }
}
