namespace AgentStudio.Security;

/// <summary>
/// The access-state half of the operator kill switch (security model SM-2,
/// decisions D1 and D7): ending operator principals and sessions, the
/// out-of-band latch that refuses every session while it exists, and the
/// test principal of a drill. <see cref="OperatorKillSwitch"/> coordinates the
/// audit entry, the Operations Server revocation, and the Owner notice.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const string OperatorKillRequestFileName = "operator-kill-switch.request.json";
    public static readonly TimeSpan DrillSessionLifetime = TimeSpan.FromHours(1);
    private const string KillSwitchCancelReason = "operator kill switch";

    public string OperatorKillRequestPath => Path.Combine(SecurityFolder, OperatorKillRequestFileName);

    /// <summary>True while an out-of-band kill is written but not yet enforced by the API that holds the host lock.</summary>
    public bool OperatorKillRequested => File.Exists(OperatorKillRequestPath);

    /// <summary>
    /// Disables every operator agent (<paramref name="agentIds"/> <c>null</c>)
    /// or the named ones, revokes each of their sessions and standing grants,
    /// and cancels their open approval requests in one save, so the next
    /// request of any of them is refused and no approved request runs. A
    /// Studio kill requires an active Owner; the out-of-band command and a
    /// drill do not.
    /// </summary>
    public OperatorAccessEnded EndOperatorAccess(IReadOnlyCollection<string>? agentIds, OperatorKillActor actor)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            if (actor.OwnerId is not null) RequireActiveOwnerLocked(next, actor.OwnerId);
            var now = Now;
            var targets = next.ServiceAccounts.Where(account => IsOperatorAgent(account)
                && (agentIds is null || agentIds.Contains(account.Id, StringComparer.Ordinal))).ToList();
            if (agentIds is not null && targets.Count != agentIds.Distinct(StringComparer.Ordinal).Count())
                throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found.");
            var sessions = targets.SelectMany(account => (account.Sessions ?? [])
                .Where(session => session.RevokedAt is null && session.ExpiresAt > now).Select(session => session.Id)).ToList();
            foreach (var account in targets)
                next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = DisabledLocked(account, now, actor.OwnerId ?? actor.Actor);
            var grants = RevokeKilledGrantsLocked(next, targets, now, actor.OwnerId ?? actor.Actor);
            var approvals = CancelKilledApprovalsLocked(next, targets, now);
            SaveLocked(next);
            AuditKilledGrants(actor.OwnerId, grants);
            AuditCancelledApprovals(actor.OwnerId, approvals);
            return new OperatorAccessEnded(now, targets.Select(account => account.Id).ToList(), sessions)
            {
                StandingGrantIds = grants.Select(grant => grant.Id).ToList(),
                ApprovalIds = approvals.Select(approval => approval.Id).ToList(),
            };
        }
    }

    /// <summary>
    /// Ends one session of an operator agent and cancels the open approval
    /// requests of that session; the agent and its other sessions keep working.
    /// </summary>
    public OperatorAccessEnded KillOperatorSession(string agentId, string sessionId, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == agentId && IsOperatorAgent(candidate))
                ?? throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found.");
            var session = agent.Sessions?.FirstOrDefault(candidate => candidate.Id == sessionId)
                ?? throw new SecurityOperationException(404, "operator-session-not-found", "Operator session not found.");
            var now = Now;
            if (session.RevokedAt is not null || session.ExpiresAt <= now || agent.DisabledAt is not null)
                throw new SecurityOperationException(409, "operator-session-ended", "This session has already ended.");
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions!.Select(candidate => candidate.Id == sessionId
                    ? candidate with { RevokedAt = now, RevokedBy = ownerId } : candidate).ToList(),
            };
            var approvals = CancelOpenApprovalsLocked(next,
                approval => approval.PrincipalId == agentId && approval.SessionId == sessionId, now, KillSwitchCancelReason);
            SaveLocked(next);
            AuditCancelledApprovals(ownerId, approvals);
            return new OperatorAccessEnded(now, [agentId], [sessionId])
            {
                ApprovalIds = approvals.Select(approval => approval.Id).ToList(),
            };
        }
    }

    /// <summary>
    /// Re-applies an out-of-band kill to the state this process holds: every
    /// operator agent created before the request is disabled, its sessions and
    /// grants revoked, and its open approvals cancelled. Changes nothing when
    /// the state already reflects the kill.
    /// </summary>
    public OperatorAccessEnded EnforceOperatorKill(DateTime requestedAt)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var now = Now;
            var targets = next.ServiceAccounts.Where(account => IsOperatorAgent(account) && account.CreatedAt <= requestedAt
                && (account.DisabledAt is null || account.Sessions?.Any(session => session.RevokedAt is null) == true
                    || next.StandingGrants.Any(grant => grant.PrincipalId == account.Id && grant.RevokedAt is null)
                    || next.OperatorApprovals.Any(approval => approval.PrincipalId == account.Id
                        && OperatorApprovalPolicy.IsOpen(approval, now)))).ToList();
            var sessions = targets.SelectMany(account => (account.Sessions ?? [])
                .Where(session => session.RevokedAt is null && session.ExpiresAt > now).Select(session => session.Id)).ToList();
            if (targets.Count == 0) return new OperatorAccessEnded(now, [], []);
            foreach (var account in targets)
                next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = DisabledLocked(account, now, OperatorKillActors.OutOfBand);
            var grants = RevokeKilledGrantsLocked(next, targets, now, OperatorKillActors.OutOfBand);
            var approvals = CancelKilledApprovalsLocked(next, targets, now);
            SaveLocked(next);
            AuditKilledGrants(null, grants);
            AuditCancelledApprovals(null, approvals);
            return new OperatorAccessEnded(now, targets.Select(account => account.Id).ToList(), sessions)
            {
                StandingGrantIds = grants.Select(grant => grant.Id).ToList(),
                ApprovalIds = approvals.Select(approval => approval.Id).ToList(),
            };
        }
    }

    /// <summary>
    /// Creates a drill's test principal with one session of at most an hour,
    /// delegated by <paramref name="ownerId"/> or, for a scheduled drill, by
    /// the first active Owner. Both steps go to the IAM audit like any other.
    /// </summary>
    public (string AgentId, string SessionId, string Secret) StartDrillOperatorSession(string? ownerId)
    {
        // A test principal created while the latch exists would be refused and, created after the
        // request, outlive the API's re-enforcement; the drill waits until the kill was applied.
        RequireNoOperatorKillRequest();
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var owners = ActiveOwnerIds(next.Users, next.RoleBindings, next.Groups);
            var delegating = ownerId ?? next.Users.Select(user => user.Id).FirstOrDefault(owners.Contains)
                ?? throw new SecurityOperationException(409, "owner-required", "A drill needs an active Owner to delegate its test session.");
            RequireActiveOwnerLocked(next, delegating);
            var now = Now;
            var sessionId = Guid.NewGuid().ToString("N");
            var secret = $"{OperatorSessionTokenPrefix}.{sessionId}.{PasswordSecretHasher.RandomToken()}";
            var account = new ServiceAccount
            {
                Id = "opa_" + Guid.NewGuid().ToString("N"), Name = $"Kill switch drill {now:yyyy-MM-dd HH:mm:ss}",
                CreatedAt = now, CreatedBy = delegating, Origin = ServiceAccountOrigins.OperatorAgent,
                Purpose = ServiceAccountPurposes.KillSwitchDrill,
                Sessions =
                [
                    new OperatorAgentSession
                    {
                        Id = sessionId, Name = "drill", SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now,
                        CreatedBy = delegating, ExpiresAt = now + DrillSessionLifetime,
                    },
                ],
            };
            next.ServiceAccounts.Add(account);
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-agent.create", "operator-agent", account.Id,
                new { account.Name, account.Purpose });
            AppendIamAudit(ownerId, "operator-session.start", "operator-agent", account.Id,
                new { sessionId, name = "drill", expiresAt = now + DrillSessionLifetime });
            return (account.Id, sessionId, secret);
        }
    }

    /// <summary>The live sessions of real (not drill) operator agents; a drill must leave them all working.</summary>
    public IReadOnlySet<string> LiveOperatorSessionIds()
    {
        lock (_gate)
        {
            var now = Now;
            return StateLocked().ServiceAccounts
                .Where(account => IsOperatorAgent(account) && !IsDrillPrincipal(account) && account.DisabledAt is null)
                .SelectMany(account => account.Sessions ?? [])
                .Where(session => session.RevokedAt is null && session.ExpiresAt > now)
                .Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The sessions of drill test principals that would still be admitted; every drill ends with none.</summary>
    internal IReadOnlySet<string> LiveDrillSessionIds()
    {
        lock (_gate)
        {
            var now = Now;
            return StateLocked().ServiceAccounts
                .Where(account => IsOperatorAgent(account) && IsDrillPrincipal(account) && account.DisabledAt is null)
                .SelectMany(account => account.Sessions ?? [])
                .Where(session => session.RevokedAt is null && session.ExpiresAt > now)
                .Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>One kill switch entry in the control plane audit trail; the details carry the kill record in the entry's own casing.</summary>
    public ControlPlaneAuditEntry AppendOperatorKillAudit(string actor, string operation, string outcome, string target, object details)
    {
        var element = System.Text.Json.JsonSerializer.SerializeToElement(details, details.GetType(), ControlPlaneAuditLog.Json);
        return ControlPlaneAudit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.KillSwitch, actor, operation, outcome, target,
            ParameterDigest: ControlPlaneAuditLog.Sha256Hex(element.GetRawText()), Details: element));
    }

    /// <summary>The live standing grants (SM-4) of the killed principals, revoked in the caller's save.</summary>
    private static List<StandingGrant> RevokeKilledGrantsLocked(SecurityState state, IEnumerable<ServiceAccount> targets, DateTime now,
        string revokedBy)
        => targets.SelectMany(account => RevokeAgentGrantsLocked(state, account.Id, now, revokedBy, "operator kill switch")).ToList();

    /// <summary>The open approval requests (SM-5) of the killed principals, cancelled in the caller's save.</summary>
    private static List<OperatorApproval> CancelKilledApprovalsLocked(SecurityState state, IEnumerable<ServiceAccount> targets, DateTime now)
    {
        var killed = targets.Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
        return CancelOpenApprovalsLocked(state, approval => killed.Contains(approval.PrincipalId), now, KillSwitchCancelReason);
    }

    /// <summary>Each revoked grant goes to the IAM audit like any other grant change.</summary>
    private void AuditKilledGrants(string? ownerId, IEnumerable<StandingGrant> grants)
    {
        foreach (var grant in grants)
            AppendIamAudit(ownerId, "standing-grant.revoke", "standing-grant", grant.Id, new { grant.PrincipalId, grant.RevokeReason });
    }

    private void RequireNoOperatorKillRequest()
    {
        if (OperatorKillRequested)
            throw new SecurityOperationException(409, "operator-kill-pending",
                "An out-of-band kill switch is being applied. Start the session after it finished.");
    }

    private static bool IsDrillPrincipal(ServiceAccount account) => account.Purpose == ServiceAccountPurposes.KillSwitchDrill;
}
