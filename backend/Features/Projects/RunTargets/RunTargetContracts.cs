namespace AgentStudio.Projects;

// Wire contract between Studio and a run-target host (IP-1). The host polls
// GET desired-state and POSTs observed-state and deploy-job reports; Studio
// never opens a connection to the host. Secret values never appear here:
// SecretRefs carries names that the host resolves just in time.

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
    IReadOnlyList<RunTargetEnvironmentSpec> Environments);

/// <summary>
/// Desired state of one environment. <see cref="Artifact"/> is null when no
/// healthy release exists yet. <see cref="JobId"/> and <see cref="LeaseToken"/>
/// name the AGT-3176 deploy job this convergence answers, if any.
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
    bool IsRollback);

public sealed record RunTargetObservedReport(IReadOnlyList<RunTargetObservation> Environments);

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

public static class RunTargetVocabulary
{
    public const string EdgeDestination = "edge";
    public static readonly IReadOnlySet<string> EdgeModes = new HashSet<string>(StringComparer.Ordinal)
        { "acme", "cloudflare" };
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
        { "running", "starting", "unhealthy", "crash-loop", "stopped", "failed" };
    public static readonly IReadOnlySet<string> EventKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "pulled", "started", "health-ok", "health-failed", "swapped", "rolled-back",
        "crash-restart", "crash-loop", "stopped", "deploy-step-failed", "domain-conflict",
    };
}
