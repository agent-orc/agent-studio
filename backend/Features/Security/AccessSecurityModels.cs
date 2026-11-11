using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

public static class SecurityProfiles
{
    public const string Local = "local";
    public const string Networked = "networked";
    public const string PublicDemo = ExecutionAdmissionPolicy.PublicDemoProfile;

    public static bool IsNetworked(IConfiguration configuration)
        => string.Equals(ActiveProfile(configuration), Networked, StringComparison.OrdinalIgnoreCase);

    public static bool IsPublicDemo(IConfiguration configuration)
        => string.Equals(ActiveProfile(configuration), PublicDemo, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocal(IConfiguration configuration)
        => !IsNetworked(configuration) && !IsPublicDemo(configuration);

    public static bool PublicCloudflareEdgeEnabled(IConfiguration configuration)
    {
        var value = configuration["Security:PublicCloudflareEdge"];
        return value switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new InvalidOperationException(
                "Security:PublicCloudflareEdge must be exactly 'true' or 'false'.")
        };
    }

    public static string ActiveProfile(IConfiguration configuration)
    {
        var configured = configuration["Security:Profile"]?.Trim();
        if (string.IsNullOrEmpty(configured)) return Local;
        if (string.Equals(configured, Local, StringComparison.OrdinalIgnoreCase)) return Local;
        if (string.Equals(configured, Networked, StringComparison.OrdinalIgnoreCase)) return Networked;
        if (string.Equals(configured, PublicDemo, StringComparison.OrdinalIgnoreCase)) return PublicDemo;
        throw new InvalidOperationException($"Unsupported Security:Profile '{configured}'.");
    }
}

public static class StudioRoles
{
    public const string Owner = "owner";
    public const string Operator = "operator";
    public const string Contributor = "contributor";
    public const string ContributorDisplayName = "Product";
    public const string Viewer = "viewer";

    public static bool IsValid(string? value) => value is Owner or Operator or Contributor or Viewer;
}

public static class RunnerScopes
{
    public const string Orchestration = "orchestration.write";
    public const string Claim = "runner.claim";
    public const string Lease = "runner.lease";
    public const string Logs = "runner.logs";
    public const string Events = "runner.events";
    public const string Artifacts = "runner.artifacts";
    public const string Completion = "runner.completion";
    public const string Deploy = "runner.deploy";
    public const string Release = "runner.release";
    public const string Gate = "gate.execute";
    /// <summary>May fetch short-lived GitHub App tokens that read every registered repository.</summary>
    public const string RepositoryRead = "repository.read";
    /// <summary>May fetch short-lived GitHub App tokens that push to every registered repository.</summary>
    public const string RepositoryWrite = "repository.write";

    /// <summary>
    /// The public-demo replay scope. It admits nothing but pre-signed simulated
    /// events on fixture tasks and is deliberately absent from
    /// <see cref="Minimum"/>: a credential only carries it when an operator asks
    /// for it explicitly, and then it may carry nothing else.
    /// </summary>
    public const string DemoReplay = "demo.replay";

    public static readonly string[] Minimum = [Claim, Lease, Logs, Events, Artifacts, Completion];
    public static readonly HashSet<string> All = new([.. Minimum, DemoReplay, Orchestration, Deploy, Release, Gate, RepositoryRead, RepositoryWrite], StringComparer.Ordinal);

    /// <summary>Replay, engine, deploy, release, and pull gate credentials each have one role; none is also an execution credential.</summary>
    public static bool IsExclusive(string scope) => scope is DemoReplay or Orchestration or Deploy or Release or Gate;
}

public sealed record StudioUser
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required string PasswordHash { get; init; }
    public List<string> Projects { get; init; } = [];
    public List<string> ProductionApprovalProjects { get; init; } = [];
    public bool Disabled { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime PasswordChangedAt { get; init; }
    public string? TotpSecret { get; init; }
    public DateTime? FactorChangedAt { get; init; }
    public List<string> RecoveryCodeHashes { get; init; } = [];
    public List<StudioPasskey> Passkeys { get; init; } = [];
}

public sealed record StudioPasskey(string Id, string Name, string PublicKey, uint SignCount, DateTime CreatedAt);

public sealed record StudioSession
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string TokenHash { get; init; }
    public required string CsrfHash { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastSeenAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required DateTime AbsoluteExpiresAt { get; init; }
    public string? Device { get; init; }
    public string? Address { get; init; }
    public bool SecondFactorEnrollmentOnly { get; init; }
    public DateTime? StepUpVerifiedAt { get; init; }
    public DateTime? RecoveryCodeUsedAt { get; init; }
    public int? RecoveryCodesRemaining { get; init; }
    public bool RecoveryEnrollmentAllowed { get; init; }
}

public sealed record RunnerCredential
{
    public required string Id { get; init; }
    public required string SecretHash { get; init; }
    public required List<string> Scopes { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

public sealed record RunnerServiceIdentity
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedAt { get; init; }
    /// <summary>
    /// The projects this runner may claim, read and review (SM-9). Empty grants
    /// none and <c>*</c> grants all. Null marks an identity enrolled before
    /// project binding: it keeps all projects and its owner sees a notice.
    /// </summary>
    public List<string>? Projects { get; init; }
    public DateTime? RevokedAt { get; init; }
    public DateTime? DrainRequestedAt { get; init; }
    public DateTime? RetireRequestedAt { get; init; }
    public DateTime? RetiredAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime? LastClaimAt { get; init; }
    public int ActiveSlots { get; init; }
    public int AvailableSlots { get; init; }
    public List<RunnerCredential> Credentials { get; init; } = [];
}

public sealed record RunnerEnrollment
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CodeHash { get; init; }
    public required List<string> Scopes { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? CredentialExpiresAt { get; init; }
    public DateTime? UsedAt { get; init; }
    /// <summary>
    /// The projects the enrolled runner is bound to; see <see cref="RunnerServiceIdentity.Projects"/>.
    /// Null only on a code created before project binding.
    /// </summary>
    public List<string>? Projects { get; init; }
}

public sealed record SecurityState
{
    /// <summary>
    /// Rights-model schema. 0 is the legacy role-per-user state; loading it
    /// migrates every user to role bindings (see <see cref="LegacyRoleMapping"/>).
    /// </summary>
    public int IamVersion { get; set; }
    public List<StudioUser> Users { get; init; } = [];
    public List<StudioInvitation> Invitations { get; init; } = [];
    public List<StudioSession> Sessions { get; init; } = [];
    public List<RunnerServiceIdentity> Runners { get; init; } = [];
    public List<RunnerEnrollment> Enrollments { get; init; } = [];
    public List<StudioGroup> Groups { get; init; } = [];
    public List<RoleBinding> RoleBindings { get; init; } = [];
    public List<ServiceAccount> ServiceAccounts { get; init; } = [];
    /// <summary>
    /// API tokens as AGT-3186 stored them. Loading migrates them into
    /// <see cref="ServiceAccounts"/> (same token id and secret hash), so the
    /// list stays empty in a current state file.
    /// </summary>
    public List<StudioApiToken> ApiTokens { get; init; } = [];
    public List<TelemetryIngestToken> TelemetryIngestTokens { get; init; } = [];
    /// <summary>Operator agents' T1 authority (security model SM-4); only the Owner changes it.</summary>
    public List<StandingGrant> StandingGrants { get; init; } = [];
    /// <summary>Owner approvals of T2 operator requests (security model SM-5).</summary>
    public List<OperatorApproval> OperatorApprovals { get; init; } = [];
    /// <summary>Hash of the Owner's once-shown offline break-glass credential.</summary>
    public string? BreakGlassCredentialHash { get; set; }
    public List<BreakGlassWindow> BreakGlassWindows { get; init; } = [];
}

public sealed record StudioInvitation
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string TokenHash { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? UsedAt { get; init; }
}

public sealed record StudioApiToken
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required List<string> Projects { get; init; }
    public required string SecretHash { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

/// <summary>
/// OTLP ingest credential of one project environment (IP-15). Only the hash is
/// stored; the plaintext is returned once by the create operation.
/// </summary>
public sealed record TelemetryIngestToken
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string Environment { get; init; }
    public required List<string> Signals { get; init; }
    public required string SecretHash { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

public sealed record ApiTokenPrincipal(StudioApiToken Token, StudioUser User);
/// <summary><see cref="ExpiresAt"/> is optional: without it a token lives 30 days, at most 365 (OP-1).</summary>
public sealed record CreateApiTokenRequest(string Name, string Role, IReadOnlyList<string>? Projects, DateTime? ExpiresAt = null);
public sealed record ApiTokenResponse(string Id, string Name, string Role, IReadOnlyList<string> Projects,
    string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret);
public sealed record RunnerEnrollmentResponse(string Id, string Name, IReadOnlyList<string> Scopes,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? UsedAt, IReadOnlyList<string>? Projects = null);
public static class TelemetrySignals
{
    public const string Metrics = "metrics";
    public const string Logs = "logs";
    public static readonly string[] All = [Metrics, Logs];
}

public sealed record TelemetryIngestPrincipal(string TokenId, string Project, string Environment, IReadOnlySet<string> Signals);

public sealed record HumanPrincipal(StudioUser User, StudioSession Session);
/// <summary>
/// An authenticated runner credential with its SM-9 binding: the enrolled host
/// identity, its scopes (the role) and the projects it may work on.
/// </summary>
public sealed record RunnerPrincipal(string RunnerId, string RunnerName, string CredentialId, IReadOnlySet<string> Scopes)
{
    /// <summary>Bound projects; <c>*</c> grants every project. The default keeps hand-built principals unbound.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];
    public DateTime? CredentialExpiresAt { get; init; }
}

public sealed record AuthStatusResponse(string Profile, bool BootstrapRequired, bool Authenticated, AuthUserResponse? User = null, string? CsrfToken = null, bool BootstrapCodeRequired = false, bool SecondFactorEnrollmentRequired = false, int? RecoveryCodesRemaining = null, bool RecoveryEnrollmentAllowed = false, RestoreNotice? RestoreNotice = null);
public sealed record RestoreNoticeAcknowledgeRequest(string ArchiveId);
public sealed record AuthUserResponse(string Id, string Username, string DisplayName, string Role, IReadOnlyList<string> Projects, bool Disabled, bool MustChangePassword, IReadOnlyList<string>? ProductionApprovalProjects = null, bool HasFactor = false, DateTime? FactorChangedAt = null);
public sealed record BootstrapRequest(string Username, string Password, string? DisplayName, string? BootstrapCode = null);
public sealed record LoginRequest(string Username, string Password, string? SecondFactorCode = null,
    PasskeyAssertionProof? Passkey = null);
public sealed record ConfirmTotpRequest(string Code);
public sealed record PasskeyAssertionOptionsRequest(string Username);
public sealed record StepUpRequest(string? Code = null, PasskeyAssertionProof? Passkey = null);
public sealed record SessionResponse(string Id, string? Device, string? Address, DateTime CreatedAt, DateTime LastUseAt, bool Current);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CreateUserRequest(string Username, string DisplayName, string Role, IReadOnlyList<string>? Projects, string? TemporaryPassword, IReadOnlyList<IamGrantRequest>? Bindings = null);
public sealed record UpdateUserRequest(string? DisplayName, string? Role, IReadOnlyList<string>? Projects, bool? Disabled, IReadOnlyList<string>? ProductionApprovalProjects = null);
/// <summary>
/// <see cref="Bindings"/>, when present, replaces the legacy <see cref="Role"/>
/// and <see cref="Projects"/> pair with explicit role bindings.
/// </summary>
public sealed record InviteUserRequest(string Username, string DisplayName, string Role, IReadOnlyList<string>? Projects, IReadOnlyList<IamGrantRequest>? Bindings = null);
public sealed record AcceptInvitationRequest(string Token, string NewPassword);
public sealed record PasswordResetRequest(string? TemporaryPassword);
public sealed record RunnerEnrollmentRequest(string Name, IReadOnlyList<string>? Scopes, DateTime? ExpiresAt, DateTime? CredentialExpiresAt,
    IReadOnlyList<string>? Projects = null);
public sealed record RunnerProjectsRequest(IReadOnlyList<string>? Projects);
public sealed record RunnerEnrollRequest(string Code);
public sealed record RunnerRotateRequest(IReadOnlyList<string>? Scopes, DateTime? ExpiresAt);
public sealed record OneTimeSecretResponse(string RunnerId, string RunnerName, string CredentialId, string Secret, IReadOnlyList<string> Scopes, DateTime? ExpiresAt);
public sealed record OneTimeEnrollmentResponse(string EnrollmentCode, string Name, IReadOnlyList<string> Scopes, DateTime ExpiresAt,
    IReadOnlyList<string>? Projects = null);
public sealed record PasswordResetResponse(string UserId, string TemporaryPassword, bool MustChangePassword);

internal sealed record LoginAttempt(int Failures, DateTime WindowStartedAt, DateTime? LockedUntil);

public sealed record RunSecurityAuditEvent(
    DateTime Timestamp,
    string Action,
    string TaskKey,
    string? Project,
    string InitiatingPrincipal,
    string ExecutingRunnerPrincipal,
    string? CredentialId,
    long? FencingToken = null,
    string? Outcome = null);
