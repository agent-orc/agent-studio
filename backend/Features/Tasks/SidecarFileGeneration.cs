using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentStudio.Tasks;

/// <summary>
/// File generations for projection sidecars. Normal reads use remembered file
/// metadata; writes and watcher events update one path. A rotating, bounded
/// probe repairs events missed by the watcher without scanning the board.
/// </summary>
internal static class SidecarFileGeneration
{
    private const int Capacity = 16384;
    internal readonly record struct Stamp(bool Exists, long Length, long WriteTicks, string? Hash);
    internal readonly record struct Observation(Stamp Stamp, long Version);

    private static readonly ConcurrentDictionary<string, Observation> Files = new(FileSystemPathComparer.Instance);
    private static readonly ConcurrentDictionary<string, long> Folders = new(FileSystemPathComparer.Instance);
    private static readonly ConcurrentDictionary<string, byte> Queued = new(FileSystemPathComparer.Instance);
    private static readonly ConcurrentQueue<string> RepairQueue = new();
    private static long _version;

    internal static Observation Observe(string path)
    {
        path = TaskListGitProjectionCache.NormalizePath(path);
        return Files.GetOrAdd(path, key =>
        {
            Enqueue(key);
            Trim();
            return new Observation(Stat(key), Interlocked.Increment(ref _version));
        });
    }

    internal static long FolderVersion(string folder)
        => Folders.TryGetValue(TaskListGitProjectionCache.NormalizePath(folder), out var value) ? value : 0;

    internal static void Changed(string path)
    {
        path = TaskListGitProjectionCache.NormalizePath(path);
        var version = Interlocked.Increment(ref _version);
        var stamp = Stat(path);
        Files.AddOrUpdate(path, key =>
        {
            Enqueue(key);
            Trim();
            return new Observation(stamp, version);
        }, (_, previous) => version > previous.Version
            ? new Observation(stamp, version) : previous);
        MarkFolders(path, version);
    }

    internal static void MarkFolderChanged(string path)
        => MarkFolders(TaskListGitProjectionCache.NormalizePath(path), Interlocked.Increment(ref _version));

    internal static int Repair(int maxFiles = 32)
    {
        var changed = 0;
        for (var i = 0; i < maxFiles && RepairQueue.TryDequeue(out var path); i++)
        {
            RepairQueue.Enqueue(path);
            if (!Files.TryGetValue(path, out var old)) continue;
            var stamp = Stat(path);
            if (stamp == old.Stamp) continue;
            var version = Interlocked.Increment(ref _version);
            if (Files.TryUpdate(path, new Observation(stamp, version), old))
            {
                MarkFolders(path, version);
                changed++;
            }
        }
        return changed;
    }

    private static Stamp Stat(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return default;
            using var stream = file.OpenRead();
            return new Stamp(true, file.Length, file.LastWriteTimeUtc.Ticks,
                Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "SidecarFileGeneration: sidecar temporarily unreadable");
            return default;
        }
    }

    private static void Trim()
    {
        while (Files.Count >= Capacity && RepairQueue.TryDequeue(out var oldest))
        {
            Queued.TryRemove(oldest, out _);
            Files.TryRemove(oldest, out _);
        }
    }

    private static void Enqueue(string path)
    {
        if (Queued.TryAdd(path, 0)) RepairQueue.Enqueue(path);
    }

    private static void MarkFolders(string path, long version)
    {
        for (var folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
            Folders.AddOrUpdate(TaskListGitProjectionCache.NormalizePath(folder), version,
                (_, previous) => Math.Max(previous, version));
    }
}
