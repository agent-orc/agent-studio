using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskCoreCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-core-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WarmDirtyAndArchivedCore_NeverCallScannerOnLookup()
    {
        var live = MakeTask("AGT-1", "2-ready");
        var archive = MakeTask("AGT-2", TaskStates.Archive);
        var scans = 0;
        var cache = Cache(() => { Interlocked.Increment(ref scans); return [live, archive]; });
        cache.GetSnapshot();
        Assert.Equal(1, scans);

        // Sidecars disappear after indexing. A read still returns the complete
        // published record, proving the core lookup does not reopen them.
        File.Delete(Path.Combine(live.FolderPath, "prompt.md"));
        File.Delete(Path.Combine(live.FolderPath, "status.md"));
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        var found = cache.GetCore("AGT-1", _root);
        Assert.Equal("ready", found.Record?.Prompt.State);
        Assert.Equal("status", found.Record?.Status.Text);
        Assert.NotNull(cache.GetCore("AGT-2", _root).Record);
        Assert.Equal(1, scans);
    }

    [Fact]
    public void UnknownIdentity_IsWarmingUntilHydrationProvesMissing()
    {
        var cache = Cache(() => []);
        Assert.True(cache.GetCore("unknown", _root).Warming);
        cache.GetSnapshot();
        var unknown = cache.GetCore("unknown", _root);
        Assert.Null(unknown.Record);
        Assert.False(unknown.Warming);
    }

    [Fact]
    public async Task SafetyTtl_DoesNotStartARequestThreadScan()
    {
        var task = MakeTask("AGT-3", "2-ready");
        var scans = 0;
        var cache = Cache(() => { Interlocked.Increment(ref scans); return [task]; }, 1);
        cache.GetSnapshot();
        await Task.Delay(1100);
        var lookup = cache.GetCore(task.Id, _root);
        Assert.NotNull(lookup.Record);
        Assert.True(lookup.Warming);
        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task ConcurrentMutationPublication_WinsAgainstAnOlderScan()
    {
        var task = MakeTask("AGT-4", "2-ready");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var cache = Cache(() =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                entered.Set();
                release.Wait();
            }
            return [task];
        });
        cache.GetSnapshot();
        cache.Invalidate();
        var refresh = Task.Run(() => cache.GetSnapshot());
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
        cache.PublishCore(task with { Title = "After durable mutation" });
        release.Set();
        await refresh;
        Assert.Equal("After durable mutation", cache.GetCore(task.Id, _root).Record?.Title);
    }

    [Fact]
    public void DeletedIdentity_IsMissingImmediatelyWhileOtherUnknownsWarm()
    {
        var task = MakeTask("AGT-deleted", "2-ready");
        var scans = 0;
        var cache = Cache(() => { Interlocked.Increment(ref scans); return [task]; });
        cache.GetSnapshot();
        cache.Invalidate();
        cache.RemoveCore(task);

        var deleted = cache.GetCore(task.Id, _root);
        Assert.Null(deleted.Record);
        Assert.False(deleted.Warming);
        Assert.Equal(1, scans);
    }

    [Fact]
    public void SidecarHeads_AreUtf8BoundedAndCarryOriginalHashes()
    {
        var task = MakeTask("AGT-5", "2-ready");
        File.WriteAllText(Path.Combine(task.FolderPath, "prompt.md"), string.Concat(Enumerable.Repeat("🧭", 2000)));
        File.WriteAllText(Path.Combine(task.FolderPath, "status.md"), string.Concat(Enumerable.Repeat("é", 1000)));
        var record = TaskCoreRecord.Create(task);
        Assert.True(Encoding.UTF8.GetByteCount(record.Prompt.Text!) <= 2048);
        Assert.True(Encoding.UTF8.GetByteCount(record.Status.Text!) <= 1024);
        Assert.True(record.Prompt.OriginalBytes > 2048);
        Assert.NotNull(record.Prompt.Hash);
        Assert.NotNull(record.Prompt.Cursor);
    }

    private TaskIndexCache Cache(Func<List<TaskInfo>> scan, int ttlSeconds = 3600)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskIndexCache:SafetyTtlSeconds"] = ttlSeconds.ToString() }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, scan);
    }

    private TaskInfo MakeTask(string id, string state)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "prompt");
        File.WriteAllText(Path.Combine(folder, "status.md"), "status");
        return new TaskInfo
        {
            Id = id, TaskKey = TaskIdentity.CreateKey(_root, id), Key = id,
            WatchPath = _root, FolderPath = folder, ProjectName = "PROJ-002",
            Title = id, State = state, EnteredLaneAt = DateTime.UtcNow,
        };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* A queued cold hydration can finish after the test. */ }
    }
}
