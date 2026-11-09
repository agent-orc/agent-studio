using System.Globalization;

namespace AgentStudio.Security;

/// <summary>The typed reasons the operator policy step refuses with (security model SM-4).</summary>
public static class OperatorPolicyReasons
{
    public const string NotCatalogued = "operation-not-catalogued";
    public const string ApprovalRequired = "approval-required";
    public const string GrantRequired = "grant-required";
    public const string GrantRateExceeded = "grant-rate-exceeded";
    public const string GrantOutsideWindow = "grant-outside-window";
}

/// <summary>What a T1 request acts on, for grant scope coverage.</summary>
public sealed record OperatorGrantTarget(string? Project = null, string? Environment = null, string? Host = null)
{
    public static readonly OperatorGrantTarget None = new();
}

/// <summary>
/// The policy step's answer; an admitted T1 request names the grant that
/// covered it, and a T2 answer the approval it concerned (SM-5).
/// </summary>
public sealed record OperatorPolicyDecision(
    bool Admitted, string? Reason, OperatorOperation? Operation, string? GrantId, string Message)
{
    public string? ApprovalId { get; init; }

    public static OperatorPolicyDecision Admit(OperatorOperation operation, string? grantId = null)
        => new(true, null, operation, grantId, grantId is null ? $"{operation.Tier} {operation.Id} admitted."
            : $"{operation.Tier} {operation.Id} admitted under standing grant {grantId}.");

    public static OperatorPolicyDecision AdmitApproved(OperatorOperation operation, string approvalId)
        => new(true, null, operation, null, $"{operation.Tier} {operation.Id} admitted under the Owner's approval {approvalId}.")
        { ApprovalId = approvalId };

    public static OperatorPolicyDecision Refuse(string reason, OperatorOperation? operation, string message)
        => new(false, reason, operation, null, message);
}

/// <summary>
/// The operator agent's policy step (security model SM-4) as a pure decision:
/// T0 is admitted, T1 only under a live standing grant of the principal that
/// covers the operation, the scope, the time window, and the hourly rate, and
/// T2 is refused with <see cref="OperatorPolicyReasons.ApprovalRequired"/>
/// until the Owner approves the exact request (<see cref="OperatorApprovalPolicy"/>,
/// SM-5). An operation outside the catalogue is refused. The policy reads
/// only grant and approval records.
/// </summary>
public static class OperatorPolicy
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    public static OperatorPolicyDecision Decide(
        OperatorOperation? operation, string principalId, OperatorGrantTarget target, IEnumerable<StandingGrant> grants,
        DateTime now, Func<string, string, bool> sameProject)
        => TierGate(operation) ?? DecideGrant(operation!, principalId, target, grants, now, sameProject);

    /// <summary>The decision the tier alone makes; <c>null</c> for T1, which needs <see cref="DecideGrant"/>.</summary>
    public static OperatorPolicyDecision? TierGate(OperatorOperation? operation) => operation?.Tier switch
    {
        null => OperatorPolicyDecision.Refuse(OperatorPolicyReasons.NotCatalogued, null,
            "This request is not in the operator agent's operation catalogue."),
        OperatorTier.T0 => OperatorPolicyDecision.Admit(operation),
        OperatorTier.T2 => OperatorPolicyDecision.Refuse(OperatorPolicyReasons.ApprovalRequired, operation,
            $"'{operation.Id}' is high risk (T2) and needs the Owner's approval of this exact request."),
        _ => null,
    };

    public static OperatorPolicyDecision DecideGrant(
        OperatorOperation operation, string principalId, OperatorGrantTarget target, IEnumerable<StandingGrant> grants,
        DateTime now, Func<string, string, bool> sameProject)
    {
        var live = grants.Where(grant => grant.PrincipalId == principalId && grant.Tier == OperatorTier.T1
                                         && grant.Operations.Contains(operation.Id, StringComparer.Ordinal)
                                         && IsLive(grant, now) && Covers(grant.Scope, target, sameProject))
            .ToList();
        if (live.Count == 0)
            return OperatorPolicyDecision.Refuse(OperatorPolicyReasons.GrantRequired, operation,
                $"No live standing grant covers '{operation.Id}' in this scope.");
        var inWindow = live.Where(grant => InWindow(grant.Window, now)).ToList();
        if (inWindow.Count == 0)
            return OperatorPolicyDecision.Refuse(OperatorPolicyReasons.GrantOutsideWindow, operation,
                $"The standing grant for '{operation.Id}' does not cover this time of day.");
        var granting = inWindow.Where(grant => UsesInRateWindow(grant, now) < grant.RatePerHour)
            .OrderBy(grant => grant.ExpiresAt).ThenBy(grant => grant.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return granting is null
            ? OperatorPolicyDecision.Refuse(OperatorPolicyReasons.GrantRateExceeded, operation,
                $"The standing grant for '{operation.Id}' has reached its hourly rate.")
            : OperatorPolicyDecision.Admit(operation, granting.Id);
    }

    public static bool IsLive(StandingGrant grant, DateTime now)
        => grant.RevokedAt is null && grant.IssuedAt <= now && grant.ExpiresAt > now;

    public static string State(StandingGrant grant, DateTime now)
        => grant.RevokedAt is not null ? StandingGrantStates.Revoked
            : grant.ExpiresAt <= now ? StandingGrantStates.Expired
            : StandingGrantStates.Active;

    public static int UsesInRateWindow(StandingGrant grant, DateTime now)
        => grant.Uses.Count(use => use > now - RateWindow && use <= now);

    /// <summary>True when a grant of <paramref name="scope"/> covers the target.</summary>
    public static bool Covers(StandingGrantScope scope, OperatorGrantTarget target, Func<string, string, bool> sameProject)
        => scope.Kind switch
        {
            OperatorScopeKinds.Workspace => true,
            OperatorScopeKinds.Project => scope.Project is { } project && target.Project is { } requested
                                          && sameProject(project, requested),
            OperatorScopeKinds.Environment => scope.Project is { } project && target.Project is { } requested
                                              && sameProject(project, requested)
                                              && string.Equals(scope.Environment, target.Environment, StringComparison.OrdinalIgnoreCase),
            OperatorScopeKinds.Host => scope.Host is { } host && string.Equals(host, target.Host, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>True when a grant of this scope kind can ever cover an operation of <paramref name="operationScopeKind"/>.</summary>
    public static bool ScopeKindCovers(string grantScopeKind, string operationScopeKind) => grantScopeKind switch
    {
        OperatorScopeKinds.Workspace => true,
        OperatorScopeKinds.Project => operationScopeKind is OperatorScopeKinds.Project or OperatorScopeKinds.Environment,
        OperatorScopeKinds.Environment => operationScopeKind == OperatorScopeKinds.Environment,
        OperatorScopeKinds.Host => operationScopeKind == OperatorScopeKinds.Host,
        _ => false,
    };

    public static bool InWindow(StandingGrantWindow? window, DateTime now)
    {
        if (window is null) return true;
        if (!TryTimeOfDay(window.StartUtc, out var start) || !TryTimeOfDay(window.EndUtc, out var end)) return false;
        var time = now.TimeOfDay;
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    public static bool TryTimeOfDay(string? value, out TimeSpan time)
    {
        time = default;
        return value is { Length: 5 }
               && TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time);
    }
}
