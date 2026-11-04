namespace AgentStudio.Security;

/// <summary>
/// The request headers an operator session uses around a T2 request (security
/// model SM-5). The instruction source and reason describe the request for the
/// Owner; they never name an actor and never authorize anything.
/// </summary>
public static class OperatorApprovalHeaders
{
    /// <summary>The approval id to run the exact request it approved.</summary>
    public const string Approval = "X-Operator-Approval";
    /// <summary>Where the instruction came from; see <see cref="OperatorInstructionSources"/>.</summary>
    public const string InstructionSource = "X-Operator-Instruction-Source";
    /// <summary>The agent's stated reason, percent-encoded UTF-8 when it is not plain ASCII.</summary>
    public const string Reason = "X-Operator-Reason";
}

/// <summary>Where an operator request's instruction came from (security model, untrusted content).</summary>
public static class OperatorInstructionSources
{
    public const string OwnerChat = "owner-chat";
    public const string Card = "card";
    public const string RepositoryFile = "repository-file";
    public const string WebPage = "web-page";
    public const string Log = "log";
    /// <summary>The request named no known source; the Owner sees that.</summary>
    public const string Unstated = "unstated";

    public static readonly IReadOnlyList<string> Stated = [OwnerChat, Card, RepositoryFile, WebPage, Log];

    public static string Normalize(string? value)
    {
        var source = value?.Trim().ToLowerInvariant();
        return source is not null && Stated.Contains(source) ? source : Unstated;
    }
}

public static class OperatorApprovalStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    /// <summary>The approved request ran; the result is recorded.</summary>
    public const string Used = "used";
    public const string Expired = "expired";
    /// <summary>The agent was disabled or the session ended before the request ran.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One approval request of an operator session (security model SM-5, decision
/// SM-D8 option A). A T2 request creates it instead of an effect; the Owner
/// approves or rejects it in Studio after a step-up. The approval binds the
/// principal, the session, the operation, and the parameter digest of the
/// exact request, runs once, and expires 15 minutes after the decision. The
/// deciding actor is the Owner's authenticated session, never a client value.
/// </summary>
public sealed record OperatorApproval
{
    public required string Id { get; init; }
    public required string PrincipalId { get; init; }
    public required string SessionId { get; init; }
    /// <summary><c>operator-agent:&lt;id&gt;/session:&lt;sid&gt;</c>.</summary>
    public required string Actor { get; init; }
    public required string DelegatedBy { get; init; }
    public required string OperationId { get; init; }
    public OperatorTier Tier { get; init; } = OperatorTier.T2;
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string Query { get; init; } = "";
    /// <summary>The body as the Owner reviews it, secret-like values masked; the digest binds the exact bytes.</summary>
    public string? Parameters { get; init; }
    public required string ParameterDigest { get; init; }
    public string? Target { get; init; }
    public required string InstructionSource { get; init; }
    public string? Reason { get; init; }
    public DateTime RequestedAt { get; init; }
    /// <summary>The Owner decides before this time; 15 minutes after the request.</summary>
    public DateTime ExpiresAt { get; init; }
    public bool? Approved { get; init; }
    public string? DecidedBy { get; init; }
    public DateTime? DecidedAt { get; init; }
    public string? DecisionNote { get; init; }
    /// <summary>An approval runs the request before this time; 15 minutes after the decision.</summary>
    public DateTime? ApprovalExpiresAt { get; init; }
    public DateTime? UsedAt { get; init; }
    public int? ResultStatus { get; init; }
    public DateTime? ResultAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancelReason { get; init; }
}

/// <summary>What the policy step knows about a T2 request when it opens an approval request.</summary>
public sealed record OperatorApprovalRequestFacts(
    string PrincipalId, string SessionId, string Actor, string DelegatedBy, OperatorOperation Operation, string Method,
    string Path, string Query, string? Parameters, string ParameterDigest, string? Target, string InstructionSource, string? Reason);

public sealed record DecideOperatorApprovalRequest(string? Note);

public sealed record OperatorApprovalResponse(
    string Id, string PrincipalId, string? PrincipalName, string SessionId, string Actor, string DelegatedBy, string Operation,
    string OperationTitle, OperatorTier Tier, string Method, string Path, string Query, string? Parameters, string ParameterDigest,
    string? Target, string InstructionSource, string? Reason, DateTime RequestedAt, DateTime ExpiresAt, string State,
    string? DecidedBy, DateTime? DecidedAt, string? DecisionNote, DateTime? ApprovalExpiresAt, DateTime? UsedAt,
    int? ResultStatus, DateTime? CancelledAt, string? CancelReason);
