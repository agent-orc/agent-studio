using System.Text.Json.Serialization;

namespace AgentStudio.Security;

[JsonConverter(typeof(JsonStringEnumConverter<IamScopeKind>))]
public enum IamScopeKind
{
    [JsonStringEnumMemberName("workspace")] Workspace,
    [JsonStringEnumMemberName("project")] Project,
    [JsonStringEnumMemberName("environment")] Environment,
}

[JsonConverter(typeof(JsonStringEnumConverter<IamPrincipalKind>))]
public enum IamPrincipalKind
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("group")] Group,
    [JsonStringEnumMemberName("service-account")] ServiceAccount,
}

/// <summary>
/// Where a role binding applies: the workspace of this installation, one
/// project, or one environment of one project.
/// </summary>
public sealed record IamScope(IamScopeKind Kind, string? Project = null, string? Environment = null)
{
    public static readonly IamScope Workspace = new(IamScopeKind.Workspace);
    public static IamScope ForProject(string project) => new(IamScopeKind.Project, project);
    public static IamScope ForEnvironment(string project, string environment) => new(IamScopeKind.Environment, project, environment);
}

public static class IamBindingSources
{
    public const string Bootstrap = "bootstrap";
    public const string Migration = "migration";
    public const string RoleAssignment = "role-assignment";
    public const string Manual = "manual";
}

public sealed record RoleBinding
{
    public required string Id { get; init; }
    public required IamPrincipalKind PrincipalKind { get; init; }
    public required string PrincipalId { get; init; }
    public required string Role { get; init; }
    public required IamScope Scope { get; init; }
    public required string Source { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed record StudioGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> Members { get; init; } = [];
    public DateTime CreatedAt { get; init; }
}

/// <summary>A non-human principal with its own API tokens, scopes, expiry, and revocation.</summary>
public sealed record ServiceAccount
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DisabledAt { get; init; }
    public List<ServiceAccountToken> Tokens { get; init; } = [];
}

public sealed record ServiceAccountToken
{
    public required string Id { get; init; }
    public required string SecretHash { get; init; }
    public string? Name { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

public sealed record ServiceAccountPrincipal(string AccountId, string AccountName, string TokenId);

/// <summary>The principal an access decision is made for, with its group memberships.</summary>
public sealed record IamSubject(IamPrincipalKind Kind, string Id, IReadOnlySet<string> GroupIds)
{
    public static IamSubject ForServiceAccount(string id) => new(IamPrincipalKind.ServiceAccount, id, new HashSet<string>());
}

public sealed record IamBindingRequest(IamPrincipalKind PrincipalKind, string PrincipalId, string Role, IamScope Scope);
public sealed record IamGrantRequest(string Role, IamScope Scope);
public sealed record CreateGroupRequest(string Name, string? Description, IReadOnlyList<string>? Members);
public sealed record UpdateGroupRequest(string? Name, string? Description, IReadOnlyList<string>? Members);
public sealed record CreateServiceAccountRequest(string Name, string? Description, IReadOnlyList<IamGrantRequest>? Bindings = null);
public sealed record CreateServiceAccountTokenRequest(string? Name, DateTime? ExpiresAt);

public sealed record ServiceAccountTokenResponse(
    string Id, string? Name, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);
public sealed record ServiceAccountResponse(
    string Id, string Name, string? Description, DateTime CreatedAt, DateTime? DisabledAt,
    IReadOnlyList<ServiceAccountTokenResponse> Tokens);
public sealed record OneTimeServiceAccountTokenResponse(
    string ServiceAccountId, string TokenId, string Token, DateTime? ExpiresAt);
public sealed record IamRoleResponse(
    string Id, string DisplayName, string Description, IReadOnlyList<string> Permissions,
    IReadOnlyList<IamScopeKind> BindableScopes, bool ServiceAccountBindable);
public sealed record IamPermissionResponse(string Name, PermissionLevel Level, string Description);
public sealed record IamInvitationResponse(string Id, string UserId, DateTime ExpiresAt, bool Used);
public sealed record IamOverviewResponse(
    IReadOnlyList<IamRoleResponse> Roles,
    IReadOnlyList<IamPermissionResponse> Permissions,
    IReadOnlyList<AuthUserResponse> Users,
    IReadOnlyList<StudioGroup> Groups,
    IReadOnlyList<ServiceAccountResponse> ServiceAccounts,
    IReadOnlyList<RoleBinding> Bindings,
    IReadOnlyList<IamInvitationResponse> Invitations,
    int IamVersion);
public sealed record IamCheckResponse(bool Allowed, string Permission, IReadOnlyList<string> GrantingBindings, string? Reason);
