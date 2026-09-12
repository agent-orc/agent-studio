namespace AgentStudio.Runner;

/// <summary>
/// Periodic filesystem counterpart to CLI orphan reaping. A partial Git
/// teardown can prune the worktree registration and remove the checkout's
/// <c>.git</c> link while a child server keeps the directory behind. Such a
/// directory is no longer visible to <c>git worktree list</c>, so it must be
/// recovered from the deterministic temporary root itself.
/// </summary>
internal sealed class WorktreeOrphanDirectorySweeper
{
    private readonly ILogger _logger;
    private readonly IWorktreeDirectoryCleanup _cleanup;
    private readonly HashSet<string> _warned = new(PathComparer);

    internal WorktreeOrphanDirectorySweeper(
        ILogger logger,
        IWorktreeDirectoryCleanup? cleanup = null)
    {
        _logger = logger;
        _cleanup = cleanup ?? new WorktreeDirectoryCleanup();
    }

    internal void Sweep(string root, DateTime utcNow, TimeSpan minimumAge)
    {
        if (!Directory.Exists(root)) return;

        foreach (var projectRoot in SafeDirectories(root))
        foreach (var worktreePath in SafeDirectories(projectRoot))
        {
            if (HasGitLink(worktreePath) || !IsOldEnough(worktreePath, utcNow, minimumAge))
                continue;

            var result = _cleanup.Clear(worktreePath);
            if (_warned.Add(worktreePath))
            {
                _logger.LogWarning(
                    "worktree-orphan-directory path={Path} action={Action} stalePath={StalePath} error={Error}",
                    worktreePath,
                    result.CanonicalPathCleared
                        ? result.StalePath is null ? "reaped" : "renamed"
                        : "blocked",
                    result.StalePath,
                    result.Error);
            }

            if (!result.CanonicalPathCleared) continue;
            _warned.Remove(worktreePath);
        }
    }

    private static bool HasGitLink(string path)
    {
        var git = Path.Combine(path, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static bool IsOldEnough(string path, DateTime utcNow, TimeSpan minimumAge)
    {
        try { return utcNow - Directory.GetLastWriteTimeUtc(path) >= minimumAge; }
        catch { return false; }
    }

    private static IReadOnlyList<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch { return []; }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
