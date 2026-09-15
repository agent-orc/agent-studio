using AgentStudio.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Git;

/// <summary>
/// The prepared integration slot, or the reason none could be prepared.
/// Integration fails closed: a caller that cannot get a worktree must report
/// the failure instead of falling back to the developer checkout.
/// </summary>
public sealed record IntegrationWorktreeResolution(
    string? Path,
    IntegrationWorktreeAction Action,
    string? Error)
{
    public bool Success => !string.IsNullOrWhiteSpace(Path) && Error is null;

    public static IntegrationWorktreeResolution Failed(string error)
        => new(null, IntegrationWorktreeAction.Blocked, error);

    public static IntegrationWorktreeResolution Prepared(string path, IntegrationWorktreeAction action)
        => new(path, action, null);
}

/// <summary>
/// Owns the per-project integration worktree: the dedicated working tree in
/// which Studio merges deliveries into the integration branch.
///
/// <para>Before AGT-2832 the merge ran in the registered project checkout, so a
/// developer's unrelated uncommitted edits refused the integration ("Integration
/// working tree has uncommitted changes; refusing to merge") and a successful
/// merge silently switched that checkout's branch. The worktree here shares the
/// repository's object store and refs - the merge result is the same commit
/// graph - but it belongs to Studio: it is reset before every integration, and
/// its HEAD is always detached so it never takes a branch away from the person
/// working in the checkout.</para>
///
/// <para>Placement is derived from the repository path
/// (<see cref="IntegrationWorktreePolicy"/>), so projects that existed before
/// this change simply get their worktree created on their next integration.</para>
/// </summary>
public sealed class IntegrationWorktreeProvider
{
    private readonly GitService _git;
    private readonly ILogger<IntegrationWorktreeProvider> _logger;
    private readonly string? _temporaryRoot;

    public IntegrationWorktreeProvider(
        GitService git,
        ILogger<IntegrationWorktreeProvider>? logger = null,
        string? temporaryRoot = null)
    {
        _git = git;
        _logger = logger ?? NullLogger<IntegrationWorktreeProvider>.Instance;
        _temporaryRoot = temporaryRoot;
    }

    /// <summary>
    /// Prepares and returns the integration worktree for <paramref name="repoRoot"/>.
    /// Boundary validation, then the pure placement decision, then the bounded
    /// git side effects: register the slot when it is missing or stale, detach
    /// it at the integration branch, and drop whatever the previous integration
    /// left behind.
    /// </summary>
    public IntegrationWorktreeResolution Resolve(
        string? repoRoot,
        string? integrationBranch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return IntegrationWorktreeResolution.Failed("Could not resolve the project repository for the integration worktree.");
        if (cancellationToken.IsCancellationRequested)
            return IntegrationWorktreeResolution.Failed("Integration worktree preparation was cancelled.");

        var baseRef = !string.IsNullOrWhiteSpace(integrationBranch)
            && _git.BranchExists(repoRoot, integrationBranch!)
                ? integrationBranch!
                : "HEAD";
        var registered = _git.ListWorktrees(repoRoot)
            .Select(entry => Normalize(entry.Path))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A checkout nested inside another repository must not get a sibling
        // container: that would be untracked content in the outer repository.
        var parent = Path.GetDirectoryName(Path.GetFullPath(repoRoot!));
        var parentIsInsideRepository = !string.IsNullOrWhiteSpace(parent) && _git.IsGitRepo(parent);

        string? lastError = null;
        foreach (var candidate in IntegrationWorktreePolicy.CandidatePaths(
                     repoRoot!, _temporaryRoot, parentIsInsideRepository))
        {
            var decision = IntegrationWorktreePolicy.Decide(Observe(repoRoot!, candidate, registered));
            if (decision.Action == IntegrationWorktreeAction.Blocked)
            {
                lastError = decision.Reason;
                continue;
            }

            var prepared = Prepare(repoRoot!, candidate, decision, baseRef);
            if (prepared.Success)
            {
                _logger.LogInformation(
                    "Integration worktree {Action} for {Repo}: {Path} (detached at {BaseRef})",
                    prepared.Action, repoRoot, prepared.Path, baseRef);
                return prepared;
            }
            lastError = prepared.Error;
            _logger.LogWarning(
                "Integration worktree slot {Path} for {Repo} could not be prepared: {Error}",
                candidate, repoRoot, prepared.Error);
        }

        return IntegrationWorktreeResolution.Failed(
            "No integration worktree could be prepared for this project"
            + (string.IsNullOrWhiteSpace(lastError) ? "." : $": {lastError}"));
    }

    private static IntegrationWorktreeState Observe(
        string repoRoot,
        string candidate,
        IReadOnlySet<string?> registered)
    {
        var exists = Directory.Exists(candidate);
        return new IntegrationWorktreeState(
            IsDeveloperCheckout: string.Equals(Normalize(candidate), Normalize(repoRoot), StringComparison.OrdinalIgnoreCase),
            DirectoryExists: exists,
            DirectoryIsEmpty: exists && IsEmptyDirectory(candidate),
            RegisteredForRepository: registered.Contains(Normalize(candidate)),
            HasGitAdminLink: exists
                && (File.Exists(Path.Combine(candidate, ".git")) || Directory.Exists(Path.Combine(candidate, ".git"))));
    }

    /// <summary>
    /// An unreadable directory counts as occupied, not empty: the slot is then
    /// unusable and the next candidate gets its turn instead of the probe
    /// throwing out of the whole resolution.
    /// </summary>
    private static bool IsEmptyDirectory(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationWorktreeProvider: slot probe is best-effort");
            return false;
        }
    }

    private IntegrationWorktreeResolution Prepare(
        string repoRoot,
        string candidate,
        IntegrationWorktreeDecision decision,
        string baseRef)
    {
        if (decision.Action == IntegrationWorktreeAction.Recreate)
        {
            _logger.LogInformation(
                "Recreating the integration worktree at {Path}: {Reason}", candidate, decision.Reason);
            _git.WorktreeRemove(repoRoot, candidate);
            try
            {
                if (Directory.Exists(candidate))
                    Directory.Delete(candidate, recursive: true);
            }
            catch (Exception ex)
            {
                return IntegrationWorktreeResolution.Failed(
                    $"The stale integration worktree at '{candidate}' could not be removed: {ex.Message}");
            }
            _git.WorktreePrune(repoRoot);
        }

        if (decision.Action != IntegrationWorktreeAction.Reuse)
        {
            try
            {
                var parent = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
            }
            catch (Exception ex)
            {
                return IntegrationWorktreeResolution.Failed(
                    $"The integration worktree container for '{candidate}' could not be created: {ex.Message}");
            }

            var added = _git.WorktreeAddDetached(repoRoot, candidate, baseRef);
            if (!added.Success)
            {
                return IntegrationWorktreeResolution.Failed(
                    $"The integration worktree at '{candidate}' could not be created: {added.Error}");
            }
        }

        var refreshed = Refresh(candidate, baseRef);
        return refreshed is null
            ? IntegrationWorktreeResolution.Prepared(candidate, decision.Action)
            : IntegrationWorktreeResolution.Failed(refreshed);
    }

    /// <summary>
    /// Resets the slot for one integration: detached at the integration branch,
    /// no leftover index or working-tree state from the previous merge, no
    /// untracked residue. The worktree belongs to Studio, so discarding its
    /// content is always safe - unlike doing the same in a developer checkout.
    /// </summary>
    private string? Refresh(string worktreePath, string baseRef)
    {
        _git.AbortInterruptedIntegration(worktreePath);

        var detached = _git.CheckoutDetachedAt(worktreePath, baseRef);
        if (!detached.Success)
            return $"The integration worktree at '{worktreePath}' could not be detached at '{baseRef}': {detached.Error}";

        var reset = _git.ResetHard(worktreePath);
        if (!reset.Success)
            return $"The integration worktree at '{worktreePath}' could not be reset: {reset.Error}";

        var cleaned = _git.CleanPreservingNodeModules(worktreePath);
        if (!cleaned.Success)
            return $"The integration worktree at '{worktreePath}' could not be cleaned: {cleaned.Error}";

        return null;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationWorktreeProvider: path normalisation is best-effort");
            return path.Trim();
        }
    }
}
