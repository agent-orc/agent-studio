namespace AgentStudio.Readiness;

/// <summary>
/// Everything <see cref="ServerReadinessPolicy"/> decides from. The collector
/// gathers these facts; the policy never touches Git, the file system or a
/// store, so every state and remedy is testable from plain values.
/// </summary>
public sealed record ServerReadinessFacts(
    DateTimeOffset Now,
    GitIdentityFact Identity,
    CommitProbeFact CommitProbe,
    bool RunsInContainer,
    IReadOnlyList<DataFolderFact> DataFolders,
    IReadOnlyList<StateFolderFact> StateFolders,
    IReadOnlyList<GitLockFact> WorkspaceLocks,
    GateWorkerFact Gate,
    ExecutionHostsFact Hosts,
    GitHubAppFact App,
    IReadOnlyList<ProjectReadinessFact> Projects);

/// <summary>
/// The effective <c>user.name</c> and <c>user.email</c> of the service user
/// and the file each came from (<c>git config --show-origin</c>). Error is set
/// when Git gave no answer at all.
/// </summary>
public sealed record GitIdentityFact(
    string? Name,
    string? NameOrigin,
    string? Email,
    string? EmailOrigin,
    string? Error = null);

/// <summary>Outcome of a real <c>git commit-tree</c> in a scratch repository.</summary>
public sealed record CommitProbeFact(bool Ran, bool Succeeded, string? Output);

/// <summary>
/// A data folder and the mount it lives on, from <c>/proc/self/mountinfo</c>.
/// <see cref="HostFolder"/> is the folder below <c>STUDIO_DATA_DIR</c> the
/// networked stack mounts there (<c>workspace</c>, <c>home</c>,
/// <c>projects/&lt;checkout&gt;</c>).
/// </summary>
public sealed record DataFolderFact(
    string Name,
    string Path,
    string HostFolder,
    bool Exists,
    string? MountPoint,
    string? FileSystemType,
    string? MountTableError = null);

/// <summary>Whether Studio could create and remove a probe file in a state folder.</summary>
public sealed record StateFolderFact(string Name, string Path, bool Writable, string? Error);

/// <summary>A Git lock file in a repository and its age.</summary>
public sealed record GitLockFact(string Path, TimeSpan Age);

/// <summary>A pull gate worker as the gate store last heard from it.</summary>
public sealed record GateWorkerHostFact(string Name, int Slots, DateTimeOffset LastHeartbeat, bool Healthy);

/// <summary>
/// The gate configuration and reachability: registered pull workers, the SSH
/// gate host (when configured) and the outcome of its last contact.
/// </summary>
public sealed record GateWorkerFact(
    IReadOnlyList<GateWorkerHostFact> PullWorkers,
    string? SshHost,
    string SshReachability,
    DateTimeOffset? LastReachedAt,
    DateTimeOffset? LastFailureAt);

/// <summary>One enrolled execution host identity that is neither revoked nor retired.</summary>
public sealed record ExecutionHostFact(string Id, string Name, DateTime EnrolledAt, DateTime? LastSeenAt);

public sealed record ExecutionHostsFact(IReadOnlyList<ExecutionHostFact> Hosts, int PendingEnrollments);

/// <summary>The GitHub App connection and its per-project coverage, keyed by project display name.</summary>
public sealed record GitHubAppFact(
    bool Connected,
    string? InstallUrl,
    string? Problem,
    IReadOnlyDictionary<string, GitHubAppCoverageFact> Coverage);

public sealed record GitHubAppCoverageFact(string State, string? RepositoryUrl, long? InstallationId, string? Detail);

/// <summary>
/// What one <c>git for-each-ref</c> in a checkout reported: the refs under
/// <c>refs/heads</c>, <c>refs/remotes/origin</c> and the Studio integration
/// lanes, plus the branch <c>origin/HEAD</c> points at.
/// </summary>
public sealed record RepositoryRefsFact(
    bool CheckoutExists,
    bool IsGitCheckout,
    IReadOnlySet<string> Refs,
    string? OriginHead,
    string? Error = null)
{
    public static RepositoryRefsFact Missing { get; } = new(false, false, new HashSet<string>(), null);
    public static RepositoryRefsFact NotGit { get; } = new(true, false, new HashSet<string>(), null);
    public bool HasOrigin => Refs.Any(item => item.StartsWith("refs/remotes/origin/", StringComparison.Ordinal));
}

/// <summary>One registered project with a repository.</summary>
public sealed record ProjectReadinessFact(
    string Name,
    string RepositoryPath,
    string? RepositoryVisibility,
    string? ConfiguredIntegrationBranch,
    RepositoryRefsFact Refs,
    DataFolderFact Checkout,
    IReadOnlyList<GitLockFact> Locks);
