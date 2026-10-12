using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Correctness tests for the Cycle-1 TaskIndexCache. The perf claim of the
/// cache is "polled hot paths see O(1) reads instead of O(N) disk walks";
/// the correctness claim is "an explicit Invalidate() makes the next read
/// see what was just written, and the safety TTL eventually picks up
/// changes the watcher missed". These tests pin both.
/// </summary>
public class TaskIndexCacheTests : IDisposable
{
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache _cache;

    public TaskIndexCacheTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-cache-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        // The shared fixture deliberately puts the safety TTL out of reach.
        // Most tests here assert the opposite of what the TTL does - that a
        // read returns the previously published snapshot until something
        // invalidates it - so a TTL that can expire mid-test turns a correct
        // "cache returned the stale view, by design" into a failure whenever
        // the run is slow enough for the rescan to land first. The one test
        // that is about the TTL builds its own tight-TTL cache below.
        _config = BuildConfig(safetyTtlSeconds: 3600);

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(_cache);
    }

    private IConfiguration BuildConfig(int safetyTtlSeconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "cache-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = safetyTtlSeconds.ToString(),
            })
            .Build();

    [Fact]
    public void FirstRead_LoadsFromDisk()
    {
        WriteJob("2-ready", "job-1", "First");
        var jobs = _scanner.ScanAllJobs();
        Assert.Single(jobs);
        Assert.Equal("job-1", jobs[0].Id);
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(0, _cache.Hits);
    }

    [Fact]
    public void SecondRead_HitsCache_WithoutDiskAccess()
    {
        WriteJob("2-ready", "job-1", "First");
        _ = _scanner.ScanAllJobs(); // miss → loads
        _ = _scanner.ScanAllJobs(); // hit
        _ = _scanner.ScanAllJobs(); // hit
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(2, _cache.Hits);
    }

    [Fact]
    public void Invalidate_CausesNextReadToSeeFreshDiskState()
    {
        WriteJob("2-ready", "job-1", "First");
        var firstScan = _scanner.ScanAllJobs();
        Assert.Single(firstScan);

        // Add a job directly on disk - cache doesn't know yet.
        WriteJob("2-ready", "job-2", "Second");
        var stillCached = _scanner.ScanAllJobs();
        Assert.Single(stillCached); // cache returned the stale view, by design

        // Mutation service would call this after a write.
        _cache.Invalidate();

        var fresh = _scanner.ScanAllJobs();
        Assert.Equal(2, fresh.Count);
        Assert.Contains(fresh, j => j.Id == "job-1");
        Assert.Contains(fresh, j => j.Id == "job-2");
        Assert.Equal(1, _cache.MutationInvalidations);
    }

    /// <summary>
    /// The safety rescan is the only behaviour in this class that needs a TTL
    /// that expires during a test, so it gets its own scanner and cache over
    /// the same folder, driven by a fake clock rather than a sleep.
    /// </summary>
    [Fact]
    public void SafetyTtl_PicksUpExternalChanges_EvenWithoutInvalidation()
    {
        var config = BuildConfig(safetyTtlSeconds: 1);
        var time = new FakeTimeProvider();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, time);
        scanner.SetIndexCache(cache);

        WriteJob("2-ready", "job-1", "First");
        var firstScan = scanner.ScanAllJobs();
        Assert.Single(firstScan);

        WriteJob("2-ready", "job-2", "Second");
        // No invalidation (simulating a missed FileSystemWatcher event).
        Assert.Single(scanner.ScanAllJobs());
        // Move past the configured 1 s safety TTL.
        time.Advance(TimeSpan.FromMilliseconds(1200));

        var fresh = scanner.ScanAllJobs();
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public void ScanAllJobsRaw_AlwaysHitsDisk_BypassingCache()
    {
        WriteJob("2-ready", "job-1", "First");
        _ = _scanner.ScanAllJobs(); // primes cache

        // Add directly on disk.
        WriteJob("2-ready", "job-2", "Second");

        // Raw read sees the new job even though cache is stale.
        var raw = _scanner.ScanAllJobsRaw();
        Assert.Equal(2, raw.Count);

        // Cached read still returns the stale view (Invalidate wasn't called).
        var cached = _scanner.ScanAllJobs();
        Assert.Single(cached);
    }

    [Fact]
    public void CachedSnapshot_ExcludesArchive_ButArchiveFindUsesCachedPartition()
    {
        WriteJob(TaskStates.Ready, "job-1", "First");
        WriteJob(TaskStates.Archive, "archived-1", "Archived");

        var raw = _scanner.ScanAllJobsRaw();
        Assert.Equal(2, raw.Count);
        Assert.Contains(raw, j => j.State == TaskStates.Archive);

        var cached = _scanner.ScanAllJobs();
        Assert.Single(cached);
        Assert.DoesNotContain(cached, j => j.State == TaskStates.Archive);

        var archived = _scanner.FindJob("archived-1", _watchPath);
        Assert.NotNull(archived);
        Assert.Equal(TaskStates.Archive, archived!.State);
        Assert.Equal("Archived", archived.Title);
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(1, _cache.Hits);
    }

    [Fact]
    public void ArchiveSnapshot_HoldsOnlyArchived_BuiltByTheSameScanAsTheBoard()
    {
        // ASS-1727: the archive partition is filled by the same single disk
        // walk that fills the board snapshot. Priming one then reading the
        // other must NOT trigger a second scan (one miss, then a hit).
        WriteJob(TaskStates.Ready, "ready-1", "Ready");
        WriteJob(TaskStates.Archive, "archived-1", "Archived A");
        WriteJob(TaskStates.Archive, "archived-2", "Archived B");

        var board = _cache.GetSnapshot();
        var archive = _cache.GetArchiveSnapshot();

        Assert.Single(board);
        Assert.DoesNotContain(board, j => j.State == TaskStates.Archive);

        Assert.Equal(2, archive.Count);
        Assert.All(archive, j => Assert.Equal(TaskStates.Archive, j.State));
        Assert.Contains(archive, j => j.Id == "archived-1");
        Assert.Contains(archive, j => j.Id == "archived-2");

        // One disk walk fed both partitions: the second read was a cache hit.
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(1, _cache.Hits);
    }

    [Fact]
    public void ScanArchivedJobs_ReturnsArchivedOnly_FromCache()
    {
        WriteJob(TaskStates.Ready, "ready-1", "Ready");
        WriteJob(TaskStates.Completed, "done-1", "Completed");
        WriteJob(TaskStates.Archive, "archived-1", "Archived");

        var archived = _scanner.ScanArchivedJobs();

        Assert.Single(archived);
        Assert.Equal("archived-1", archived[0].Id);
        Assert.Equal(TaskStates.Archive, archived[0].State);
    }

    [Fact]
    public void ScanArchivedJobs_ProjectsColdMarkerFromArchiveManifest()
    {
        WriteJob(TaskStates.Archive, "archived-cold", "Cold archive");
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.Archive, "archived-cold", "archive-manifest.json"), "{}");

        var archived = Assert.Single(_scanner.ScanArchivedJobs());

        Assert.Equal("cold", archived.ArchiveState);
        Assert.Equal("cold", ArchivedTaskInfo.From(archived).ArchiveState);
    }

    [Fact]
    public void ScanArchivedJobs_ProjectsRestoredMarkerFromArchiveManifest()
    {
        WriteJob(TaskStates.Archive, "archived-restored", "Restored archive");
        var manifestPath = Path.Combine(_watchPath, TaskStates.Archive, "archived-restored", "archive-manifest.json");
        File.WriteAllText(manifestPath, "{}");

        Assert.Equal("cold", Assert.Single(_scanner.ScanArchivedJobs()).ArchiveState);

        File.WriteAllText(manifestPath, """{"restoredAt":"2026-09-08T12:00:00Z"}""");
        _cache.Invalidate();

        var archived = Assert.Single(_scanner.ScanArchivedJobs());

        Assert.Equal("hot-restored", archived.ArchiveState);
    }

    [Fact]
    public void ScanArchivedJobs_PicksUpNewArchivedFolder_AfterInvalidate()
    {
        WriteJob(TaskStates.Archive, "archived-1", "First");
        Assert.Single(_scanner.ScanArchivedJobs());

        WriteJob(TaskStates.Archive, "archived-2", "Second");
        // Stale by design until something invalidates the cache.
        Assert.Single(_scanner.ScanArchivedJobs());

        _cache.Invalidate();
        Assert.Equal(2, _scanner.ScanArchivedJobs().Count);
    }

    [Fact]
    public void UnchangedArchiveFolder_IsHydratedOnceAcrossSnapshotRefreshes()
    {
        WriteJob(TaskStates.Archive, "archived-1", "Archived");
        SettleWriteTime(TaskStates.Archive, "archived-1");
        var first = Assert.Single(_scanner.ScanArchivedJobs());

        _cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var second = Assert.Single(_scanner.ScanArchivedJobs());

        Assert.Same(first, second);
    }

    [Fact]
    public void ReferenceIndex_IsReusedUntilSnapshotInvalidation()
    {
        WriteJob(TaskStates.Ready, "job-1", "First");
        var first = _scanner.GetReferenceIndex();
        var warm = _scanner.GetReferenceIndex();

        Assert.Same(first, warm);
        Assert.NotNull(first.Resolve("JOB-1"));

        WriteJob(TaskStates.Ready, "job-2", "Second");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var refreshed = _scanner.GetReferenceIndex();

        Assert.NotSame(first, refreshed);
        Assert.NotNull(refreshed.Resolve("JOB-2"));
    }

    [Fact]
    public void LiveSnapshotAndReferenceIndex_AreCapturedFromOneGeneration()
    {
        WriteJob(TaskStates.Ready, "job-1", "First");

        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();

        Assert.Single(snapshot.Live);
        Assert.Same(_scanner.GetReferenceIndex(), snapshot.References);
        Assert.NotNull(snapshot.References.Resolve("JOB-1"));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void WarmArchiveInclusiveScan_Over1000Folders_FinishesUnderOneSecond()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var state = i < 800 ? TaskStates.Archive : TaskStates.Ready;
            WriteJob(state, $"perf-{i:D4}", $"Performance {i:D4}");
        }

        Assert.Equal(1_000, _scanner.ScanAllJobsWithArchive().Count);
        var missesAfterWarmup = _cache.Misses;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var warm = _scanner.ScanAllJobsWithArchive();
        stopwatch.Stop();

        Assert.Equal(1_000, warm.Count);
        Assert.Equal(missesAfterWarmup, _cache.Misses);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Warm scan of 1000 folders took {stopwatch.Elapsed.TotalMilliseconds:0.###} ms.");
    }

    [Fact]
    public void WithoutCache_ScanAllJobs_AlwaysHitsDisk()
    {
        // Build a scanner without a cache (mimics test fixtures that don't
        // wire one). Each call must read from disk.
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        var bareScanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);

        WriteJob("2-ready", "job-1", "First");
        Assert.Single(bareScanner.ScanAllJobs());

        WriteJob("2-ready", "job-2", "Second");
        // No cache → next call sees the new job immediately.
        Assert.Equal(2, bareScanner.ScanAllJobs().Count);
    }

    [Fact]
    public async Task ConcurrentReaders_ReturnStaleSnapshot_WhileOneRefreshIsInFlight()
    {
        var refreshEntered = new ManualResetEventSlim(false);
        var releaseRefresh = new ManualResetEventSlim(false);
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                if (scan == 2)
                {
                    refreshEntered.Set();
                    Assert.True(releaseRefresh.Wait(TimeSpan.FromSeconds(5)));
                }
                return [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
            });

        Assert.Equal("job-1", Assert.Single(cache.GetSnapshot()).Id);
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);

        var refresher = Task.Run(() => cache.GetSnapshot());
        Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(5)));

        var readers = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => cache.GetSnapshot()))
            .ToArray();
        var allReaders = Task.WhenAll(readers);

        Assert.Same(allReaders, await Task.WhenAny(allReaders, Task.Delay(TimeSpan.FromSeconds(2))));
        Assert.All(await allReaders, snapshot => Assert.Equal("job-1", Assert.Single(snapshot).Id));
        Assert.Equal(2, Volatile.Read(ref scans));

        releaseRefresh.Set();
        Assert.Equal("job-2", Assert.Single(await refresher).Id);
    }

    [Fact]
    public async Task InvalidationsDuringRefresh_AreCoalescedIntoOneFollowupRefresh()
    {
        var secondScanEntered = new ManualResetEventSlim(false);
        var releaseSecondScan = new ManualResetEventSlim(false);
        var thirdScanEntered = new ManualResetEventSlim(false);
        var releaseThirdScan = new ManualResetEventSlim(false);
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                if (scan == 2)
                {
                    secondScanEntered.Set();
                    Assert.True(releaseSecondScan.Wait(TimeSpan.FromSeconds(5)));
                }
                if (scan == 3)
                {
                    thirdScanEntered.Set();
                    Assert.True(releaseThirdScan.Wait(TimeSpan.FromSeconds(5)));
                }
                return [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
            });

        _ = cache.GetSnapshot();
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        var secondRefresh = Task.Run(() => cache.GetSnapshot());
        Assert.True(secondScanEntered.Wait(TimeSpan.FromSeconds(5)));

        for (var i = 0; i < 50; i++)
            cache.Invalidate(TaskIndexCache.InvalidationSource.External);

        releaseSecondScan.Set();
        Assert.Equal("job-2", Assert.Single(await secondRefresh).Id);

        var thirdRefresh = Task.Run(() => cache.GetSnapshot());
        Assert.True(thirdScanEntered.Wait(TimeSpan.FromSeconds(5)));
        var staleReaders = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => cache.GetSnapshot()))
            .ToArray();
        await Task.WhenAll(staleReaders);

        Assert.Equal(3, Volatile.Read(ref scans));
        Assert.All(staleReaders, reader => Assert.Equal("job-2", Assert.Single(reader.Result).Id));

        releaseThirdScan.Set();
        Assert.Equal("job-3", Assert.Single(await thirdRefresh).Id);
        Assert.Equal(3, Volatile.Read(ref scans));
    }

    [Fact]
    public async Task MutationDuringInFlightRefresh_WaitsAndDrivesOneFreshFollowup()
    {
        var secondScanEntered = new ManualResetEventSlim(false);
        var releaseSecondScan = new ManualResetEventSlim(false);
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                if (scan == 2)
                {
                    secondScanEntered.Set();
                    Assert.True(releaseSecondScan.Wait(TimeSpan.FromSeconds(5)));
                }
                return [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
            });

        Assert.Equal("job-1", Assert.Single(cache.GetSnapshot()).Id);
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        var externalRefresh = Task.Run(() => cache.GetSnapshot());
        Assert.True(secondScanEntered.Wait(TimeSpan.FromSeconds(5)));

        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var postMutationReader = Task.Run(() => cache.GetSnapshot());
        Assert.NotSame(postMutationReader, await Task.WhenAny(
            postMutationReader, Task.Delay(TimeSpan.FromMilliseconds(150))));

        releaseSecondScan.Set();
        _ = await externalRefresh;
        Assert.Equal("job-3", Assert.Single(await postMutationReader).Id);
        Assert.Equal(3, Volatile.Read(ref scans));
    }

    [Fact]
    public async Task LaterMutation_DoesNotMoveGoalpostForAlreadyWaitingReader()
    {
        var secondScanEntered = new ManualResetEventSlim(false);
        var releaseSecondScan = new ManualResetEventSlim(false);
        var thirdScanEntered = new ManualResetEventSlim(false);
        var releaseThirdScan = new ManualResetEventSlim(false);
        var waiterCalling = new ManualResetEventSlim(false);
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                if (scan == 2)
                {
                    secondScanEntered.Set();
                    Assert.True(releaseSecondScan.Wait(TimeSpan.FromSeconds(5)));
                }
                if (scan == 3)
                {
                    thirdScanEntered.Set();
                    Assert.True(releaseThirdScan.Wait(TimeSpan.FromSeconds(5)));
                }
                return [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
            });

        Assert.Equal("job-1", Assert.Single(cache.GetSnapshot()).Id);
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var owner = Task.Run(() => cache.GetSnapshot());
        Assert.True(secondScanEntered.Wait(TimeSpan.FromSeconds(5)));

        var alreadyWaiting = Task.Run(() =>
        {
            waiterCalling.Set();
            return cache.GetSnapshot();
        });
        Assert.True(waiterCalling.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotSame(alreadyWaiting, await Task.WhenAny(
            alreadyWaiting, Task.Delay(TimeSpan.FromMilliseconds(150))));

        // This mutation overlaps the waiting read. It must make a later reader
        // refresh again, but it must not move the older reader's consistency
        // target and serialize that reader behind another full workspace scan.
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);

        try
        {
            releaseSecondScan.Set();
            Assert.Equal("job-2", Assert.Single(await owner).Id);
            Assert.Same(alreadyWaiting, await Task.WhenAny(
                alreadyWaiting, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.Equal("job-2", Assert.Single(await alreadyWaiting).Id);
            Assert.False(thirdScanEntered.IsSet);
            Assert.Equal(2, Volatile.Read(ref scans));
        }
        finally
        {
            releaseSecondScan.Set();
            releaseThirdScan.Set();
            await Task.WhenAll(owner, alreadyWaiting);
        }
    }

    [Fact]
    public async Task SnapshotPartitions_AlwaysComeFromOnePublishedGeneration()
    {
        var secondScanEntered = new ManualResetEventSlim(false);
        var releaseSecondScan = new ManualResetEventSlim(false);
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                if (scan == 2)
                {
                    secondScanEntered.Set();
                    Assert.True(releaseSecondScan.Wait(TimeSpan.FromSeconds(5)));
                }
                return
                [
                    new TaskInfo { Id = $"live-{scan}", State = TaskStates.Ready },
                    new TaskInfo { Id = $"archive-{scan}", State = TaskStates.Archive },
                ];
            });

        var first = cache.GetSnapshotPartitions();
        Assert.Equal("live-1", Assert.Single(first.Live).Id);
        Assert.Equal("archive-1", Assert.Single(first.Archive).Id);

        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        var refresher = Task.Run(() => cache.GetSnapshot());
        Assert.True(secondScanEntered.Wait(TimeSpan.FromSeconds(5)));

        var stale = cache.GetSnapshotPartitions();
        Assert.Equal("live-1", Assert.Single(stale.Live).Id);
        Assert.Equal("archive-1", Assert.Single(stale.Archive).Id);

        releaseSecondScan.Set();
        _ = await refresher;
        var fresh = cache.GetSnapshotPartitions();
        Assert.Equal("live-2", Assert.Single(fresh.Live).Id);
        Assert.Equal("archive-2", Assert.Single(fresh.Archive).Id);
        Assert.Equal(2, Volatile.Read(ref scans));
    }

    [Fact]
    // Races a mutation into the refresh's generation-capture window; the window
    // timing flakes under gate load (night-flake tail) while passing at idle.
    [Trait("Category", "MachineBound")]
    public async Task MutationBeforeGenerationCapture_IsAbsorbedByCurrentRefresh()
    {
        var secondCaptureEntered = new ManualResetEventSlim(false);
        var releaseSecondCapture = new ManualResetEventSlim(false);
        var refreshes = 0;
        var scans = 0;
        var cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config,
            () =>
            {
                var scan = Interlocked.Increment(ref scans);
                return [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
            },
            beforeRefreshGenerationCapture: () =>
            {
                if (Interlocked.Increment(ref refreshes) != 2) return;
                secondCaptureEntered.Set();
                Assert.True(releaseSecondCapture.Wait(TimeSpan.FromSeconds(5)));
            });

        Assert.Equal("job-1", Assert.Single(cache.GetSnapshot()).Id);
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        var refresher = Task.Run(() => cache.GetSnapshot());
        Assert.True(secondCaptureEntered.Wait(TimeSpan.FromSeconds(5)));

        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        releaseSecondCapture.Set();

        Assert.Equal("job-2", Assert.Single(await refresher).Id);
        Assert.Equal("job-2", Assert.Single(cache.GetSnapshot()).Id);
        Assert.Equal(2, Volatile.Read(ref scans));
        Assert.Equal(2, cache.Misses);
    }


    // ---------------------------------------------------------------------
    // AGT-3088: board reads take a published generation, pin it, and never
    // refresh on the request path.
    // ---------------------------------------------------------------------

    private static readonly AsyncLocal<string?> RequestFlow = new();

    /// <summary>
    /// A scan delegate whose disk walk can be held open. It reads its result
    /// before blocking, which is what a real walk racing a write does, and it
    /// records whether it ran on a board request's flow.
    /// </summary>
    private sealed class GatedScan
    {
        private int _scans;
        public int Scans => Volatile.Read(ref _scans);
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(true);
        public ConcurrentQueue<string?> Flows { get; } = new();
        public Func<int, List<TaskInfo>> Produce { get; set; } =
            scan => [new TaskInfo { Id = $"job-{scan}", State = TaskStates.Ready }];
        public Exception? FailNext;

        public List<TaskInfo> Scan()
        {
            var scan = Interlocked.Increment(ref _scans);
            Flows.Enqueue(RequestFlow.Value);
            var result = Produce(scan);
            Entered.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(10)));
            if (Interlocked.Exchange(ref FailNext, null) is { } failure) throw failure;
            return result;
        }

        public void Hold()
        {
            Entered.Reset();
            Release.Reset();
        }
    }

    private TaskIndexCache NewCache(GatedScan gate, TimeProvider? time = null, int safetyTtlSeconds = 3600)
        => new(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            BuildConfig(safetyTtlSeconds),
            gate.Scan,
            timeProvider: time);

    private static async Task WaitForRefreshAsync(TaskIndexCache cache)
    {
        if (cache.RefreshInFlight is not { } refresh) return;
        try { await refresh.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { /* a deliberately failed refresh */ }
    }

    private static Task<TaskIndexPublication> BoardRead(TaskIndexCache cache, string? client)
        => cache.AcquireBoardReadAsync(client, CancellationToken.None).AsTask();

    private static async Task AssertStillWaiting(Task task)
        => Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(150))));

    [Fact]
    public async Task BoardRead_OnADirtyCache_ServesThePublishedSnapshot_AndRefreshesOffTheRequestFlow()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        RequestFlow.Value = "board-request";
        try
        {
            var cold = await BoardRead(cache, "client-x");
            Assert.Equal("job-1", Assert.Single(cold.Live).Id);

            gate.Hold();
            cache.Invalidate(TaskIndexCache.InvalidationSource.External);
            var stale = await BoardRead(cache, "client-x").WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(cold, stale);
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(5)), "a dirty read must start a background refresh");
            gate.Release.Set();
            await WaitForRefreshAsync(cache);

            Assert.Equal("job-2", Assert.Single((await BoardRead(cache, "client-x")).Live).Id);
            Assert.Equal(2, gate.Scans);
            // Neither the cold start nor the dirty refresh ran on the request flow.
            Assert.All(gate.Flows, flow => Assert.Null(flow));
        }
        finally
        {
            RequestFlow.Value = null;
            gate.Release.Set();
        }
    }

    [Fact]
    public async Task TenConcurrentBoardReads_DuringASlowRefresh_AllReturnAtOnce()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        var published = await BoardRead(cache, "client-x");

        gate.Hold();
        // A runner write: in-process, no client.
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var reads = Enumerable.Range(0, 10).Select(_ => Task.Run(() => BoardRead(cache, "client-x"))).ToArray();
        var all = Task.WhenAll(reads);
        try
        {
            Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.All(await all, read => Assert.Same(published, read));
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, gate.Scans);
        }
        finally
        {
            gate.Release.Set();
        }
        await WaitForRefreshAsync(cache);
        Assert.Equal(2, gate.Scans);
    }

    [Fact]
    public async Task IdentifiedBoardRead_WaitsForItsOwnMutation_AndNoOtherClientDoes()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        var published = await BoardRead(cache, "client-x");

        gate.Hold();
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: "client-x");
        try
        {
            var other = BoardRead(cache, "client-y");
            Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.Same(published, await other);

            var writer = BoardRead(cache, "client-x");
            await AssertStillWaiting(writer);
            gate.Release.Set();
            Assert.Equal("job-2", Assert.Single((await writer.WaitAsync(TimeSpan.FromSeconds(5))).Live).Id);
        }
        finally
        {
            gate.Release.Set();
        }
    }

    [Fact]
    public async Task AnonymousBoardRead_StillWaitsForEveryMutation()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        _ = await BoardRead(cache, null);

        gate.Hold();
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var anonymous = BoardRead(cache, null);
        try
        {
            await AssertStillWaiting(anonymous);
        }
        finally
        {
            gate.Release.Set();
        }
        Assert.Equal("job-2", Assert.Single((await anonymous.WaitAsync(TimeSpan.FromSeconds(5))).Live).Id);
    }

    [Fact]
    public async Task ColdStart_Waits_ForTheFirstPublish()
    {
        var gate = new GatedScan();
        gate.Hold();
        var cache = NewCache(gate);

        var first = BoardRead(cache, "client-x");
        try
        {
            await AssertStillWaiting(first);
        }
        finally
        {
            gate.Release.Set();
        }
        Assert.Equal("job-1", Assert.Single((await first.WaitAsync(TimeSpan.FromSeconds(5))).Live).Id);
    }

    [Fact]
    public async Task WaitingBoardRead_HonoursTheRequestAbort()
    {
        var gate = new GatedScan();
        gate.Hold();
        var cache = NewCache(gate);
        using var aborted = new CancellationTokenSource();

        var read = cache.AcquireBoardReadAsync("client-x", aborted.Token).AsTask();
        aborted.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        finally
        {
            gate.Release.Set();
        }
    }

    [Fact]
    public async Task FailedBackgroundRefresh_KeepsTheSnapshot_AndTheNextReadRetries()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        var published = await BoardRead(cache, "client-x");

        gate.FailNext = new IOException("workspace walk failed");
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        Assert.Same(published, await BoardRead(cache, "client-x"));
        await WaitForRefreshAsync(cache);
        Assert.Equal(2, gate.Scans);

        // The failure left the cache dirty: the next read serves the last
        // snapshot again and starts the retry.
        Assert.Same(published, await BoardRead(cache, "client-x"));
        await WaitForRefreshAsync(cache);
        Assert.Equal(3, gate.Scans);
        Assert.Equal("job-3", Assert.Single((await BoardRead(cache, "client-x")).Live).Id);
    }

    [Fact]
    public async Task OptimisticReorder_TheWritersNextBoardReadNeverShowsTheOldOrder()
    {
        string[] disk = ["a", "b"];
        var gate = new GatedScan
        {
            Produce = _ =>
            {
                var order = Volatile.Read(ref disk);
                return order.Select((id, index) => new TaskInfo { Id = id, State = TaskStates.Ready, Order = index + 1 })
                    .ToList();
            },
        };
        var cache = NewCache(gate);
        Assert.Equal(["a", "b"], OrderedIds(await BoardRead(cache, "client-x")));

        // Watcher churn starts a background walk that reads the old order...
        gate.Hold();
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        _ = await BoardRead(cache, "client-y");
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(5)));

        // ...and the writer's reorder lands while that walk is still running.
        Volatile.Write(ref disk, ["b", "a"]);
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: "client-x");
        var writersRead = BoardRead(cache, "client-x");
        await AssertStillWaiting(writersRead);

        gate.Release.Set();
        Assert.Equal(["b", "a"], OrderedIds(await writersRead.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(3, gate.Scans);

        static string[] OrderedIds(TaskIndexPublication publication)
            => publication.Live.OrderBy(task => task.Order).Select(task => task.Id).ToArray();
    }

    [Fact]
    public async Task ClientTargets_AreBounded_AndAnEvictedWriterStillReadsItsWrite()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        _ = await BoardRead(cache, "client-0");

        gate.Hold();
        for (var i = 0; i <= TaskIndexCache.MaxTrackedClients; i++)
            cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: $"client-{i}");

        // client-0 had the oldest entry and was evicted by the bound; it must
        // still wait for its own write instead of reading around it.
        var evicted = BoardRead(cache, "client-0");
        try
        {
            await AssertStillWaiting(evicted);
        }
        finally
        {
            gate.Release.Set();
        }
        Assert.Equal("job-2", Assert.Single((await evicted.WaitAsync(TimeSpan.FromSeconds(5))).Live).Id);
    }

    [Fact]
    public async Task ScannerInvalidation_IsAttributedOnlyToTheWriteRequestsOwnClient()
    {
        var gate = new GatedScan();
        var cache = NewCache(gate);
        _scanner.SetIndexCache(cache);
        _ = await BoardRead(cache, "client-x");

        gate.Hold();
        using (AmbientClient.Enter("client-x"))
            _scanner.InvalidateCache();
        try
        {
            var other = BoardRead(cache, "client-y");
            Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(2))));
            await AssertStillWaiting(BoardRead(cache, "client-x"));
        }
        finally
        {
            gate.Release.Set();
        }
        await WaitForRefreshAsync(cache);
        _ = await BoardRead(cache, "client-x");

        // Work started inside a write request but writing after it finished
        // is background work, not the client's own mutation.
        var requestDone = new TaskCompletionSource();
        Task background;
        using (AmbientClient.Enter("client-z"))
            background = Task.Run(async () =>
            {
                await requestDone.Task;
                _scanner.InvalidateCache();
            });
        gate.Hold();
        requestDone.SetResult();
        await background;
        try
        {
            var read = BoardRead(cache, "client-z");
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2))));
        }
        finally
        {
            gate.Release.Set();
        }
    }

    [Fact]
    public async Task Pin_ServesEveryNestedIndexReadFromOneGeneration_WithoutEnteringTheRefreshPath()
    {
        var gate = new GatedScan
        {
            Produce = scan =>
            [
                new TaskInfo { Id = $"job-{scan}", Key = $"JOB-{scan}", State = TaskStates.Ready },
                new TaskInfo { Id = $"fixture-{scan}", Key = $"FIX-{scan}", State = TaskStates.Ready, Fixture = true },
                new TaskInfo { Id = $"done-{scan}", Key = $"DONE-{scan}", State = TaskStates.Archive },
            ],
        };
        var cache = NewCache(gate);
        _scanner.SetIndexCache(cache);
        var pinned = await BoardRead(cache, "client-x");

        // A publish lands after the read took its generation.
        cache.ForceRefresh();
        Assert.Equal(pinned.Generation + 1, cache.Generation);
        // And the index is dirty again, with the next walk held open.
        gate.Hold();
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);

        using (_scanner.PinBoardRead(pinned))
        {
            var hits = cache.Hits;
            var misses = cache.Misses;
            Assert.Equal(pinned.Generation, _scanner.SnapshotGeneration);
            Assert.Equal(["job-1", "fixture-1"], _scanner.ScanAllJobs().Select(task => task.Id));
            Assert.Equal(["job-1", "done-1"], _scanner.ScanAllAutomationJobsWithArchive().Select(task => task.Id));
            Assert.Same(pinned.References, _scanner.GetReferenceIndex());
            Assert.Equal(pinned.Generation, await Task.Run(() => _scanner.SnapshotGeneration));

            // The dependency lookups of the body build take the pinned graph.
            var lookups = TaskEndpointHelpers.BuildDependencyGraphLookups(
                [new TaskInfo { Id = "waiter", TaskKey = "waiter-key", Key = "W-1", State = TaskStates.Ready,
                    References = new TaskReferences { DependsOn = ["DONE-1"] } }],
                _scanner,
                references: pinned.References);
            Assert.True(lookups.WaitsOn["waiter-key"].Items.Single().Fulfilled);

            Assert.Equal(hits, cache.Hits);
            Assert.Equal(misses, cache.Misses);
        }

        gate.Release.Set();
        Assert.NotEqual(pinned.Generation, _scanner.SnapshotGeneration);
        Assert.Contains(_scanner.ScanAllJobs(), task => task.Id == "job-3");
    }

    [Fact]
    public async Task Publish_AfterAnInvalidationAStaleReadSaw_PushesOnce_AndATtlOnlyPublishNeverPushes()
    {
        var time = new FakeTimeProvider();
        var gate = new GatedScan();
        var cache = NewCache(gate, time, safetyTtlSeconds: 30);
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        var pushes = 0;
        notifier.JobsBulkChanged += () => Interlocked.Increment(ref pushes);
        cache.AttachBulkChangedPublisher(notifier.PublishBulkChanged);

        _ = await BoardRead(cache, "client-x");
        time.Advance(TaskIndexCache.BulkChangedDebounce);
        Assert.Equal(0, pushes);

        // Two invalidations, one stale read: one background publish, one push.
        cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        _ = await BoardRead(cache, "client-x");
        await WaitForRefreshAsync(cache);
        Assert.Equal(0, pushes);
        time.Advance(TaskIndexCache.BulkChangedDebounce);
        Assert.Equal(1, pushes);

        // A clean read publishes nothing and pushes nothing.
        _ = await BoardRead(cache, "client-x");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, gate.Scans);
        Assert.Equal(1, pushes);

        // The safety TTL rescans in the background, but does not push.
        time.Advance(TimeSpan.FromSeconds(31));
        _ = await BoardRead(cache, "client-x");
        await WaitForRefreshAsync(cache);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, gate.Scans);
        Assert.Equal(1, pushes);

        // The writer waits for its own mutation, so no stale read occurs.
        // The background publish still announces the new generation.
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: "client-x");
        _ = await BoardRead(cache, "client-x");
        time.Advance(TaskIndexCache.BulkChangedDebounce);
        Assert.Equal(2, pushes);
    }

    /// <summary>
    /// Moves a folder's <c>task.json</c> write time clear of the filesystem's
    /// timestamp granule, which is what the scanner requires before it will
    /// memoize the folder at all.
    ///
    /// <para>A write that recent cannot be told apart from the next write of
    /// equal length - both land on the same coarse stamp - so the scanner
    /// deliberately re-parses such a folder instead of trusting the fingerprint
    /// (<c>TaskFolderMemoPolicy</c>, AGT-2867). Back-dating the stamp is the
    /// deterministic form of letting that granule pass, with no sleep and no
    /// dependency on how fast the host runs.</para>
    /// </summary>
    private void SettleWriteTime(string state, string slug)
    {
        var taskJson = Path.Combine(_watchPath, state, slug, "task.json");
        File.SetLastWriteTimeUtc(taskJson, File.GetLastWriteTimeUtc(taskJson).AddMinutes(-5));
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = slug,
                key = slug.ToUpperInvariant(),
                title,
                state,
                order = 1,
                agent = "claude",
                ownerClientId = DefaultClientIdentity.Id,
            }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }
}
