using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3864: a closed manifest is written once, but the pilot tick lists every
/// manifest many times. The store keeps each parsed manifest keyed by its path
/// and parses the file again only when its length or write time changed, so an
/// unchanged manifest is parsed once per store lifetime.
/// </summary>
public sealed partial class BatchGateStore
{
    private sealed record CachedManifest(long Length, DateTime WrittenAtUtc, BatchGateManifest Manifest);

    private readonly ConcurrentDictionary<string, CachedManifest> _manifestCache = new(StringComparer.Ordinal);
    private long _manifestParses;
    private long _manifestReads;
    private long _manifestListings;

    /// <summary>How many times this store parsed a manifest file; a cached read does not count.</summary>
    internal long ManifestParses => Interlocked.Read(ref _manifestParses);

    /// <summary>How many manifest reads this store served, parsed or cached.</summary>
    internal long ManifestReads => Interlocked.Read(ref _manifestReads);

    /// <summary>How many times <see cref="ListManifests"/> ran on this store.</summary>
    internal long ManifestListings => Interlocked.Read(ref _manifestListings);

    // The stamp is taken before the read: a file replaced in between is cached
    // under the older stamp and read again on the next call.
    private BatchGateManifest ReadManifestFile(string path)
    {
        Interlocked.Increment(ref _manifestReads);
        var file = new FileInfo(path);
        var stamped = file.Exists;
        if (stamped && _manifestCache.TryGetValue(path, out var cached)
            && cached.Length == file.Length && cached.WrittenAtUtc == file.LastWriteTimeUtc)
            return cached.Manifest;
        Interlocked.Increment(ref _manifestParses);
        var manifest = Read<BatchGateManifest>(path);
        if (stamped) _manifestCache[path] = new CachedManifest(file.Length, file.LastWriteTimeUtc, manifest);
        return manifest;
    }

    private T ReadRecordFile<T>(string path) where T : class
        => typeof(T) == typeof(BatchGateManifest) ? (T)(object)ReadManifestFile(path) : Read<T>(path);

    // A folder the retention sweep removed leaves the cache with its manifest.
    private void ForgetRemovedManifests(IReadOnlyList<string> folders)
    {
        if (_manifestCache.Count <= folders.Count) return;
        var present = folders.Select(folder => Path.Combine(folder, "manifest.json"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in _manifestCache.Keys)
            if (!present.Contains(path)) _manifestCache.TryRemove(path, out _);
    }
}
