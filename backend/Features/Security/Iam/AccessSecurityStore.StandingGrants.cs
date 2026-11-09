namespace AgentStudio.Security;

/// <summary>
/// Standing grants (security model SM-4, decision D7 option A): the recorded
/// T1 authority of one operator agent over an operation set, a scope, an
/// hourly rate, and an optional daily window. Only an active Owner issues and
/// renews a grant, each after a fresh step-up; the Owner revokes without one,
/// so taking authority away never waits for a second factor. A grant lasts
/// seven days by default and at most thirty. Every change goes to the IAM
/// audit, and the policy step reads the grant records live, so a revoked or
/// expired grant stops admitting the next request.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const int DefaultStandingGrantDays = 7;
    public const int MaxStandingGrantDays = 30;
    public const int DefaultStandingGrantRatePerHour = 10;
    public const int MaxStandingGrantRatePerHour = 120;
    private const int MaxGrantReasonLength = 500;

    public IReadOnlyList<StandingGrant> ListStandingGrants(string? principalId = null)
    {
        lock (_gate)
            return StateLocked().StandingGrants
                .Where(grant => principalId is null || grant.PrincipalId == principalId)
                .OrderByDescending(grant => grant.IssuedAt).ThenBy(grant => grant.Id, StringComparer.Ordinal).ToList();
    }

    public StandingGrant CreateStandingGrant(CreateStandingGrantRequest request, HumanPrincipal owner)
    {
        var operations = ValidateGrantOperations(request.Operations);
        var scope = ValidateGrantScope(request.Scope, operations);
        var rate = request.RatePerHour ?? DefaultStandingGrantRatePerHour;
        if (rate is < 1 or > MaxStandingGrantRatePerHour)
            throw new SecurityOperationException(400, "invalid-rate", $"A standing grant allows 1 to {MaxStandingGrantRatePerHour} requests per hour.");
        var window = ValidateGrantWindow(request.Window);
        var lifetime = StandingGrantLifetime(request.ExpiresInDays);
        var reason = ValidateGrantReason(request.Reason);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveGrantOwnerLocked(next, owner.User.Id);
            var agent = ActiveOperatorAgentLocked(next, request.PrincipalId);
            ConsumeStepUpLocked(next, owner);
            var now = Now;
            var grant = new StandingGrant
            {
                Id = "sgr_" + Guid.NewGuid().ToString("N"), PrincipalId = agent.Id, Operations = operations, Scope = scope,
                RatePerHour = rate, Window = window, IssuedBy = owner.User.Id, IssuedAt = now, ExpiresAt = now + lifetime,
                Reason = reason,
            };
            next.StandingGrants.Add(grant);
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "standing-grant.create", "standing-grant", grant.Id, new
            {
                grant.PrincipalId, grant.Operations, grant.Scope, grant.RatePerHour, grant.Window, grant.ExpiresAt, grant.Reason,
            });
            return grant;
        }
    }

    /// <summary>Extends a live grant to the requested days from now, never beyond thirty.</summary>
    public StandingGrant RenewStandingGrant(string grantId, RenewStandingGrantRequest request, HumanPrincipal owner)
    {
        var lifetime = StandingGrantLifetime(request.ExpiresInDays);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveGrantOwnerLocked(next, owner.User.Id);
            var grant = GrantLocked(next, grantId);
            var now = Now;
            if (!OperatorPolicy.IsLive(grant, now))
                throw new SecurityOperationException(409, "standing-grant-ended",
                    "This grant has ended. Issue a new grant instead.");
            ActiveOperatorAgentLocked(next, grant.PrincipalId);
            ConsumeStepUpLocked(next, owner);
            var renewed = grant with { ExpiresAt = now + lifetime, RenewedAt = now, RenewedBy = owner.User.Id };
            next.StandingGrants[next.StandingGrants.IndexOf(grant)] = renewed;
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "standing-grant.renew", "standing-grant", grantId,
                new { renewed.PrincipalId, renewed.ExpiresAt });
            return renewed;
        }
    }

    public StandingGrant RevokeStandingGrant(string grantId, RevokeStandingGrantRequest request, string ownerId)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : ValidateGrantReason(request.Reason);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveGrantOwnerLocked(next, ownerId);
            var grant = GrantLocked(next, grantId);
            if (grant.RevokedAt is not null) return grant;
            var revoked = grant with { RevokedAt = Now, RevokedBy = ownerId, RevokeReason = reason };
            next.StandingGrants[next.StandingGrants.IndexOf(grant)] = revoked;
            SaveLocked(next);
            AppendIamAudit(ownerId, "standing-grant.revoke", "standing-grant", grantId,
                new { revoked.PrincipalId, revoked.RevokeReason });
            return revoked;
        }
    }

    /// <summary>
    /// Decides a T1 request against the principal's grants and, when a grant
    /// admits it, records the use against that grant's hourly rate in the same
    /// lock, so concurrent requests cannot exceed the rate.
    /// </summary>
    public OperatorPolicyDecision AdmitOperatorGrant(
        string principalId, OperatorOperation operation, OperatorGrantTarget target, Func<string, string, bool> sameProject)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var decision = OperatorPolicy.DecideGrant(operation, principalId, target, state.StandingGrants, now, sameProject);
            if (!decision.Admitted) return decision;
            var grant = state.StandingGrants.First(candidate => candidate.Id == decision.GrantId);
            state.StandingGrants[state.StandingGrants.IndexOf(grant)] = grant with
            {
                Uses = [.. grant.Uses.Where(use => use > now - OperatorPolicy.RateWindow), now],
            };
            SaveLocked(state);
            return decision;
        }
    }

    public StandingGrantResponse ToStandingGrantResponse(StandingGrant grant)
    {
        var now = Now;
        string? name;
        lock (_gate)
            name = StateLocked().ServiceAccounts.FirstOrDefault(account => account.Id == grant.PrincipalId)?.Name;
        return new StandingGrantResponse(grant.Id, grant.PrincipalId, name, grant.Tier, grant.Operations, grant.Scope,
            grant.RatePerHour, grant.Window, grant.IssuedBy, grant.IssuedAt, grant.ExpiresAt, grant.RenewedAt, grant.RenewedBy,
            grant.RevokedAt, grant.RevokedBy, grant.Reason, grant.RevokeReason, OperatorPolicy.State(grant, now),
            OperatorPolicy.UsesInRateWindow(grant, now));
    }

    /// <summary>Revokes every live grant of an operator agent; the caller saves and audits.</summary>
    private static List<StandingGrant> RevokeAgentGrantsLocked(SecurityState state, string agentId, DateTime now, string? actingUserId,
        string reason)
    {
        var revoked = new List<StandingGrant>();
        for (var index = 0; index < state.StandingGrants.Count; index++)
        {
            var grant = state.StandingGrants[index];
            if (grant.PrincipalId != agentId || grant.RevokedAt is not null) continue;
            state.StandingGrants[index] = grant with { RevokedAt = now, RevokedBy = actingUserId, RevokeReason = reason };
            revoked.Add(state.StandingGrants[index]);
        }
        return revoked;
    }

    private static StandingGrant GrantLocked(SecurityState state, string grantId)
        => state.StandingGrants.FirstOrDefault(grant => grant.Id == grantId)
           ?? throw new SecurityOperationException(404, "standing-grant-not-found", "Standing grant not found.");

    private static void RequireActiveGrantOwnerLocked(SecurityState state, string? userId)
    {
        if (userId is null || !ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(userId))
            throw new SecurityOperationException(403, "owner-required", "Only the Owner issues, renews, or revokes standing grants.");
    }

    private static List<string> ValidateGrantOperations(IReadOnlyList<string>? operations)
    {
        var ids = (operations ?? []).Select(id => id?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            throw new SecurityOperationException(400, "grant-operation-required", "Name at least one operation.");
        foreach (var id in ids)
        {
            var operation = OperatorOperationCatalog.Find(id)
                ?? throw new SecurityOperationException(400, "grant-operation-invalid", $"'{id}' is not a catalogued operation.");
            if (operation.Tier != OperatorTier.T1)
                throw new SecurityOperationException(400, "grant-operation-invalid",
                    $"'{id}' is {operation.Tier}; a standing grant covers only routine (T1) operations.");
        }
        return ids;
    }

    private static StandingGrantScope ValidateGrantScope(StandingGrantScope? scope, IReadOnlyList<string> operations)
    {
        if (scope is null || !OperatorScopeKinds.All.Contains(scope.Kind))
            throw new SecurityOperationException(400, "invalid-scope", "Choose a workspace, project, environment, or host scope.");
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var normalized = scope.Kind switch
        {
            OperatorScopeKinds.Workspace => new StandingGrantScope(scope.Kind),
            OperatorScopeKinds.Project => new StandingGrantScope(scope.Kind, Clean(scope.Project)
                ?? throw new SecurityOperationException(400, "invalid-scope", "A project scope names a project.")),
            OperatorScopeKinds.Environment => new StandingGrantScope(scope.Kind,
                Clean(scope.Project) ?? throw new SecurityOperationException(400, "invalid-scope", "An environment scope names a project."),
                Clean(scope.Environment)?.ToLowerInvariant() is IamEnvironments.Stage or IamEnvironments.Production
                    ? scope.Environment!.Trim().ToLowerInvariant()
                    : throw new SecurityOperationException(400, "invalid-scope", "An environment scope names stage or production.")),
            _ => new StandingGrantScope(scope.Kind, Host: Clean(scope.Host)
                ?? throw new SecurityOperationException(400, "invalid-scope", "A host scope names a host.")),
        };
        var uncovered = operations.Where(id => !OperatorPolicy.ScopeKindCovers(normalized.Kind, OperatorOperationCatalog.Find(id)!.ScopeKind))
            .ToList();
        if (uncovered.Count > 0)
            throw new SecurityOperationException(400, "grant-scope-mismatch",
                $"A {normalized.Kind} scope cannot cover {string.Join(", ", uncovered)}.");
        return normalized;
    }

    private static StandingGrantWindow? ValidateGrantWindow(StandingGrantWindow? window)
    {
        if (window is null) return null;
        if (!OperatorPolicy.TryTimeOfDay(window.StartUtc, out var start) || !OperatorPolicy.TryTimeOfDay(window.EndUtc, out var end)
            || start == end)
            throw new SecurityOperationException(400, "invalid-window", "A time window has a different start and end, each as HH:mm in UTC.");
        return window;
    }

    private static string ValidateGrantReason(string? reason)
    {
        var value = reason?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new SecurityOperationException(400, "reason-required", "Say why the operator agent needs this grant.");
        if (value.Length > MaxGrantReasonLength)
            throw new SecurityOperationException(400, "invalid-reason", $"A reason has at most {MaxGrantReasonLength} characters.");
        return value;
    }

    private static TimeSpan StandingGrantLifetime(int? days)
    {
        if (days is null) return TimeSpan.FromDays(DefaultStandingGrantDays);
        if (days is < 1 or > MaxStandingGrantDays)
            throw new SecurityOperationException(400, "invalid-lifetime", $"A standing grant lasts 1 to {MaxStandingGrantDays} days.");
        return TimeSpan.FromDays(days.Value);
    }
}
