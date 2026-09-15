namespace AgentStudio.Git;

/// <summary>
/// What <see cref="IntegrationWorktreeService.Prepare"/> has to do before the
/// merge may run. Kept as a closed set so the decision can be taken by pure
/// policy and proven by a direct matrix test.
/// </summary>
public enum IntegrationWorktreeAction
{
    /// <summary>No integration worktree exists yet; add one.</summary>
    Create,
    /// <summary>A registered, intact worktree is there; reset it and use it.</summary>
    Reuse,
    /// <summary>A stale directory or a stale registration survived; drop it and add a fresh one.</summary>
    Recreate,
    /// <summary>The project has no usable git repository to hang a worktree off.</summary>
    Unavailable,
}

/// <summary>
/// The observable on-disk and git-registration facts about the canonical
/// integration worktree path of one project.
/// </summary>
public sealed record IntegrationWorktreeFacts(
    bool RepositoryResolved,
    bool DirectoryExists,
    bool RegisteredWithRepository,
    bool HasGitLink);

public sealed record IntegrationWorktreeDecision(IntegrationWorktreeAction Action, string Reason);

/// <summary>
/// Pure lifecycle policy for the Studio-owned integration worktree. A crash, a
/// manually deleted temp folder, or a pruned registration must all converge on
/// a usable worktree without ever falling back to the developer checkout, so
/// every combination of the four facts has an explicit answer here.
/// </summary>
public static class IntegrationWorktreePolicy
{
    public static IntegrationWorktreeDecision Decide(IntegrationWorktreeFacts facts)
    {
        if (!facts.RepositoryResolved)
            return new(
                IntegrationWorktreeAction.Unavailable,
                "The project has no resolvable git repository to host an integration worktree.");

        if (!facts.DirectoryExists)
            return facts.RegisteredWithRepository
                ? new(
                    IntegrationWorktreeAction.Recreate,
                    "The worktree registration outlived its directory.")
                : new(
                    IntegrationWorktreeAction.Create,
                    "No integration worktree exists yet.");

        if (!facts.RegisteredWithRepository)
            return new(
                IntegrationWorktreeAction.Recreate,
                "The directory is not a registered worktree of this repository.");

        if (!facts.HasGitLink)
            return new(
                IntegrationWorktreeAction.Recreate,
                "The registered worktree directory lost its .git link.");

        return new(IntegrationWorktreeAction.Reuse, "The integration worktree is registered and intact.");
    }
}

/// <summary>
/// Canonical location of the Studio-owned integration worktrees. One directory
/// per project under a single root, so an operator can find and delete them
/// without guessing, and so
/// <c>WorktreeOrphanDirectorySweeper</c> - which only reaps directories WITHOUT
/// a <c>.git</c> link - leaves a live integration worktree alone.
/// </summary>
public static class IntegrationWorktreeLocator
{
    /// <summary>Directory name inside the per-project worktree folder.</summary>
    public const string DirectoryName = "_integration";

    /// <summary>Default root: the same temp root the per-task coding worktrees use.</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "ass-worktrees");

    public static string PathFor(string root, string project)
        => Path.Combine(
            string.IsNullOrWhiteSpace(root) ? DefaultRoot : root,
            SafeSegment(project),
            DirectoryName);

    /// <summary>True when a prepared integration worktree lives at <paramref name="path"/>.</summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var git = Path.Combine(path, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string SafeSegment(string value)
        => System.Text.RegularExpressions.Regex.Replace(
            string.IsNullOrWhiteSpace(value) ? "project" : value,
            "[^A-Za-z0-9_.-]",
            "-");
}

/// <summary>
/// The prepared integration checkout for one project. <see cref="Path"/> is
/// only meaningful when <see cref="Success"/> is true.
/// </summary>
public sealed record IntegrationWorktreeLease(string? Path, string? Error)
{
    public bool Success => Error is null && !string.IsNullOrWhiteSpace(Path);

    public static IntegrationWorktreeLease Ready(string path) => new(path, null);
    public static IntegrationWorktreeLease Failed(string error) => new(null, error);
}

/// <summary>
/// Owns the integration checkout of a local project (AGT-2832).
///
/// <para>
/// Integration used to merge, check out, and fast-forward inside the project's
/// configured repository path - the developer's own checkout. Any unrelated
/// uncommitted edit there refused the merge ("Integration working tree has
/// uncommitted changes; refusing to merge"), and every merge silently moved the
/// developer's checked-out branch. Studio now performs those mutations in its
/// own linked worktree instead: the object store and refs are shared (so local
/// <c>task/&lt;id&gt;</c> deliveries remain visible and the integration branch
/// still advances), while the developer's working tree and index are never
/// touched.
/// </para>
///
/// <para>
/// The worktree is created on demand at
/// <c>&lt;root&gt;/&lt;project&gt;/_integration</c> on a detached HEAD, and is
/// reset (in-progress merge/rebase aborted, <c>reset --hard</c>,
/// <c>clean -fd</c>) before every integration, so a crashed merge cannot poison
/// the next one. It is disposable: deleting the directory costs one
/// re-creation.
/// </para>
/// </summary>
public sealed class IntegrationWorktreeService
{
    private readonly GitService _git;
    private readonly ILogger<IntegrationWorktreeService> _logger;
    private readonly string _root;

    public IntegrationWorktreeService(
        GitService git,
        ILogger<IntegrationWorktreeService> logger,
        IConfiguration? config = null)
    {
        _git = git;
        _logger = logger;
        var configured = config?["Integration:WorktreeRoot"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? IntegrationWorktreeLocator.DefaultRoot
            : configured!;
    }

    /// <summary>Canonical integration worktree path of a project, whether or not it exists yet.</summary>
    public string PathFor(string project) => IntegrationWorktreeLocator.PathFor(_root, project);

    /// <summary>
    /// Returns a clean, Studio-owned checkout of <paramref name="repositoryRoot"/>
    /// to integrate in. Never returns the developer checkout: when the worktree
    /// cannot be prepared the lease fails and the caller reports a typed
    /// integration error instead of mutating somebody else's working tree.
    /// </summary>
    public IntegrationWorktreeLease Prepare(
        string project,
        string? repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(project);

        // Idempotent: a caller that already holds the integration worktree (a
        // retry, or a project whose configured repository path IS a Studio
        // worktree) only needs the reset.
        if (!string.IsNullOrWhiteSpace(repositoryRoot)
            && PathsEqual(repositoryRoot!, path))
        {
            return ResetOrFail(path);
        }

        var facts = Inspect(repositoryRoot, path);
        var decision = IntegrationWorktreePolicy.Decide(facts);
        if (decision.Action == IntegrationWorktreeAction.Unavailable)
            return IntegrationWorktreeLease.Failed(
                $"Integration worktree unavailable for project '{project}': {decision.Reason}");

        var root = repositoryRoot!;
        if (decision.Action == IntegrationWorktreeAction.Recreate)
        {
            _logger.LogInformation(
                "Recreating integration worktree for {Project} at {Path}: {Reason}",
                project, path, decision.Reason);
            Discard(root, path);
        }

        if (decision.Action != IntegrationWorktreeAction.Reuse)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _git.WorktreePrune(root);
            var added = _git.WorktreeAddDetached(root, path, "HEAD");
            if (!added.Success)
                return IntegrationWorktreeLease.Failed(
                    $"Could not create the integration worktree at '{path}': {added.Error}");
            _logger.LogInformation(
                "Integration worktree created for {Project} at {Path} (repository {Repository})",
                project, path, root);
        }

        return ResetOrFail(path);
    }

    /// <summary>
    /// Removes the integration worktree of a project (registration first, then
    /// the directory). Safe to call when nothing exists.
    /// </summary>
    public bool Remove(string project, string? repositoryRoot)
    {
        var path = PathFor(project);
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return !Directory.Exists(path);
        Discard(repositoryRoot!, path);
        return !Directory.Exists(path);
    }

    private IntegrationWorktreeLease ResetOrFail(string path)
    {
        var reset = _git.ResetIntegrationWorktree(path);
        return reset.Success
            ? IntegrationWorktreeLease.Ready(path)
            : IntegrationWorktreeLease.Failed(
                $"Could not reset the integration worktree at '{path}': {reset.Error}");
    }

    private IntegrationWorktreeFacts Inspect(string? repositoryRoot, string path)
    {
        var resolved = !string.IsNullOrWhiteSpace(repositoryRoot)
                       && Directory.Exists(repositoryRoot)
                       && _git.IsGitRepo(repositoryRoot);
        if (!resolved) return new(false, Directory.Exists(path), false, false);

        var registered = _git.ListWorktrees(repositoryRoot!)
            .Any(entry => PathsEqual(entry.Path, path));
        return new(true, Directory.Exists(path), registered, IntegrationWorktreeLocator.Exists(path));
    }

    private void Discard(string repositoryRoot, string path)
    {
        if (Directory.Exists(path))
            _git.WorktreeRemove(repositoryRoot, path);
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(
                ex, "IntegrationWorktreeService: stale integration worktree directory");
        }
        _git.WorktreePrune(repositoryRoot);
    }

    private static bool PathsEqual(string left, string right)
    {
        static string Normalize(string value)
        {
            try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return value; }
        }

        return string.Equals(
            Normalize(left),
            Normalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
