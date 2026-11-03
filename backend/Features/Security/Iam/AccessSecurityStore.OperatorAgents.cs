namespace AgentStudio.Security;

/// <summary>
/// Operator agents (security model SM-1, decisions D1 and D11): a service
/// account of origin <see cref="ServiceAccountOrigins.OperatorAgent"/> whose
/// only credentials are child sessions. The Owner creates the agent, starts
/// each session, and alone renews it; a session token lives at most eight
/// hours. Every request made with it is attributed to
/// <c>operator-agent:&lt;id&gt;/session:&lt;sid&gt;</c> and delegated by the
/// Owner who started the session.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const string OperatorSessionTokenPrefix = "oas";
    public static readonly TimeSpan MaxOperatorSessionLifetime = TimeSpan.FromHours(8);

    public IReadOnlyList<ServiceAccount> ListOperatorAgents()
    {
        lock (_gate)
            return StateLocked().ServiceAccounts.Where(IsOperatorAgent)
                .OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ServiceAccount CreateOperatorAgent(CreateOperatorAgentRequest request, string ownerId)
    {
        var name = ValidateName(request.Name, "operator agent");
        var description = ValidateDescription(request.Description);
        var grants = (request.Bindings ?? []).Select(grant => ValidateGrant(IamPrincipalKind.ServiceAccount, grant)).ToList();
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            if (next.ServiceAccounts.Any(account => IsOperatorAgent(account)
                && string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "operator-agent-exists", "An operator agent with this name already exists.");
            var account = new ServiceAccount
            {
                Id = "opa_" + Guid.NewGuid().ToString("N"), Name = name, Description = description, CreatedAt = Now,
                CreatedBy = ownerId, Origin = ServiceAccountOrigins.OperatorAgent, Sessions = [],
            };
            var bindings = grants.Select(grant =>
                NewBinding(IamPrincipalKind.ServiceAccount, account.Id, grant, IamBindingSources.Manual, ownerId)).ToList();
            next.ServiceAccounts.Add(account);
            next.RoleBindings.AddRange(bindings);
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-agent.create", "operator-agent", account.Id, new { account.Name });
            AuditBindings(ownerId, "binding.add", bindings);
            return account;
        }
    }

    /// <summary>Starts a session and returns its bearer once; the store keeps only its hash.</summary>
    public (ServiceAccount Agent, OperatorAgentSession Session, string Secret) StartOperatorSession(
        string agentId, StartOperatorSessionRequest request, string ownerId)
    {
        var name = ValidateTokenName(request.Name);
        var lifetime = OperatorSessionLifetime(request.LifetimeHours);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = ActiveOperatorAgentLocked(next, agentId);
            var now = Now;
            var sessionId = Guid.NewGuid().ToString("N");
            var secret = $"{OperatorSessionTokenPrefix}.{sessionId}.{PasswordSecretHasher.RandomToken()}";
            var session = new OperatorAgentSession
            {
                Id = sessionId, Name = name, SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now,
                CreatedBy = ownerId, ExpiresAt = now + lifetime,
            };
            var updated = agent with { Sessions = [.. agent.Sessions ?? [], session] };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = updated;
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-session.start", "operator-agent", agentId,
                new { sessionId, session.Name, session.ExpiresAt });
            return (updated, session, secret);
        }
    }

    /// <summary>Extends an active session to at most eight hours from now. Only an active Owner may renew.</summary>
    public OperatorAgentSession RenewOperatorSession(string agentId, string sessionId, RenewOperatorSessionRequest request, string ownerId)
    {
        var lifetime = OperatorSessionLifetime(request.LifetimeHours);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = ActiveOperatorAgentLocked(next, agentId);
            var session = agent.Sessions?.FirstOrDefault(candidate => candidate.Id == sessionId)
                ?? throw new SecurityOperationException(404, "operator-session-not-found", "Operator session not found.");
            var now = Now;
            if (session.RevokedAt is not null || session.ExpiresAt <= now)
                throw new SecurityOperationException(409, "operator-session-ended",
                    "This session has ended. Start a new session instead.");
            var renewed = session with { ExpiresAt = now + lifetime, RenewedAt = now, RenewedBy = ownerId };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions!.Select(candidate => candidate.Id == sessionId ? renewed : candidate).ToList(),
            };
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-session.renew", "operator-agent", agentId, new { sessionId, renewed.ExpiresAt });
            return renewed;
        }
    }

    public void RevokeOperatorSession(string agentId, string sessionId, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == agentId && IsOperatorAgent(candidate))
                ?? throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found.");
            if (agent.Sessions?.Any(session => session.Id == sessionId) != true)
                throw new SecurityOperationException(404, "operator-session-not-found", "Operator session not found.");
            var now = Now;
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions!.Select(session => session.Id == sessionId && session.RevokedAt is null
                    ? session with { RevokedAt = now, RevokedBy = ownerId } : session).ToList(),
            };
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-session.revoke", "operator-agent", agentId, new { sessionId });
        }
    }

    /// <summary>Disables the agent and revokes every session.</summary>
    public void DisableOperatorAgent(string agentId, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == agentId && IsOperatorAgent(candidate))
                ?? throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found.");
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = DisabledLocked(agent, Now, ownerId);
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-agent.disable", "operator-agent", agentId, null);
        }
    }

    /// <summary>
    /// Authenticates an <c>oas.*</c> session bearer. The session must be
    /// unrevoked and unexpired, its agent enabled, and the Owner who started it
    /// still an active Owner: a session never outlives its delegation.
    /// </summary>
    public ServiceAccountPrincipal? AuthenticateOperatorSession(string? bearer)
    {
        if (!TryTokenParts(bearer, OperatorSessionTokenPrefix, out var sessionId)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var agent = state.ServiceAccounts.FirstOrDefault(candidate => IsOperatorAgent(candidate)
                && candidate.Sessions?.Any(session => session.Id == sessionId) == true);
            var session = agent?.Sessions!.First(candidate => candidate.Id == sessionId);
            if (agent is null || session is null || agent.DisabledAt is not null || session.RevokedAt is not null
                || session.ExpiresAt <= now || !PasswordSecretHasher.VerifySecret(bearer!, session.SecretHash))
                return null;
            if (!ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(session.CreatedBy)) return null;
            if (session.LastUsedAt is null || now - session.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                state.ServiceAccounts[state.ServiceAccounts.IndexOf(agent)] = agent with
                {
                    Sessions = agent.Sessions!.Select(candidate => candidate.Id == sessionId
                        ? candidate with { LastUsedAt = now } : candidate).ToList(),
                };
                SaveLocked(state);
            }
            return new ServiceAccountPrincipal(agent.Id, agent.Name, session.Id, agent.Origin, agent.CreatedBy,
                new AgentStudio.Shared.OperatorActor(agent.Id, session.Id, session.CreatedBy));
        }
    }

    public static string OperatorSessionState(OperatorAgentSession session, DateTime now)
        => session.RevokedAt is not null ? OperatorSessionStates.Revoked
            : session.ExpiresAt <= now ? OperatorSessionStates.Expired
            : OperatorSessionStates.Active;

    public OperatorAgentResponse ToOperatorAgentResponse(ServiceAccount agent)
    {
        var now = Now;
        return new OperatorAgentResponse(agent.Id, agent.Name, agent.Description, agent.CreatedAt, agent.CreatedBy, agent.DisabledAt,
            (agent.Sessions ?? []).OrderByDescending(session => session.CreatedAt)
                .Select(session => ToOperatorSessionResponse(agent.Id, session, now)).ToList());
    }

    public OperatorSessionResponse ToOperatorSessionResponse(string agentId, OperatorAgentSession session)
        => ToOperatorSessionResponse(agentId, session, Now);

    private static OperatorSessionResponse ToOperatorSessionResponse(string agentId, OperatorAgentSession session, DateTime now)
    {
        var actor = new AgentStudio.Shared.OperatorActor(agentId, session.Id, session.CreatedBy);
        return new OperatorSessionResponse(session.Id, agentId, session.Name, actor.Actor, actor.DelegatedBy, session.CreatedAt,
            session.CreatedBy, session.ExpiresAt, session.RenewedAt, session.LastUsedAt, session.RevokedAt,
            OperatorSessionState(session, now));
    }

    private static bool IsOperatorAgent(ServiceAccount account) => account.Origin == ServiceAccountOrigins.OperatorAgent;

    private static ServiceAccount DisabledLocked(ServiceAccount account, DateTime now, string? actingUserId) => account with
    {
        DisabledAt = account.DisabledAt ?? now,
        Tokens = account.Tokens.Select(token => token with { RevokedAt = token.RevokedAt ?? now }).ToList(),
        Sessions = account.Sessions?.Select(session => session.RevokedAt is null
            ? session with { RevokedAt = now, RevokedBy = actingUserId } : session).ToList(),
    };

    private static ServiceAccount ActiveOperatorAgentLocked(SecurityState state, string agentId)
        => state.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == agentId && IsOperatorAgent(candidate)
               && candidate.DisabledAt is null)
           ?? throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found or disabled.");

    private static void RequireActiveOwnerLocked(SecurityState state, string? userId)
    {
        if (userId is null || !ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(userId))
            throw new SecurityOperationException(403, "owner-required", "Only the Owner manages operator agents and their sessions.");
    }

    private static TimeSpan OperatorSessionLifetime(int? hours)
    {
        if (hours is null) return MaxOperatorSessionLifetime;
        if (hours is < 1 or > 8)
            throw new SecurityOperationException(400, "invalid-lifetime", "An operator session lasts 1 to 8 hours.");
        return TimeSpan.FromHours(hours.Value);
    }
}
