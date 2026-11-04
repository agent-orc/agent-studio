namespace AgentStudio.Security;

/// <summary>The typed reasons a T2 request with an approval id is refused with (security model SM-5).</summary>
public static class OperatorApprovalReasons
{
    /// <summary>No approval with this id belongs to this operator session.</summary>
    public const string NotFound = "approval-not-found";
    public const string Pending = "approval-pending";
    public const string Rejected = "approval-rejected";
    public const string Used = "approval-used";
    public const string Expired = "approval-expired";
    public const string Cancelled = "approval-cancelled";
    /// <summary>The request differs from the approved one: another operation, route, query, or body.</summary>
    public const string DigestMismatch = "approval-digest-mismatch";
    /// <summary>The body is too large to digest in full, so no approval could bind it.</summary>
    public const string Unbindable = "approval-unbindable";
}

/// <summary>
/// The approval lifecycle as pure decisions (security model SM-5): the state
/// an approval is in at a time, whether the Owner may still decide it, and
/// whether a request may run under it. The store applies the answers under
/// its lock, so a single-use approval cannot run twice.
/// </summary>
public static class OperatorApprovalPolicy
{
    /// <summary>How long the Owner has to decide, and how long an approval stays usable after the decision.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static string State(OperatorApproval approval, DateTime now)
        => approval.CancelledAt is not null ? OperatorApprovalStates.Cancelled
            : approval.UsedAt is not null ? OperatorApprovalStates.Used
            : approval.Approved is false ? OperatorApprovalStates.Rejected
            : approval.Approved is true
                ? approval.ApprovalExpiresAt is { } usableUntil && now < usableUntil ? OperatorApprovalStates.Approved : OperatorApprovalStates.Expired
            : now < approval.ExpiresAt ? OperatorApprovalStates.Pending
            : OperatorApprovalStates.Expired;

    /// <summary><c>null</c> when the Owner may approve or reject now, otherwise the refusal code.</summary>
    public static string? DecideRefusal(OperatorApproval approval, DateTime now) => State(approval, now) switch
    {
        OperatorApprovalStates.Pending => null,
        OperatorApprovalStates.Expired => OperatorApprovalReasons.Expired,
        OperatorApprovalStates.Cancelled => OperatorApprovalReasons.Cancelled,
        OperatorApprovalStates.Used => OperatorApprovalReasons.Used,
        // Approved or rejected: a decision is final.
        _ => "approval-decided",
    };

    /// <summary>
    /// <c>null</c> when the request may run under <paramref name="approval"/>,
    /// otherwise the refusal code. The approval must belong to the same
    /// principal and session, name the same operation and parameter digest,
    /// be approved, unused, and inside its 15 minutes.
    /// </summary>
    public static string? RedeemRefusal(
        OperatorApproval? approval, string principalId, string sessionId, string operationId, string parameterDigest, DateTime now)
    {
        if (approval is null || approval.PrincipalId != principalId || approval.SessionId != sessionId)
            return OperatorApprovalReasons.NotFound;
        if (approval.OperationId != operationId || !string.Equals(approval.ParameterDigest, parameterDigest, StringComparison.Ordinal))
            return OperatorApprovalReasons.DigestMismatch;
        return State(approval, now) switch
        {
            OperatorApprovalStates.Approved => null,
            OperatorApprovalStates.Pending => OperatorApprovalReasons.Pending,
            OperatorApprovalStates.Rejected => OperatorApprovalReasons.Rejected,
            OperatorApprovalStates.Used => OperatorApprovalReasons.Used,
            OperatorApprovalStates.Cancelled => OperatorApprovalReasons.Cancelled,
            _ => OperatorApprovalReasons.Expired,
        };
    }

    /// <summary>True while the request still waits for the Owner or for its single run.</summary>
    public static bool IsOpen(OperatorApproval approval, DateTime now)
        => State(approval, now) is OperatorApprovalStates.Pending or OperatorApprovalStates.Approved;

    public static string RefusalMessage(string reason, OperatorOperation operation) => reason switch
    {
        OperatorApprovalReasons.NotFound => "No approval with this id belongs to this operator session.",
        OperatorApprovalReasons.Pending => $"The Owner has not decided the approval request for '{operation.Id}' yet.",
        OperatorApprovalReasons.Rejected => $"The Owner rejected this '{operation.Id}' request.",
        OperatorApprovalReasons.Used => "This approval was used. An approval runs its request once; ask for a new one.",
        OperatorApprovalReasons.Expired => "This approval expired. An approval lasts 15 minutes; ask for a new one.",
        OperatorApprovalReasons.Cancelled => "This approval was cancelled.",
        OperatorApprovalReasons.DigestMismatch => "This request differs from the approved one. An approval covers only the exact request.",
        _ => "The approval does not admit this request.",
    };
}
