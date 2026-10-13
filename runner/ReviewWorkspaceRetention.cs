namespace AgentRunner;

/// <summary>
/// Removes abandoned remote-review attempt workspaces after a fixed safety
/// window. The reusable baseline, verify and dependency caches and every attempt
/// the daemon still owns are outside the deletion set.
/// </summary>
internal static class ReviewWorkspaceRetention
{
    internal static readonly TimeSpan MaximumOrphanAge = TimeSpan.FromHours(72);
    private const string BaselineCacheDirectoryName = ".baseline-cache";
    private const string DependencyCacheDirectoryName = ".dependency-cache";
    private const string VerifyCacheDirectoryName = ".verify-cache";
    private const string AttemptDirectoryPrefix = "review-";

    internal static ReviewWorkspaceRetentionResult Sweep(
        string reviewWorkDir,
        IEnumerable<string> activeResourceNamespaces,
        DateTime utcNow,
        Action<string> log)
    {
        var root = Path.GetFullPath(reviewWorkDir);
        if (!Directory.Exists(root))
            return new ReviewWorkspaceRetentionResult(0, 0, 0, 0, 0);

        var active = activeResourceNamespaces
            .Select(RemoteReviewWorkspace.SafeSegment)
            .ToHashSet(StringComparer.Ordinal);
        var inspected = 0;
        var removed = 0;
        var activeSkipped = 0;
        var youngSkipped = 0;
        var unsafeSkipped = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            if (info.Name is BaselineCacheDirectoryName or DependencyCacheDirectoryName or VerifyCacheDirectoryName)
                continue;
            if (!info.Name.StartsWith(AttemptDirectoryPrefix, StringComparison.Ordinal))
            {
                unsafeSkipped++;
                continue;
            }

            inspected++;
            if (active.Contains(info.Name))
            {
                activeSkipped++;
                continue;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                unsafeSkipped++;
                continue;
            }
            if (utcNow - info.LastWriteTimeUtc <= MaximumOrphanAge)
            {
                youngSkipped++;
                continue;
            }

            try
            {
                var age = utcNow - info.LastWriteTimeUtc;
                ResilientDirectory.Delete(info.FullName);
                if (!Directory.Exists(info.FullName))
                {
                    removed++;
                    log($"removed expired review workspace path={info.FullName} ageHours={age.TotalHours:F1}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"expired review workspace cleanup failed path={info.FullName}: {exception.Message}");
            }
        }

        var result = new ReviewWorkspaceRetentionResult(
            inspected,
            removed,
            activeSkipped,
            youngSkipped,
            unsafeSkipped);
        log($"review workspace retention inspected={result.Inspected} removed={result.Removed} activeSkipped={result.ActiveSkipped} youngSkipped={result.YoungSkipped} unsafeSkipped={result.UnsafeSkipped} reusableCaches=preserved cutoffHours={MaximumOrphanAge.TotalHours:F0}");
        return result;
    }
}

internal sealed record ReviewWorkspaceRetentionResult(
    int Inspected,
    int Removed,
    int ActiveSkipped,
    int YoungSkipped,
    int UnsafeSkipped);
