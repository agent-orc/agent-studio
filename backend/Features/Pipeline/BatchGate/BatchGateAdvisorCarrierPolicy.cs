using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>Who runs the batch plan advisor, and the mode that actually applies.</summary>
public sealed record BatchGateAdvisorCarrier(string EffectiveMode, string Carrier, string? Reason)
{
    public const string Monolith = "monolith";
    public const string Engine = "engine";
    public const string None = "none";
}

/// <summary>
/// Decides which component carries the batch plan advisor (AGT-3411). The
/// monolith asks a host CLI, which only the local profile may start. Under
/// Engine ownership of <c>BatchPlan</c> the Orchestrator Engine calls the
/// provider API itself. Any other networked combination cannot run the advisor,
/// so its effective mode is <c>off</c> and the reason is reported.
/// </summary>
public static class BatchGateAdvisorCarrierPolicy
{
    public const string HostCliDisabledReason =
        "The networked profile does not start the advisor CLI on the server, and the Orchestrator Engine "
        + "does not own the batch plan. Set Orchestration:Transitions:BatchPlan=Engine so the engine carries the advisor.";

    public static BatchGateAdvisorCarrier Resolve(string configuredMode, bool hostCliAllowed,
        OrchestrationExecutionMode owner)
    {
        if (configuredMode == "off") return new("off", BatchGateAdvisorCarrier.None, null);
        if (owner == OrchestrationExecutionMode.Engine)
            return new(configuredMode, BatchGateAdvisorCarrier.Engine, null);
        return hostCliAllowed
            ? new(configuredMode, BatchGateAdvisorCarrier.Monolith, null)
            : new("off", BatchGateAdvisorCarrier.None, HostCliDisabledReason);
    }

    /// <summary>Carrier for the running host; a host without configuration keeps the local behaviour.</summary>
    public static BatchGateAdvisorCarrier Resolve(BatchGateAdvisorOptions? advisor, IConfiguration? configuration)
        => Resolve((advisor ?? new BatchGateAdvisorOptions()).Mode,
            configuration is null || HostCliDiscovery.Allowed(configuration),
            BatchGatePilotService.OwnerOfBatchPlan(configuration, out _));
}
