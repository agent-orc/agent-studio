using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using AgentStudio.Clients;

namespace AgentStudio.Tasks;

/// <summary>
/// In-memory snapshot cache of <see cref="TaskInfo"/> across all watch paths,
/// invalidated by <see cref="TaskWatcherService"/> events and by direct
/// notifications from mutation services. The polled hot paths
/// (<c>/api/tasks</c>, <c>/api/tasks/grouped</c>, <c>/api/runner/status</c>,
/// <c>FindJob</c>, <c>GetJobDetail</c>, supervisor observations) all bottom
/// out in <see cref="TaskScannerService.ScanAllJobs"/>; routing that call
/// through this cache turns each poll from an O(N) disk walk + JSON parse
/// into an O(1) reference return when nothing changed.
///
/// <para><b>Consistency model:</b> read-after-write is guaranteed for any
/// mutation that calls <see cref="Invalidate"/>. The FileSystemWatcher
/// signal (trailing-edge debounced in <see cref="TaskWatcherService"/>) covers
/// external changes - things touched outside the API. There is also a
/// safety re-scan TTL (default 30 s) so a missed watcher event cannot
/// produce an indefinitely stale view.</para>
///
/// <para><b>Board reads (AGT-3088)</b> do not use that global contract. A
/// board read takes one already published generation through
/// <see cref="AcquireBoardReadAsync"/> and pins it with <see cref="Pin"/>, so
/// every nested index read of that request (snapshot, reference graph, runner
/// status, the validator's generation) answers from the same generation and
/// never refreshes on the request path. A dirty or expired cache starts one
/// background refresh instead. Only two cases wait: a cold start, and the
/// reader's own pending mutation. A mutation belongs to a client when it runs
/// inside that client's write request (<see cref="AmbientClient"/>); the
/// required generation is then recorded per client in a bounded map, and a
/// board read that names its client waits only for those writes. Runner,
/// review and post-processing writes, and writes of other clients, never make
/// it wait. A board read without a client identity keeps the global
/// read-after-write target. When a background refresh publishes data that a
/// board read was served stale against, one debounced bulk-changed push tells
/// clients to revalidate. Every other caller keeps the global semantics
/// described above.</para>
///
/// <para><b>Concurrency:</b> the cache slot is one immutable
/// <see cref="TaskIndexPublication"/> swapped under a coarse lock; readers
/// under the lock get a stable snapshot. Refresh is single-flight. While one
/// thread is rescanning, readers dirtied only by external watcher churn see
/// the previous snapshot rather than queueing behind the disk walk. A reader
/// whose required mutation generation at reader entry is newer than the
/// published snapshot awaits that same refresh without spinning and, when
/// necessary, admits exactly one follow-up refresh. Later overlapping
/// mutations do not move that reader's consistency target. This preserves API
/// read-after-write without a thundering herd or starvation under continuous
/// mutation churn.</para>
/// </summary>
public sealed class TaskIndexCache
{
    /// <summary>Coalescing window for the bulk-changed push after a background publish.</summary>
    internal static readonly TimeSpan BulkChangedDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound on clients with a pending read-your-writes target.</summary>
    internal const int MaxTrackedClients = 1024;

    private readonly TimeSpan _safetyTtl;
    private readonly TimeProvider _time;
    private readonly Func<List<TaskInfo>> _scanAllJobsRaw;
    private readonly TaskScannerService _scanner;
    private readonly Action? _beforeRefreshGenerationCapture;
    private readonly ILogger<TaskIndexCache> _logger;

    // Cache slot: snapshot + when it was taken + whether a mutation/watcher
    // event marked it stale before the next read got there.
    private readonly Lock _lock = new();
    private readonly Lock _corePublicationLock = new();
    // One published generation: the live board partition, the archive
    // partition of the same scan, and the reference graphs built from it. The
    // terminal 7-archive lane is kept out of the live partition (board reads
    // must never page through hundreds of terminal cards), but the
    // slim-hydrated archived records are still walked once per refresh, so they
    // are kept for the dedicated paged archive read (ASS-1727) instead of
    // re-walking disk for that endpoint.
    private TaskIndexPublication _publication = TaskIndexPublication.Empty;
    // Core is keyed independently of the freshness-enforcing board partitions.
    private Dictionary<string, TaskCoreRecord> _core = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TaskInfo> _coreFactsByFolder = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _removedCoreKeys = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TaskCoreRecord> _coreByFolder = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _staleCoreFolders = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _pendingCoreFolders = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private long _coreWriteGeneration;
    private int _coreHydrationQueued;
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;
    private bool _dirty = true;
    private bool _hasSnapshot;

    // AGT-3088 board-read pin. Set for the duration of one board request (and
    // its body build) so every nested index read answers from one generation.
    private readonly AsyncLocal<BoardReadPin?> _pin = new();

    // Per-client read-your-writes targets: the newest mutation generation each
    // client's own write requests raised. Entries are dropped once published,
    // so the map only holds clients with a write still in flight.
    private readonly Dictionary<string, long> _clientMutationGen = new(StringComparer.Ordinal);
    // Highest target evicted by the size bound. An unknown client waits for
    // it, so eviction can delay a read but never break read-your-writes.
    private long _evictedClientFloor;

    // A board read was answered from a snapshot that an invalidation had
    // already made stale. The publish that catches up pushes one debounced
    // bulk-changed signal so that client revalidates.
    private bool _staleServed;
    private long _publishedInvalidationGen;
    private readonly Lock _pushLock = new();
    private ITimer? _pushTimer;
    private Action? _bulkChangedPublisher;

    // Single-flight refresh ownership. Readers never spin. External-only
    // readers return the last good snapshot immediately; cold-start and
    // mutation-freshness readers await the same completion source without
    // consuming CPU.
    private TaskCompletionSource<bool>? _refreshCompletion;

    // Invalidation generation counter. Incremented on every Invalidate so the
    // refresher can detect "did a mutation land while my disk walk was in
    // flight?" — captured before ScanAllJobsRaw, compared after. When the
    // counter has advanced, the just-read snapshot is racy relative to that
    // mutation: we install the snapshot (it's still better than nothing) but
    // leave _dirty=true so the very next read forces another refresh that
    // observes the post-mutation state. Without this guard, a torn read can
    // overwrite a true invalidation with stale data and the cache happily
    // serves the stale snapshot until the next external watcher event or the
    // 30s safety TTL, producing the "optimistic reorder reverts to the old
    // order after the next poll" symptom.
    private long _invalidationGen;
    // Mutation invalidations carry the stronger read-after-write contract.
    // A reader may return stale data for external watcher churn, but never
    // while the published snapshot predates an API mutation.
    private long _requiredMutationGen;
    private long _publishedMutationGen;
    // The published generation (TaskIndexPublication.Generation) is the
    // monotonic id of the current snapshot. It is incremented once per
    // successful disk-walk publish (a mutation, a watcher event, or the safety
    // TTL). Downstream projections that re-derive expensive per-request state
    // from the same on-disk facts (token snapshots, task-list Git signatures)
    // memoize against it so a warm poll where nothing changed pays O(1)
    // instead of re-walking the workspace.

    // Cheap diagnostics so a perf regression here is visible in /healthz or
    // a future debug endpoint without spinning up a profiler.
    public long Hits;
    public long Misses;
    public long StaleHits;
    public long ExternalInvalidations;
    public long MutationInvalidations;

    public TaskIndexCache(
        TaskScannerService scanner,
        ILogger<TaskIndexCache> logger,
        IConfiguration config,
        TimeProvider? timeProvider = null)
        : this(scanner, logger, config, scanner.ScanAllJobsRaw, timeProvider: timeProvider)
    {
    }

    internal TaskIndexCache(
        TaskScannerService scanner,
        ILogger<TaskIndexCache> logger,
        IConfiguration config,
        Func<List<TaskInfo>> scanAllJobsRaw,
        Action? beforeRefreshGenerationCapture = null,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _time = timeProvider ?? TimeProvider.System;
        _scanAllJobsRaw = scanAllJobsRaw;
        _beforeRefreshGenerationCapture = beforeRefreshGenerationCapture;
        _logger = logger;
        var ttlSec = int.TryParse(config["TaskIndexCache:SafetyTtlSeconds"], out var v) ? v : 30;
        _safetyTtl = TimeSpan.FromSeconds(Math.Max(1, ttlSec));
    }

    /// <summary>The refresh in flight, if any (tests wait on it).</summary>
    internal Task? RefreshInFlight
    {
        get { lock (_lock) return _refreshCompletion?.Task; }
    }

    internal TaskIndexCacheStats GetStats() => new(
        Interlocked.Read(ref Hits),
        Interlocked.Read(ref Misses),
        Interlocked.Read(ref StaleHits),
        Interlocked.Read(ref ExternalInvalidations),
        Interlocked.Read(ref MutationInvalidations));

    /// <summary>
    /// Returns the cached snapshot of board jobs (every lane except the
    /// terminal 7-archive). If the cache is dirty or has aged past the safety
    /// TTL, refreshes from disk first. The archive partition of the same scan
    /// is available via <see cref="GetArchiveSnapshot"/>.
    /// </summary>
    public ImmutableList<TaskInfo> GetSnapshot() => Current().Live;

    /// <summary>
    /// Version stamp of the currently published snapshot. It advances on every
    /// snapshot publish (mutation, watcher event, or safety-TTL rescan) and is
    /// stable between publishes, so a caller that already forced freshness via
    /// <see cref="GetSnapshot"/> can memoize derived projections against this
    /// value without re-reading the workspace. This getter never triggers a
    /// refresh; read it after a snapshot accessor when a current value matters.
    /// Inside a pinned board read it is the pinned generation.
    /// </summary>
    public long Generation
    {
        get
        {
            if (Pinned is { } pinned) return pinned.Generation;
            lock (_lock) return _publication.Generation;
        }
    }

    /// <summary>
    /// Returns the cached snapshot of terminal 7-archive jobs, slim-hydrated.
    /// Populated by the same single disk walk that feeds <see cref="GetSnapshot"/>
    /// (partitioned in <see cref="EnsureFresh"/>), so the dedicated paged
    /// archive endpoint (ASS-1727) pays no extra scan: it reads this field in
    /// O(1) when the cache is warm.
    /// </summary>
    public ImmutableList<TaskInfo> GetArchiveSnapshot() => Current().Archive;

    /// <summary>
    /// Atomically captures the live and archive partitions from one published
    /// cache generation. Archive-inclusive readers must use this method rather
    /// than calling <see cref="GetSnapshot"/> and <see cref="GetArchiveSnapshot"/>
    /// separately: a refresh between those calls could otherwise duplicate or
    /// omit a task that changed between a live lane and archive.
    /// </summary>
    public (ImmutableList<TaskInfo> Live, ImmutableList<TaskInfo> Archive) GetSnapshotPartitions()
    {
        var publication = Current();
        return (publication.Live, publication.Archive);
    }

    /// <summary>
    /// Returns a projection derived from one published generation, built at
    /// most once per (<paramref name="name"/>, <paramref name="version"/>,
    /// generation) and shared by every caller. Use it for read-model state that
    /// many callers would otherwise rebuild from the same snapshot, such as the
    /// runner queue order that all project runners display.
    /// </summary>
    public T GetDerived<T>(string name, long version, Func<TaskIndexPublication, T> build)
        where T : class
        => Current().GetOrAddDerived(name, version, build);

    /// <summary>
    /// The board-read entry point (AGT-3088). Returns an already published
    /// generation without running a disk walk on the caller's thread. A dirty
    /// or expired cache starts one background refresh and the caller gets the
    /// last snapshot. Only a cold start, or a pending mutation of
    /// <paramref name="clientId"/>'s own (any pending mutation when the read
    /// carries no client identity), waits, and it waits asynchronously.
    /// Callers pin the result with <see cref="Pin"/>.
    /// </summary>
    public async ValueTask<TaskIndexPublication> AcquireBoardReadAsync(
        string? clientId, CancellationToken cancellationToken)
    {
        using var lookupTrace = TaskSwitchTrace.Span("index.lookup");
        long targetMutationGen;
        lock (_lock)
        {
            targetMutationGen = string.IsNullOrEmpty(clientId)
                ? _requiredMutationGen
                : Math.Max(_clientMutationGen.GetValueOrDefault(clientId), _evictedClientFloor);
        }

        while (true)
        {
            Task refresh;
            lock (_lock)
            {
                if (_hasSnapshot && _publishedMutationGen >= targetMutationGen)
                {
                    Interlocked.Increment(ref Hits);
                    if (_dirty || _time.GetUtcNow() - _snapshotAt >= _safetyTtl)
                    {
                        Interlocked.Increment(ref StaleHits);
                        // Only an invalidation makes the push worthwhile: a
                        // TTL rescan normally finds nothing new.
                        if (_dirty) _staleServed = true;
                        StartBackgroundRefreshLocked();
                    }
                    return _publication;
                }
                StartBackgroundRefreshLocked();
                refresh = _refreshCompletion!.Task;
            }

            using (TaskSwitchTrace.Span("index.wait"))
                await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pins <paramref name="publication"/> for the current async flow until the
    /// returned scope is disposed: every index read below answers from it
    /// without refreshing. Call it from the request method itself, not from an
    /// async helper, because an async-local value set inside an awaited method
    /// does not flow back to its caller. Work that outlives the scope (a
    /// fire-and-forget task started inside it) falls back to the normal
    /// semantics once the scope is disposed.
    /// </summary>
    public IDisposable Pin(TaskIndexPublication publication)
    {
        var pin = new BoardReadPin(this, publication, _pin.Value);
        _pin.Value = pin;
        return pin;
    }

    internal void Unpin(BoardReadPin pin, BoardReadPin? previous)
    {
        if (ReferenceEquals(_pin.Value, pin)) _pin.Value = previous;
    }

    /// <summary>
    /// Registers the push sent after a background publish that caught up with
    /// data a board read was served stale against. Debounced by
    /// <see cref="BulkChangedDebounce"/>.
    /// </summary>
    public void AttachBulkChangedPublisher(Action publish) => _bulkChangedPublisher = publish;

    private TaskIndexPublication? Pinned => _pin.Value is { Active: true } pin ? pin.Publication : null;

    private TaskIndexPublication Current()
    {
        if (Pinned is { } pinned) return pinned;
        EnsureFresh();
        lock (_lock) return _publication;
    }

    /// <summary>Cache-only core lookup. This never invokes EnsureFresh.</summary>
    public TaskCoreLookup GetCore(string identity, string watchPath)
    {
        lock (_lock)
        {
            var key = CoreKey(watchPath, identity);
            if (_core.TryGetValue(key, out var record))
            {
                var expired = _time.GetUtcNow() - _snapshotAt >= _safetyTtl;
                var stale = _staleCoreFolders.Contains(record.FolderPath) || expired;
                if (expired) QueueCoreHydration();
                return new TaskCoreLookup(record, stale);
            }
            if (_removedCoreKeys.Contains(key)) return new TaskCoreLookup(null, false);
            if (!_hasSnapshot || _dirty || _time.GetUtcNow() - _snapshotAt >= _safetyTtl)
            {
                QueueCoreHydration();
                return new TaskCoreLookup(null, true);
            }
            return new TaskCoreLookup(null, false);
        }
    }

    private void QueueCoreHydration()
    {
        if (Interlocked.Exchange(ref _coreHydrationQueued, 1) != 0) return;
        // A queued refresh is background ownership. Do not inherit a core
        // request's Git or scan trace through Task.Run's execution context.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() =>
            {
                try { GetSnapshot(); }
                catch (Exception ex) { _logger.LogWarning(ex, "task-core-hydration-failed"); }
                finally { Interlocked.Exchange(ref _coreHydrationQueued, 0); }
            });
        }
    }

    private static string CoreKey(string watchPath, string identity) =>
        $"{watchPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}::{identity}";

    /// <summary>Publish one durable write without a workspace walk.</summary>
    public void PublishCore(TaskInfo info)
    {
        lock (_corePublicationLock)
        {
            Dictionary<string, TaskInfo> facts;
            Dictionary<string, TaskCoreRecord> records;
            lock (_lock)
            {
                facts = new Dictionary<string, TaskInfo>(_coreFactsByFolder, _coreFactsByFolder.Comparer);
                records = new Dictionary<string, TaskCoreRecord>(_coreByFolder, _coreByFolder.Comparer);
            }
            records.TryGetValue(info.FolderPath, out var previous);
            // A move or project transfer may change the folder before publication.
            foreach (var old in facts.Where(pair => pair.Value.TaskKey == info.TaskKey
                || (pair.Value.Id == info.Id && WatchPathComparison.PathsEqual(pair.Value.WatchPath, info.WatchPath)))
                .Select(pair => pair.Key).ToArray())
            {
                if (old != info.FolderPath)
                {
                    facts.Remove(old);
                    records.Remove(old);
                }
            }
            facts[info.FolderPath] = info;
            var references = TaskReferenceIndex.Build(facts.Values);
            var waitsOn = info.References.DependsOn.Count > 0 ? references.EvaluateWaitsOn(info) : null;
            records[info.FolderPath] = TaskCoreRecord.Create(info, waitsOn, previous, forceSidecars: true);
            PublishCoreFacts(facts, records, references);
            lock (_lock)
            {
                _removedCoreKeys.Remove(CoreKey(info.WatchPath, info.Id));
                _removedCoreKeys.Remove(CoreKey(info.WatchPath, info.TaskKey));
                if (!string.IsNullOrWhiteSpace(info.Key))
                    _removedCoreKeys.Remove(CoreKey(info.WatchPath, info.Key));
                _staleCoreFolders.Remove(info.FolderPath);
            }
        }
    }

    private void PublishCoreFacts(Dictionary<string, TaskInfo> facts,
        Dictionary<string, TaskCoreRecord> records, TaskReferenceIndex references)
    {
        var byFolder = new Dictionary<string, TaskCoreRecord>(records.Comparer);
        var aliases = new Dictionary<string, TaskCoreRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, task) in facts)
        {
            if (!records.TryGetValue(folder, out var record)) continue;
            var waitsOn = task.References.DependsOn.Count > 0 ? references.EvaluateWaitsOn(task) : null;
            record = record.WithDependencies(task, waitsOn);
            byFolder[folder] = record;
            AddCoreAliases(aliases, record);
        }
        AddLegacyCoreAliases(aliases, byFolder.Values);
        lock (_lock)
        {
            _coreFactsByFolder = facts;
            _coreByFolder = byFolder;
            _core = aliases;
            _coreWriteGeneration++;
        }
    }

    public void RemoveCore(TaskInfo info)
    {
        lock (_corePublicationLock)
        {
            Dictionary<string, TaskInfo> facts;
            Dictionary<string, TaskCoreRecord> records;
            lock (_lock)
            {
                facts = new Dictionary<string, TaskInfo>(_coreFactsByFolder, _coreFactsByFolder.Comparer);
                records = new Dictionary<string, TaskCoreRecord>(_coreByFolder, _coreByFolder.Comparer);
                foreach (var key in _core.Where(pair => pair.Value.TaskKey == info.TaskKey
                    || (pair.Value.Id == info.Id && WatchPathComparison.PathsEqual(pair.Value.WatchPath, info.WatchPath)))
                    .Select(pair => pair.Key))
                    _removedCoreKeys.Add(key);
            }
            facts.Remove(info.FolderPath);
            records.Remove(info.FolderPath);
            PublishCoreFacts(facts, records, TaskReferenceIndex.Build(facts.Values));
            lock (_lock) _staleCoreFolders.Remove(info.FolderPath);
        }
    }

    public void RemoveCoreByFolder(string folder)
    {
        TaskCoreRecord? record;
        lock (_lock) _coreByFolder.TryGetValue(folder, out record);
        if (record is not null) RemoveCore(new TaskInfo
        {
            Id = record.Id, TaskKey = record.TaskKey,
            WatchPath = record.WatchPath, FolderPath = record.FolderPath,
        });
    }

    public TaskCoreRecord? GetCoreByFolder(string folder)
    {
        lock (_lock) return _coreByFolder.GetValueOrDefault(folder);
    }

    /// <summary>Debounced sidecar refresh on the watcher thread, never on a core read.</summary>
    public void NotifyCoreFileChanged(string path)
    {
        var name = Path.GetFileName(path);
        if (name is not ("task.json" or "prompt.md" or "status.md" or "timeline.jsonl")) return;
        var folder = name == "timeline.jsonl"
            ? Path.GetDirectoryName(Path.GetDirectoryName(path))
            : Path.GetDirectoryName(path);
        if (folder is null) return;
        TaskCoreRecord? known;
        lock (_lock)
        {
            if (!_coreByFolder.TryGetValue(folder, out known)) return;
            _staleCoreFolders.Add(folder);
            if (!_pendingCoreFolders.Add(folder)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250);
                _scanner.PublishCoreFromFolder(folder, known.WatchPath, known.ProjectName, known.State);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "task-core-sidecar-refresh-failed path={Path}", path); }
            finally { lock (_lock) _pendingCoreFolders.Remove(folder); }
        });
    }

    private static void AddCoreAliases(Dictionary<string, TaskCoreRecord> core, TaskCoreRecord record)
    {
        core[CoreKey(record.WatchPath, record.Id)] = record;
        core[CoreKey(record.WatchPath, record.TaskKey)] = record;
        if (!string.IsNullOrWhiteSpace(record.Key)) core[CoreKey(record.WatchPath, record.Key)] = record;
    }

    private static void AddLegacyCoreAliases(Dictionary<string, TaskCoreRecord> core,
        IEnumerable<TaskCoreRecord> records)
    {
        // Older core replies exposed only this prefix. Keep it as an alias
        // when it identifies one task and does not shadow a canonical key.
        var prefixes = records.Select(record => (Record: record, Prefix: TaskCoreRecord.Limit(record.Id, 128)!))
            .Where(entry => entry.Prefix != entry.Record.Id)
            .GroupBy(entry => CoreKey(entry.Record.WatchPath, entry.Prefix), StringComparer.OrdinalIgnoreCase);
        foreach (var group in prefixes)
            if (group.Count() == 1) core.TryAdd(group.Key, group.Single().Record);
    }

    /// <summary>
    /// Returns the reference graph published with the current live/archive
    /// partitions. Claim polling and endpoint overlays share this instance, so
    /// the O(N) graph build happens once per snapshot refresh, not per request.
    /// </summary>
    public TaskReferenceIndex GetReferenceIndex() => Current().References;

    /// <summary>
    /// Atomically captures the live partition and reference graph from one
    /// published generation for runner pickup and remote claim decisions.
    /// </summary>
    public (ImmutableList<TaskInfo> Live, TaskReferenceIndex References)
        GetLiveSnapshotWithReferenceIndex()
    {
        var publication = Current();
        return (publication.Live, publication.References);
    }

    /// <summary>
    /// Ensures the published generation (<see cref="_publication"/>)
    /// reflects a scan taken after the last <see cref="Invalidate"/> / safety-TTL
    /// expiry. Single-flight: only one thread does the disk walk. External-only
    /// readers receive the last good snapshot instead of waiting or spinning.
    /// Cold-start and mutation-freshness readers share one non-spinning wait
    /// because stale data is not valid for them.
    /// </summary>
    private void EnsureFresh()
    {
        using var lookupTrace = TaskSwitchTrace.Span("index.lookup");
        // Freeze the read-after-write target at reader entry. Comparing every
        // retry with the latest global generation turns continuous task churn
        // into a moving goalpost: waiters can be serialized behind one full
        // workspace scan per later mutation even after their own prerequisite
        // generation has been published.
        long targetMutationGen;
        lock (_lock) targetMutationGen = _requiredMutationGen;

        while (true)
        {
            TaskCompletionSource<bool>? refresh = null;
            Task? coldStartRefresh = null;
            lock (_lock)
            {
                if (!_dirty
                    && Volatile.Read(ref _publishedMutationGen)
                    >= targetMutationGen
                    && _time.GetUtcNow() - _snapshotAt < _safetyTtl)
                {
                    Interlocked.Increment(ref Hits);
                    return;
                }

                if (_refreshCompletion != null)
                {
                    if (_hasSnapshot
                        && Volatile.Read(ref _publishedMutationGen)
                        >= targetMutationGen)
                    {
                        Interlocked.Increment(ref Hits);
                        Interlocked.Increment(ref StaleHits);
                        return;
                    }
                    coldStartRefresh = _refreshCompletion.Task;
                }
                else
                {
                    refresh = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _refreshCompletion = refresh;
                }
            }

            if (coldStartRefresh != null)
            {
                using (TaskSwitchTrace.Span("index.wait"))
                    coldStartRefresh.GetAwaiter().GetResult();

                // The completed refresh may have published this reader's
                // target while a later mutation dirtied the cache again. That
                // later write overlaps this read and belongs to a later
                // reader; do not chase it with another global scan here.
                lock (_lock)
                {
                    if (_hasSnapshot && _publishedMutationGen >= targetMutationGen)
                    {
                        Interlocked.Increment(ref Hits);
                        if (_dirty) Interlocked.Increment(ref StaleHits);
                        return;
                    }
                }
                continue;
            }

            if (refresh == null) continue;
            using (TaskSwitchTrace.Span("index.refresh"))
                Refresh(refresh);
            return;
        }
    }

    private void Refresh(TaskCompletionSource<bool> completion)
    {
        try
        {
            // Every invalidation during this disk walk advances the generation.
            // They collapse into one dirty bit, so after this single-flight
            // finishes at most one follow-up refresh can be admitted.
            _beforeRefreshGenerationCapture?.Invoke();
            long genBefore;
            long mutationGenBefore;
            long coreWriteGenBefore;
            Dictionary<string, TaskCoreRecord> previousCoreByFolder;
            lock (_lock)
            {
                // These generations describe one logical cache state and must
                // be captured atomically. Reading them separately allowed a
                // mutation between the reads to look both included and racy,
                // forcing an unnecessary second full scan.
                genBefore = _invalidationGen;
                mutationGenBefore = _requiredMutationGen;
                coreWriteGenBefore = _coreWriteGeneration;
                previousCoreByFolder = new Dictionary<string, TaskCoreRecord>(_coreByFolder, _coreByFolder.Comparer);
            }
            var refreshTimer = System.Diagnostics.Stopwatch.StartNew();
            var fresh = _scanAllJobsRaw();
            refreshTimer.Stop();
            BatchMoveOperationTelemetry.RecordScannerRefresh(refreshTimer.Elapsed);
            var board = new List<TaskInfo>(fresh.Count);
            var archive = new List<TaskInfo>();
            foreach (var job in fresh)
            {
                if (string.Equals(job.State, TaskStates.Archive, StringComparison.Ordinal))
                    archive.Add(job);
                else
                    board.Add(job);
            }
            var referenceIndex = TaskReferenceIndex.Build(fresh);
            // Automated pickup and runner queues never see E2E fixtures, and a
            // fixture can satisfy (or shadow) a waits-on key, so they resolve
            // against their own graph: same live-then-archive order as
            // ScanAllAutomationJobsWithArchive, built here once per generation
            // instead of once per runner status read.
            var automationReferences = TaskReferenceIndex.Build(
                board.Concat(archive).Where(task => !task.Fixture));
            var projected = new Dictionary<string, TaskCoreRecord>(StringComparer.OrdinalIgnoreCase);
            var byFolder = new Dictionary<string, TaskCoreRecord>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var factsByFolder = new Dictionary<string, TaskInfo>(byFolder.Comparer);
            foreach (var job in fresh)
            {
                var waitsOn = job.References.DependsOn.Count > 0 ? referenceIndex.EvaluateWaitsOn(job) : null;
                previousCoreByFolder.TryGetValue(job.FolderPath, out var previous);
                var record = TaskCoreRecord.Create(job, waitsOn, previous);
                AddCoreAliases(projected, record);
                byFolder[record.FolderPath] = record;
                factsByFolder[job.FolderPath] = job;
            }
            AddLegacyCoreAliases(projected, byFolder.Values);

            var push = false;
            lock (_corePublicationLock)
            lock (_lock)
            {
                _publication = new TaskIndexPublication(
                    board.ToImmutableList(),
                    archive.ToImmutableList(),
                    referenceIndex,
                    automationReferences,
                    _publication.Generation + 1);
                // A targeted mutation may publish while the scan is running.
                // Never overwrite that more recent publication with a torn scan.
                if (_coreWriteGeneration == coreWriteGenBefore)
                {
                    _core = projected;
                    _coreByFolder = byFolder;
                    _coreFactsByFolder = factsByFolder;
                    _removedCoreKeys.Clear();
                    _staleCoreFolders.Clear();
                }
                _snapshotAt = _time.GetUtcNow();
                _hasSnapshot = true;
                _publishedMutationGen = Math.Max(_publishedMutationGen, mutationGenBefore);
                _dirty = _invalidationGen != genBefore;
                PruneClientTargetsLocked();
                // A TTL-only rescan captures the same invalidation generation
                // as the previous publish and never pushes.
                var followedInvalidation = genBefore != _publishedInvalidationGen;
                _publishedInvalidationGen = genBefore;
                if (followedInvalidation && _staleServed)
                {
                    _staleServed = false;
                    push = true;
                }
                if (ReferenceEquals(_refreshCompletion, completion))
                    _refreshCompletion = null;
                Interlocked.Increment(ref Misses);
                // A board read was served stale and this walk started before
                // the invalidation that made it stale: catch up in the
                // background rather than waiting for the next read.
                if (!push && _staleServed && _dirty) StartBackgroundRefreshLocked();
            }
            // Arm the push before releasing waiters, so a reader that resumes
            // on this publication already has the push it implies scheduled.
            if (push) SchedulePush();
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_refreshCompletion, completion))
                    _refreshCompletion = null;
            }
            completion.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Marks the cache stale so the next <see cref="GetSnapshot"/> call
    /// rescans from disk. Called from TaskWatcherService (external changes)
    /// and from mutation services (API-driven changes) so a write is always
    /// visible on the next read.
    /// </summary>
    /// <param name="clientId">The client whose write request caused a
    /// mutation, when there is one. Board reads of that client then wait for
    /// this generation; see the class summary.</param>
    public void Invalidate(
        InvalidationSource source = InvalidationSource.Mutation,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        string? clientId = null)
    {
        // Publish the required mutation generation, general invalidation
        // generation and dirty bit under the same lock. A reader can therefore
        // never observe the old clean snapshot in the middle of a mutation
        // invalidation. The refresher still uses generation comparisons because
        // its disk walk intentionally runs outside this lock.
        lock (_lock)
        {
            if (source == InvalidationSource.Mutation)
            {
                var required = Interlocked.Increment(ref _requiredMutationGen);
                if (!string.IsNullOrEmpty(clientId)) RecordClientMutationLocked(clientId, required);
            }
            Interlocked.Increment(ref _invalidationGen);
            _dirty = true;
        }
        if (source == InvalidationSource.Mutation)
            BatchMoveOperationTelemetry.RecordScannerInvalidation();
        if (source == InvalidationSource.External)
            Interlocked.Increment(ref ExternalInvalidations);
        else
            Interlocked.Increment(ref MutationInvalidations);
        _logger.LogDebug(
            "task-index-cache-invalidated source={Source} caller={Caller} callerFile={CallerFile}",
            source,
            callerMemberName,
            callerFilePath);
    }

    private void RecordClientMutationLocked(string clientId, long required)
    {
        _clientMutationGen[clientId] = required;
        if (_clientMutationGen.Count <= MaxTrackedClients) return;
        PruneClientTargetsLocked();
        if (_clientMutationGen.Count <= MaxTrackedClients) return;
        var oldest = _clientMutationGen.MinBy(entry => entry.Value);
        _clientMutationGen.Remove(oldest.Key);
        _evictedClientFloor = Math.Max(_evictedClientFloor, oldest.Value);
    }

    private void PruneClientTargetsLocked()
    {
        if (_clientMutationGen.Count == 0) return;
        foreach (var published in _clientMutationGen
                     .Where(entry => entry.Value <= _publishedMutationGen)
                     .Select(entry => entry.Key)
                     .ToList())
            _clientMutationGen.Remove(published);
    }

    /// <summary>
    /// Starts the single-flight refresh on a pool thread unless one is already
    /// running. The walk is background ownership: it must inherit neither the
    /// request's trace nor its board-read pin, so the execution context is not
    /// flowed (the same rule as <see cref="QueueCoreHydration"/>).
    /// </summary>
    private void StartBackgroundRefreshLocked()
    {
        if (_refreshCompletion != null) return;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(() => RunBackgroundRefresh(completion));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => RunBackgroundRefresh(completion));
    }

    private void RunBackgroundRefresh(TaskCompletionSource<bool> completion)
    {
        try
        {
            Refresh(completion);
        }
        catch (Exception ex)
        {
            // The last published snapshot stays in place and the dirty bit
            // stays set, so the next read starts a fresh attempt. Waiters see
            // the failure through the completion; nobody else has to.
            _ = completion.Task.Exception;
            _logger.LogWarning(ex, "task-index-background-refresh-failed");
        }
    }

    private void SchedulePush()
    {
        if (_bulkChangedPublisher is null) return;
        lock (_pushLock)
        {
            if (_pushTimer is not null) return;
            if (ExecutionContext.IsFlowSuppressed())
            {
                _pushTimer = _time.CreateTimer(_ => FirePush(), null, BulkChangedDebounce, Timeout.InfiniteTimeSpan);
                return;
            }
            using (ExecutionContext.SuppressFlow())
                _pushTimer = _time.CreateTimer(_ => FirePush(), null, BulkChangedDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FirePush()
    {
        lock (_pushLock)
        {
            _pushTimer?.Dispose();
            _pushTimer = null;
        }
        try
        {
            _bulkChangedPublisher?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-index-bulk-changed-push-failed");
        }
    }

    /// <summary>
    /// Test / debug hook: forces a synchronous rescan and returns the new
    /// snapshot size. Useful in unit tests that want to assert "the cache
    /// reflects what we just wrote to disk" without racing the lazy path.
    /// </summary>
    public int ForceRefresh()
    {
        Invalidate(InvalidationSource.Mutation);
        // Deliberately not GetSnapshot: a board-read pin must not turn a
        // forced rescan into a read of the pinned generation.
        EnsureFresh();
        lock (_lock) return _publication.Live.Count;
    }

    public enum InvalidationSource
    {
        /// <summary>FileSystemWatcher fired (something outside the API touched the workspace).</summary>
        External,
        /// <summary>An API mutation just wrote to disk and explicitly invalidated.</summary>
        Mutation,
    }
}

/// <summary>
/// One published generation of the task index: both partitions of one disk
/// walk, the reference graphs built from them, and the read-model projections
/// derived from that generation. Immutable once published.
/// </summary>
public sealed class TaskIndexPublication
{
    private const int MaxDerivedEntries = 32;
    private readonly ConcurrentDictionary<(string Name, long Version), Lazy<object>> _derived = new();

    internal TaskIndexPublication(
        ImmutableList<TaskInfo> live,
        ImmutableList<TaskInfo> archive,
        TaskReferenceIndex references,
        TaskReferenceIndex automationReferences,
        long generation)
    {
        Live = live;
        Archive = archive;
        References = references;
        AutomationReferences = automationReferences;
        Generation = generation;
    }

    internal static TaskIndexPublication Empty { get; } = new(
        ImmutableList<TaskInfo>.Empty,
        ImmutableList<TaskInfo>.Empty,
        TaskReferenceIndex.Build(Array.Empty<TaskInfo>()),
        TaskReferenceIndex.Build(Array.Empty<TaskInfo>()),
        0);

    /// <summary>Every lane except the terminal 7-archive.</summary>
    public ImmutableList<TaskInfo> Live { get; }

    /// <summary>The terminal 7-archive lane, slim-hydrated.</summary>
    public ImmutableList<TaskInfo> Archive { get; }

    /// <summary>Archive-inclusive reference graph over every task, E2E fixtures included.</summary>
    public TaskReferenceIndex References { get; }

    /// <summary>
    /// Archive-inclusive reference graph without E2E fixtures, the graph the
    /// runner's waits-on gate resolves against.
    /// </summary>
    public TaskReferenceIndex AutomationReferences { get; }

    public long Generation { get; }

    internal T GetOrAddDerived<T>(string name, long version, Func<TaskIndexPublication, T> build)
        where T : class
    {
        var key = (name, version);
        var entry = _derived.GetOrAdd(key, _ => new Lazy<object>(
            () => build(this), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = (T)entry.Value;
            if (_derived.Count > MaxDerivedEntries) TrimOlderVersions(name, version);
            return value;
        }
        catch
        {
            // Do not cache a failed build for the rest of the generation.
            _derived.TryRemove(new KeyValuePair<(string, long), Lazy<object>>(key, entry));
            throw;
        }
    }

    private void TrimOlderVersions(string name, long version)
    {
        foreach (var stale in _derived.Keys.Where(key => key.Name == name && key.Version != version))
            _derived.TryRemove(stale, out _);
    }
}

internal sealed class BoardReadPin(TaskIndexCache owner, TaskIndexPublication publication, BoardReadPin? previous)
    : IDisposable
{
    private volatile bool _disposed;

    public TaskIndexPublication Publication => publication;

    public bool Active => !_disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        owner.Unpin(this, previous);
    }
}

internal sealed record TaskIndexCacheStats(
    long Hits,
    long Misses,
    long StaleHits,
    long ExternalInvalidations,
    long MutationInvalidations);
