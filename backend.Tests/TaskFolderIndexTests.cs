using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class TaskFolderIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "folder-index-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache _cache;
    private readonly ITestOutputHelper _output;

    public TaskFolderIndexTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "fixture",
            ["WatchPaths:0:Path"] = _root,
            ["TaskIndexCache:ReconciliationSeconds"] = "900",
        }).Build();
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config));
        _cache = NewCache();
    }

    private TaskIndexCache NewCache()
    {
        var cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config, _time);
        _scanner.SetIndexCache(cache);
        return cache;
    }

    private string Write(string lane, string id, string title, string? key = null)
    {
        var folder = Path.Combine(_root, lane, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = key ?? id.ToUpperInvariant(), title, state = lane, order = 1,
            agent = "claude", ownerClientId = DefaultClientIdentity.Id,
        }));
        return folder;
    }

    [Fact]
    public void Events_RefreshOnlyAffectedFolders_AndPreserveDuplicateKeys()
    {
        var first = Write(TaskStates.Ready, "one", "First", "SAME-1");
        Assert.Single(_cache.GetSnapshot());
        Assert.Equal(1, _cache.FullRawScans);

        var second = Write(TaskStates.Ready, "two", "Second", "SAME-1");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External,
            changedPath: Path.Combine(second, "task.json"));
        Assert.Equal(2, _cache.GetSnapshot().Count);
        Assert.Equal(1, _cache.FolderReloads);
        Assert.Equal(2, _cache.GetMatchingCoreFacts("SAME-1").Count);

        Write(TaskStates.Ready, "one", "Edited", "SAME-1");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External,
            changedPath: Path.Combine(first, "task.json"));
        Assert.Contains(_cache.GetSnapshot(), task => task.Title == "Edited");

        var archived = Path.Combine(_root, TaskStates.Archive, "one");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        Directory.Move(first, archived);
        Write(TaskStates.Archive, "one", "Archived", "SAME-1");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External,
            changedPath: archived);
        var (live, archive) = _cache.GetSnapshotPartitions();
        Assert.Single(live);
        Assert.Single(archive);
        Assert.Equal("Archived", archive[0].Title);
        var progressed = Path.Combine(_root, TaskStates.Progress, "two");
        Directory.CreateDirectory(Path.GetDirectoryName(progressed)!);
        Directory.Move(second, progressed);
        Write(TaskStates.Progress, "two", "Progress", "SAME-1");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External,
            changedPath: progressed);
        Assert.Equal(TaskStates.Progress, Assert.Single(_cache.GetSnapshot()).State);

        Write(TaskStates.Progress, "two", "Mutation published", "SAME-1");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        Assert.Equal("Mutation published", Assert.Single(_cache.GetSnapshot()).Title);
        Assert.Equal(1, _cache.FullRawScans);
    }

    [Fact]
    public async Task ThreeThousandCards_OneHourOfPollingAndSweeps_HasNoRawRefresh_AndReconcilesMissedEvent()
    {
        for (var i = 0; i < 3000; i++)
        {
            var folder = Write(TaskStates.Ready, $"card-{i:D4}", $"Title {i:D4}");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "task.json"),
                _time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        }
        var initial = Stopwatch.StartNew();
        Assert.Equal(3000, _scanner.ScanAllJobsWithArchive().Count);
        initial.Stop();
        var rawAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rawWarm = Stopwatch.StartNew();
        Assert.Equal(3000, _scanner.ScanAllJobsRaw().Count);
        rawWarm.Stop();
        var rawAllocated = GC.GetTotalAllocatedBytes(precise: true) - rawAllocationBefore;
        var rawBefore = _cache.FullRawScans;
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var warm = Stopwatch.StartNew();
        for (var tick = 1; tick <= 240; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(3000, _cache.GetSnapshot().Count);
            if (tick % 3 == 0)
                Assert.Equal(3000, _scanner.ScanAllJobsWithArchive().Count);
            if (tick % 60 == 0)
                await WaitForGeneration(_cache, 1 + tick / 60);
        }
        warm.Stop();
        var warmAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        Assert.Equal(rawBefore, _cache.FullRawScans);
        Assert.Equal(0, _cache.FolderReloads);
        var changed = Path.Combine(_root, TaskStates.Ready, "card-0001", "task.json");
        var oldStamp = File.GetLastWriteTimeUtc(changed);
        Write(TaskStates.Ready, "card-0001", "Externally edited title");
        File.SetLastWriteTimeUtc(changed, oldStamp.AddMinutes(1));
        var generation = _cache.Generation;
        _time.Advance(TimeSpan.FromMinutes(15));
        await WaitForGeneration(_cache, generation + 1);
        Assert.Contains(_cache.GetSnapshot(), task => task.Title == "Externally edited title");
        Assert.Equal(1, _cache.FullRawScans);

        var restart = NewCache();
        Assert.Equal(3000, restart.GetSnapshot().Count);
        Assert.Equal(1, restart.FullRawScans);
        _output.WriteLine($"folder-index benchmark: initialMs={initial.Elapsed.TotalMilliseconds:F1} " +
            $"rawWarmMs={rawWarm.Elapsed.TotalMilliseconds:F1} rawWarmAllocatedBytes={rawAllocated} " +
            $"warm240Board80GitMs={warm.Elapsed.TotalMilliseconds:F1} " +
            $"warm240Board80GitAllocatedBytes={warmAllocated} " +
            $"fullRawScansAfterStartup={_cache.FullRawScans - rawBefore}");
    }

    [Fact]
    public void DirectPublicationsRemainInIndexAcrossLaterFolderRefreshes()
    {
        var first = Write(TaskStates.Ready, "one", "First");
        Assert.Single(_cache.GetSnapshot());
        var entry = Assert.Single(_scanner.GetWatchPaths());

        var created = Write(TaskStates.Ready, "two", "Created");
        _cache.PublishCreatedMutation(_scanner.ScanJobFolder(created, entry, TaskStates.Ready,
            bypassMemo: true)!);
        Assert.Equal(2, _cache.GetSnapshot().Count);

        Write(TaskStates.Ready, "two", "Routed");
        _cache.PublishRoutingMutation(_scanner.ScanJobFolder(created, entry, TaskStates.Ready,
            bypassMemo: true)!);
        Assert.Contains(_cache.GetSnapshot(), task => task.Title == "Routed");

        Write(TaskStates.Ready, "one", "Changed");
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External,
            changedPath: Path.Combine(first, "task.json"));
        Assert.Contains(_cache.GetSnapshot(), task => task.Title == "Routed");
        Assert.Equal(2, _cache.GetSnapshot().Count);
        Assert.Equal(1, _cache.FullRawScans);
    }

    private static async Task WaitForGeneration(TaskIndexCache cache, long generation)
    {
        var elapsed = Stopwatch.StartNew();
        while (cache.Generation < generation && elapsed.Elapsed < TimeSpan.FromMinutes(2))
            await Task.Delay(20);
        Assert.True(cache.Generation >= generation);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
