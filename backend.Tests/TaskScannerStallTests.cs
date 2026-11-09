using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskScannerStallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanner-stall-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Blocked_full_scan_uses_at_most_four_workers_and_leaves_health_work_served()
    {
        var watch = Path.Combine(_root, "one");
        for (var i = 0; i < 12; i++) Directory.CreateDirectory(Path.Combine(watch, TaskStates.Backlog, $"job-{i}"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "one", ["WatchPaths:0:Path"] = watch
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        using var release = new ManualResetEventSlim();
        var expectedWorkers = Math.Min(4, Math.Max(1, Environment.ProcessorCount));
        using var entered = new CountdownEvent(expectedWorkers);
        var scanner = new BlockingScanner(config, summary, release, entered);
        var first = Task.Run(scanner.ScanAllJobsRaw);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "four scan workers did not enter");
            var second = Task.Run(scanner.ScanAllJobsRaw);
            var health = Task.Run(() => 200);
            Assert.Equal(200, await health.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.InRange(scanner.MaxConcurrent, 1, 4);
            Assert.Equal(expectedWorkers, scanner.Started);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(12, first.Result.Count);
            Assert.Equal(12, second.Result.Count);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Project_scan_does_not_visit_another_watch_path()
    {
        var one = Path.Combine(_root, "one");
        var two = Path.Combine(_root, "two");
        Directory.CreateDirectory(Path.Combine(one, TaskStates.Backlog, "a"));
        Directory.CreateDirectory(Path.Combine(two, TaskStates.Backlog, "b"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "one", ["WatchPaths:0:Path"] = one,
            ["WatchPaths:1:Name"] = "two", ["WatchPaths:1:Path"] = two
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new BlockingScanner(config, summary, null, null);
        var tasks = scanner.ScanWatchPathRaw(one);
        Assert.Single(tasks);
        Assert.Equal(one, tasks[0].WatchPath);
        Assert.Equal(1, scanner.Started);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class BlockingScanner(
        IConfiguration config, SummaryGenerationService summary,
        ManualResetEventSlim? release, CountdownEvent? entered)
        : TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary)
    {
        private int _active;
        private int _max;
        private int _started;
        public int MaxConcurrent => Volatile.Read(ref _max);
        public int Started => Volatile.Read(ref _started);

        public override TaskInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state, bool bypassMemo = false)
        {
            var active = Interlocked.Increment(ref _active);
            var started = Interlocked.Increment(ref _started);
            int current;
            while ((current = Volatile.Read(ref _max)) < active
                && Interlocked.CompareExchange(ref _max, active, current) != current) { }
            try
            {
                if (entered is not null && started <= entered.InitialCount) entered.Signal();
                release?.Wait(TimeSpan.FromSeconds(30));
                return new TaskInfo
                {
                    Id = Path.GetFileName(jobDir), Key = Path.GetFileName(jobDir),
                    TaskKey = Path.GetFileName(jobDir), Title = "test", State = TaskStates.Backlog,
                    WatchPath = entry.Path, FolderPath = jobDir, ProjectName = entry.Name
                };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
