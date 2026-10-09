using AgentStudio.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Git;

/// <summary>
/// Which locks a caller may clear. <see cref="SharedRefs"/> is for a
/// repository whose working tree the server does not own (the project checkout
/// a gate worktree is created from): only the shared ref store is touched,
/// never that checkout's own index or HEAD.
/// </summary>
public enum GitLockSurface
{
    All,
    SharedRefs,
}

/// <summary>One lock file the guard saw and what it decided.</summary>
public sealed record GitLockFinding(string Path, TimeSpan Age, GitLockVerdict Verdict);

/// <summary>
/// Outcome of one <see cref="GitStaleLockGuard.EnsureWritable"/> call: the
/// locks it removed and the locks still in place after the single wait.
/// </summary>
public sealed record GitLockGuardResult(
    IReadOnlyList<GitLockFinding> Cleared,
    IReadOnlyList<GitLockFinding> Remaining,
    bool Waited)
{
    public static readonly GitLockGuardResult Clean = new([], [], false);
}

/// <summary>
/// Runs before a git write on a repository the Task Server owns (workspace
/// repository, integration worktrees, gate worktrees). It removes a lock file
/// that is older than the threshold and held by no git process, waits once for
/// a young or owned lock, and then lets the write proceed so git fails exactly
/// as before when the lock is still there (AGT-3000).
///
/// <para>Order: resolve the git directories from the filesystem (no git
/// spawn: a git spawn is what the lock is blocking), observe each lock's age,
/// probe process ownership only for aged locks, decide through
/// <see cref="GitStaleLockPolicy"/>, then delete. Never throws.</para>
/// </summary>
public sealed class GitStaleLockGuard
{
    private readonly ILogger<GitStaleLockGuard> _logger;
    private readonly IGitLockOwnerProbe _probe;
    private readonly TimeProvider _time;
    private readonly Action<TimeSpan> _wait;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _waitDuration;

    public GitStaleLockGuard(
        IConfiguration? configuration = null,
        ILogger<GitStaleLockGuard>? logger = null,
        IGitLockOwnerProbe? probe = null,
        TimeProvider? time = null)
        : this(configuration, logger, probe, time, wait: null)
    {
    }

    internal GitStaleLockGuard(
        IConfiguration? configuration,
        ILogger<GitStaleLockGuard>? logger,
        IGitLockOwnerProbe? probe,
        TimeProvider? time,
        Action<TimeSpan>? wait)
    {
        _logger = logger ?? NullLogger<GitStaleLockGuard>.Instance;
        _probe = probe ?? new GitProcessLockOwnerProbe();
        _time = time ?? TimeProvider.System;
        _wait = wait ?? Thread.Sleep;
        _threshold = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<int?>("GitStaleLocks:ThresholdMinutes")
                ?? (int)GitStaleLockPolicy.DefaultThreshold.TotalMinutes,
            1,
            24 * 60));
        _waitDuration = TimeSpan.FromMilliseconds(Math.Clamp(
            configuration?.GetValue<int?>("GitStaleLocks:WaitMilliseconds")
                ?? (int)GitStaleLockPolicy.DefaultWait.TotalMilliseconds,
            0,
            30_000));
    }

    public TimeSpan Threshold => _threshold;

    /// <summary>Boot cleanup for an owned integration slot after a timed-out git process.</summary>
    public bool ClearStaleIntegrationIndexLock(string worktreePath)
    {
        try
        {
            var scope = ResolveScope(worktreePath);
            if (scope is null || !File.Exists(Path.Combine(worktreePath, ".git"))) return false;
            var path = Path.Combine(scope.GitDirectory, "index.lock");
            if (!File.Exists(path)) return false;
            var observed = LockIdentity.Read(path);
            var age = _time.GetUtcNow().UtcDateTime - observed.LastWriteTimeUtc;
            if (observed.Length != 0 || age <= GitNetworkProcessRunner.DefaultTimeout
                || _probe.Probe(scope) != GitLockOwnership.None
                || LockIdentity.Read(path) != observed)
                return false;
            File.Delete(path);
            _logger.LogWarning("git-stale-integration-index-lock-cleared worktree={Worktree} lock={Lock} age={Age}",
                worktreePath, path, GitStaleLockPolicy.FormatAge(age));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git-stale-integration-index-lock-sweep-failed worktree={Worktree}", worktreePath);
            return false;
        }
    }

    /// <summary>Synchronous form for the lock-holding commit paths.</summary>
    public GitLockGuardResult EnsureWritable(string? repositoryPath, GitLockSurface surface = GitLockSurface.All)
    {
        var first = Sweep(repositoryPath, surface);
        if (first.Remaining.Count == 0) return first;
        _wait(_waitDuration);
        return Finish(repositoryPath, first);
    }

    /// <summary>Asynchronous form for the async gate preparation path.</summary>
    public async Task<GitLockGuardResult> EnsureWritableAsync(
        string? repositoryPath,
        GitLockSurface surface = GitLockSurface.All,
        CancellationToken ct = default)
    {
        var first = Sweep(repositoryPath, surface);
        if (first.Remaining.Count == 0) return first;
        await Task.Delay(_waitDuration, _time, ct).ConfigureAwait(false);
        return Finish(repositoryPath, first);
    }

    private GitLockGuardResult Finish(string? repositoryPath, GitLockGuardResult first)
    {
        // A lock that was young, owned, or replaced during the first sweep is
        // live for this write attempt. After the wait, only check existence;
        // a second deletion decision could remove a replacement with an old
        // timestamp. The next independent write may evaluate it afresh.
        var remaining = first.Remaining.Where(finding => File.Exists(finding.Path)).ToList();
        foreach (var kept in remaining)
        {
            _logger.LogWarning(
                "git-lock-kept repo={Repo} lock={Lock} age={Age} reason={Reason}",
                repositoryPath, kept.Path, GitStaleLockPolicy.FormatAge(kept.Age),
                GitStaleLockPolicy.Reason(kept.Verdict));
        }
        return new GitLockGuardResult(first.Cleared, remaining, Waited: true);
    }

    /// <summary>One pass: observe, decide, clear. No waiting.</summary>
    internal GitLockGuardResult Sweep(string? repositoryPath, GitLockSurface surface = GitLockSurface.All)
    {
        try
        {
            var scope = ResolveScope(repositoryPath);
            if (scope is null) return GitLockGuardResult.Clean;

            var locks = LockFiles(scope, surface).ToList();
            if (locks.Count == 0) return GitLockGuardResult.Clean;

            var now = _time.GetUtcNow().UtcDateTime;
            GitLockOwnership? ownership = null;
            var cleared = new List<GitLockFinding>();
            var remaining = new List<GitLockFinding>();
            foreach (var lockPath in locks)
            {
                LockIdentity observed;
                try { observed = LockIdentity.Read(lockPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                var age = now - observed.LastWriteTimeUtc;
                if (GitStaleLockPolicy.NeedsOwnershipProbe(age, _threshold))
                    ownership ??= _probe.Probe(scope);
                var verdict = GitStaleLockPolicy.Decide(age, _threshold, ownership ?? GitLockOwnership.Unknown);
                var finding = new GitLockFinding(lockPath, age, verdict);
                if (verdict != GitLockVerdict.Clear)
                {
                    remaining.Add(finding);
                    continue;
                }

                try
                {
                    // The ownership probe may take long enough for git to replace
                    // the lock. Never delete a different file at the same path.
                    if (LockIdentity.Read(lockPath) != observed)
                    {
                        remaining.Add(finding with { Verdict = GitLockVerdict.KeepOwnerUnknown });
                        continue;
                    }
                    File.Delete(lockPath);
                    cleared.Add(finding);
                    _logger.LogWarning(
                        "git-stale-lock-cleared repo={Repo} lock={Lock} age={Age}",
                        scope.WorkTree, lockPath, GitStaleLockPolicy.FormatAge(age));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    remaining.Add(finding with { Verdict = GitLockVerdict.KeepOwnerUnknown });
                    _logger.LogWarning(ex,
                        "git-stale-lock-clear-failed repo={Repo} lock={Lock} age={Age}",
                        scope.WorkTree, lockPath, GitStaleLockPolicy.FormatAge(age));
                }
            }
            return new GitLockGuardResult(cleared, remaining, Waited: false);
        }
        catch (Exception ex)
        {
            // The guard is an optimisation of recovery, never a new failure mode.
            _logger.LogWarning(ex, "git-stale-lock-guard failed repo={Repo}", repositoryPath);
            return GitLockGuardResult.Clean;
        }
    }

    private readonly record struct LockIdentity(DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, long Length)
    {
        public static LockIdentity Read(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Git lock disappeared during inspection", path);
            return new LockIdentity(file.CreationTimeUtc, file.LastWriteTimeUtc, file.Length);
        }
    }

    /// <summary>
    /// Resolves the working tree, private git directory and common directory
    /// from <c>.git</c> (a directory for a main checkout, a <c>gitdir:</c>
    /// pointer file for a linked worktree, whose <c>commondir</c> file names the
    /// shared repository). Returns null when the path is not a repository root.
    /// </summary>
    internal static GitLockScope? ResolveScope(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath)) return null;
        var workTree = Path.GetFullPath(repositoryPath);
        var dotGit = Path.Combine(workTree, ".git");

        string gitDir;
        if (Directory.Exists(dotGit))
        {
            gitDir = dotGit;
        }
        else if (File.Exists(dotGit))
        {
            var pointer = File.ReadAllText(dotGit).Trim();
            if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            var target = pointer[7..].Trim();
            gitDir = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(workTree, target));
            if (!Directory.Exists(gitDir)) return null;
        }
        else
        {
            return null;
        }

        var commonDir = gitDir;
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            var common = File.ReadAllText(commonFile).Trim();
            if (!string.IsNullOrWhiteSpace(common))
            {
                var resolved = Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(gitDir, common));
                if (Directory.Exists(resolved)) commonDir = resolved;
            }
        }
        var complete = TryResolveSharedWorkTrees(commonDir, out var sharedWorkTrees);
        return new GitLockScope(
            workTree, gitDir, commonDir,
            sharedWorkTrees.Where(path => !string.Equals(path, workTree, StringComparison.OrdinalIgnoreCase)).ToArray(),
            complete);
    }

    /// <summary>
    /// Every working tree that shares <paramref name="commonDir"/>'s ref store:
    /// the main checkout (parent of a non-bare <c>.git</c>) and each linked
    /// worktree registered under <c>worktrees/*/gitdir</c>. Returns false when
    /// any of them cannot be determined; the probe then treats ownership as
    /// unknown rather than miss a git process running from that tree.
    /// </summary>
    internal static bool TryResolveSharedWorkTrees(string commonDir, out List<string> workTrees)
    {
        workTrees = [];
        var complete = true;
        try
        {
            if (string.Equals(Path.GetFileName(commonDir.TrimEnd('/', '\\')), ".git", StringComparison.OrdinalIgnoreCase))
            {
                var mainRoot = Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(mainRoot)) workTrees.Add(Path.GetFullPath(mainRoot));
            }
            else if (!IsBareRepository(commonDir))
            {
                // A non-bare repository whose git directory is not named .git
                // (core.worktree or a separate git dir): its main tree is not
                // derivable from the layout.
                complete = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitStaleLockGuard: main working tree is unresolved");
            complete = false;
        }

        var registry = Path.Combine(commonDir, "worktrees");
        if (!Directory.Exists(registry)) return complete;
        try
        {
            foreach (var entry in Directory.EnumerateDirectories(registry))
            {
                try
                {
                    var pointer = File.ReadAllText(Path.Combine(entry, "gitdir")).Trim();
                    if (string.IsNullOrWhiteSpace(pointer)) { complete = false; continue; }
                    var dotGit = Path.GetFullPath(Path.IsPathRooted(pointer) ? pointer : Path.Combine(entry, pointer));
                    var root = Path.GetDirectoryName(dotGit);
                    if (string.IsNullOrEmpty(root)) { complete = false; continue; }
                    workTrees.Add(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SilentCatch.Note(ex, "GitStaleLockGuard: linked worktree registration is unreadable");
                    complete = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitStaleLockGuard: linked worktree registry is unreadable");
            complete = false;
        }
        return complete;
    }

    private static bool IsBareRepository(string gitDir)
    {
        var config = Path.Combine(gitDir, "config");
        if (!File.Exists(config)) return false;
        return File.ReadLines(config)
            .Select(line => line.Replace(" ", "").Replace("\t", "").Trim())
            .Any(line => string.Equals(line, "bare=true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The locks a git write can trip over: this tree's index and HEAD, and the
    /// shared ref store (HEAD, packed-refs, every loose ref).
    /// <see cref="GitLockSurface.SharedRefs"/> keeps only packed-refs and loose refs.
    /// </summary>
    internal static IEnumerable<string> LockFiles(GitLockScope scope, GitLockSurface surface = GitLockSurface.All)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fixedCandidates = surface == GitLockSurface.SharedRefs
            ? new[] { Path.Combine(scope.CommonDirectory, "packed-refs.lock") }
            : new[]
            {
                Path.Combine(scope.GitDirectory, "index.lock"),
                Path.Combine(scope.GitDirectory, "HEAD.lock"),
                Path.Combine(scope.CommonDirectory, "packed-refs.lock"),
            };
        foreach (var candidate in fixedCandidates)
        {
            if (File.Exists(candidate) && seen.Add(candidate)) yield return candidate;
        }

        var refRoots = surface == GitLockSurface.SharedRefs
            ? new[] { scope.CommonDirectory }
            : new[] { scope.CommonDirectory, scope.GitDirectory };
        foreach (var refsRoot in refRoots
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select(dir => Path.Combine(dir, "refs"))
                     .Where(Directory.Exists))
        {
            IEnumerable<string> refLocks;
            try
            {
                refLocks = Directory.EnumerateFiles(refsRoot, "*.lock", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "GitStaleLockGuard: ref lock enumeration is best-effort");
                continue;
            }
            foreach (var refLock in refLocks)
            {
                if (seen.Add(refLock)) yield return refLock;
            }
        }
    }
}
