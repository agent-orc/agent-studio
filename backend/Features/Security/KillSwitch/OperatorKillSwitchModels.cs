namespace AgentStudio.Security;

/// <summary>
/// The audit event types of the operator kill switch (security model SM-2).
/// Each use writes exactly one of the first three; the Operations Server
/// revocation that follows a kill writes its own entry when it completes or fails.
/// </summary>
public static class OperatorKillEvents
{
    public const string All = "operator-kill.all";
    public const string Session = "operator-kill.session";
    public const string Drill = "operator-kill.drill";
    public const string OperationsServer = "operator-kill.operations-server";
    /// <summary>The API re-applied an out-of-band kill to state it had loaded before the command ran.</summary>
    public const string Enforced = "operator-kill.enforced";
}

public static class OperatorKillKinds
{
    public const string All = "all";
    public const string Session = "session";
    public const string Drill = "drill";
}

public static class OperatorKillTriggers
{
    /// <summary>The Owner in Settings &gt; Access.</summary>
    public const string Studio = "studio";
    /// <summary>The command on the control plane host, for when the UI is unreachable.</summary>
    public const string OutOfBand = "out-of-band";
    public const string ScheduledDrill = "scheduled-drill";
    public const string ManualDrill = "manual-drill";
}

/// <summary>Where the revocation of the operator's Operations Server principals stands for one kill.</summary>
public static class OperatorKillPropagationStates
{
    public const string NotConfigured = "not-configured";
    public const string NotRequired = "not-required";
    public const string Pending = "pending";
    public const string Revoked = "revoked";
}

/// <summary>The fixed actor of the out-of-band command: whoever can run it holds the control plane host.</summary>
public static class OperatorKillActors
{
    public const string OutOfBand = "system:control-plane-host";
    public const string Drill = "system:kill-switch-drill";
}

/// <summary>Who ends operator access: an audit actor and, for a Studio kill, the Owner whose role is checked.</summary>
public sealed record OperatorKillActor(string Actor, string? OwnerId)
{
    public static OperatorKillActor Owner(string ownerId) => new(AgentStudio.Shared.TimelineActors.Human(ownerId), ownerId);
    public static readonly OperatorKillActor OutOfBand = new(OperatorKillActors.OutOfBand, null);
    public static readonly OperatorKillActor Drill = new(OperatorKillActors.Drill, null);
}

/// <summary>What the access store changed for one kill: the principals disabled and the sessions revoked.</summary>
public sealed record OperatorAccessEnded(DateTime At, IReadOnlyList<string> OperatorAgentIds, IReadOnlyList<string> SessionIds);

public sealed record OperatorKillPropagation(
    string State,
    int Attempts = 0,
    DateTime? LastAttemptAt = null,
    string? LastError = null,
    IReadOnlyList<string>? RevokedPrincipalIds = null,
    int CancelledOperations = 0,
    DateTime? CompletedAt = null);

/// <summary>One use of the kill switch as the Owner sees it in Settings &gt; Access.</summary>
public sealed record OperatorKillRecord
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Trigger { get; init; }
    public required string Actor { get; init; }
    public string? Reason { get; init; }
    public DateTime At { get; init; }
    public IReadOnlyList<string> OperatorAgentIds { get; init; } = [];
    public IReadOnlyList<string> SessionIds { get; init; } = [];
    /// <summary><c>true</c> when the kill covered every operator agent rather than named ones.</summary>
    public bool AllOperators { get; init; }
    public OperatorKillPropagation OperationsServer { get; init; } = new(OperatorKillPropagationStates.NotRequired);
    /// <summary><c>null</c> until the Owner notice reached the operator feed; the out-of-band command leaves it for the API.</summary>
    public DateTime? NotifiedAt { get; init; }
    public string? RequestId { get; init; }
}

/// <summary>One drill: the switch exercised against a test principal, with the measured time.</summary>
public sealed record OperatorKillDrillResult(
    string Id,
    string Trigger,
    DateTime StartedAt,
    double DurationMs,
    double? StudioRefusedMs,
    string OperationsServerState,
    double? OperationsServerMs,
    bool OtherSessionsKept,
    bool Passed,
    string? Failure,
    string? OperatorAgentId);

/// <summary>The kill switch history and drill schedule, persisted next to the access state.</summary>
public sealed record OperatorKillSwitchState
{
    public List<OperatorKillRecord> Kills { get; init; } = [];
    public List<OperatorKillDrillResult> Drills { get; init; } = [];
    public DateTime? NextDrillAt { get; init; }
}

/// <summary>
/// The out-of-band latch: while this file exists every operator session is
/// refused. The command writes it; the API that holds the host lock applies or
/// re-enforces it and then removes it.
/// </summary>
public sealed record OperatorKillRequest(string Id, DateTime RequestedAt, string? Reason, string? AppliedRecordId = null);

public sealed record EndOperatorAccessRequest(string? Reason);
public sealed record KillOperatorSessionRequest(string? Reason);

public sealed record OperatorKillSwitchStatusResponse(
    IReadOnlyList<OperatorKillRecord> Kills,
    IReadOnlyList<OperatorKillDrillResult> Drills,
    DateTime? NextDrillAt,
    int DrillIntervalHours,
    bool OperationsServerConfigured,
    bool OutOfBandRequestPending);
