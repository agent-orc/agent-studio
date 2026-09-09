using System.Collections.Immutable;
using System.Runtime.CompilerServices;

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
/// <para><b>Concurrency:</b> the cache slot is an <see cref="ImmutableList{T}"/>
/// updated under a coarse lock; readers under the lock get a stable
/// snapshot. Refresh is single-flight. While one thread is rescanning,
/// readers dirtied only by external watcher churn see the previous snapshot
/// rather than queueing behind the disk walk. A reader whose required mutation
/// generation at reader entry is newer than the published snapshot awaits that
/// same refresh without spinning and, when necessary, admits exactly one
/// follow-up refresh. Later overlapping mutations do not move that reader's
/// consistency target. This preserves API read-after-write without a
/// thundering herd or starvation under continuous mutation churn.</para>
/// </summary>
public sealed class TaskIndexCache
{
    private readonly TimeSpan _safetyTtl;
    private readonly Func<List<TaskInfo>> _scanAllJobsRaw;
    private readonly Action? _beforeRefreshGenerationCapture;
    private readonly Action<long>? _beforeAwaitRefresh;
    private readonly ILogger<TaskIndexCache> _logger;

    // Cache slot: snapshot + when it was taken + whether a mutation/watcher
    // event marked it stale before the next read got there.
    private readonly Lock _lock = new();
    private ImmutableList<TaskInfo> _snapshot = ImmutableList<TaskInfo>.Empty;
    // Archive partition of the same scan. The terminal 7-archive lane is kept
    // out of _snapshot (board reads must never page through hundreds of
    // terminal cards), but the slim-hydrated archived records are still walked
    // once per refresh, so we keep them here for the dedicated paged archive
    // read (ASS-1727) instead of re-walking disk for that endpoint.
    private ImmutableList<TaskInfo> _archiveSnapshot = ImmutableList<TaskInfo>.Empty;
    private TaskReferenceIndex _referenceIndex = TaskReferenceIndex.Build(Array.Empty<TaskInfo>());
    private DateTime _snapshotAtUtc = DateTime.MinValue;
    private bool _dirty = true;
    private bool _hasSnapshot;

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
    // Monotonic id of the currently published snapshot. Incremented once per
    // successful disk-walk publish (a mutation, a watcher event, or the safety
    // TTL). Downstream projections that re-derive expensive per-request state
    // from the same on-disk facts (token snapshots, task-list Git signatures)
    // memoize against this so a warm poll where nothing changed pays O(1)
    // instead of re-walking the workspace.
    private long _snapshotGeneration;

    // Cheap diagnostics so a perf regression here is visible in /healthz or
    // a future debug endpoint without spinning up a profiler.
    public long Hits;
    public long Misses;
    public long StaleHits;
    public long ExternalInvalidations;
    public long MutationInvalidations;

    public TaskIndexCache(TaskScannerService scanner, ILogger<TaskIndexCache> logger, IConfiguration config)
        : this(scanner, logger, config, scanner.ScanAllJobsRaw)
    {
    }

    internal TaskIndexCache(
        TaskScannerService scanner,
        ILogger<TaskIndexCache> logger,
        IConfiguration config,
        Func<List<TaskInfo>> scanAllJobsRaw,
        Action? beforeRefreshGenerationCapture = null,
        Action<long>? beforeAwaitRefresh = null)
    {
        _scanAllJobsRaw = scanAllJobsRaw;
        _beforeRefreshGenerationCapture = beforeRefreshGenerationCapture;
        _beforeAwaitRefresh = beforeAwaitRefresh;
        _logger = logger;
        var ttlSec = int.TryParse(config["TaskIndexCache:SafetyTtlSeconds"], out var v) ? v : 30;
        _safetyTtl = TimeSpan.FromSeconds(Math.Max(1, ttlSec));
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
    public ImmutableList<TaskInfo> GetSnapshot()
    {
        EnsureFresh();
        lock (_lock) return _snapshot;
    }

    /// <summary>
    /// Version stamp of the currently published snapshot. It advances on every
    /// snapshot publish (mutation, watcher event, or safety-TTL rescan) and is
    /// stable between publishes, so a caller that already forced freshness via
    /// <see cref="GetSnapshot"/> can memoize derived projections against this
    /// value without re-reading the workspace. This getter never triggers a
    /// refresh; read it after a snapshot accessor when a current value matters.
    /// </summary>
    public long Generation
    {
        get { lock (_lock) return _snapshotGeneration; }
    }

    /// <summary>
    /// Returns the cached snapshot of terminal 7-archive jobs, slim-hydrated.
    /// Populated by the same single disk walk that feeds <see cref="GetSnapshot"/>
    /// (partitioned in <see cref="EnsureFresh"/>), so the dedicated paged
    /// archive endpoint (ASS-1727) pays no extra scan: it reads this field in
    /// O(1) when the cache is warm.
    /// </summary>
    public ImmutableList<TaskInfo> GetArchiveSnapshot()
    {
        EnsureFresh();
        lock (_lock) return _archiveSnapshot;
    }

    /// <summary>
    /// Atomically captures the live and archive partitions from one published
    /// cache generation. Archive-inclusive readers must use this method rather
    /// than calling <see cref="GetSnapshot"/> and <see cref="GetArchiveSnapshot"/>
    /// separately: a refresh between those calls could otherwise duplicate or
    /// omit a task that changed between a live lane and archive.
    /// </summary>
    public (ImmutableList<TaskInfo> Live, ImmutableList<TaskInfo> Archive) GetSnapshotPartitions()
    {
        EnsureFresh();
        lock (_lock) return (_snapshot, _archiveSnapshot);
    }

    /// <summary>
    /// Returns the reference graph published with the current live/archive
    /// partitions. Claim polling and endpoint overlays share this instance, so
    /// the O(N) graph build happens once per snapshot refresh, not per request.
    /// </summary>
    public TaskReferenceIndex GetReferenceIndex()
    {
        EnsureFresh();
        lock (_lock) return _referenceIndex;
    }

    /// <summary>
    /// Atomically captures the live partition and reference graph from one
    /// published generation for runner pickup and remote claim decisions.
    /// </summary>
    public (ImmutableList<TaskInfo> Live, TaskReferenceIndex References)
        GetLiveSnapshotWithReferenceIndex()
    {
        EnsureFresh();
        lock (_lock) return (_snapshot, _referenceIndex);
    }

    /// <summary>
    /// Ensures both partitions (<see cref="_snapshot"/> + <see cref="_archiveSnapshot"/>)
    /// reflect a scan taken after the last <see cref="Invalidate"/> / safety-TTL
    /// expiry. Single-flight: only one thread does the disk walk. External-only
    /// readers receive the last good snapshot instead of waiting or spinning.
    /// Cold-start and mutation-freshness readers share one non-spinning wait
    /// because stale data is not valid for them.
    /// </summary>
    private void EnsureFresh()
    {
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
                    && DateTime.UtcNow - _snapshotAtUtc < _safetyTtl)
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
                _beforeAwaitRefresh?.Invoke(targetMutationGen);
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
            lock (_lock)
            {
                // These generations describe one logical cache state and must
                // be captured atomically. Reading them separately allowed a
                // mutation between the reads to look both included and racy,
                // forcing an unnecessary second full scan.
                genBefore = _invalidationGen;
                mutationGenBefore = _requiredMutationGen;
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

            lock (_lock)
            {
                _snapshot = board.ToImmutableList();
                _archiveSnapshot = archive.ToImmutableList();
                _referenceIndex = referenceIndex;
                _snapshotAtUtc = DateTime.UtcNow;
                _snapshotGeneration++;
                _hasSnapshot = true;
                _publishedMutationGen = Math.Max(_publishedMutationGen, mutationGenBefore);
                _dirty = _invalidationGen != genBefore;
                if (ReferenceEquals(_refreshCompletion, completion))
                    _refreshCompletion = null;
                Interlocked.Increment(ref Misses);
            }
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
    public void Invalidate(
        InvalidationSource source = InvalidationSource.Mutation,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        // Publish the required mutation generation, general invalidation
        // generation and dirty bit under the same lock. A reader can therefore
        // never observe the old clean snapshot in the middle of a mutation
        // invalidation. The refresher still uses generation comparisons because
        // its disk walk intentionally runs outside this lock.
        lock (_lock)
        {
            if (source == InvalidationSource.Mutation)
                Interlocked.Increment(ref _requiredMutationGen);
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

    /// <summary>
    /// Test / debug hook: forces a synchronous rescan and returns the new
    /// snapshot size. Useful in unit tests that want to assert "the cache
    /// reflects what we just wrote to disk" without racing the lazy path.
    /// </summary>
    public int ForceRefresh()
    {
        Invalidate(InvalidationSource.Mutation);
        return GetSnapshot().Count;
    }

    public enum InvalidationSource
    {
        /// <summary>FileSystemWatcher fired (something outside the API touched the workspace).</summary>
        External,
        /// <summary>An API mutation just wrote to disk and explicitly invalidated.</summary>
        Mutation,
    }
}

internal sealed record TaskIndexCacheStats(
    long Hits,
    long Misses,
    long StaleHits,
    long ExternalInvalidations,
    long MutationInvalidations);
