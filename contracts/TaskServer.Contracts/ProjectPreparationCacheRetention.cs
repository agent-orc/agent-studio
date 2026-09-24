using System.Globalization;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One published, immutable preparation cache entry as the retention sweep sees
/// it. <paramref name="LastUsedUtc"/> is the entry directory's write time, which
/// a cache hit touches, so it is a genuine last-use stamp and not a creation
/// date.
/// </summary>
public sealed record PreparationCacheEntry(
    string Path,
    string Block,
    string Key,
    DateTime LastUsedUtc,
    long Bytes);

/// <summary>
/// Bounds for one project's preparation cache (AGT-2858). Before this, published
/// entries had no age and no size policy at all: the M1 pilot cache reached
/// 36 GB in the host's shared <c>/tmp</c>, and the only way an operator could
/// bound it was <c>rm -rf</c> plus a hand-made backup copy.
/// </summary>
/// <param name="MaximumAge">
/// How long an entry survives without being used. A repository whose lockfiles
/// have not changed keeps hitting the same key, and each hit resets the clock,
/// so this expires abandoned branches and retired toolchains, not live ones.
/// </param>
/// <param name="MaximumBytes">
/// Upper bound on the published entries of one project. Once exceeded, the
/// least recently used entries are dropped until the cache fits again.
/// </param>
public sealed record PreparationCacheRetentionPolicy(TimeSpan MaximumAge, long MaximumBytes)
{
    /// <summary>Operator override, in days, for <see cref="MaximumAge"/>.</summary>
    public const string MaximumAgeVariable = "AGENT_STUDIO_PREPARE_CACHE_MAX_AGE_DAYS";

    /// <summary>Operator override, in gibibytes, for <see cref="MaximumBytes"/>.</summary>
    public const string MaximumSizeVariable = "AGENT_STUDIO_PREPARE_CACHE_MAX_GIB";

    private const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary>
    /// 30 days and 20 GiB per project. The age is long enough that a fortnight of
    /// holiday does not cost a cold restore; the size is above the ~10 GB a full
    /// dotnet plus node plus Playwright cache reaches for this repository and far
    /// below the 36 GB the unbounded cache had grown to.
    /// </summary>
    public static readonly PreparationCacheRetentionPolicy Default =
        new(TimeSpan.FromDays(30), 20 * Gibibyte);

    /// <summary>
    /// The default, with both bounds optionally raised or lowered by the host.
    /// An unparsable or non-positive value is ignored rather than silently
    /// disabling a bound - a cache with no ceiling is what this policy exists to
    /// prevent.
    /// </summary>
    public static PreparationCacheRetentionPolicy FromEnvironment(
        Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var age = ReadPositiveDouble(read(MaximumAgeVariable)) is { } days
            ? TimeSpan.FromDays(days)
            : Default.MaximumAge;
        var size = ReadPositiveDouble(read(MaximumSizeVariable)) is { } gib
            ? (long)(gib * Gibibyte)
            : Default.MaximumBytes;
        return new PreparationCacheRetentionPolicy(age, size);
    }

    private static double? ReadPositiveDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
           && parsed > 0
            ? parsed
            : null;
}

/// <summary>
/// Decides which published preparation cache entries to drop. Pure and total:
/// the caller supplies the inventory, the clock and the entries the current run
/// is standing on, and gets back the eviction list. All filesystem work lives in
/// <see cref="ProjectPreparationCacheSweep"/>.
/// </summary>
public static class ProjectPreparationCachePolicy
{
    /// <summary>
    /// Entries to remove, in the order they should be removed.
    ///
    /// Three rules, applied in this order:
    /// 1. An entry in <paramref name="retained"/> is never evicted. It is what
    ///    the preparation that triggered this sweep just restored from or
    ///    published, so evicting it would delete the cache of the live run.
    /// 2. Anything unused for longer than the policy's age is evicted.
    /// 3. If the survivors still exceed the size bound, the least recently used
    ///    go until they fit. Ties break on path so the decision is deterministic.
    /// </summary>
    public static IReadOnlyList<PreparationCacheEntry> Evictions(
        IReadOnlyList<PreparationCacheEntry> entries,
        PreparationCacheRetentionPolicy policy,
        DateTime utcNow,
        IReadOnlyCollection<string>? retained = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        var protectedPaths = new HashSet<string>(
            retained ?? [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var candidates = entries
            .Where(entry => !protectedPaths.Contains(entry.Path))
            .OrderBy(entry => entry.LastUsedUtc)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        var evictions = new List<PreparationCacheEntry>();
        var expiredBefore = utcNow - policy.MaximumAge;
        foreach (var entry in candidates.Where(entry => entry.LastUsedUtc < expiredBefore))
            evictions.Add(entry);

        var expired = evictions.ToHashSet();
        // The protected entries still occupy the cache, so they count towards the
        // size bound even though they can never be the ones evicted for it.
        var total = entries.Where(entry => !expired.Contains(entry)).Sum(entry => entry.Bytes);
        foreach (var entry in candidates)
        {
            if (total <= policy.MaximumBytes) break;
            if (expired.Contains(entry)) continue;
            evictions.Add(entry);
            total -= entry.Bytes;
        }

        return evictions;
    }
}

/// <summary>
/// Reads a project cache root, applies <see cref="ProjectPreparationCachePolicy"/>
/// and removes what it names. Bounded and best-effort: a concurrent preparation
/// may be publishing or reading an entry, and losing that race must never fail
/// the run that happened to trigger the sweep.
/// </summary>
public static class ProjectPreparationCacheSweep
{
    /// <summary>Directory holding the published, immutable entries.</summary>
    public const string EntriesDirectoryName = "entries";

    /// <summary>Directory holding the per-run working copies of those entries.</summary>
    public const string RunsDirectoryName = ".runs";

    /// <summary>
    /// Atomic rename target for incomplete and legacy-empty entries. Renaming
    /// first takes a broken entry out of lookup circulation; the normal sweep
    /// removes the quarantined tree after the detecting run has moved on.
    /// </summary>
    public const string QuarantineDirectoryName = ".quarantine";

    /// <summary>
    /// Sweeps <paramref name="productCacheRoot"/> and returns what was removed.
    /// </summary>
    public static PreparationCacheSweepResult Run(
        string productCacheRoot,
        PreparationCacheRetentionPolicy policy,
        DateTime utcNow,
        IReadOnlyCollection<string>? retained = null,
        Action<string>? log = null,
        TimeSpan? runRootRetention = null)
    {
        RemoveEvictionLeftovers(productCacheRoot);
        RemoveQuarantine(productCacheRoot);
        // Run roots dominate a real cache: each one is a full working copy of
        // every block this repository uses, and a killed gate or coding run never
        // gets to release its own. On this repository's runner cache 1.3 GB of
        // published entries sat under 16 GB of them (AGT-2858).
        var runRoots = PruneRunRoots(productCacheRoot, runRootRetention ?? DefaultRunRootRetention, utcNow, log);
        var entries = Inventory(productCacheRoot);
        if (entries.Count == 0)
            return runRoots == PreparationCacheSweepResult.Empty
                ? PreparationCacheSweepResult.Empty
                : runRoots;

        var evictions = ProjectPreparationCachePolicy.Evictions(entries, policy, utcNow, retained);
        var removed = 0;
        long bytes = 0;
        foreach (var entry in evictions)
        {
            if (!TryRemove(entry.Path)) continue;
            removed++;
            bytes += entry.Bytes;
            log?.Invoke(
                $"project-prepare cache evicted block={entry.Block} key={entry.Key} "
                + $"ageDays={(utcNow - entry.LastUsedUtc).TotalDays:F1} bytes={entry.Bytes}");
        }

        var result = new PreparationCacheSweepResult(
            entries.Count + runRoots.Inspected,
            removed + runRoots.Removed,
            bytes + runRoots.FreedBytes,
            entries.Sum(item => item.Bytes) - bytes);
        if (result.Removed > 0)
            log?.Invoke(
                $"project-prepare cache swept inspected={result.Inspected} removed={result.Removed} "
                + $"freedBytes={result.FreedBytes} retainedBytes={result.RetainedBytes}");
        return result;
    }

    /// <summary>
    /// Every published entry under the cache root, with the size recorded in its
    /// manifest. Entries published before the manifest carried a size are
    /// measured once, on the spot.
    /// </summary>
    public static IReadOnlyList<PreparationCacheEntry> Inventory(string productCacheRoot)
    {
        var root = Path.Combine(productCacheRoot, EntriesDirectoryName);
        if (!Directory.Exists(root)) return [];

        var entries = new List<PreparationCacheEntry>();
        try
        {
            foreach (var blockDirectory in Directory.EnumerateDirectories(root))
            {
                var block = Path.GetFileName(blockDirectory);
                foreach (var entryDirectory in Directory.EnumerateDirectories(blockDirectory))
                {
                    var name = Path.GetFileName(entryDirectory);
                    // A staging directory belongs to a publication in flight; an
                    // evicting one was already taken out of circulation.
                    if (name.Contains(".staging-", StringComparison.Ordinal)) continue;
                    if (name.Contains(EvictionMarker, StringComparison.Ordinal)) continue;
                    var manifest = Path.Combine(entryDirectory, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    entries.Add(new PreparationCacheEntry(
                        entryDirectory,
                        block,
                        name,
                        Directory.GetLastWriteTimeUtc(entryDirectory),
                        // Same subject as the published sizeBytes: the content
                        // tree, not the few bytes of manifest next to it.
                        ReadSize(manifest) ?? Measure(Path.Combine(entryDirectory, "content"))));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent publication moved a directory mid-enumeration. What
            // was collected so far is still a valid basis for a bounded sweep.
        }
        return entries;
    }

    /// <summary>
    /// Records that an entry was used now, which is what turns the directory's
    /// write time into a last-use stamp for the LRU rule. The entry's content
    /// stays write-once; only the timestamp moves.
    /// </summary>
    public static void Touch(string entryPath, DateTime utcNow)
    {
        try
        {
            if (Directory.Exists(entryPath)) Directory.SetLastWriteTimeUtc(entryPath, utcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only or concurrently moved entry keeps its old stamp and is
            // simply a stronger eviction candidate next time.
        }
    }

    /// <summary>Total bytes of a directory tree; unreadable parts count as zero.</summary>
    public static long Measure(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long? ReadSize(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("sizeBytes", out var size)
                   && size.TryGetInt64(out var bytes)
                ? bytes
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How long an unreleased per-run folder is assumed to still belong to a
    /// live gate or coding run. Anything older is the residue of a process that
    /// died before it could call <c>ReleaseRunRoot</c>.
    /// </summary>
    public static readonly TimeSpan DefaultRunRootRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Drops per-run working copies left behind by a process that died before it
    /// released its own. Bounded by age, so a folder a live run is still reading
    /// from is never touched.
    /// </summary>
    public static PreparationCacheSweepResult PruneRunRoots(
        string productCacheRoot,
        TimeSpan retention,
        DateTime utcNow,
        Action<string>? log = null)
    {
        var runs = Path.Combine(productCacheRoot, RunsDirectoryName);
        if (!Directory.Exists(runs)) return PreparationCacheSweepResult.Empty;

        var deadline = utcNow - retention;
        var inspected = 0;
        var removed = 0;
        long freed = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(runs))
            {
                inspected++;
                var info = new DirectoryInfo(candidate);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.LastWriteTimeUtc > deadline) continue;
                var bytes = Measure(candidate);
                if (!TryRemove(candidate)) continue;
                removed++;
                freed += bytes;
                log?.Invoke(
                    $"project-prepare cache run-root released path={info.Name} "
                    + $"ageHours={(utcNow - info.LastWriteTimeUtc).TotalHours:F1} bytes={bytes}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent preparation is creating or releasing its own folder.
        }
        return new PreparationCacheSweepResult(inspected, removed, freed, 0);
    }

    /// <summary>Names a directory a previous sweep renamed but could not delete.</summary>
    private const string EvictionMarker = ".evicting-";

    /// <summary>
    /// Deletes what an interrupted sweep renamed aside. Those directories are
    /// already invisible to the cache, so nothing depends on them.
    /// </summary>
    private static void RemoveEvictionLeftovers(string productCacheRoot)
    {
        var root = Path.Combine(productCacheRoot, EntriesDirectoryName);
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var blockDirectory in Directory.EnumerateDirectories(root))
            foreach (var candidate in Directory.EnumerateDirectories(blockDirectory, "*" + EvictionMarker + "*"))
            {
                try { Directory.Delete(candidate, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another sweep is working the same root; one of them wins.
        }
    }

    /// <summary>
    /// Removes cache entries that lookup atomically renamed out of circulation.
    /// A current reader cannot be using one: entry lookup/copy and quarantine
    /// share the same per-entry lock, and readers execute from their run-root
    /// copy after releasing that lock.
    /// </summary>
    private static void RemoveQuarantine(string productCacheRoot)
    {
        var root = Path.Combine(productCacheRoot, QuarantineDirectoryName);
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var blockDirectory in Directory.EnumerateDirectories(root))
            {
                foreach (var candidate in Directory.EnumerateDirectories(blockDirectory))
                {
                    try { Directory.Delete(candidate, recursive: true); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(blockDirectory).Any())
                        Directory.Delete(blockDirectory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent lookup may be adding another quarantined entry.
        }
    }

    private static bool TryRemove(string path)
    {
        // Moved aside first so a concurrent reader can never see a half-deleted
        // entry: the cache treats a directory without manifest.json as incomplete
        // and would fail the preparation that found it.
        var condemned = path + EvictionMarker + Guid.NewGuid().ToString("N");
        try
        {
            Directory.Move(path, condemned);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            Directory.Delete(condemned, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The rename already took the entry out of circulation; the leftover
            // is picked up by the next sweep.
            return true;
        }
    }
}

/// <summary>What one cache sweep inspected and removed.</summary>
public sealed record PreparationCacheSweepResult(
    int Inspected,
    int Removed,
    long FreedBytes,
    long RetainedBytes)
{
    public static readonly PreparationCacheSweepResult Empty = new(0, 0, 0, 0);
}
