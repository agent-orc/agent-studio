using System.Collections.Concurrent;

namespace AgentStudio.Docs;

/// <summary>
/// Central in-memory projection of one project's complete wiki read model.
/// A fill performs the filesystem work once; every wiki endpoint reads the
/// published stable snapshot without validating it through another docs/
/// walk. Invalidations rebuild synchronously before returning, so a reader
/// arriving after a watcher event or API mutation never pays a cold fill and
/// never observes the pre-mutation snapshot.
/// </summary>
public sealed class WikiContentCache
{
    private readonly Func<string, WikiContentSnapshot?> _build;
    private readonly Func<string, string> _normalizeProjectKey;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CacheSlot> _slots =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheSlot
    {
        public readonly Lock Gate = new();
        public WikiContentSnapshot? Snapshot;
    }

    public long Hits;
    public long Misses;
    public long Fills;
    public long Preloads;
    public long WatcherInvalidations;
    public long MutationInvalidations;
    private long _fillMsTotal;

    public WikiContentCache(ProjectDocsService docs, ILogger<WikiContentCache> logger)
        : this(docs.BuildWikiContentSnapshotRaw, logger, docs.ResolveWikiCacheKey)
    {
    }

    internal WikiContentCache(
        Func<string, WikiContentSnapshot?> build,
        ILogger? logger = null,
        Func<string, string>? normalizeProjectKey = null)
    {
        _build = build;
        _logger = logger;
        _normalizeProjectKey = normalizeProjectKey ?? (projectName => projectName);
    }

    internal WikiContentSnapshot? GetSnapshot(string projectName)
    {
        projectName = _normalizeProjectKey(projectName);
        var slot = _slots.GetOrAdd(projectName, _ => new CacheSlot());
        lock (slot.Gate)
        {
            if (slot.Snapshot != null)
            {
                Interlocked.Increment(ref Hits);
                return slot.Snapshot;
            }

            Interlocked.Increment(ref Misses);
            return FillLocked(projectName, slot, "cold");
        }
    }

    /// <summary>
    /// The published snapshot, or null while the project is not filled yet.
    /// Never fills: a read surface that only adds Wiki facts to another answer
    /// (the orchestrator feed's Dossier decisions) must not pay a cold fill and
    /// its Git reads inside its own request (AGT-3793). The warmup service and
    /// the eager invalidations keep the slots filled.
    /// </summary>
    internal WikiContentSnapshot? TryGetPublished(string projectName)
    {
        if (!_slots.TryGetValue(_normalizeProjectKey(projectName), out var slot)) return null;
        lock (slot.Gate) return slot.Snapshot;
    }

    /// <summary>
    /// The docs/ staleness signature of the published snapshot (path + mtime +
    /// size over the whole tree). Consumers that keep a derived projection of
    /// their own - the BM25 search index, for example - gate on this instead of
    /// walking docs/ a second time to decide whether their projection is stale.
    ///
    /// <para>Null means "no usable gate, decide for yourself". That happens in
    /// two cases, both of which must reindex rather than trust a signature.
    /// First, <paramref name="wikiDir"/> - the directory the caller actually
    /// reads - is not the directory this snapshot projects: a project with a
    /// configured <c>wikiSourceBranch</c> publishes a snapshot of the branch
    /// worktree, so its signature says nothing about the checkout and would
    /// hide every real edit there. Second, the signature is one of the
    /// placeholders a fill stores when it could not enumerate a tree
    /// (<c>unavailable</c>) or found none (<c>empty</c>); those are constants,
    /// and a constant gate never opens again.</para>
    /// </summary>
    internal string? GetDocsSignature(string projectName, string wikiDir)
    {
        var snapshot = GetSnapshot(projectName);
        if (snapshot == null) return null;
        if (snapshot.Signature is "unavailable" or "empty") return null;
        return SameDirectory(snapshot.WikiDir, wikiDir) ? snapshot.Signature : null;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            SilentCatch.Note(ex, "Wiki cache could not compare the snapshot and caller wiki directories.");
            return false;
        }
    }

    /// <summary>
    /// Fills a project during startup warmup or project registration. Warmup
    /// fills are counted separately from read misses, so a non-zero
    /// <see cref="Misses"/> in steady state means a reader really did arrive
    /// before the cache was warm.
    /// </summary>
    public bool Preload(string projectName)
    {
        projectName = _normalizeProjectKey(projectName);
        var slot = _slots.GetOrAdd(projectName, _ => new CacheSlot());
        lock (slot.Gate)
        {
            if (slot.Snapshot != null) return true;
            Interlocked.Increment(ref Preloads);
            return FillLocked(projectName, slot, "preload") != null;
        }
    }

    /// <summary>Counter readout for the periodic telemetry rollup.</summary>
    public WikiContentCacheStats GetStats() => new(
        Interlocked.Read(ref Hits),
        Interlocked.Read(ref Misses),
        Interlocked.Read(ref Fills),
        Interlocked.Read(ref Preloads),
        Interlocked.Read(ref WatcherInvalidations),
        Interlocked.Read(ref MutationInvalidations),
        Interlocked.Read(ref _fillMsTotal),
        _slots.Count);

    /// <summary>
    /// Rebuilds immediately and publishes only after the complete replacement
    /// snapshot is ready. The old snapshot remains unreachable once this method
    /// returns, which is the cache's read-after-write boundary.
    /// </summary>
    public void Invalidate(
        string projectName,
        InvalidationSource source = InvalidationSource.Mutation)
    {
        projectName = _normalizeProjectKey(projectName);
        if (source == InvalidationSource.Watcher)
            Interlocked.Increment(ref WatcherInvalidations);
        else
            Interlocked.Increment(ref MutationInvalidations);

        var slot = _slots.GetOrAdd(projectName, _ => new CacheSlot());
        lock (slot.Gate)
            _ = FillLocked(projectName, slot, source == InvalidationSource.Watcher ? "watcher" : "mutation");
    }

    internal void InvalidateAll()
    {
        foreach (var projectName in _slots.Keys)
            Invalidate(projectName, InvalidationSource.Mutation);
    }

    private WikiContentSnapshot? FillLocked(string projectName, CacheSlot slot, string reason)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = _build(projectName);
            slot.Snapshot = snapshot;
            Interlocked.Increment(ref Fills);
            Interlocked.Add(ref _fillMsTotal, started.ElapsedMilliseconds);
            _logger?.LogInformation(
                "wiki-cache-fill project={Project} reason={Reason} elapsedMs={ElapsedMs} files={Files} folders={Folders} hits={Hits} misses={Misses} fills={Fills}",
                projectName,
                reason,
                started.ElapsedMilliseconds,
                snapshot?.Files.Count ?? 0,
                snapshot?.Folders.Count ?? 0,
                Interlocked.Read(ref Hits),
                Interlocked.Read(ref Misses),
                Interlocked.Read(ref Fills));
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "wiki-cache-fill-failed project={Project} reason={Reason}", projectName, reason);
            throw;
        }
    }

    public enum InvalidationSource
    {
        Watcher,
        Mutation,
    }
}

/// <summary>
/// Counter snapshot of the central wiki cache. Read by the warmup service's
/// periodic rollup log; the ratio that matters is Hits vs Misses (a warm
/// process should approach zero misses) and Fills vs invalidations (how often
/// docs/ churn forces a rebuild).
/// </summary>
public sealed record WikiContentCacheStats(
    long Hits,
    long Misses,
    long Fills,
    long Preloads,
    long WatcherInvalidations,
    long MutationInvalidations,
    long FillMsTotal,
    int Projects);

internal sealed record WikiContentSnapshot(
    string ProjectName,
    WikiSourceContext Source,
    string WikiDir,
    string Signature,
    WikiTreeResult TreeResult,
    List<WikiFileEntry> Files,
    IReadOnlyDictionary<string, WikiFileEntry> FilesByRelPath,
    IReadOnlyDictionary<string, WikiTreeMetadata> MetadataByRelPath,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FolderOrderByParent,
    IReadOnlyDictionary<string, WikiFolderView> Folders,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FolderDescendantPages,
    IReadOnlyDictionary<string, string?> TaskKeysByRelPath,
    WikiHomeView Home,
    WikiPulseLifecycle Lifecycle,
    WikiPulseInbox Inbox,
    WikiPulseCritical Critical,
    WikiPulseWarnings Warnings,
    WorkbenchCatalogue? Workbenches);
