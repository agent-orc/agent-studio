namespace AgentStudio.OrchestrationDecisions;

public enum DeliveryFenceRefusalAction { Escalate, ContinueFromResult }

/// <summary>Recovery for a published result whose recorded base is outside its history.</summary>
public static class DeliveryFenceRefusalPolicy
{
    public static DeliveryFenceRefusalAction Decide(
        bool baseNotAncestor, bool hasResultRef, bool hasResultSha, bool hasBaseSha)
        => baseNotAncestor && hasResultRef && hasResultSha && hasBaseSha
            ? DeliveryFenceRefusalAction.ContinueFromResult
            : DeliveryFenceRefusalAction.Escalate;

    public static bool AllowsContinuation(bool isFenceRefusal, bool roundBudgetExhausted)
        => isFenceRefusal || !roundBudgetExhausted;
}
