namespace AgentStudio.Security;

/// <summary>
/// Where a standing grant applies: the workspace, one project, one environment
/// kind of one project (<see cref="IamEnvironments"/>), or one host.
/// </summary>
public sealed record StandingGrantScope(string Kind, string? Project = null, string? Environment = null,
    string? Host = null, IReadOnlyList<string>? Projects = null);

/// <summary>A daily window in UTC, <c>HH:mm</c> each; a start after the end wraps past midnight.</summary>
public sealed record StandingGrantWindow(string StartUtc, string EndUtc);

/// <summary>
/// A recorded T1 authority of one operator agent (security model SM-4). Only
/// the Owner issues, renews, or revokes it; the policy step reads nothing
/// else, so a chat message or an agent's memory never creates authority.
/// </summary>
public sealed record StandingGrant
{
    public required string Id { get; init; }
    /// <summary>The operator agent the grant authorizes.</summary>
    public required string PrincipalId { get; init; }
    public OperatorTier Tier { get; init; } = OperatorTier.T1;
    public List<string> Operations { get; init; } = [];
    public required StandingGrantScope Scope { get; init; }
    public int RatePerHour { get; init; }
    public StandingGrantWindow? Window { get; init; }
    public required string IssuedBy { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? RenewedAt { get; init; }
    public string? RenewedBy { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }
    public required string Reason { get; init; }
    public string? RevokeReason { get; init; }
    /// <summary>Admitted requests of the last hour; the rate limit counts them.</summary>
    public List<DateTime> Uses { get; init; } = [];
}

public static class StandingGrantStates
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

public sealed record CreateStandingGrantRequest(
    string PrincipalId, IReadOnlyList<string>? Operations, StandingGrantScope? Scope, int? RatePerHour,
    StandingGrantWindow? Window, int? ExpiresInDays, string? Reason);

public sealed record RenewStandingGrantRequest(int? ExpiresInDays);

public sealed record RevokeStandingGrantRequest(string? Reason);

public sealed record StandingGrantResponse(
    string Id, string PrincipalId, string? PrincipalName, OperatorTier Tier, IReadOnlyList<string> Operations,
    StandingGrantScope Scope, int RatePerHour, StandingGrantWindow? Window, string IssuedBy, DateTime IssuedAt,
    DateTime ExpiresAt, DateTime? RenewedAt, string? RenewedBy, DateTime? RevokedAt, string? RevokedBy, string Reason,
    string? RevokeReason, string State, int UsesLastHour);

public sealed record OperatorCatalogueResponse(
    IReadOnlyList<OperatorOperation> Operations, int DefaultGrantDays, int MaxGrantDays, int DefaultRatePerHour,
    int MaxRatePerHour);
