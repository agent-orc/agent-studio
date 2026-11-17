namespace AgentRunner.RunTargets;

// Mirror of backend/Features/Projects/RunTargets/RunTargetContracts.cs. The
// run-target host only reads these from Studio and writes observations back;
// it has no listener of its own. Skipped and Removals are optional so a host
// that talks to an older Studio never removes anything.

public sealed record RunTargetResources(double Cpus, int MemoryMb, int PidsLimit);

public sealed record RunTargetDesiredState(
    string HostId,
    DateTime GeneratedAt,
    IReadOnlyList<RunTargetEnvironmentSpec> Environments,
    IReadOnlyList<RunTargetSkip>? Skipped = null,
    IReadOnlyList<RunTargetRemoval>? Removals = null,
    IReadOnlyList<RunTargetDatabaseDeletion>? DatabaseDeletions = null);

public sealed record RunTargetSkip(string Project, string Reason);

public sealed record RunTargetRemoval(string Project, string EnvironmentId);

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

/// <summary>A managed database (AGT-3877). The password arrives as a <c>secret://</c> reference only.</summary>
public sealed record RunTargetDatabaseSpec(
    string Name,
    string Engine,
    string Version,
    string Size,
    RunTargetResources Resources,
    string Variable,
    string PasswordRef);

public sealed record RunTargetDatabaseDeletion(string Project, string EnvironmentId, string Name);

public sealed record RunTargetDatabaseObservation(
    string Project,
    string EnvironmentId,
    string Name,
    string State,
    string? Version,
    long? VolumeBytes,
    DateTime ObservedAt);

public sealed record RunTargetObservedReport(IReadOnlyList<RunTargetObservation> Environments,
    IReadOnlyList<RunTargetDatabaseObservation>? Databases = null);

public sealed record RunTargetObservation(
    string Project,
    string EnvironmentId,
    string State,
    string? RunningArtifact,
    string? PreviousArtifact,
    int Restarts,
    DateTime ObservedAt,
    IReadOnlyList<RunTargetEvent> Events);

public sealed record RunTargetEvent(DateTime At, string Kind, string? Artifact);

/// <summary>Answer to an AGT-3176 deploy job, posted to its existing report route.</summary>
public sealed record RunTargetJobReport(string Project, string JobId, string LeaseToken, bool Healthy, string Evidence);

public static class RunTargetEvidence
{
    public const string HealthOk = "health-ok";
    public const string HealthFailed = "health-http-failed";
    public const string StepFailed = "deploy-step-failed";
}
