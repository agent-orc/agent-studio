namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// I05 human identity and project bootstrap (docs/operations/deployment-story/index.html,
/// D5 option A). One installer-owned bootstrap feeds server-owned human,
/// principal and project records. Every one-time secret in this file is
/// returned exactly once and stored only as a hash.
/// </summary>
public sealed record InstallationIdentityDto(
    string InstallationId,
    DateTime CreatedAt,
    bool OwnerBootstrapped,
    bool OwnerBootstrapArmed);

/// <summary>
/// Owner bootstrap result: the session fields of <see cref="StudioAuthSessionDto"/>
/// plus the owner's recovery code, shown once.
/// </summary>
public sealed record StudioOwnerBootstrapSessionDto(
    string SessionToken,
    string CsrfToken,
    StudioAuthStatusDto Status,
    string RecoveryCode);

public sealed record StudioRecoverRequest(string Username, string RecoveryCode, string NewPassword);

/// <summary>Recovery result. The consumed code is replaced by a new one, shown once.</summary>
public sealed record StudioRecoverResult(StudioAuthUserDto User, string RecoveryCode);

public sealed record StudioReissueRecoveryCodeRequest(string CurrentPassword);

public sealed record StudioCreateUserRequest(
    string Username,
    string Password,
    string Role,
    string? DisplayName = null);

/// <summary>
/// Owner request for a one-time enrolment code for a distinct service
/// principal: one Studio edge, one engine, or one runner per host.
/// </summary>
public sealed record CreateEnrolmentRequest(
    string PrincipalId,
    string Kind,
    string? RunnerId = null,
    int? TimeToLiveSeconds = null);

/// <summary>
/// The enrolment code is short-lived and single-use. Transfer it over the
/// protected administration channel into a restricted host file; it is not a
/// service bearer and grants nothing until exchanged.
/// </summary>
public sealed record IssuedEnrolment(
    string EnrolmentId,
    string PrincipalId,
    string Kind,
    string? RunnerId,
    string InstallationId,
    string EnrolmentCode,
    DateTime ExpiresAt);

public sealed record EnrolmentDto(
    string EnrolmentId,
    string PrincipalId,
    string Kind,
    string? RunnerId,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    DateTime? RevokedAt);

/// <summary>
/// Host-side exchange. The host names the installation it expects so two
/// installations cannot silently share one identity.
/// </summary>
public sealed record ExchangeEnrolmentRequest(string EnrolmentCode, string ExpectedInstallationId);

public sealed record ExchangedEnrolment(
    string InstallationId,
    IssuedPrincipalCredential Issued);

public static class ProjectDeliveryPolicies
{
    public const string ReviewedPublication = "reviewed-publication";
    public const string ManualPublication = "manual-publication";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([ReviewedPublication, ManualPublication], StringComparer.Ordinal);
}

/// <summary>
/// API registration of a project with its single canonical repository. The
/// project id is stable and required; the repository URL must carry no
/// credentials, query or fragment.
/// </summary>
public sealed record RegisterProjectRepositoryRequest(
    string ProjectId,
    string WorkspaceId,
    string Name,
    string TaskKeyPrefix,
    string RepositoryUrl,
    string IntegrationRef,
    string? ReleaseRef = null,
    string DeliveryPolicy = ProjectDeliveryPolicies.ReviewedPublication);

public sealed record ProjectRepositoryDto(
    string ProjectId,
    string RepositoryId,
    string RepositoryUrl,
    string IntegrationRef,
    string? ReleaseRef,
    string DeliveryPolicy,
    DateTime RegisteredAt,
    string RegisteredBy);

/// <summary>
/// What a runner host observed when it probed the registered project from
/// itself. <see cref="UsedFallbackRemote"/> marks a probe that went to a
/// runner-configured fallback remote instead of the registered origin.
/// </summary>
public sealed record ProjectRepositoryProbeRequest(
    string ObservedFetchUrl,
    string? ObservedPushUrl,
    bool FetchSucceeded,
    bool PushSucceeded,
    bool UsedFallbackRemote,
    string? Detail = null);

public sealed record ProjectRepositoryProbeDto(
    string ProjectId,
    string RunnerId,
    bool Admitted,
    string Verdict,
    string? Detail,
    DateTime ObservedAt);

public sealed record ProjectRepositoryStatusDto(
    ProjectRepositoryDto Registration,
    IReadOnlyList<ProjectRepositoryProbeDto> Probes);

public static class ProjectRepositoryProbeVerdicts
{
    public const string Admitted = "admitted";
    public const string FetchFailed = "fetch-failed";
    public const string PushFailed = "push-failed";
    public const string FallbackRemoteOnly = "fallback-remote-only";
    public const string OriginMismatch = "origin-mismatch";
}
