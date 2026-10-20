namespace AgentStudio.OrchestrationDecisions;

/// <summary>Routes a disproved result whose only contradiction is base ancestry.</summary>
public enum DeliveryFenceRefusalAction
{
    Escalate,
    RequeueOnResult,
}

public sealed record DeliveryFenceRefusalFacts(
    bool IsDisproved,
    string? Warning,
    string? BaseSha,
    string? ResultSha,
    string? ResultRef);

public static class DeliveryFenceContinuationPolicy
{
    public static DeliveryFenceRefusalAction Decide(DeliveryFenceRefusalFacts facts)
        => facts.IsDisproved
           && facts.Warning?.Contains("is not an ancestor of result", StringComparison.Ordinal) == true
           && !string.IsNullOrWhiteSpace(facts.BaseSha)
           && !string.IsNullOrWhiteSpace(facts.ResultSha)
           && !string.IsNullOrWhiteSpace(facts.ResultRef)
            ? DeliveryFenceRefusalAction.RequeueOnResult
            : DeliveryFenceRefusalAction.Escalate;
}
