using System.Globalization;
using AgentStudio.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>What one prune of one gate host cache removed.</summary>
internal sealed record GateHostCachePrune(string Target, string Path, int Entries, long FreedBytes);

/// <summary>
/// The gate host's own cache retention, run by the remote gate worker
/// (AGT-3866). On 16 November 2026 the root file system of kori-gate was 95 %
/// full, mostly a 51 GB Node compile cache in /tmp and old gate workspaces:
/// Angular CLI, Vite, ESLint and TypeScript call
/// <c>module.enableCompileCache()</c>, which writes below
/// <c>NODE_COMPILE_CACHE</c> or, when it is unset, <c>&lt;tmp&gt;/node-compile-cache</c>.
/// Node keys each entry by the module's file name and never evicts one, and
/// every gate run loads node_modules from a fresh workspace path, so each run
/// added entries that no later run could hit.
/// </summary>
internal static class GateHostCachePruner
{
    /// <summary>Default age after which an untouched entry is removed.</summary>
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Floor above the 12-hour gate run budget, so a prune never removes the
    /// workspace of a gate that is still running.
    /// </summary>
    internal static readonly TimeSpan MinimumMaxAge = TimeSpan.FromHours(13);

    /// <summary>Workers on one host prune at most this often.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    internal const string NodeCompileCacheTarget = "node-compile-cache";
    internal const string GateWorkspacesTarget = "gate-workspaces";

    /// <summary>The directory Node writes its compile cache to for a gate command.</summary>
    internal static string NodeCompileCacheRoot(string? configured, string tempRoot)
        => string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(tempRoot, "node-compile-cache")
            : Path.GetFullPath(configured);

    /// <summary>A configured age in hours, clamped to <see cref="MinimumMaxAge"/>; zero or less is the default.</summary>
    internal static TimeSpan MaxAge(int configuredHours)
        => configuredHours <= 0 ? DefaultMaxAge
            : TimeSpan.FromHours(Math.Max(configuredHours, MinimumMaxAge.TotalHours));

    /// <summary>
    /// Prunes both targets unless another worker on this host pruned within
    /// <see cref="Interval"/> or is pruning now. Writes one log line per
    /// pruned target naming its freed bytes.
    /// </summary>
    internal static IReadOnlyList<GateHostCachePrune> PruneIfDue(string nodeCompileCache,
        string gateWorkspaces, string stampPath, TimeSpan maxAge, DateTime nowUtc,
        Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
        FileStream held;
        try
        {
            held = new FileStream(stampPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return []; }
        using (held)
        {
            if (File.Exists(stampPath) && nowUtc - File.GetLastWriteTimeUtc(stampPath) < Interval)
                return [];
            var pruned = new[]
            {
                PruneFiles(NodeCompileCacheTarget, nodeCompileCache, nowUtc - maxAge),
                PruneWorkspaces(gateWorkspaces, nowUtc - maxAge),
            };
            foreach (var prune in pruned)
                log(string.Create(CultureInfo.InvariantCulture,
                    $"remote_gate_cache_pruned target={prune.Target} path={prune.Path} "
                    + $"entries={prune.Entries} freed_bytes={prune.FreedBytes} "
                    + $"max_age_hours={maxAge.TotalHours:0}"));
            File.WriteAllText(stampPath, nowUtc.ToString("O", CultureInfo.InvariantCulture));
            File.SetLastWriteTimeUtc(stampPath, nowUtc);
            return pruned;
        }
    }

    /// <summary>
    /// Removes compile cache files older than <paramref name="cutoffUtc"/>.
    /// Node treats a missing entry as a miss, so a concurrent reader only
    /// recompiles that module.
    /// </summary>
    internal static GateHostCachePrune PruneFiles(string target, string root, DateTime cutoffUtc)
    {
        var entries = 0;
        long freed = 0;
        if (!Directory.Exists(root)) return new(target, root, 0, 0);
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoffUtc) continue;
                var length = info.Length;
                info.Delete();
                entries++;
                freed += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(exception, "Gate host cache prune skipped an entry it could not remove");
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint })
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(exception, "Gate host cache prune skipped an entry it could not remove");
            }
        }
        return new(target, root, entries, freed);
    }

    /// <summary>
    /// Removes gate workspaces (one directory per gate run below the review
    /// workspace root) whose newest top-level change is older than
    /// <paramref name="cutoffUtc"/>. The shared dependency cache is not a
    /// workspace and keeps its own retention.
    /// </summary>
    internal static GateHostCachePrune PruneWorkspaces(string root, DateTime cutoffUtc)
    {
        var entries = 0;
        long freed = 0;
        if (!Directory.Exists(root)) return new(GateWorkspacesTarget, root, 0, 0);
        foreach (var workspace in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(workspace) == BuildTestGateRunner.DependencyCacheDirectoryName) continue;
            try
            {
                var info = new DirectoryInfo(workspace);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.LastWriteTimeUtc >= cutoffUtc) continue;
                var bytes = Bytes(workspace);
                Directory.Delete(workspace, recursive: true);
                entries++;
                freed += bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(exception, "Gate host cache prune skipped an entry it could not remove");
            }
        }
        return new(GateWorkspacesTarget, root, entries, freed);
    }

    private static long Bytes(string root)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { bytes += new FileInfo(file).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(exception, "Gate host cache prune skipped an entry it could not remove");
            }
        }
        return bytes;
    }
}
