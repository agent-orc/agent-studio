using System.Collections.Concurrent;

namespace AgentStudio.Git;

public sealed record ManagedRepositoryMutationResult(
    bool Success,
    bool Changed,
    bool DidCommit,
    string? CommitSha,
    bool PushQueued,
    string? Error,
    bool PreflightBlocked = false)
{
    public static ManagedRepositoryMutationResult Failed(string error, bool preflightBlocked = false) =>
        new(false, false, false, null, false, error, preflightBlocked);
}

/// <summary>
/// Commit boundary for backend-owned writes to tracked project-repository
/// files. The write and its path-scoped commit share one repository gate. A
/// commit failure restores the bounded paths to HEAD, so the backend never
/// strands its own uncommitted mutation in an integration checkout.
/// </summary>
public sealed class ManagedRepositoryMutationService
{
    private static readonly ConcurrentDictionary<string, object> RepositoryGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly GitService _git;
    private readonly ProjectSettingsService? _settings;
    private readonly WorkspaceArtifactPushQueue? _pushQueue;
    private readonly ILogger<ManagedRepositoryMutationService>? _logger;

    public ManagedRepositoryMutationService(
        GitService git,
        ProjectSettingsService? settings = null,
        WorkspaceArtifactPushQueue? pushQueue = null,
        ILogger<ManagedRepositoryMutationService>? logger = null)
    {
        _git = git;
        _settings = settings;
        _pushQueue = pushQueue;
        _logger = logger;
    }

    public ManagedRepositoryMutationResult Execute(
        string projectName,
        string repositoryRoot,
        string operationId,
        string commitMessage,
        IReadOnlyCollection<string> repositoryRelativePaths,
        Action write,
        bool requireCleanCheckoutAndPush = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return ManagedRepositoryMutationResult.Failed("Project repository does not exist.", requireCleanCheckoutAndPush);

        var paths = repositoryRelativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        if (paths.Length == 0)
            return ManagedRepositoryMutationResult.Failed("No repository paths were supplied for the mutation.");

        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (RepositoryGates.GetOrAdd(root, static _ => new object()))
        {
            var before = _git.GetStatusForRepoRoot(root);
            if (before.IsRepo && before.Error != null)
                return ManagedRepositoryMutationResult.Failed(
                    $"Project repository status could not be read: {before.Error}", requireCleanCheckoutAndPush);
            if (requireCleanCheckoutAndPush)
            {
                if (!before.IsRepo)
                    return ManagedRepositoryMutationResult.Failed("Project checkout is not a git repository.", true);
                if (before.Files.Count > 0)
                    return ManagedRepositoryMutationResult.Failed(
                        $"Project checkout has pre-existing changes: {string.Join(", ", before.Files.Select(file => file.Path))}.", true);
                if (_pushQueue == null)
                    return ManagedRepositoryMutationResult.Failed("Repository push queue is unavailable.", true);
                if (string.IsNullOrWhiteSpace(before.Branch) || before.Branch == "HEAD")
                    return ManagedRepositoryMutationResult.Failed("Project checkout has no writable branch.", true);
                if (_git.RepositoryWriteLockReason(root, before.Branch) is { } lockReason)
                    return ManagedRepositoryMutationResult.Failed(lockReason, true);
                if (_settings != null && AutoPushStrategies.Normalize(_settings.Get(projectName).AutoPushStrategy)
                    == AutoPushStrategies.Never)
                    return ManagedRepositoryMutationResult.Failed("Project auto-push is disabled.", true);
            }
            if (before.IsRepo)
            {
                var alreadyDirty = RepositoryStatusPathPolicy.CoveredPaths(
                    before.Files.Select(file => file.Path), paths, PathComparer);
                if (alreadyDirty.Count > 0)
                {
                    return ManagedRepositoryMutationResult.Failed(
                        $"Refusing to overwrite pre-existing changes: {string.Join(", ", alreadyDirty)}.");
                }
            }

            try
            {
                write();
            }
            catch (Exception ex)
            {
                if (before.IsRepo) RestoreAfterFailure(root, paths, operationId);
                return ManagedRepositoryMutationResult.Failed(ex.Message);
            }

            // Non-git fixtures and local exported docs still use the same write
            // services, but there is no versioned mutation to record there.
            if (!before.IsRepo)
                return new ManagedRepositoryMutationResult(true, true, false, null, false, null);

            var afterWrite = _git.GetStatusForRepoRoot(root);
            if (!afterWrite.IsRepo || afterWrite.Error != null)
            {
                var cleanup = RestoreAfterFailure(root, paths, operationId);
                var suffix = cleanup.Success ? string.Empty : $" Cleanup also failed: {cleanup.Error}";
                return ManagedRepositoryMutationResult.Failed(
                    $"Project repository status became unavailable after the write: {afterWrite.Error ?? "not a git repository"}.{suffix}".Trim());
            }
            var changed = RepositoryStatusPathPolicy.CoveredPaths(
                afterWrite.Files.Select(file => file.Path), paths, PathComparer).Count > 0;
            if (!changed)
                return new ManagedRepositoryMutationResult(true, false, false, null, false, null);

            var commit = _git.CommitPaths(root, commitMessage, paths);
            if (!commit.Success || string.IsNullOrWhiteSpace(commit.Sha))
            {
                var cleanup = RestoreAfterFailure(root, paths, operationId);
                var suffix = cleanup.Success ? string.Empty : $" Cleanup also failed: {cleanup.Error}";
                return ManagedRepositoryMutationResult.Failed(
                    $"Managed repository commit failed: {commit.Error ?? "unknown git error"}.{suffix}".Trim());
            }

            var fullSha = _git.ReadHeadShaAt(root) ?? commit.Sha!;
            var pushQueued = QueuePush(projectName, root, operationId, fullSha,
                requireCleanCheckoutAndPush ? before.Branch : null, requireCleanCheckoutAndPush);
            _logger?.LogInformation(
                "managed-repository-mutation committed project={Project} operation={Operation} sha={Sha} paths={Paths} pushQueued={PushQueued}",
                projectName, operationId, fullSha, string.Join(",", paths), pushQueued);
            return new ManagedRepositoryMutationResult(
                true, true, true, fullSha, pushQueued, null);
        }
    }

    private bool QueuePush(string projectName, string repositoryRoot, string operationId, string sha,
        string? knownBranch = null, bool required = false)
    {
        if (_pushQueue == null) return false;

        var strategy = AutoPushStrategies.AlwaysImmediate;
        if (_settings != null && !required)
        {
            try { strategy = AutoPushStrategies.Normalize(_settings.Get(projectName).AutoPushStrategy); }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "ManagedRepositoryMutationService: project auto-push settings defaulted to immediate.");
            }
        }
        if (strategy == AutoPushStrategies.Never) return false;

        var branch = knownBranch ?? _git.ReadCurrentBranchAt(repositoryRoot);
        if (string.IsNullOrWhiteSpace(branch))
        {
            _logger?.LogWarning(
                "managed-repository-mutation push skipped because HEAD is detached project={Project} operation={Operation} sha={Sha}",
                projectName, operationId, sha);
            return false;
        }

        return _pushQueue.Enqueue(new WorkspaceArtifactPushRequest(
            repositoryRoot,
            operationId,
            TargetBranch: branch,
            Sha: sha,
            Project: projectName));
    }

    private GitWorktreeResult RestoreAfterFailure(
        string repositoryRoot,
        IReadOnlyCollection<string> paths,
        string operationId)
    {
        var restored = _git.RestorePathsToHead(repositoryRoot, paths);
        if (!restored.Success)
        {
            _logger?.LogError(
                "managed-repository-mutation cleanup failed operation={Operation} repo={Repo} paths={Paths} error={Error}",
                operationId, repositoryRoot, string.Join(",", paths), restored.Error);
        }
        return restored;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
