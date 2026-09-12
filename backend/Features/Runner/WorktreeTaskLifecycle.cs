using Microsoft.Extensions.Logging;

namespace AgentStudio.Runner;

/// <summary>Result of the worktree pre-step: an isolated worktree on a fresh task branch.</summary>
/// <param name="Reused">
/// True when an EXISTING branch/worktree was re-used (resume / reissue / recovery);
/// false when a fresh <c>task/&lt;id&gt;</c> branch was cut. The runner uses this to
/// decide whether a recorded CLI session may be resumed: a session born in this same
/// worktree can be resumed, but a fresh cut must start a new session (the old session's
/// cwd was the main checkout and would hang on <c>--resume</c>).
/// </param>
public sealed record WorktreePreparation(bool Success, string? WorktreePath, string? Branch, string? Error, bool Reused = false);

public enum IntegrationOutcome
{
    /// <summary>Task branch was folded into the integration branch (direct-merge).</summary>
    Merged,
    /// <summary>Rebase onto the integration tip conflicted; left for escalation / PR fallback.</summary>
    Conflict,
    /// <summary>pull-request strategy: nothing was merged here; the team's review owns it.</summary>
    PushedForReview,
    /// <summary>A git step failed for a reason other than a conflict.</summary>
    Error,
}

/// <summary>Result of the integration (merge) post-step.</summary>
public sealed record IntegrationResult(
    IntegrationOutcome Outcome,
    string? IntegratedSha,
    string? Error,
    IReadOnlyList<string>? ConflictedFiles = null)
{
    public bool Merged => Outcome == IntegrationOutcome.Merged;
}

/// <summary>Result of the worktree teardown post-step.</summary>
public sealed record TeardownResult(bool Success, string? Error, string? StalePath = null);

internal sealed record WorktreeDirectoryCleanupResult(
    bool CanonicalPathCleared,
    string? StalePath,
    string? Error);

internal interface IWorktreeDirectoryCleanup
{
    WorktreeDirectoryCleanupResult Clear(string path);
}

/// <summary>
/// Bounded physical cleanup after Git has released (or already lost) its
/// worktree registration. A directory that remains undeletable is renamed out
/// of the canonical slot so the next pickup never collides with it.
/// </summary>
internal sealed class WorktreeDirectoryCleanup : IWorktreeDirectoryCleanup
{
    private readonly Action<string> _delete;
    private readonly Func<DateTime> _utcNow;

    internal WorktreeDirectoryCleanup(Action<string>? delete = null, Func<DateTime>? utcNow = null)
    {
        _delete = delete ?? DeleteDirectoryWithoutFollowingReparsePoints;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public WorktreeDirectoryCleanupResult Clear(string path)
    {
        if (!Directory.Exists(path)) return new(true, null, null);

        Exception? last = null;
        foreach (var delay in new[] { 0, 50, 150 })
        {
            if (delay > 0) Thread.Sleep(delay);
            try
            {
                _delete(path);
                if (!Directory.Exists(path)) return new(true, null, null);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        var stamp = _utcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var stalePath = path + $".stale-{stamp}" + (suffix == 0 ? string.Empty : $"-{suffix + 1}");
            if (Directory.Exists(stalePath)) continue;
            try
            {
                Directory.Move(path, stalePath);
                return new(true, stalePath, last?.Message);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        return new(!Directory.Exists(path), null, last?.Message ?? $"Could not remove or rename '{path}'.");
    }

    internal static void DeleteDirectoryWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            ClearReadOnly(path, attributes);
            Directory.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var entryAttributes = File.GetAttributes(entry);
            if ((entryAttributes & FileAttributes.Directory) == 0)
            {
                ClearReadOnly(entry, entryAttributes);
                File.Delete(entry);
                continue;
            }

            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                ClearReadOnly(entry, entryAttributes);
                Directory.Delete(entry);
                continue;
            }

            DeleteDirectoryWithoutFollowingReparsePoints(entry);
        }

        ClearReadOnly(path, attributes);
        Directory.Delete(path);
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}

/// <summary>
/// ADR-0052 §3-§6: composes the low-level <see cref="GitService"/> worktree
/// primitives (added in slice 1) into the per-task pipeline steps the parallel
/// runner drives - the worktree <b>pre-step</b> (<see cref="Prepare"/>), the
/// integration / merge <b>post-step</b> (<see cref="Integrate"/>), and the
/// cleanup <b>post-step</b> (<see cref="Teardown"/>). All git lives here, never
/// in the run agent (§4): the agent only edits files inside its worktree.
///
/// <para>
/// The <c>direct-merge</c> strategy rebases the task branch onto the latest
/// integration tip and fast-forwards the integration branch onto it, so history
/// stays linear and a conflict surfaces deterministically (rebase aborts, leaving
/// the worktree clean) rather than as a silent merge commit. The
/// <c>pull-request</c> strategy stops short of merging - PR creation is its own
/// slice (concept §8.6); this step leaves the pushed branch for the team's review
/// gate. The per-project <b>merge-queue</b> that serializes concurrent
/// integrations into one branch at a time is a runner concern (it owns the slots)
/// and is layered on top of this stateless service.
/// </para>
/// </summary>
public sealed class WorktreeTaskLifecycle
{
    private readonly GitService _git;
    private readonly ILogger<WorktreeTaskLifecycle> _logger;
    private readonly IWorktreeDirectoryCleanup _directoryCleanup;

    public WorktreeTaskLifecycle(GitService git, ILogger<WorktreeTaskLifecycle> logger)
        : this(git, logger, null)
    {
    }

    internal WorktreeTaskLifecycle(
        GitService git,
        ILogger<WorktreeTaskLifecycle> logger,
        IWorktreeDirectoryCleanup? directoryCleanup)
    {
        _git = git;
        _logger = logger;
        _directoryCleanup = directoryCleanup ?? new WorktreeDirectoryCleanup();
    }

    /// <summary>The ephemeral branch name for a task: <c>task/&lt;sanitized-id&gt;</c>.</summary>
    public static string BranchFor(string taskId) => "task/" + SanitizeId(taskId);

    /// <summary>
    /// Pre-step: cut a fresh <c>task/&lt;id&gt;</c> branch off
    /// <paramref name="integrationBranch"/> and check it out in its own worktree
    /// under <paramref name="worktreeRoot"/>. The shared <c>.git</c> is reused
    /// (no clone). Fails cleanly when the branch already exists or the path is
    /// occupied.
    /// </summary>
    public WorktreePreparation Prepare(string repoRoot, string taskId, string integrationBranch, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new WorktreePreparation(false, null, null, "Task id is required.");
        if (string.IsNullOrWhiteSpace(worktreeRoot))
            return new WorktreePreparation(false, null, null, "Worktree root is required.");

        var branch = BranchFor(taskId);
        var path = Path.Combine(worktreeRoot, WorktreeDirName(taskId));

        var add = _git.WorktreeAdd(repoRoot, path, branch, integrationBranch);
        if (!add.Success)
        {
            _logger.LogWarning("Worktree prepare failed for task {TaskId} on {Branch}: {Error}", taskId, branch, add.Error);
            return new WorktreePreparation(false, path, branch, add.Error);
        }
        _logger.LogInformation("Prepared worktree for task {TaskId}: branch {Branch} off {From} at {Path}",
            taskId, branch, integrationBranch, add.Path);
        return new WorktreePreparation(true, add.Path, branch, null);
    }

    /// <summary>
    /// Pre-step variant that is <b>idempotent across resume / reissue</b>. A task's
    /// worktree is owned by the TASK, not the run: the first run cuts a fresh
    /// <c>task/&lt;id&gt;</c> branch + worktree (delegates to <see cref="Prepare"/>),
    /// and every later run for the same task REUSES it. This is what stops a
    /// <c>continue</c>/reissue from failing with "branch already exists" and then
    /// falling back to the shared main checkout (the corruption bug ADR-0052 §3-§4
    /// exists to prevent at <c>MaxParallelism &gt; 1</c>).
    ///
    /// <para>
    /// Reuse order: (1) the branch is already checked out in a live worktree ->
    /// reuse that path as-is; (2) the branch exists but is not checked out
    /// anywhere -> attach it to the task's deterministic worktree path
    /// (<see cref="GitService.WorktreeAddExisting"/>), preserving its commits;
    /// (3) no branch yet -> fresh cut off <paramref name="integrationBranch"/>.
    /// </para>
    /// </summary>
    public WorktreePreparation PrepareOrReuse(string repoRoot, string taskId, string integrationBranch, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new WorktreePreparation(false, null, null, "Task id is required.");
        if (string.IsNullOrWhiteSpace(worktreeRoot))
            return new WorktreePreparation(false, null, null, "Worktree root is required.");

        var branch = BranchFor(taskId);
        var path = Path.Combine(worktreeRoot, WorktreeDirName(taskId));

        // Clear any dead registrations a crash left behind so a deterministic
        // path can be reused rather than colliding with a stale admin entry.
        _git.WorktreePrune(repoRoot);

        // 1) Branch is live in a worktree already -> reuse it unchanged.
        var live = _git.WorktreePathForBranch(repoRoot, branch);
        if (!string.IsNullOrEmpty(live) && Directory.Exists(live))
        {
            _logger.LogInformation("Reusing live worktree for task {TaskId}: branch {Branch} at {Path}", taskId, branch, live);
            return new WorktreePreparation(true, live, branch, null, Reused: true);
        }

        // 2) Branch exists but is detached from any worktree -> re-attach it.
        if (_git.BranchExists(repoRoot, branch))
        {
            // A stale dir/registration at the canonical path (registered-but-
            // orphaned, or a leftover from a partial teardown) would block the
            // attach. Clear it AND verify it is actually free — a busy holder
            // (leftover capture server) means reject cleanly here instead of
            // letting the add throw a confusing "already exists" (AGT-1785).
            if (!ClearStaleCanonicalWorktreePath(repoRoot, taskId, branch, path))
                return new WorktreePreparation(false, path, branch, $"Orphan worktree dir busy at {path}; deferring task {taskId}.");

            // S11: a stale branch that is folded into the integration branch AND
            // strictly BEHIND its current tip (a leftover from a failed/escalated
            // run) would be re-attached onto OLD commits instead of cutting fresh.
            // When it is merged-and-stale and not checked out anywhere, delete it
            // and fresh-cut so the rerun gets the latest code.
            //  - MERGED-AND-STALE only: an unmerged branch carries real in-progress
            //    work and is preserved (re-attached below) — same invariant as
            //    TeardownIfIntegrated.
            //  - A branch already AT the integration tip has nothing to gain from a
            //    recut, so it is re-attached as before (no behavior change).
            var checkedOut = _git.WorktreePathForBranch(repoRoot, branch);
            var notCheckedOut = string.IsNullOrEmpty(checkedOut) || !Directory.Exists(checkedOut);
            var mergedButStale = _git.IsAncestor(repoRoot, branch, integrationBranch)
                                 && !_git.IsAncestor(repoRoot, integrationBranch, branch);
            if (notCheckedOut && mergedButStale)
            {
                _git.DeleteBranch(repoRoot, branch, force: true);
                _logger.LogInformation(
                    "Recut: deleted merged stale branch {Branch} for task {TaskId}; cutting fresh off {Integration}.",
                    branch, taskId, integrationBranch);
                return PrepareWithRetry(repoRoot, taskId, integrationBranch, worktreeRoot, branch, path);
            }

            var attach = _git.WorktreeAddExisting(repoRoot, path, branch);
            if (attach.Success)
            {
                _logger.LogInformation("Re-attached worktree for task {TaskId}: existing branch {Branch} at {Path}", taskId, branch, attach.Path);
                return new WorktreePreparation(true, attach.Path, branch, null, Reused: true);
            }
            _logger.LogWarning("Re-attach of existing branch {Branch} for task {TaskId} failed: {Error}", branch, taskId, attach.Error);
            return new WorktreePreparation(false, path, branch, attach.Error);
        }

        // 3) First run for this task -> fresh cut off the integration branch.
        //    A stale dir at the canonical path (leftover from a crashed/partial
        //    run whose branch was already pruned/deleted) makes `git worktree
        //    add` fail with "already exists"; clear it (and verify free) so the
        //    fresh cut succeeds.
        if (!ClearStaleCanonicalWorktreePath(repoRoot, taskId, branch, path))
            return new WorktreePreparation(false, path, branch, $"Orphan worktree dir busy at {path}; deferring task {taskId}.");
        return PrepareWithRetry(repoRoot, taskId, integrationBranch, worktreeRoot, branch, path);
    }

    /// <summary>
    /// S11: fresh-cut with one bounded retry. If <see cref="Prepare"/>'s
    /// <c>git worktree add -b</c> still reports a path/branch collision (a rare
    /// race where git re-materialised the registration between the prune and the
    /// add), re-prune once and retry exactly once. No infinite retry — a second
    /// collision returns the failure for the caller's existing reject path.
    /// </summary>
    private WorktreePreparation PrepareWithRetry(string repoRoot, string taskId, string integrationBranch, string worktreeRoot, string branch, string path)
    {
        var first = Prepare(repoRoot, taskId, integrationBranch, worktreeRoot);
        if (first.Success || !LooksLikeWorktreeCollision(first.Error))
            return first;

        _git.WorktreePrune(repoRoot);
        ClearStaleCanonicalWorktreePath(repoRoot, taskId, branch, path);
        _logger.LogInformation("Retrying fresh worktree cut for task {TaskId} after collision: {Error}", taskId, first.Error);
        return Prepare(repoRoot, taskId, integrationBranch, worktreeRoot);
    }

    private static bool LooksLikeWorktreeCollision(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        var e = error.ToLowerInvariant();
        return e.Contains("already exists") || e.Contains("already registered") || e.Contains("already checked out") || e.Contains("missing but already registered");
    }

    /// <summary>
    /// Merge post-step: fold the finished <paramref name="taskBranch"/> back into
    /// <paramref name="integrationBranch"/> per <paramref name="strategy"/>.
    /// For <c>direct-merge</c>: rebase the worktree onto the integration tip, then
    /// fast-forward the integration branch (which must be checked out in
    /// <paramref name="repoRoot"/>). A rebase conflict returns
    /// <see cref="IntegrationOutcome.Conflict"/> with the branch and worktree left
    /// intact for the conflict-resolution agent / PR fallback. Pass
    /// <paramref name="preserveConflictForResolution"/> to keep the conflicted
    /// rebase in place for a managed resolver; the default aborts and leaves the
    /// worktree clean for callers that only need conflict evidence. For
    /// <c>pull-request</c>: returns <see cref="IntegrationOutcome.PushedForReview"/>
    /// without merging.
    /// </summary>
    public IntegrationResult Integrate(
        string repoRoot,
        string worktreePath,
        string taskBranch,
        string integrationBranch,
        string strategy,
        bool preserveConflictForResolution = false)
    {
        if (string.Equals(strategy, IntegrationStrategies.PullRequest, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Integration for {Branch} deferred to review (pull-request strategy); not auto-merging into {Integration}",
                taskBranch, integrationBranch);
            return new IntegrationResult(IntegrationOutcome.PushedForReview, null, null);
        }

        var rebase = _git.RebaseOnto(
            worktreePath,
            integrationBranch,
            abortOnConflict: !preserveConflictForResolution);
        if (!rebase.Success)
        {
            _logger.LogWarning("Integration of {Branch} hit a rebase conflict onto {Integration}: {Error}",
                taskBranch, integrationBranch, rebase.Error);
            return new IntegrationResult(IntegrationOutcome.Conflict, null, rebase.Error, rebase.ConflictedFiles);
        }

        var ff = _git.MergeFastForward(repoRoot, taskBranch);
        if (!ff.Success)
        {
            _logger.LogWarning("Fast-forward of {Integration} onto {Branch} failed: {Error}",
                integrationBranch, taskBranch, ff.Error);
            return new IntegrationResult(IntegrationOutcome.Error, null, ff.Error);
        }

        var sha = _git.ReadHeadShaAt(repoRoot);
        _logger.LogInformation("Integrated {Branch} into {Integration} at {Sha}", taskBranch, integrationBranch, sha ?? "<unknown>");
        return new IntegrationResult(IntegrationOutcome.Merged, sha, null);
    }

    /// <summary>
    /// Completes a previously conflicted direct-merge integration after the
    /// orchestrator-owned resolver has edited the worktree. If a rebase is still
    /// active, the lifecycle continues it first, then fast-forwards the shared
    /// integration branch. No force merge is attempted.
    /// </summary>
    public IntegrationResult CompleteIntegrationAfterResolution(
        string repoRoot,
        string worktreePath,
        string taskBranch,
        string integrationBranch)
    {
        var unmerged = _git.ListUnmergedFiles(worktreePath);
        if (unmerged.Count > 0)
        {
            return new IntegrationResult(
                IntegrationOutcome.Conflict,
                null,
                "Unmerged files remain after conflict-resolution.",
                unmerged);
        }

        if (_git.IsRebaseInProgress(worktreePath))
        {
            var cont = _git.ContinueRebase(worktreePath);
            if (!cont.Success)
            {
                return new IntegrationResult(
                    IntegrationOutcome.Conflict,
                    null,
                    string.IsNullOrWhiteSpace(cont.Error) ? "Could not continue the resolved rebase." : cont.Error,
                    cont.ConflictedFiles);
            }
        }

        var ff = _git.MergeFastForward(repoRoot, taskBranch);
        if (!ff.Success)
        {
            return new IntegrationResult(
                IntegrationOutcome.Error,
                null,
                string.IsNullOrWhiteSpace(ff.Error) ? "Fast-forward merge failed after conflict-resolution." : ff.Error);
        }

        var sha = _git.ReadHeadShaAt(repoRoot);
        _logger.LogInformation(
            "Completed resolved integration of {Branch} into {Integration} at {Sha}",
            taskBranch, integrationBranch, sha ?? "<unknown>");
        return new IntegrationResult(IntegrationOutcome.Merged, sha, null);
    }

    public Task<GitPushResult> PushTaskBranchWithRetryAsync(
        string repoRoot,
        string taskSha,
        string taskBranch,
        CancellationToken ct = default,
        int attempts = 3,
        TimeSpan? retryDelay = null)
    {
        return _git.PushShaWithRetryAsync(
            taskSha,
            repoRoot,
            ct,
            targetBranch: taskBranch,
            attempts: attempts,
            retryDelay: retryDelay);
    }

    /// <summary>
    /// Cleanup post-step: remove the task worktree and, when
    /// <paramref name="deleteBranch"/> is set, drop the task branch ref. The
    /// worktree is removed first because git refuses to delete a branch that is
    /// still checked out in a live worktree. When
    /// <paramref name="deleteRemoteBranch"/> is set, <c>origin/task/&lt;id&gt;</c>
    /// is dropped before the local ref so a merged task branch does not linger
    /// on the shared remote. <paramref name="force"/> chooses an unconditional
    /// branch delete (used after the work has been integrated or abandoned) over
    /// the safe merged-only delete. Best-effort: a failure on one step does not
    /// skip the other, and all errors are reported together.
    /// </summary>
    public TeardownResult Teardown(
        string repoRoot,
        string worktreePath,
        string? taskBranch,
        bool deleteBranch,
        bool force = false,
        bool deleteRemoteBranch = false)
    {
        string? error = null;

        var rm = _git.WorktreeRemove(repoRoot, worktreePath);
        string? stalePath = null;
        if (Directory.Exists(worktreePath))
        {
            var physical = _directoryCleanup.Clear(worktreePath);
            stalePath = physical.StalePath;
            if (!physical.CanonicalPathCleared)
                error = JoinErrors(rm.Error, physical.Error);
            else if (!string.IsNullOrWhiteSpace(stalePath))
                _logger.LogWarning(
                    "worktree-teardown-stale-renamed path={Path} stalePath={StalePath} gitError={GitError}",
                    worktreePath,
                    stalePath,
                    rm.Error);
        }
        else if (!rm.Success)
        {
            // Git may report "not a working tree" after a prior partial
            // teardown. The canonical filesystem slot is already clear, so it
            // is safe to continue and prune the stale administration entry.
            _logger.LogWarning(
                "worktree-teardown-registration-already-gone path={Path} gitError={GitError}",
                worktreePath,
                rm.Error);
        }
        _git.WorktreePrune(repoRoot);

        if (deleteBranch && !string.IsNullOrWhiteSpace(taskBranch))
        {
            if (deleteRemoteBranch)
            {
                var remoteDel = _git.DeleteRemoteBranch(repoRoot, taskBranch!);
                if (!remoteDel.Success) error = JoinErrors(error, remoteDel.Error);
            }

            var del = _git.DeleteBranch(repoRoot, taskBranch!, force);
            if (!del.Success) error = JoinErrors(error, del.Error);
        }

        if (error != null)
            _logger.LogWarning("Worktree teardown for {Path} reported: {Error}", worktreePath, error);
        return new TeardownResult(error is null, error, stalePath);
    }

    /// <summary>
    /// Terminal cleanup for a task that is leaving the run loop (accepted into
    /// review or escalated). Resolves the task's branch + worktree from disk (so
    /// it works without an in-memory run record on resume) and tears them down
    /// ONLY when the branch is already an ancestor of
    /// <paramref name="integrationBranch"/> - i.e. fully folded in. Unmerged work
    /// (a conflict left for resolution) is preserved, never force-dropped.
    /// Idempotent + best-effort: a task that never ran in a worktree, or whose
    /// branch is already gone, is a clean no-op. This is the deferred counterpart
    /// to <see cref="Teardown"/>: the runner no longer tears down per run so a
    /// resume/reissue can reuse the worktree (<see cref="PrepareOrReuse"/>).
    ///
    /// <para>
    /// AGT-1945 invariant: a worktree that still carries UNCOMMITTED work must
    /// never be torn down. A run whose auto-commit failed or was skipped leaves
    /// its deliverable only as dirty/untracked files in the worktree; the branch
    /// tip then still equals <paramref name="integrationBranch"/>, so the
    /// merge-ancestor gate below reads it as "already folded in" and the
    /// force-remove would wipe the work irreversibly. So we snapshot any
    /// uncommitted work onto <c>task/&lt;id&gt;</c> as a platform WIP commit
    /// FIRST (commit-push-doctrine: the platform owns the commit boundary). That
    /// commit puts the branch ahead of the integration branch, which then trips
    /// the same merged-ancestor gate into deferring teardown so a reissue / human
    /// review can still reach the work. If the snapshot itself fails we refuse to
    /// remove the worktree and report the failure rather than silently dropping
    /// the deliverable.
    /// </para>
    /// </summary>
    public TeardownResult TeardownIfIntegrated(string repoRoot, string taskId, string integrationBranch, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new TeardownResult(true, null);

        var branch = BranchFor(taskId);
        if (!_git.BranchExists(repoRoot, branch))
            return new TeardownResult(true, null); // never isolated / already cleaned

        var path = _git.WorktreePathForBranch(repoRoot, branch)
                   ?? Path.Combine(worktreeRoot, WorktreeDirName(taskId));

        // Preserve uncommitted work BEFORE any merge-state check: the check only
        // looks at committed history, so a dirty worktree on a branch that reads
        // as "merged" would otherwise be force-removed and lost (AGT-1945).
        if (Directory.Exists(path) && _git.RepoHasUncommittedChanges(path))
        {
            if (!PreserveUncommittedWork(taskId, branch, path))
            {
                var error = $"Refusing teardown for task {taskId}: worktree at {path} still carries "
                          + $"uncommitted work that could not be snapshotted onto {branch}; kept intact "
                          + "so the deliverable is not lost.";
                _logger.LogWarning("{Message}", error);
                return new TeardownResult(false, error);
            }
        }

        if (!_git.IsAncestor(repoRoot, branch, integrationBranch))
        {
            _logger.LogInformation(
                "Deferring teardown for task {TaskId}: branch {Branch} not yet merged into {Integration} (left for resolution)",
                taskId, branch, integrationBranch);
            return new TeardownResult(true, null);
        }

        return Teardown(repoRoot, path, branch, deleteBranch: true, force: true, deleteRemoteBranch: true);
    }

    /// <summary>
    /// AGT-1945 safety net: snapshot a worktree's uncommitted changes onto its
    /// <c>task/&lt;id&gt;</c> branch as a platform WIP commit before teardown
    /// removes the worktree. The commit is platform-owned (a run inside a managed
    /// slot never commits for itself) and carries the durable
    /// <see cref="GitService.WorktreeRunCommitTrailer"/> so per-task history
    /// reconstruction still finds it. Returns <c>true</c> when the worktree is
    /// clean afterwards - either the snapshot committed, or a benign race had
    /// already left nothing to commit - and <c>false</c> only when a real git
    /// failure means the work is still uncommitted and must not be discarded.
    /// </summary>
    private bool PreserveUncommittedWork(string taskId, string branch, string worktreePath)
    {
        var message =
            "chore(wip): preserve uncommitted task work before teardown\n\n"
            + "Platform WIP safety commit (AGT-1945): the worktree still carried "
            + $"uncommitted changes at terminal teardown; snapshotting onto {branch} "
            + "so a reissue or human review never loses the deliverable.\n\n"
            + GitService.WorktreeRunCommitTrailer(taskId);

        var commit = _git.WorktreeRunCommit(taskId, worktreePath, message,
            taskId: taskId, expectedBranch: branch);
        if (commit.Success)
        {
            _logger.LogWarning(
                "Preserved uncommitted work for task {TaskId} as WIP safety commit {Sha} on {Branch} before teardown.",
                taskId, commit.Sha ?? "<unknown>", branch);
            return true;
        }

        // WorktreeRunCommit reports a clean tree as a non-success "Nothing to
        // commit"; treat that as already-safe (the dirt was committed between the
        // status probe and here).
        if (!string.IsNullOrEmpty(commit.Error)
            && commit.Error.Contains("Nothing to commit", StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogWarning(
            "Could not preserve uncommitted work for task {TaskId} on {Branch}: {Error}",
            taskId, branch, commit.Error);
        return false;
    }

    /// <summary>
    /// Reduces a task id / slug to a git-safe branch + path segment: letters,
    /// digits, and <c>-_.</c> survive; everything else becomes a hyphen; leading
    /// and trailing separators are trimmed. Keeps the result inside what
    /// <see cref="GitService"/>'s branch-name guard accepts.
    /// </summary>
    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "task";
        var chars = id.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-', '.', '/');
        return string.IsNullOrEmpty(s) ? "task" : s;
    }

    /// <summary>
    /// On-disk worktree DIRECTORY name for a task. The branch keeps the full
    /// sanitized slug, but the worktree dir must stay SHORT: the agent builds
    /// INSIDE it (bin/obj/.../net10.0/runtimes/linux-arm64/native/...) and task
    /// slugs run up to ~160 chars, so the full slug plus deep build/node_modules
    /// paths blow past Windows MAX_PATH (260) -> "Filename too long" git/build
    /// failures -> the parallel run fails/wedges. Short ids keep their readable
    /// name; long ids become 24 readable chars + an 8-hex stable hash of the FULL
    /// id (deterministic, so Prepare / PrepareOrReuse / Teardown all resolve to
    /// the same directory).
    /// </summary>
    private static string WorktreeDirName(string taskId)
    {
        var sane = SanitizeId(taskId);
        if (sane.Length <= 40) return sane;
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(taskId)))[..8].ToLowerInvariant();
        return sane[..24] + "-" + hash;
    }

    /// <summary>
    /// S11 self-heal: ensure the canonical worktree path is FREE for a fresh
    /// <c>git worktree add</c>. Removes a stale dir (git worktree remove, then a
    /// reparse-safe manual delete if it survives), then RE-PRUNES so any admin
    /// registration whose dir is now gone is dropped. Returns <c>true</c> iff
    /// the path is actually clear afterwards; <c>false</c> means a holder still
    /// has it busy (e.g. a leftover capture server) and the caller must reject
    /// cleanly rather than collide on the add.
    /// </summary>
    private bool ClearStaleCanonicalWorktreePath(string repoRoot, string taskId, string branch, string path)
    {
        if (!Directory.Exists(path))
        {
            // The top-of-PrepareOrReuse prune already drops registrations whose
            // dir is gone, so an absent path is clean.
            return true;
        }

        var remove = _git.WorktreeRemove(repoRoot, path);
        if (Directory.Exists(path))
        {
            var cleanup = _directoryCleanup.Clear(path);
            if (cleanup.CanonicalPathCleared)
            {
                if (cleanup.StalePath is null)
                    _logger.LogWarning(
                        "worktree-stale-deleted task={TaskId} branch={Branch} path={Path} gitSuccess={RemoveSuccess} gitError={RemoveError}",
                        taskId,
                        branch,
                        path,
                        remove.Success,
                        remove.Error);
                else
                    _logger.LogWarning(
                        "worktree-stale-renamed task={TaskId} branch={Branch} path={Path} stalePath={StalePath} gitSuccess={RemoveSuccess} gitError={RemoveError}",
                        taskId,
                        branch,
                        path,
                        cleanup.StalePath,
                        remove.Success,
                        remove.Error);
            }
            else
            {
                _logger.LogWarning(
                    "worktree-stale-cleanup-failed task={TaskId} branch={Branch} path={Path} gitSuccess={RemoveSuccess} gitError={RemoveError} cleanupError={CleanupError}",
                    taskId,
                    branch,
                    path,
                    remove.Success,
                    remove.Error,
                    cleanup.Error);
            }
        }

        // S11 prune-order fix (AGT-1785): the prune at the top of PrepareOrReuse
        // ran while this orphan dir still existed, so `git worktree prune` (which
        // only drops entries whose dir is GONE) could not clear its registration.
        // Re-prune now that the dir is removed, otherwise the next
        // `git worktree add` collides with the surviving registration
        // ("<path> is already registered") → pick-reverted-no-run loop (AGT-1791).
        _git.WorktreePrune(repoRoot);

        // Verify: a detached holder (leftover Playwright capture server) may still
        // hold the dir busy so the manual delete threw and the path is occupied.
        return !Directory.Exists(path);
    }

    private static string? JoinErrors(string? first, string? second)
        => string.IsNullOrWhiteSpace(first)
            ? second
            : string.IsNullOrWhiteSpace(second) ? first : $"{first}; {second}";
}
