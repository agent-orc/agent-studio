namespace AgentStudio.Security;

/// <summary>
/// Owner approval of T2 operator requests (security model SM-5, decision
/// SM-D8 option A). The policy step opens an approval request for a T2
/// request; only an active Owner approves or rejects it, each after a fresh
/// step-up, and the deciding actor is the Owner's session user. An approved
/// request runs once, from the same session, with the same parameter digest,
/// within 15 minutes; its result is recorded against the approval. Disabling
/// the agent or revoking the session cancels its open approvals. Every change
/// goes to the IAM audit.
/// </summary>
public sealed partial class AccessSecurityStore
{
    /// <summary>Open (pending or approved) requests one session may hold, so an agent cannot flood the Owner.</summary>
    public const int MaxOpenApprovalsPerSession = 20;
    private const int MaxApprovalNoteLength = 500;
    /// <summary>Settled approvals are kept this long for the Owner's review, then dropped from the identity store.</summary>
    private static readonly TimeSpan ApprovalRetention = TimeSpan.FromDays(30);

    public IReadOnlyList<OperatorApproval> ListOperatorApprovals(string? principalId = null)
    {
        lock (_gate)
            return StateLocked().OperatorApprovals
                .Where(approval => principalId is null || approval.PrincipalId == principalId)
                .OrderByDescending(approval => approval.RequestedAt).ThenBy(approval => approval.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Opens an approval request for a T2 request, or answers the open request
    /// of the same session for the same operation and digest, so a retried
    /// request does not ask the Owner twice.
    /// </summary>
    public (OperatorApproval Approval, bool Created) RequestOperatorApproval(OperatorApprovalRequestFacts facts)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var now = Now;
            var existing = next.OperatorApprovals.FirstOrDefault(approval => approval.SessionId == facts.SessionId
                && approval.PrincipalId == facts.PrincipalId && approval.OperationId == facts.Operation.Id
                && approval.ParameterDigest == facts.ParameterDigest
                && OperatorApprovalPolicy.State(approval, now) == OperatorApprovalStates.Pending);
            if (existing is not null) return (existing, false);
            if (next.OperatorApprovals.Count(approval => approval.SessionId == facts.SessionId
                    && OperatorApprovalPolicy.IsOpen(approval, now)) >= MaxOpenApprovalsPerSession)
                throw new SecurityOperationException(429, "approval-queue-full",
                    $"This session already has {MaxOpenApprovalsPerSession} open approval requests. Wait for the Owner's decisions.");
            next.OperatorApprovals.RemoveAll(approval => !OperatorApprovalPolicy.IsOpen(approval, now)
                                                         && approval.RequestedAt < now - ApprovalRetention);
            var created = new OperatorApproval
            {
                Id = "apr_" + Guid.NewGuid().ToString("N"), PrincipalId = facts.PrincipalId, SessionId = facts.SessionId,
                Actor = facts.Actor, DelegatedBy = facts.DelegatedBy, OperationId = facts.Operation.Id, Tier = facts.Operation.Tier,
                Method = facts.Method, Path = facts.Path, Query = facts.Query, Parameters = facts.Parameters,
                ParameterDigest = facts.ParameterDigest, Target = facts.Target, InstructionSource = facts.InstructionSource,
                Reason = facts.Reason, RequestedAt = now, ExpiresAt = now + OperatorApprovalPolicy.Lifetime,
            };
            next.OperatorApprovals.Add(created);
            SaveLocked(next);
            AppendIamAudit(facts.Actor, "operator-approval.request", "operator-approval", created.Id, new
            {
                created.PrincipalId, created.SessionId, operation = created.OperationId, created.Method, created.Path, created.Query,
                created.ParameterDigest, created.Target, created.InstructionSource, created.Reason, created.ExpiresAt,
            });
            return (created, true);
        }
    }

    /// <summary>
    /// Records the Owner's decision. The actor is <paramref name="owner"/>, the
    /// authenticated session's user; the request body carries only a note.
    /// </summary>
    public OperatorApproval DecideOperatorApproval(string approvalId, bool approve, DecideOperatorApprovalRequest request, HumanPrincipal owner)
    {
        var note = ValidateApprovalNote(request.Note);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            if (!ActiveOwnerIds(next.Users, next.RoleBindings, next.Groups).Contains(owner.User.Id))
                throw new SecurityOperationException(403, "owner-required", "Only the Owner approves or rejects operator requests.");
            var approval = next.OperatorApprovals.FirstOrDefault(candidate => candidate.Id == approvalId)
                ?? throw new SecurityOperationException(404, OperatorApprovalReasons.NotFound, "Approval request not found.");
            var now = Now;
            if (OperatorApprovalPolicy.DecideRefusal(approval, now) is { } refusal)
                throw new SecurityOperationException(409, refusal, refusal switch
                {
                    OperatorApprovalReasons.Expired => "This request was not decided within 15 minutes. The agent must ask again.",
                    OperatorApprovalReasons.Cancelled => "This request was cancelled.",
                    _ => "This request is already decided.",
                });
            ConsumeStepUpLocked(next, owner, "Verify a second factor before deciding an approval request.");
            var decided = approval with
            {
                Approved = approve, DecidedBy = owner.User.Id, DecidedAt = now, DecisionNote = note,
                ApprovalExpiresAt = approve ? now + OperatorApprovalPolicy.Lifetime : null,
            };
            next.OperatorApprovals[next.OperatorApprovals.IndexOf(approval)] = decided;
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, approve ? "operator-approval.approve" : "operator-approval.reject", "operator-approval",
                approvalId, new { decided.PrincipalId, decided.SessionId, operation = decided.OperationId, decided.ParameterDigest,
                    decided.DecisionNote, decided.ApprovalExpiresAt });
            return decided;
        }
    }

    /// <summary>
    /// Decides whether a T2 request may run under an approval and, when it
    /// may and <paramref name="consume"/> is set, marks the approval used in
    /// the same lock, so a replay or a concurrent second request finds it used.
    /// Without <paramref name="consume"/> it only answers, so the policy step
    /// can refuse a replayed, changed, or expired request before it resolves
    /// the target.
    /// </summary>
    public OperatorPolicyDecision RedeemOperatorApproval(
        string approvalId, string principalId, string sessionId, OperatorOperation operation, string parameterDigest, bool consume = true)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var approval = state.OperatorApprovals.FirstOrDefault(candidate => candidate.Id == approvalId);
            var refusal = OperatorApprovalPolicy.RedeemRefusal(approval, principalId, sessionId, operation.Id, parameterDigest, now);
            if (refusal is not null)
                return OperatorPolicyDecision.Refuse(refusal, operation, OperatorApprovalPolicy.RefusalMessage(refusal, operation))
                    with { ApprovalId = refusal == OperatorApprovalReasons.NotFound ? null : approval!.Id };
            if (!consume) return OperatorPolicyDecision.AdmitApproved(operation, approval!.Id);
            state.OperatorApprovals[state.OperatorApprovals.IndexOf(approval!)] = approval! with { UsedAt = now };
            SaveLocked(state);
            return OperatorPolicyDecision.AdmitApproved(operation, approval!.Id);
        }
    }

    /// <summary>Records the answer of the request an approval admitted; the first result stands.</summary>
    public void RecordOperatorApprovalResult(string approvalId, int status)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var approval = state.OperatorApprovals.FirstOrDefault(candidate => candidate.Id == approvalId);
            if (approval is null || approval.ResultAt is not null) return;
            var recorded = approval with { ResultStatus = status, ResultAt = Now };
            state.OperatorApprovals[state.OperatorApprovals.IndexOf(approval)] = recorded;
            SaveLocked(state);
            AppendIamAudit(approval.Actor, "operator-approval.execute", "operator-approval", approvalId,
                new { recorded.PrincipalId, recorded.SessionId, operation = recorded.OperationId, recorded.ParameterDigest, status });
        }
    }

    public OperatorApprovalResponse ToOperatorApprovalResponse(OperatorApproval approval)
    {
        string? name;
        lock (_gate)
            name = StateLocked().ServiceAccounts.FirstOrDefault(account => account.Id == approval.PrincipalId)?.Name;
        return new OperatorApprovalResponse(approval.Id, approval.PrincipalId, name, approval.SessionId, approval.Actor,
            approval.DelegatedBy, approval.OperationId, OperatorOperationCatalog.Find(approval.OperationId)?.Title ?? approval.OperationId,
            approval.Tier, approval.Method, approval.Path, approval.Query, approval.Parameters, approval.ParameterDigest, approval.Target,
            approval.InstructionSource, approval.Reason, approval.RequestedAt, approval.ExpiresAt,
            OperatorApprovalPolicy.State(approval, Now), approval.DecidedBy, approval.DecidedAt, approval.DecisionNote,
            approval.ApprovalExpiresAt, approval.UsedAt, approval.ResultStatus, approval.CancelledAt, approval.CancelReason);
    }

    /// <summary>Cancels every open approval <paramref name="match"/> selects; the caller saves and audits.</summary>
    private static List<OperatorApproval> CancelOpenApprovalsLocked(
        SecurityState state, Func<OperatorApproval, bool> match, DateTime now, string reason)
    {
        var cancelled = new List<OperatorApproval>();
        for (var index = 0; index < state.OperatorApprovals.Count; index++)
        {
            var approval = state.OperatorApprovals[index];
            if (!match(approval) || !OperatorApprovalPolicy.IsOpen(approval, now)) continue;
            state.OperatorApprovals[index] = approval with { CancelledAt = now, CancelReason = reason };
            cancelled.Add(state.OperatorApprovals[index]);
        }
        return cancelled;
    }

    private void AuditCancelledApprovals(string? actingUserId, IEnumerable<OperatorApproval> cancelled)
    {
        foreach (var approval in cancelled)
            AppendIamAudit(actingUserId, "operator-approval.cancel", "operator-approval", approval.Id,
                new { approval.PrincipalId, approval.SessionId, approval.CancelReason });
    }

    private static string? ValidateApprovalNote(string? note)
    {
        var value = note?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > MaxApprovalNoteLength)
            throw new SecurityOperationException(400, "invalid-note", $"A note has at most {MaxApprovalNoteLength} characters.");
        return value;
    }
}
