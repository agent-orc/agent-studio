namespace AgentRunner;

/// <summary>
/// Removes abandoned remote-review workspaces by age and size budget.
/// The current root's reusable baseline, verify and dependency caches and every attempt
/// the daemon still owns are outside the deletion set. An attempt's short temp
/// root below <c>/tmp</c> goes with its attempt, and one whose attempt is
/// already gone is removed by its owner file (AGT-3429).
///
/// The host-shared NuGet package folder inside the dependency cache is the one
/// reusable cache that grows with every new package version. While no attempt
/// is active, package versions extracted more than
/// <see cref="MaximumPackageAge"/> ago are removed; a later restore extracts
/// them again. Every non-skippable preparation that restores therefore heals a
/// cached <c>obj</c> that still names a removed version.
/// </summary>
internal static class ReviewWorkspaceRetention
{
    internal static readonly TimeSpan MaximumOrphanAge = TimeSpan.FromHours(72);
    internal const long MaximumRetainedBytes = 20L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan MaximumPackageAge = TimeSpan.FromDays(30);
    private const string NuGetPackageMarkerFileName = ".nupkg.metadata";
    private const string NuGetLockDirectoryName = "lock";
    private const string BaselineCacheDirectoryName = ".baseline-cache";
    private const string DependencyCacheDirectoryName = ".dependency-cache";
    private const string VerifyCacheDirectoryName = ".verify-cache";
    private const string AttemptDirectoryPrefix = "review-";

    internal static ReviewWorkspaceRetentionResult Sweep(
        string reviewWorkDir,
        IEnumerable<string> activeResourceNamespaces,
        DateTime utcNow,
        Action<string> log,
        string? tempBase = null,
        long maximumRetainedBytes = MaximumRetainedBytes)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reviewWorkDir));
        tempBase ??= ReviewAttemptTemp.DefaultBase();

        var active = activeResourceNamespaces
            .Select(RemoteReviewWorkspace.SafeSegment)
            .ToHashSet(StringComparer.Ordinal);
        var inspected = 0;
        var removed = 0;
        var activeSkipped = 0;
        var youngSkipped = 0;
        var unsafeSkipped = 0;

        // A previous installation may have moved settled work to this sibling.
        // Never traverse a linked root, or unrecognized folders inside either root.
        var roots = new[] { root, root + "-stale" }
            .Where(path => Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
        var staleRoot = root + "-stale";
        var staleHasActiveAttempt = roots.Contains(staleRoot) && SafeDirectories(staleRoot)
            .Any(path => active.Contains(Path.GetFileName(path)));
        var candidates = new List<(DirectoryInfo Info, long Bytes, long TempBytes)>();
        foreach (var directory in roots.SelectMany(path => Directory.EnumerateDirectories(path)))
        {
            var info = new DirectoryInfo(directory);
            var reusableCache = info.Name is BaselineCacheDirectoryName or DependencyCacheDirectoryName or VerifyCacheDirectoryName;
            var stale = string.Equals(info.Parent?.FullName, staleRoot, StringComparison.Ordinal);
            if (reusableCache && (!stale || staleHasActiveAttempt)) continue;
            if (!info.Name.StartsWith(AttemptDirectoryPrefix, StringComparison.Ordinal) && !reusableCache)
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
            try
            {
                var temp = ReviewAttemptTemp.Root(tempBase, info.FullName);
                var tempBytes = Directory.Exists(temp)
                    && (File.GetAttributes(temp) & FileAttributes.ReparsePoint) == 0 ? DirectoryBytes(temp) : 0;
                candidates.Add((info, DirectoryBytes(info.FullName) + tempBytes, tempBytes));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unsafeSkipped++;
                log($"review retention measurement failed path={info.FullName}: {exception.Message}");
            }
        }

        var retainedBytes = candidates.Sum(candidate => candidate.Bytes);
        foreach (var (info, bytes, tempBytes) in candidates.OrderBy(candidate => candidate.Info.LastWriteTimeUtc))
        {
            if (utcNow - info.LastWriteTimeUtc <= MaximumOrphanAge
                && retainedBytes <= Math.Max(0, maximumRetainedBytes))
            {
                youngSkipped++;
                continue;
            }
            try
            {
                var age = utcNow - info.LastWriteTimeUtc;
                var tempGone = ReviewAttemptTemp.Remove(ReviewAttemptTemp.Root(tempBase, info.FullName));
                ResilientDirectory.Delete(info.FullName);
                if (!Directory.Exists(info.FullName))
                {
                    removed++;
                    retainedBytes -= bytes - (tempGone ? 0 : tempBytes);
                    log($"removed retained review workspace path={info.FullName} ageHours={age.TotalHours:F1}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"expired review workspace cleanup failed path={info.FullName}: {exception.Message}");
            }
        }

        var tempRemoved = new[] { root, root + "-stale" }
            .Sum(path => ReviewAttemptTemp.SweepOrphans(tempBase, path, active, log));
        // A package version is only safe to remove while no attempt can be
        // resolving or compiling against it.
        var packagesRemoved = active.Count == 0
            ? PruneNuGetPackages(Path.Combine(root, DependencyCacheDirectoryName), utcNow, log)
            : 0;

        var result = new ReviewWorkspaceRetentionResult(
            inspected,
            removed,
            activeSkipped,
            youngSkipped,
            unsafeSkipped,
            tempRemoved,
            packagesRemoved);
        log($"review workspace retention inspected={result.Inspected} removed={result.Removed} activeSkipped={result.ActiveSkipped} youngSkipped={result.YoungSkipped} unsafeSkipped={result.UnsafeSkipped} tempRemoved={result.TempRemoved} nugetPackagesRemoved={result.PackagesRemoved} reusableCaches=preserved cutoffHours={MaximumOrphanAge.TotalHours:F0} retainedBytes={retainedBytes} budgetBytes={maximumRetainedBytes}");
        return result;
    }

    /// <summary>
    /// Removes package versions of the shared NuGet folder whose extraction
    /// marker is older than <see cref="MaximumPackageAge"/>, and stale entries of
    /// the shared NuGet scratch folder. Returns the number of removed versions.
    /// </summary>
    internal static int PruneNuGetPackages(string dependencyCacheRoot, DateTime utcNow, Action<string> log)
    {
        var removed = 0;
        var packages = Path.Combine(dependencyCacheRoot, RemoteReviewWorkspace.NuGetPackagesDirectoryName);
        foreach (var package in SafeDirectories(packages))
        {
            foreach (var version in SafeDirectories(package))
            {
                var marker = Path.Combine(version, NuGetPackageMarkerFileName);
                var extractedAt = File.Exists(marker)
                    ? File.GetLastWriteTimeUtc(marker)
                    : Directory.GetLastWriteTimeUtc(version);
                if (utcNow - extractedAt <= MaximumPackageAge) continue;
                if (!ResilientDirectory.TryDelete(version)) continue;
                removed++;
            }
            if (!Directory.EnumerateFileSystemEntries(package).Any())
                ResilientDirectory.TryDelete(package);
        }

        var scratch = Path.Combine(dependencyCacheRoot, RemoteReviewWorkspace.NuGetScratchDirectoryName);
        var locks = Path.Combine(scratch, NuGetLockDirectoryName);
        foreach (var entry in SafeEntries(scratch).Concat(SafeEntries(locks)))
        {
            if (string.Equals(entry, locks, StringComparison.Ordinal)) continue;
            if (utcNow - File.GetLastWriteTimeUtc(entry) <= MaximumPackageAge) continue;
            if (Directory.Exists(entry)) ResilientDirectory.TryDelete(entry);
            else
            {
                try { File.Delete(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        if (removed > 0)
            log($"pruned shared review nuget packages removed={removed} cutoffDays={MaximumPackageAge.TotalDays:F0} path={packages}");
        return removed;
    }

    private static long DirectoryBytes(string path)
    {
        long bytes = 0;
        foreach (var entry in Directory.GetFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            bytes += (attributes & FileAttributes.Directory) != 0
                ? DirectoryBytes(entry) : new FileInfo(entry).Length;
        }
        return bytes;
    }

    private static IEnumerable<string> SafeDirectories(string path)
        => SafeEntries(path).Where(entry =>
            Directory.Exists(entry)
            && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0);

    private static string[] SafeEntries(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetFileSystemEntries(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal sealed record ReviewWorkspaceRetentionResult(
    int Inspected,
    int Removed,
    int ActiveSkipped,
    int YoungSkipped,
    int UnsafeSkipped,
    int TempRemoved = 0,
    int PackagesRemoved = 0);
