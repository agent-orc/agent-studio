namespace AgentStudio.Projects;

// Wire contract between Studio and a run-target host (IP-1). The host polls
// GET desired-state and POSTs observed-state and deploy-job reports; Studio
// never opens a connection to the host. Secret values never appear here:
// SecretRefs carries names that the host resolves just in time. The host
// removes an app only when Studio names it in Removals; an app Studio does
// not mention, or whose project Studio lists in Skipped, keeps running.

/// <summary>Hosting part of a container environment declared with <c>destination: "edge"</c>.</summary>
public sealed record RunTargetHosting(
    IReadOnlyList<string> Domains,
    string Edge,
    string HealthPath,
    IReadOnlyDictionary<string, string> Variables,
    RunTargetResources Resources);

public sealed record RunTargetResources(double Cpus, int MemoryMb, int PidsLimit);

public sealed record RunTargetDesiredState(
    string HostId,
    DateTime GeneratedAt,
    IReadOnlyList<RunTargetEnvironmentSpec> Environments,
    IReadOnlyList<RunTargetSkip> Skipped,
    IReadOnlyList<RunTargetRemoval> Removals,
    IReadOnlyList<RunTargetDatabaseDeletion>? DatabaseDeletions = null,
    IReadOnlyList<RunTargetDatabaseOperation>? DatabaseOperations = null);

/// <summary>
/// A project whose declarations or state Studio could not read in this poll:
/// <c>checkout-missing</c>, <c>declarations-missing</c>, <c>declaration-invalid</c>,
/// <c>declaration-unreadable</c> or <c>state-unreadable</c>.
/// </summary>
public sealed record RunTargetSkip(string Project, string Reason);

/// <summary>
/// Explicit signal to take an environment off the host. The host acknowledges
/// with an observation in state <c>removed</c>, which ends the signal.
/// </summary>
public sealed record RunTargetRemoval(string Project, string EnvironmentId);

/// <summary>
/// Desired state of one environment. <see cref="Artifact"/> is null when no
/// healthy release exists yet. <see cref="JobId"/> and <see cref="LeaseToken"/>
/// name the AGT-3176 deploy job this convergence answers, if any.
/// <see cref="Domains"/> holds only the domains this environment owns on the
/// host; <see cref="RefusedDomains"/> lists declared domains that another
/// environment on the same host claimed first.
/// </summary>
public sealed record RunTargetEnvironmentSpec(
    string Project,
    string EnvironmentId,
    string Kind,
    string? Artifact,
    int ContainerPort,
    string HealthPath,
    IReadOnlyList<string> Domains,
    string Edge,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> SecretRefs,
    RunTargetResources Resources,
    string? JobId,
    string? LeaseToken,
    bool IsRollback,
    IReadOnlyList<string>? RefusedDomains = null,
    IReadOnlyList<RunTargetDatabaseSpec>? Databases = null);

/// <summary>
/// A managed database of one environment (AGT-3877). <see cref="PasswordRef"/>
/// is the <c>secret://</c> reference Studio generated and bound to the
/// environment; the host resolves it just in time and never receives the value
/// in the desired state. The host provisions PostgreSQL on a network that only
/// this environment's app containers join, with its own named volume.
/// </summary>
public sealed record RunTargetDatabaseSpec(
    string Name,
    string Engine,
    string Version,
    string Size,
    RunTargetResources Resources,
    string Variable,
    string PasswordRef,
    RunTargetBackupPolicy? Backup = null);

/// <summary>
/// How often the host dumps a managed database and how long it keeps the dumps
/// (AGT-3878). A dump is kept while it is younger than <see cref="RetentionDays"/>
/// or one of the <see cref="MinimumKept"/> newest.
/// </summary>
public sealed record RunTargetBackupPolicy(int IntervalHours, int RetentionDays, int MinimumKept);

/// <summary>
/// An operation Studio asks the host to run on a managed database (AGT-3878):
/// <c>backup</c> dumps <see cref="Name"/> now; <c>verify</c> restores backup
/// <see cref="BackupId"/> of <see cref="Name"/> into a scratch database; <c>restore</c>
/// replaces the data of <see cref="TargetName"/>, which is <see cref="Name"/> or another
/// database of the same environment, with that backup. A restore that reaches the host
/// is approved; the host answers once with a <see cref="RunTargetDatabaseOperationResult"/>.
/// </summary>
public sealed record RunTargetDatabaseOperation(string Id, string Project, string EnvironmentId, string Name,
    string Kind, string? BackupId, string? TargetName);

/// <summary>One dump the host keeps for a database, outside its volume.</summary>
public sealed record RunTargetBackupRecord(string Id, DateTime TakenAt, long Bytes, string Kind,
    DateTime? VerifiedAt = null, int? VerifiedTables = null);

/// <summary>What one operation did. <see cref="State"/> is <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record RunTargetDatabaseOperationResult(string Id, string Project, string EnvironmentId, string Name,
    string State, string? Message, DateTime FinishedAt, string? BackupId = null);

/// <summary>An owner's confirmed deletion of a database the environment no longer declares; the host drops its volume.</summary>
public sealed record RunTargetDatabaseDeletion(string Project, string EnvironmentId, string Name);

/// <summary>
/// What the host runs for one managed database. <see cref="State"/> is in
/// <see cref="RunTargetVocabulary.DatabaseStates"/>; <c>deleted</c> confirms a deletion.
/// </summary>
public sealed record RunTargetDatabaseObservation(
    string Project,
    string EnvironmentId,
    string Name,
    string State,
    string? Version,
    long? VolumeBytes,
    DateTime ObservedAt,
    bool? Ready = null,
    int? Connections = null,
    int? MaxConnections = null,
    IReadOnlyList<RunTargetBackupRecord>? Backups = null,
    DateTime? LastBackupAttemptAt = null,
    string? LastBackupError = null);

/// <summary>Operator confirmation of a pending removal; without a host it applies to every host it is pending on.</summary>
public sealed record RunTargetRemovalConfirmRequest(string? HostId);

/// <summary>Replaces a project's operator-owned run-target domain suffixes.</summary>
public sealed record RunTargetDomainSuffixesRequest(IReadOnlyList<string>? Suffixes);

public sealed record RunTargetObservedReport(IReadOnlyList<RunTargetObservation> Environments,
    IReadOnlyList<RunTargetDatabaseObservation>? Databases = null,
    IReadOnlyList<RunTargetDatabaseOperationResult>? DatabaseOperations = null);

/// <summary>A container template that the deploy host must supervise between deploy jobs.</summary>
public sealed record DeployHostContainerTarget(string Project, string EnvironmentId, string Container,
    string Artifact);

public sealed record RunTargetObservation(
    string Project,
    string EnvironmentId,
    string State,
    string? RunningArtifact,
    string? PreviousArtifact,
    int Restarts,
    DateTime ObservedAt,
    IReadOnlyList<RunTargetEvent> Events,
    IReadOnlyList<string>? CrashOutput = null);

public sealed record RunTargetEvent(DateTime At, string Kind, string? Artifact);

public static class RunTargetVocabulary
{
    public const string EdgeDestination = "edge";
    public static readonly IReadOnlySet<string> EdgeModes = new HashSet<string>(StringComparer.Ordinal)
        { "acme", "cloudflare" };
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
        { "running", "starting", "unhealthy", "crash-loop", "stopped", "failed", "removed" };
    public static readonly IReadOnlySet<string> EventKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "pulled", "started", "health-ok", "health-failed", "swapped", "rolled-back",
        "crash-restart", "crash-loop", "crash-rollback", "stopped", "deploy-step-failed", "domain-conflict",
    };

    /// <summary>
    /// <c>retained</c>: no longer declared, container stopped, volume kept until
    /// an owner deletes it. <c>upgrade-refused</c>: the deploy asked for another
    /// major version, which the host does not apply to existing data.
    /// </summary>
    public static readonly IReadOnlySet<string> DatabaseStates = new HashSet<string>(StringComparer.Ordinal)
        { "provisioning", "running", "unhealthy", "failed", "retained", "upgrade-refused", "deleted" };

    public static readonly IReadOnlySet<string> DatabaseOperationKinds = new HashSet<string>(StringComparer.Ordinal)
        { "backup", "verify", "restore" };

    /// <summary><c>pre-restore</c>: the dump the host takes of a database right before a restore replaces its data.</summary>
    public static readonly IReadOnlySet<string> BackupKinds = new HashSet<string>(StringComparer.Ordinal)
        { "scheduled", "manual", "pre-restore" };
}
