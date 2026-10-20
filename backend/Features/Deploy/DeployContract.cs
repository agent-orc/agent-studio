namespace AgentStudio.Deploy;

// BD-1 shared data contract for stage P1 of the deployment Dossier
// (docs/operations/bereitstellung). Property names are the wire names the
// Bereitstellung UI and the workspace dashboard use; keep them stable.

public static class EnvironmentKinds
{
    public const string Staging = "staging";
    public const string Production = "production";
}

public static class DeploymentStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string AwaitingApproval = "awaiting-approval";

    public static bool IsActive(string state) => state is Queued or Running or AwaitingApproval;
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
    IReadOnlyList<DeploymentStep> Steps, string Trigger, string? RequestedBy, DateTime CreatedAtUtc);

public sealed record EnvironmentStatus(
    DeployEnvironment Environment, Release? CurrentRelease, Deployment? CurrentDeployment,
    Release? NewerRelease, IReadOnlyList<string> CardsSinceLastDeploy, string Health);

public sealed record ReleaseDetailResponse(Release Release, IReadOnlyList<Deployment> Deployments);

public sealed record DeploymentLogLine(string Step, DateTime AtUtc, string Message);

public sealed record DeploymentLogResponse(string DeploymentId, IReadOnlyList<DeploymentLogLine> Lines);

public sealed record DeployAuditEntry(DateTime AtUtc, string Actor, string Action, string? ReleaseId,
    string? DeploymentId, string? EnvironmentId, string Detail);

public sealed record DeployMatrixRow(string ProjectId, IReadOnlyList<EnvironmentStatus> Statuses, string? Error);

/// <summary>Workspace dashboard: projects as rows, environment kinds as columns.</summary>
public sealed record DeployMatrixResponse(IReadOnlyList<string> EnvironmentKinds, IReadOnlyList<DeployMatrixRow> Projects);

public sealed record DeploymentRequest(string EnvironmentId, bool Approve = false);

public sealed record EnvironmentActionRequest(bool Approve = false);

/// <summary>Who asks, and whether they hold the Owner or Maintainer approval for the project.</summary>
public sealed record DeployActor(string Id, bool IsApprover);
