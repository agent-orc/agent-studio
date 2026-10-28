using System.Collections.Concurrent;
using AgentStudio.Bus;
using AgentStudio.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Git;

/// <summary>Outcome of one <see cref="GitRefLockJanitor"/> pass.</summary>
public sealed record GitRefLockJanitorResult(
    IReadOnlyList<GitLockFinding> Cleared,
    IReadOnlyList<GitLockFinding> Remaining,
    IReadOnlyList<string> PrunedWorktrees,
    bool Reported)
{
    public static readonly GitRefLockJanitorResult Clean = new([], [], [], false);
}

/// <summary>
/// Removes ref locks that a killed git fetch left behind (AGT-3337). On
/// 28.10.2026 a <c>fetch --prune</c> over 6,604 refs hit the 30-second git
/// timeout around the clock; each kill left hundreds of <c>refs/**/*.lock</c>
/// files, and an empty <c>refs/remotes/origin/develop.lock</c> blocked every
/// update of origin/develop for 90 minutes.
///
/// <para>Runs before a fetch, after a fetch timeout, and after a fetch that
/// failed on a ref lock. It only touches the shared ref store
/// (<c>packed-refs.lock</c> and <c>refs/**/*.lock</c>) through
/// <see cref="GitStaleLockGuard"/> with a shorter threshold (default 5 minutes)
/// and never waits. A lock is removed only when it is older than the threshold
/// and no git process (a child of this server or any OS git process tied to the
/// repository) runs there. In the same pass it prunes
/// <c>.git/worktrees/&lt;name&gt;</c> admin entries whose working tree no longer
/// exists, under the same rule.</para>
///
/// <para>Every removal becomes one <c>git-stale-lock-cleared</c> operations
/// event; locks it must keep after a timeout or a ref-lock failure become a
/// rate-limited <c>git-ref-lock-blocked</c> event. No git process is spawned:
/// a git spawn is what the lock blocks.</para>
/// </summary>
public sealed class GitRefLockJanitor
{
    public const string TriggerBeforeFetch = "before-fetch";
    public const string TriggerAfterTimeout = "after-timeout";
    public const string TriggerRefLockFailure = "cannot-lock-ref";

    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultReportWindow = TimeSpan.FromMinutes(15);

    private readonly ILogger<GitRefLockJanitor> _logger;
    private readonly AgentMessageBusBridge? _bus;
    private readonly TaskScannerService? _scanner;
    private readonly IGitLockOwnerProbe _probe;
    private readonly TimeProvider _time;
    private readonly GitStaleLockGuard _guard;
    private readonly TimeSpan _reportWindow;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastBlockedReport =
        new(StringComparer.OrdinalIgnoreCase);

    public GitRefLockJanitor(
        IConfiguration? configuration = null,
        ILogger<GitRefLockJanitor>? logger = null,
        AgentMessageBusBridge? bus = null,
        TaskScannerService? scanner = null,
        IGitLockOwnerProbe? probe = null,
        TimeProvider? time = null)
    {
        _logger = logger ?? NullLogger<GitRefLockJanitor>.Instance;
        _bus = bus;
        _scanner = scanner;
        _probe = probe ?? new GitProcessLockOwnerProbe();
        _time = time ?? TimeProvider.System;
        Threshold = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<int?>("GitStaleLocks:RefJanitorThresholdMinutes")
                ?? (int)DefaultThreshold.TotalMinutes,
            1,
            24 * 60));
        _reportWindow = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<int?>("GitStaleLocks:RefLockReportWindowMinutes")
                ?? (int)DefaultReportWindow.TotalMinutes,
            1,
            24 * 60));
        _guard = new GitStaleLockGuard(
            configuration, NullLogger<GitStaleLockGuard>.Instance, _probe, _time, wait: _ => { }, Threshold);
    }

    public TimeSpan Threshold { get; }

    /// <summary>Clears aged ref locks and orphaned worktree entries before a fetch. Never throws.</summary>
    public GitRefLockJanitorResult BeforeFetch(string? repositoryPath)
        => Run(repositoryPath, TriggerBeforeFetch, error: null, reportRemaining: false);

    /// <summary>
    /// Runs after a git fetch failed. A timeout kill or a ref-lock failure
    /// triggers a pass that also reports the locks it had to keep; any other
    /// failure (offline, auth) is not a lock condition and is ignored.
    /// </summary>
    public GitRefLockJanitorResult AfterFetchFailure(string? repositoryPath, string? error)
    {
        if (IsTimeout(error)) return Run(repositoryPath, TriggerAfterTimeout, error, reportRemaining: true);
        if (IsRefLockFailure(error)) return Run(repositoryPath, TriggerRefLockFailure, error, reportRemaining: true);
        return GitRefLockJanitorResult.Clean;
    }

    public static bool IsTimeout(string? error)
        => error?.Contains("timed out after", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsRefLockFailure(string? error)
        => error is not null
           && (error.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase)
               || (error.Contains("Unable to create", StringComparison.OrdinalIgnoreCase)
                   && error.Contains(".lock", StringComparison.OrdinalIgnoreCase)));

    private GitRefLockJanitorResult Run(string? repositoryPath, string trigger, string? error, bool reportRemaining)
    {
        try
        {
            var scope = GitStaleLockGuard.ResolveScope(repositoryPath);
            if (scope is null) return GitRefLockJanitorResult.Clean;

            var locks = _guard.Sweep(scope.WorkTree, GitLockSurface.SharedRefs);
            var pruned = PruneOrphanedWorktrees(scope);
            var project = ProjectFor(scope.WorkTree);
            if (locks.Cleared.Count > 0 || pruned.Count > 0)
            {
                _logger.LogWarning(
                    "git-ref-lock-janitor repo={Repo} trigger={Trigger} cleared={Cleared} prunedWorktrees={Pruned} kept={Kept}",
                    scope.WorkTree, trigger, locks.Cleared.Count, pruned.Count, locks.Remaining.Count);
                Emit(() => _bus?.EmitGitLockInterventionAsync(
                    project, scope.WorkTree, trigger,
                    locks.Cleared.Select(finding => finding.Path).ToArray(), pruned, Threshold));
            }

            var reported = false;
            if (reportRemaining && (locks.Remaining.Count > 0 || trigger == TriggerRefLockFailure) && ShouldReport(scope.WorkTree))
            {
                reported = true;
                _logger.LogWarning(
                    "git-ref-lock-blocked repo={Repo} trigger={Trigger} locks={Locks} error={Error}",
                    scope.WorkTree, trigger, locks.Remaining.Count, error);
                Emit(() => _bus?.EmitGitRefLockBlockedAsync(
                    project, scope.WorkTree, trigger,
                    locks.Remaining.Select(finding =>
                        $"{finding.Path} (age {GitStaleLockPolicy.FormatAge(finding.Age)}, {GitStaleLockPolicy.Reason(finding.Verdict)})")
                        .ToArray(),
                    error));
            }
            return new GitRefLockJanitorResult(locks.Cleared, locks.Remaining, pruned, reported);
        }
        catch (Exception ex)
        {
            // Recovery aid only: a janitor failure must never fail the fetch.
            _logger.LogWarning(ex, "git-ref-lock-janitor failed repo={Repo} trigger={Trigger}", repositoryPath, trigger);
            return GitRefLockJanitorResult.Clean;
        }
    }

    /// <summary>
    /// Removes <c>&lt;common&gt;/worktrees/&lt;name&gt;</c> entries whose
    /// <c>gitdir</c> names a working tree directory that no longer exists (what
    /// <c>git worktree prune</c> does), but only for unlocked entries older than
    /// the threshold and only while no git process runs in the repository.
    /// Their <c>index.lock</c> files go with them.
    /// </summary>
    private IReadOnlyList<string> PruneOrphanedWorktrees(GitLockScope scope)
    {
        var registry = Path.Combine(scope.CommonDirectory, "worktrees");
        if (!Directory.Exists(registry)) return [];

        var now = _time.GetUtcNow().UtcDateTime;
        var candidates = new List<string>();
        foreach (var entry in Directory.EnumerateDirectories(registry))
        {
            try
            {
                if (File.Exists(Path.Combine(entry, "locked"))) continue;
                if (now - Directory.GetLastWriteTimeUtc(entry) < Threshold) continue;
                var gitdirFile = Path.Combine(entry, "gitdir");
                if (!File.Exists(gitdirFile)) continue;
                var pointer = File.ReadAllText(gitdirFile).Trim();
                if (string.IsNullOrWhiteSpace(pointer)) continue;
                var dotGit = Path.GetFullPath(Path.IsPathRooted(pointer) ? pointer : Path.Combine(entry, pointer));
                var workTree = Path.GetDirectoryName(dotGit);
                if (string.IsNullOrEmpty(workTree) || Directory.Exists(workTree)) continue;
                candidates.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "GitRefLockJanitor: unreadable worktree entry is left in place");
            }
        }
        if (candidates.Count == 0) return [];
        if (_probe.Probe(scope, now) != GitLockOwnership.None) return [];

        var pruned = new List<string>();
        foreach (var entry in candidates)
        {
            try
            {
                Directory.Delete(entry, recursive: true);
                pruned.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "git-worktree-entry-prune-failed repo={Repo} entry={Entry}", scope.WorkTree, entry);
            }
        }
        return pruned;
    }

    private bool ShouldReport(string repository)
    {
        var now = _time.GetUtcNow();
        while (true)
        {
            if (!_lastBlockedReport.TryGetValue(repository, out var last))
            {
                if (_lastBlockedReport.TryAdd(repository, now)) return true;
                continue;
            }
            if (now - last < _reportWindow) return false;
            if (_lastBlockedReport.TryUpdate(repository, now, last)) return true;
        }
    }

    private string? ProjectFor(string workTree)
    {
        if (_scanner is null) return null;
        var normalized = Normalize(workTree);
        return _scanner.GetWatchPaths()
            .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.RepositoryPath)
                && string.Equals(Normalize(entry.RepositoryPath), normalized, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private void Emit(Func<Task?> emit)
    {
        try
        {
            emit()?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git-ref-lock-janitor event emit failed");
        }
    }
}
