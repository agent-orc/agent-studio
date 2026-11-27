using System.Text.Json.Serialization;

namespace AgentStudio.Security;

/// <summary>ES-D5 L2 and ES-D15 A. Null role limits mean until revoked; zero in a choice means until revoked.</summary>
public sealed record OperatorSessionLimits(int DefaultLifetimeHours = 168, int? OwnerLifetimeHours = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? AdminLifetimeHours = 168, int IdleHours = 24);
public sealed record OperatorAgentLifetimeLimitRequest(int? LifetimeHours);

public sealed partial class AccessSecurityStore
{
    public static readonly int[] BoundSessionLifetimeHours = [8, 24, 168, 720, 0];

    public OperatorSessionLimits GetOperatorSessionLimits()
    {
        lock (_gate) return StateLocked().OperatorSessionLimits;
    }

    public OperatorSessionLimits SetOperatorSessionLimits(OperatorSessionLimits limits, HumanPrincipal owner)
    {
        ValidateLifetimeChoice(limits.DefaultLifetimeHours);
        if (limits.OwnerLifetimeHours is { } own) ValidateLifetimeChoice(own);
        if (limits.AdminLifetimeHours is { } admin) ValidateLifetimeChoice(admin);
        if (limits.IdleHours is < 1 or > 168)
            throw new SecurityOperationException(400, "invalid-idle-limit", "The idle limit is 1 hour to 7 days.");
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            ConsumeStepUpLocked(next, owner, "Verify a second factor before changing session limits.");
            var before = next.OperatorSessionLimits;
            next.OperatorSessionLimits = limits;
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "operator-session-limits.change", "session-limits", "installation", new { before, limits }, owner.Session.Id);
            return limits;
        }
    }

    public void SetOperatorAgentLifetimeLimit(string agentId, int? hours, HumanPrincipal owner)
    {
        if (hours is { } value) ValidateLifetimeChoice(value);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            ConsumeStepUpLocked(next, owner, "Verify a second factor before changing an agent lifetime limit.");
            var agent = ActiveOperatorAgentLocked(next, agentId);
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with { OperatorLifetimeLimitHours = hours };
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "operator-agent.lifetime-limit", "operator-agent", agentId,
                new { before = agent.OperatorLifetimeLimitHours, hours }, owner.Session.Id);
        }
    }

    private static void ValidateLifetimeChoice(int hours)
    {
        if (!BoundSessionLifetimeHours.Contains(hours))
            throw new SecurityOperationException(400, "invalid-lifetime", "Choose 8 hours, 24 hours, 7 days, 30 days or until revoked.");
    }

    private static int? FiniteLimit(int? hours) => hours == 0 ? null : hours;
    private static int? EffectiveSessionLimit(SecurityState state, ServiceAccount agent, string? bindingKind)
    {
        // Only Owners confirm today. The Admin limit is stored for project-scoped confirmation.
        var limits = new int?[] { FiniteLimit(state.OperatorSessionLimits.OwnerLifetimeHours),
            FiniteLimit(agent.OperatorLifetimeLimitHours), bindingKind is null ? 8 : bindingKind == "memory" ? 24 : null };
        return limits.Where(value => value.HasValue).Select(value => value!.Value).Cast<int?>().Min();
    }

    private static int[] AllowedSessionLifetimes(SecurityState state, ServiceAccount agent, string bindingKind)
        => BoundSessionLifetimeHours.Where(hours => EffectiveSessionLimit(state, agent, bindingKind) is not { } max
            || (hours != 0 && hours <= max)).ToArray();

    private static int DefaultSessionLifetime(SecurityState state, ServiceAccount agent, OperatorSignIn signIn)
    {
        var allowed = AllowedSessionLifetimes(state, agent, signIn.BindingKind);
        var preferred = state.OperatorSessionLimits.DefaultLifetimeHours;
        if (signIn.RequestedLifetimeHours is { } requested && (preferred == 0 || requested < preferred)) preferred = requested;
        return allowed.Where(hours => preferred == 0 || (hours != 0 && hours <= preferred)).Last();
    }

    private static DateTime OperatorIdleEnd(SecurityState state, OperatorAgentSession session)
        => (session.LastUsedAt ?? session.CreatedAt).AddHours(state.OperatorSessionLimits.IdleHours);

    private static bool SessionEnded(SecurityState state, OperatorAgentSession session, DateTime now)
        => session.RevokedAt is not null || session.ExpiresAt <= now
            || OperatorIdleEnd(state, session) <= now
            || (session.Device is not null && (session.KeyExpiresAt ?? session.CreatedAt.AddMinutes(60)) <= now);

    private static bool ConfirmingPersonHasFactor(SecurityState state, OperatorAgentSession session)
        => session.Device is null || state.Users.Any(user => user.Id == session.CreatedBy && !user.Disabled
            && (!string.IsNullOrEmpty(user.TotpSecret) || user.Passkeys.Count > 0));

    public void RecordOperatorSessionUse(string bearer, string? address)
    {
        lock (_gate)
        {
            if (AuthenticateOperatorSession(bearer, recordUse: false) is not { } principal) return;
            var next = Copy(StateLocked());
            var agent = next.ServiceAccounts.First(account => account.Id == principal.AccountId);
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions!.Select(session => session.Id != principal.TokenId ? session : session with
                {
                    LastUsedAt = Now, LastAddress = address ?? session.LastAddress,
                    UsedAddresses = RememberAddress(session.UsedAddresses, address),
                }).ToList(),
            };
            SaveLocked(next);
        }
    }

    private static List<string> RememberAddress(List<string> addresses, string? address)
        => address is null || addresses.Contains(address) ? addresses : [.. addresses.TakeLast(255), address];

    public void ActOnElevatedSession(string agentId, string sessionId, string action, string ownerId)
    {
        if (action == "end") { RevokeOperatorSession(agentId, sessionId, ownerId); return; }
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = ActiveOperatorAgentLocked(next, agentId);
            if (agent.Sessions?.Any(session => session.Id == sessionId) != true)
                throw new SecurityOperationException(404, "operator-session-not-found", "Operator session not found.");
            if (action is not ("end-elevation" or "keep"))
                throw new SecurityOperationException(400, "invalid-action", "Unknown session action.");
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions.Select(session => session.Id != sessionId ? session : action == "keep"
                    ? session with { ReviewedAt = Now } : session with { Classes = [], Elevation = null, ElevationRateEndedAt = null }).ToList(),
            };
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-session." + action, "operator-agent", agentId, new { sessionId });
        }
    }

    public void EndAllElevatedSessions(string ownerId)
    {
        lock (_gate)
        {
            RequireActiveOwnerLocked(StateLocked(), ownerId);
            foreach (var agent in ListOperatorAgents())
            foreach (var session in agent.Sessions ?? [])
                if (session.RevokedAt is null && (session.ElevationRateEndedAt is not null
                    || !SessionEnded(StateLocked(), session, Now) && IsElevatedSession(session)))
                    RevokeOperatorSession(agent.Id, session.Id, ownerId);
        }
    }

    public static bool IsElevatedSession(OperatorAgentSession session)
        => session.Classes?.Count > 0 || session.ExpiresAt - session.CreatedAt > TimeSpan.FromHours(8);
}
