namespace AgentStudio.Deploy;

// BD-1 shared data contract for stage P1 of the deployment Dossier
// (docs/operations/bereitstellung). Property names are the wire names the
// Bereitstellung UI and the workspace dashboard use; keep them stable.

public static class EnvironmentKinds
{
    public const string Preview = "preview";
    public const string Staging = "staging";
    public const string Production = "production";

    /// <summary>
    /// A declared lifecycle environment beyond Staging and Production, such as
    /// Dev (AGT-3276 D6). Lifecycle phases name it by its environment id.
    /// </summary>
    public const string Other = "other";
}

public static class DeploymentStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string AwaitingApproval = "awaiting-approval";
    public const string WaitingForIntervention = "waiting-for-intervention";

    public static bool IsActive(string state) => state is Queued or Running or AwaitingApproval or WaitingForIntervention;
    public static bool IsFinished(string state) => state is Succeeded or Failed or Cancelled;
}

/// <summary>Step states reuse the deployment vocabulary plus not-yet-started and skipped.</summary>
public static class DeploymentStepStates
{
    public const string Pending = "pending";
    public const string Skipped = "skipped";
}

public static class DeploymentTriggers
{
    public const string Deploy = "deploy";
    public const string Redeploy = "redeploy";
    public const string Rollback = "rollback";
    public const string AutomaticRollback = "automatic-rollback";
}

/// <summary>The machine-readable <c>reason</c> of a refusal body.</summary>
public static class DeployRefusalReasons
{
    /// <summary>A newer release exists, so this release never starts Staging.</summary>
    public const string Superseded = "superseded";

    /// <summary>The configured release card attribution source (the Task Server, AGT-3320) could not answer.</summary>
    public const string AttributionUnavailable = "attribution-unavailable";
}

public static class EnvironmentHealth
{
    public const string Healthy = "healthy";
    public const string Unhealthy = "unhealthy";
    public const string Unknown = "unknown";
}

public sealed record DeployEnvironment(string Id, string ProjectId, string Name, int Order, string Kind)
{
    public bool? Protected { get; init; }
    public bool? Automatic { get; init; }
    public string? Requires { get; init; }
}

/// <summary>
/// Immutable snapshot of what was built. Written once by
/// <see cref="DeployStore.AppendRelease"/> and never rewritten.
/// </summary>
public sealed record Release(
    string Id, string ProjectId, string Version, string CommitSha,
    IReadOnlyList<string> ArtifactDigests, string ProcessVersion, string VariablesVersion,
    IReadOnlyList<string> CardKeys, DateTime CreatedAtUtc, string Channel);

public sealed record DeploymentStep(string Name, string State, DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc, string LogRef);

/// <summary>
/// One deploy run of a release into one environment. <c>Trigger</c>,
/// <c>RequestedBy</c> and <c>CreatedAtUtc</c> extend the P1 contract for the
/// timeline; the contract fields keep their names.
/// </summary>
public sealed record Deployment(
    string Id, string ReleaseId, string EnvironmentId, string State,
    DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? ApprovedBy,
    IReadOnlyList<DeploymentStep> Steps, string Trigger, string? RequestedBy, DateTime CreatedAtUtc,
    DateTime? InterventionExpiresAtUtc = null, string? FailedStep = null, string? InterventionDecidedBy = null,
    string? InterventionDecision = null)
{
    public string? Tenant { get; init; }
}

public sealed record EnvironmentStatus(
    DeployEnvironment Environment, Release? CurrentRelease, Deployment? CurrentDeployment,
    Release? NewerRelease, IReadOnlyList<string> CardsSinceLastDeploy, string Health,
    InterventionSummary? Intervention = null)
{
    public string? Tenant { get; init; }
}

public sealed record InterventionSummary(string DeploymentId, string FailedStep, DateTime? ExpiresAtUtc,
    string? Decision, string? DecidedBy);

public sealed record ReleaseDetailResponse(Release Release, IReadOnlyList<Deployment> Deployments);

public sealed record DeploymentLogLine(string Step, DateTime AtUtc, string Message);

public sealed record DeploymentLogResponse(string DeploymentId, IReadOnlyList<DeploymentLogLine> Lines)
{
    public string? Tenant { get; init; }
}

/// <summary>A bounded, newest-first slice. Offset and total refer to the filtered set.</summary>
public sealed record DeployPage<T>(IReadOnlyList<T> Items, int Offset, int Limit, int Total);

public sealed record DeployAuditEntry(DateTime AtUtc, string Actor, string Action, string? ReleaseId,
    string? DeploymentId, string? EnvironmentId, string Detail);

public sealed record DeployMatrixRow(string ProjectId, IReadOnlyList<EnvironmentStatus> Statuses, string? Error)
{
    public IReadOnlyList<EnvironmentStatus> TenantStatuses { get; init; } = [];
}

/// <summary>Workspace dashboard: projects as rows, environment kinds as columns.</summary>
public sealed record DeployMatrixResponse(IReadOnlyList<string> EnvironmentKinds, IReadOnlyList<DeployMatrixRow> Projects);

public sealed record DeploymentRequest(string EnvironmentId, bool Approve = false, bool? GuidedFailure = null)
{
    public string? Tenant { get; init; }
}

public sealed record EnvironmentActionRequest(bool Approve = false, bool? GuidedFailure = null)
{
    public string? Tenant { get; init; }
}

public sealed record InterventionDecisionRequest(string Action, string? Reason = null);

public sealed record DeploymentApprovalRequest(bool? GuidedFailure = null);

/// <summary>Who asks, and whether they hold the Owner or Maintainer approval for the project.</summary>
public sealed record DeployActor(string Id, bool IsApprover, string? ApprovalRole = null);
