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

/// <summary>
/// The canonical environment key of an environment scope: the kind an
/// environment declaration names in <c>docs/deployments/environments/*.json</c>
/// (<c>stage</c>, <c>production</c>, or <c>other</c>), never its id. Releases
/// address the stage environment, promotions the production environment, and a
/// route that names an environment id resolves it to its declaration's kind,
/// so every binding matches one key per environment kind.
/// </summary>
public static class IamEnvironments
{
    public const string Stage = "stage";
    public const string Production = "production";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Stage, Production, Other];

    /// <summary>The canonical key for a scope value, or <c>null</c> when it names no environment kind.</summary>
    public static string? Canonical(string? value)
    {
        var key = value?.Trim().ToLowerInvariant();
        return key is Stage or Production or Other ? key : null;
    }

    /// <summary>
    /// The canonical key of the declaration whose id a route names, or
    /// <c>null</c> when the project declares no such environment.
    /// </summary>
    public static string? ForDeclaration(
        IEnumerable<AgentStudio.Projects.EnvironmentDeclaration>? declarations, string? environmentId)
        => string.IsNullOrWhiteSpace(environmentId)
            ? null
            : Canonical(declarations?.FirstOrDefault(declaration
                => string.Equals(declaration.Id, environmentId, StringComparison.Ordinal))?.Kind);
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

public static class ServiceAccountOrigins
{
    /// <summary>
    /// A Studio API token (AGT-3186), migrated or issued through
    /// <c>/api/auth/api-tokens</c>: one token, <c>api.*</c> secret, and
    /// valid only while the user who created it stays active.
    /// </summary>
    public const string ApiToken = "api-token";
}

/// <summary>A non-human principal with its own API tokens, scopes, expiry, and revocation.</summary>
public sealed record ServiceAccount
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    /// <summary><c>null</c> for an account created in the Access area, else <see cref="ServiceAccountOrigins"/>.</summary>
    public string? Origin { get; init; }
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

public sealed record ServiceAccountPrincipal(
    string AccountId, string AccountName, string TokenId, string? Origin = null, string? CreatedBy = null)
{
    /// <summary>
    /// How handlers, audit records, and card timelines attribute the account's
    /// actions: the one non-human actor id <c>service-account:&lt;id&gt;</c>.
    /// </summary>
    public string ActorId => AgentStudio.Shared.TimelineActors.ServiceAccount(AccountId);
}

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
    IReadOnlyList<ServiceAccountTokenResponse> Tokens, string? Origin = null, string? CreatedBy = null);
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
