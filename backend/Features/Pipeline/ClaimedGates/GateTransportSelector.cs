namespace AgentStudio.Pipeline;

/// <summary>
/// Routes each remote gate to the SSH bridge or the claimed gate transport
/// (DP-S4). The mode is read per gate, so an operator switch or rollback takes
/// effect for the next gate. A switch never cancels or re-dispatches an SSH
/// invocation already in flight: those drain on the bridge while new
/// verification gates are claimed, and the bridge default is retirement-ready
/// only after that drain and a recorded canary pass.
/// </summary>
public sealed class GateTransportSelector(
    ShardedRemoteGateTransport bridge,
    ClaimedGateTransport claimed,
    ClaimedGateCanaryLedger canary,
    GateSlotUsageTracker sshSlots,
    IConfiguration configuration,
    ILogger<GateTransportSelector> logger) : IRemoteGateTransport
{
    public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
        => Route(request, out _) == GateTransportRoute.Claimed
            ? claimed.RunAsync(request, changedFiles, profile, mode, timeout, ct)
            : bridge.RunAsync(request, changedFiles, profile, mode, timeout, ct);

    internal GateTransportRoute Route(BuildTestGateRequest request, out bool bridgeRetirementReady)
    {
        // Startup already validated the mode against the Task Server topology.
        var transport = GateTransportSelectionPolicy.Mode(configuration, taskServerConfigured: true);
        var decision = GateTransportSelectionPolicy.Decide(transport, request.GateId, request.Project,
            GateTransportSelectionPolicy.CanaryProjects(configuration), canary.Passed);
        var sshInFlight = sshSlots.Snapshot().Busy + sshSlots.Dispatching;
        bridgeRetirementReady = GateTransportSelectionPolicy.BridgeRetirementReady(
            transport, canary.Passed, sshInFlight);
        logger.LogInformation(
            "gate_transport_selected gate={GateId} project={Project} mode={Mode} route={Route} reason={Reason} ssh_in_flight={SshInFlight} bridge_retirement_ready={Ready}",
            request.GateId, request.Project, transport, decision.Route, decision.Reason, sshInFlight,
            bridgeRetirementReady);
        return decision.Route;
    }
}
