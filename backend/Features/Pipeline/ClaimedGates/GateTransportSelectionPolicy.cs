using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public enum GateTransportRoute
{
    Bridge,
    Claimed,
}

public sealed record GateTransportDecision(GateTransportRoute Route, string Reason);

/// <summary>
/// Pure transport choice for merge, promotion and batch verification gates
/// (DP-S4, dossier D4 option A). <c>bridge</c> keeps the SSH bridge default.
/// <c>canary</c> claims only the configured canary projects. <c>claimed</c>
/// claims every verification gate once a fenced canary pass is recorded; until
/// then it keeps the compatibility bridge for non-canary projects.
/// </summary>
public static class GateTransportSelectionPolicy
{
    public const string ModeKey = "RemoteGate:Transport";
    public const string CanaryProjectsKey = "RemoteGate:ClaimedCanaryProjects";
    public const string BridgeMode = "bridge";
    public const string CanaryMode = "canary";
    public const string ClaimedMode = "claimed";

    /// <summary>Configured mode; an unknown value or a claimed mode without a Task Server fails closed.</summary>
    public static string Mode(IConfiguration? configuration, bool taskServerConfigured)
    {
        var mode = configuration?[ModeKey]?.Trim().ToLowerInvariant() switch
        {
            null or "" or BridgeMode => BridgeMode,
            CanaryMode => CanaryMode,
            ClaimedMode => ClaimedMode,
            _ => throw new InvalidOperationException(
                $"{ModeKey} must be '{BridgeMode}', '{CanaryMode}' or '{ClaimedMode}'."),
        };
        if (mode != BridgeMode && !taskServerConfigured)
            throw new InvalidOperationException(
                $"{ModeKey}={mode} needs TaskServer:BaseUrl: the gate bundle endpoint and claimed attempts live at the Task Server.");
        return mode;
    }

    public static IReadOnlyList<string> CanaryProjects(IConfiguration? configuration)
        => (configuration?[CanaryProjectsKey] ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static GateTransportDecision Decide(string mode, string gateId, string? project,
        IReadOnlyCollection<string> canaryProjects, bool canaryPassed)
    {
        if (GateSubjectKinds.ForGateId(gateId) is null)
            return new(GateTransportRoute.Bridge, "gate-not-claimable");
        var inCanary = project is not null && canaryProjects.Contains(project, StringComparer.OrdinalIgnoreCase);
        return mode switch
        {
            ClaimedMode when canaryPassed => new(GateTransportRoute.Claimed, "claimed"),
            ClaimedMode or CanaryMode when inCanary => new(GateTransportRoute.Claimed, "claimed-canary"),
            ClaimedMode => new(GateTransportRoute.Bridge, "awaiting-canary-pass"),
            CanaryMode => new(GateTransportRoute.Bridge, "outside-canary"),
            _ => new(GateTransportRoute.Bridge, "bridge-default"),
        };
    }

    /// <summary>
    /// The bridge default may be retired only when every gate is claimed, a
    /// canary passed, and no SSH invocation is still in flight.
    /// </summary>
    public static bool BridgeRetirementReady(string mode, bool canaryPassed, int sshInFlight)
        => mode == ClaimedMode && canaryPassed && sshInFlight == 0;
}
