using AgentStudio.TaskServer.Contracts;

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
/// A release attempt the release-candidate gate refused (F2): the reason the
/// gate answered and the owner's next step, shown in the Deploy area.
/// </summary>
public sealed record ReleaseGateRefusal(string CommitSha, string Mode, string Reason, string? NextStep,
    DateTimeOffset AtUtc);

/// <summary>The Deploy area read: the standing refusal, or null when the last attempt was released.</summary>
public sealed record ReleaseGateStatus(ReleaseGateRefusal? Refusal);

/// <summary>
/// Immutable snapshot of what was built. Written once by
/// <see cref="DeployStore.AppendRelease"/> and never rewritten.
/// </summary>
public sealed record Release(
    string Id, string ProjectId, string Version, string CommitSha,
    IReadOnlyList<string> ArtifactDigests, string ProcessVersion, string VariablesVersion,
    IReadOnlyList<string> CardKeys, DateTime CreatedAtUtc, string Channel)
{
    public ReleaseSuiteVerdict? PreDeliveryVerdict { get; init; }
    public ReleaseQuality? Quality { get; init; }
}

public sealed record DeploymentStep(string Name, string State, DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc, string LogRef);

/// <summary>
/// One deploy run of a release into one environment. <c>Trigger</c>,
/// <c>RequestedBy</c> and <c>CreatedAtUtc</c> extend the P1 contract for the
/// timeline; the contract fields keep their names.
/// </summary>
/// <summary>How the Deploy area names a deploy job: its environment's name and its release version.</summary>
public sealed record DeployJobLabel(string Environment, string? ReleaseVersion);

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
    /// <summary>The release a rollback runs now, or null when the environment has none.</summary>
    public RollbackTarget? RollbackTarget { get; init; }
    /// <summary>A failed run may have changed the host even though the last successful release remains the lifecycle baseline.</summary>
    public bool ReleaseUncertain { get; init; }
    /// <summary>Set only for the running Studio instance, the Production environment of its source project (AGT-3949).</summary>
    public InstanceEnvironmentFacts? Instance { get; init; }
}

/// <summary>
/// What the instance deploy records (AGT-3879) say about the running Studio
/// instance, for the workspace dashboard (AGT-3949). The live release itself
/// (version, commit) is <see cref="EnvironmentStatus.CurrentRelease"/>.
/// </summary>
/// <param name="LiveSinceUtc">When the live release went live; null while no release is live.</param>
/// <param name="LastRun">The latest deploy run the operator scripts reported; null before the first report.</param>
public sealed record InstanceEnvironmentFacts(DateTime? LiveSinceUtc, InstanceLastRun? LastRun);

/// <param name="State">
/// <c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>stalled</c>. The generic
/// <see cref="Deployment.State"/> of the same run reads a stalled run as failed.
/// </param>
/// <param name="Step">The failed step of a failed run, else the step that reported last.</param>
/// <param name="AtUtc">When the run ended, or its last report while it is open.</param>
public sealed record InstanceLastRun(string Id, string State, string Version, string CommitSha, DateTime StartedAtUtc,
    DateTime AtUtc, string? Step);

/// <summary>
/// The release a rollback of one environment picks (F13): its last deploy
/// there and its runtime health while it ran (<see cref="ReleaseRuntimeHealthPolicy"/>).
/// <see cref="SkippedVersions"/> are newer older releases passed over because
/// they had an incident while running. A target whose
/// <see cref="RuntimeHealth"/> is <c>incident</c> runs only when the request
/// accepts it explicitly.
/// </summary>
public sealed record RollbackTarget(Release Release, DateTime? DeployedAtUtc, string RuntimeHealth,
    ReleaseRuntimeIncident? Incident, IReadOnlyList<string> SkippedVersions);

public sealed record InterventionSummary(string DeploymentId, string FailedStep, DateTime? ExpiresAtUtc,
    string? Decision, string? DecidedBy);

public sealed record ReleaseDetailResponse(Release Release, IReadOnlyList<Deployment> Deployments);

public static class DeployActionKinds
{
    public const string Deploy = "deploy";
    public const string Redeploy = "redeploy";
}

/// <summary>
/// A deploy or redeploy a person may start in one environment, as the
/// lifecycle decides it for the asking actor. <c>NeedsApproval</c> means the
/// request waits for an Owner or Maintainer; otherwise it queues at once.
/// A refused action carries the lifecycle's reason.
/// </summary>
public sealed record DeployActionOption(string EnvironmentId, string Action, string ReleaseId, string ReleaseVersion,
    bool Allowed, bool NeedsApproval, string? Reason);

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

    /// <summary>
    /// False for a watched project whose repository declares no environment
    /// under <c>docs/deployments/environments</c> (AGT-3949). Such a row has no
    /// statuses, except the instance's Production for the source project.
    /// </summary>
    public bool EnvironmentsDeclared { get; init; } = true;
}

/// <summary>
/// Workspace dashboard: one row per project this instance watches, environment
/// kinds as columns. A project the workspace registers but this instance does
/// not watch has no row.
/// </summary>
public sealed record DeployMatrixResponse(IReadOnlyList<string> EnvironmentKinds, IReadOnlyList<DeployMatrixRow> Projects);

public sealed record DeploymentRequest(string EnvironmentId, bool Approve = false, bool? GuidedFailure = null)
{
    public string? Tenant { get; init; }

    /// <summary>F14: the approver saw the missing secret bindings and approves anyway.</summary>
    public bool ConfirmMissingSecrets { get; init; }
}

/// <param name="AcceptIncidentTarget">A rollback may target a release that had an incident while running.</param>
public sealed record EnvironmentActionRequest(bool Approve = false, bool? GuidedFailure = null,
    bool AcceptIncidentTarget = false)
{
    public string? Tenant { get; init; }

    /// <summary>F14: the approver saw the missing secret bindings and approves anyway.</summary>
    public bool ConfirmMissingSecrets { get; init; }
}

public sealed record InterventionDecisionRequest(string Action, string? Reason = null);

public sealed record DeploymentApprovalRequest(bool? GuidedFailure = null, bool ConfirmMissingSecrets = false);

/// <summary>
/// F14: the secret names one deploy of a release carries into one environment.
/// <c>Declared</c> lists the names the current declaration declares, the only
/// names an environment binding accepts.
/// </summary>
public sealed record EnvironmentSecretDemand(DeployEnvironment Environment, string DeclarationKind, string? ReleaseId,
    IReadOnlyList<string> Names, IReadOnlyList<string> Declared);

/// <summary>One secret a deploy would miss; <c>Bindable</c> when an environment binding can resolve it.</summary>
public sealed record SecretReadinessItem(string Name, string Reference, string Reason, bool Bindable);

/// <summary>F14: whether a deploy into the environment finds every secret it needs bound.</summary>
public sealed record EnvironmentSecretReadiness(string EnvironmentId, string EnvironmentName, string Kind,
    string? ReleaseId, bool RequiresApproval, IReadOnlyList<SecretReadinessItem> Missing)
{
    public bool Ready => Missing.Count == 0;
}

public sealed record SecretReadinessResponse(string Project, string? ReleaseId,
    IReadOnlyList<EnvironmentSecretReadiness> Environments);

/// <summary>Who asks, and whether they hold the Owner or Maintainer approval for the project.</summary>
public sealed record DeployActor(string Id, bool IsApprover, string? ApprovalRole = null);
