namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// A verified result ref whose claimed base is outside the result's ancestry
/// can be continued from its retained result. Missing or mismatched result
/// refs still require an operator because no reliable checkout is named.
/// </summary>
public static class FencedDeliveryRefusalPolicy
{
    public static TransitionAction Decide(bool baseNotAncestor)
        => baseNotAncestor ? TransitionAction.ReturnToReady : TransitionAction.Escalate;
}
