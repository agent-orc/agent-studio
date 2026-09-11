using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "cache-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "1", // tight TTL for the safety test
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(_cache);
    }

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

    [Fact]
    public void SafetyTtl_PicksUpExternalChanges_EvenWithoutInvalidation()
    {
        WriteJob("2-ready", "job-1", "First");
        var firstScan = _scanner.ScanAllJobs();
        Assert.Single(firstScan);

        WriteJob("2-ready", "job-2", "Second");
        // No invalidation (simulating a missed FileSystemWatcher event).
        // Wait past the configured 1 s safety TTL.
        Thread.Sleep(1200);

        var fresh = _scanner.ScanAllJobs();
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
