using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskWatcherServiceTests : IDisposable
{
    private readonly string _watchPath;
    private readonly TaskWatcherService _watcher;

    public TaskWatcherServiceTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchPath);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "watcher-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskWatcher:DebounceMs"] = "80",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _watcher = new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, config);
    }

    [Fact]
    public void TaskJsonHeartbeatOnlyChange_DoesNotInvalidateIndexAgain()
    {
        var taskJson = WriteTaskJson("original", DateTime.UtcNow);

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));

        WriteTaskJson("original", DateTime.UtcNow.AddSeconds(5));

        Assert.False(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));
    }

    [Fact]
    public void TaskJsonContextUsageOnlyChange_DoesNotInvalidateIndexAgain()
    {
        var taskJson = WriteTaskJson("original", DateTime.UnixEpoch);
        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(taskJson))!;
        for (var flush = 0; flush < 20; flush++)
        {
            metadata["contextUsage"] = new { at = DateTime.UnixEpoch.AddSeconds(flush), tokens = flush };
            File.WriteAllText(taskJson, JsonSerializer.Serialize(metadata));
            Assert.False(_watcher.ShouldNotifyIndexChange(
                _watchPath, taskJson, WatcherChangeTypes.Changed));
        }

        metadata["title"] = "renamed during telemetry";
        File.WriteAllText(taskJson, JsonSerializer.Serialize(metadata));
        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));
    }

    [Fact]
    public void TaskJsonBoardFieldChange_InvalidatesIndex()
    {
        var taskJson = WriteTaskJson("original", DateTime.UtcNow);
        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));

        WriteTaskJson("renamed", DateTime.UtcNow.AddSeconds(5));

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));
    }

    [Theory]
    [InlineData("logs/cli-output.log")]
    [InlineData("results/report.md")]
    [InlineData("pipeline-execution.json")]
    [InlineData("post-processing-outcome.json")]
    public void GeneratedSidecarChange_DoesNotInvalidateTaskIndex(string relativePath)
    {
        var path = Path.Combine(_watchPath, TaskStates.Progress, "demo", relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.False(_watcher.ShouldNotifyIndexChange(
            _watchPath, path, WatcherChangeTypes.Changed));
    }

    [Fact]
    public void ExternalLegacyTaskFolderMove_IsIndexRelevant()
    {
        var oldPath = Path.Combine(_watchPath, TaskStates.Progress, "demo");
        var newPath = Path.Combine(_watchPath, TaskStates.Ready, "demo");

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, newPath, WatcherChangeTypes.Renamed, oldPath));
    }

    [Fact]
    public void ExternalFlatTaskFolderDelete_IsIndexRelevant()
    {
        var deletedPath = Path.Combine(_watchPath, TaskStorageLayout.JobsDirName, "000", "ATP-42");

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, deletedPath, WatcherChangeTypes.Deleted));
    }

    // MachineBound 19.07.: Quiet-Window-Timing (Delay/Stopwatch) flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task RelevantBurst_DispatchesOnceAfterTheQuietWindow()
    {
        var fired = 0;
        var dispatched = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.OnJobChanged += path =>
        {
            Interlocked.Increment(ref fired);
            dispatched.TrySetResult(path);
        };

        var taskJson = WriteTaskJson("original", DateTime.UtcNow);
        _watcher.HandleChange(_watchPath, taskJson, WatcherChangeTypes.Changed);
        await Task.Delay(30);
        WriteTaskJson("renamed", DateTime.UtcNow.AddSeconds(1));
        var quietWindow = System.Diagnostics.Stopwatch.StartNew();
        _watcher.HandleChange(_watchPath, taskJson, WatcherChangeTypes.Changed);

        Assert.Equal(0, Volatile.Read(ref fired));
        Assert.Equal(taskJson, await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(quietWindow.Elapsed >= TimeSpan.FromMilliseconds(60));
        await Task.Delay(120);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task DocsFileSystemWatcher_DispatchesDebouncedProjectEvent()
    {
        var docsDir = Path.Combine(_watchPath, "docs");
        Directory.CreateDirectory(docsDir);
        var dispatched = new TaskCompletionSource<(string Project, string Path)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.OnWikiChanged += (project, path) => dispatched.TrySetResult((project, path));

        Assert.True(_watcher.EnsureWatching(new WatchPathEntry
        {
            Name = "watcher-test",
            Path = _watchPath,
            RootPath = _watchPath,
        }));

        var page = Path.Combine(docsDir, "changed.md");
        File.WriteAllText(page, "# Changed\n");

        var observed = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("watcher-test", observed.Project);
        Assert.Equal(page, observed.Path);
    }

    [Fact]
    public async Task Dispose_CancelsPendingDispatch()
    {
        var fired = 0;
        _watcher.OnJobChanged += _ => Interlocked.Increment(ref fired);
        var taskJson = WriteTaskJson("original", DateTime.UtcNow);
        _watcher.HandleChange(_watchPath, taskJson, WatcherChangeTypes.Changed);

        _watcher.Dispose();
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref fired));
    }

    private string WriteTaskJson(string title, DateTime lastProgressAt)
    {
        var directory = Path.Combine(_watchPath, TaskStates.Progress, "demo");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            id = "demo",
            title,
            state = TaskStates.Progress,
            order = 1,
            lastProgressAt = lastProgressAt.ToString("O"),
        }));
        return path;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }
}
