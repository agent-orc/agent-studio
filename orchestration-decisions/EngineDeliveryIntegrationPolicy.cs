namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// AGT-3883: where the engine-owned per-task integration of one passed review
/// stands, as the host reads it from the engine's occurrences, the delivery
/// settlement and Git.
/// </summary>
public enum EngineDeliveryIntegrationState
{
    /// <summary>The engine holds no decision, integration or result for the passed review.</summary>
    NotStarted,

    /// <summary>An engine decision is pending or applying, or the integration or its gate runs.</summary>
    Running,

    /// <summary>The integration branch carries the delivery; only the lane move is owed.</summary>
    Published,

    /// <summary>The integration or the application of the engine decision ended without the lane move.</summary>
    Failed,
}

public enum EngineDeliveryIntegrationAction
{
    /// <summary>The engine still owns the card; the watchdog leaves it.</summary>
    Wait,

    /// <summary>Ask the engine for a fresh decision on the same passed review.</summary>
    Request,

    /// <summary>Nothing the engine can retry; escalate with its reason.</summary>
    Escalate,
}

/// <param name="Retryable">The same reviewed delivery may integrate again without a new review.</param>
public sealed record EngineDeliveryIntegrationFacts(EngineDeliveryIntegrationState State, bool Retryable);

public static class EngineDeliveryIntegrationPolicy
{
    /// <summary>
    /// Running stays with the engine. A published delivery owes only the lane
    /// move and a delivery the engine never started owes its integration, so
    /// both ask the engine again. A failure is asked again only when the same
    /// reviewed delivery may retry, such as a gate host or run budget fault.
    /// The stranding watchdog asks once per lane visit; its next pass escalates.
    /// </summary>
    public static EngineDeliveryIntegrationAction Decide(EngineDeliveryIntegrationFacts facts)
        => facts.State switch
        {
            EngineDeliveryIntegrationState.Running => EngineDeliveryIntegrationAction.Wait,
            EngineDeliveryIntegrationState.Failed when !facts.Retryable => EngineDeliveryIntegrationAction.Escalate,
            _ => EngineDeliveryIntegrationAction.Request,
        };

    /// <summary>
    /// Integration outcomes the same passed review may retry. A gate
    /// environment failure rolled the merge back and promised a retry without
    /// a new review; a product gate failure or a conflict needs a coding round.
    /// </summary>
    public static bool IsRetryableOutcome(string? integrationOutcome)
        => integrationOutcome is "GateEnvironmentFailure";

    /// <summary>Integration outcomes that published or found the delivery on the integration branch.</summary>
    public static bool IsPublishedOutcome(string? integrationOutcome)
        => integrationOutcome is "Merged" or "MergedAfterRebase" or "AlreadyMerged" or "AlreadyOnIntegrationBranch";
}
