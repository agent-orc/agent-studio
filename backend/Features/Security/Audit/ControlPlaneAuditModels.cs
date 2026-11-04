using System.Text.Json;

namespace AgentStudio.Security;

/// <summary>The audit streams the control plane writer carries (security model SM-1).</summary>
public static class ControlPlaneAuditStreams
{
    public const string Iam = "iam";
    public const string ServiceAccount = "service-account";
    public const string ApiToken = "api-token";
    public const string OperatorAgent = "operator-agent";
    public const string Secret = "secret";
    public const string Run = "run";
    /// <summary>Every use of the operator kill switch and its drills (security model SM-2).</summary>
    public const string KillSwitch = "kill-switch";
}

/// <summary>
/// What a producer hands the control plane audit writer. The writer adds the
/// sequence, the time when none is given, the previous entry's hash, and its
/// own hash. <paramref name="Details"/> carries the producer's full legacy
/// record, so routing an existing audit through the writer drops no field.
/// </summary>
public sealed record ControlPlaneAuditRecord(
    string Stream,
    string Actor,
    string Operation,
    string Outcome,
    string? Target = null,
    string? Session = null,
    string? DelegatedBy = null,
    string? ParameterDigest = null,
    object? Details = null,
    DateTime? At = null);

/// <summary>
/// One stored entry. <see cref="Raw"/> is the exact stored line; its hash
/// covers every byte of the line except the trailing <c>hash</c> member.
/// </summary>
public sealed record ControlPlaneAuditEntry
{
    public long Sequence { get; init; }
    public DateTime At { get; init; }
    public string Stream { get; init; } = "";
    public string Actor { get; init; } = "";
    public string? ReportedActor { get; init; }
    public string? Session { get; init; }
    public string? DelegatedBy { get; init; }
    public string Operation { get; init; } = "";
    public string? Target { get; init; }
    public string? ParameterDigest { get; init; }
    public string Outcome { get; init; } = "";
    public JsonElement? Details { get; init; }
    public string PreviousHash { get; init; } = "";
    public string Hash { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Raw { get; init; } = "";
}

/// <summary>Where forwarding to the off-host sink stands; persisted after every acknowledged batch.</summary>
public sealed record ControlPlaneAuditForwardPosition(long Sequence, string Hash, DateTime? ForwardedAt);

public static class ControlPlaneAuditIntegrityStates
{
    public const string NotRun = "not-run";
    public const string Verified = "verified";
    public const string Failed = "failed";
}

/// <summary>The outcome of one chain verification.</summary>
public sealed record ControlPlaneAuditIntegrity(
    string State, DateTime? CheckedAt, long VerifiedThrough, string? Message, bool Alert,
    bool SinkCompared = false, string? Problem = null, long? ProblemSequence = null);

public sealed record ControlPlaneAuditForwarding(
    string SinkKind, bool Configured, long HeadSequence, long ForwardedSequence, long Pending,
    DateTime? LastForwardedAt, DateTime? OldestPendingAt, string? LastError, bool Alert);

/// <summary>One acute alert of the audit trail: a broken chain or a sink that stopped accepting entries.</summary>
public sealed record ControlPlaneAuditAlert(string Code, string Message, DateTime RaisedAt, long? Sequence = null);

public sealed record ControlPlaneAuditEntryView(
    long Sequence, DateTime At, string Stream, string Actor, string? Session, string? DelegatedBy,
    string Operation, string? Target, string? ParameterDigest, string Outcome, string Hash, string PreviousHash);

public sealed record OperatorActivityResponse(
    IReadOnlyList<OperatorAgentResponse> OperatorAgents,
    IReadOnlyList<ControlPlaneAuditEntryView> Entries,
    ControlPlaneAuditIntegrity Integrity,
    ControlPlaneAuditForwarding Forwarding);
