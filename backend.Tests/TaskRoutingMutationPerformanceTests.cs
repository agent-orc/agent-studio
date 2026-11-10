using System.Text.Json;
using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class TaskRoutingMutationPerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-routing-perf-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public TaskRoutingMutationPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RoutingWrites_UseOneAtomicReplacementAndNoWorkspaceRescanOrGitProcess()
    {
        var watchPath = Path.Combine(_root, "project");
        var folder = Path.Combine(watchPath, TaskStates.Ready, "routing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """
            {"id":"routing","title":"Routing","state":"2-ready","order":10,
             "agent":"codex","cliType":"codex","model":"gpt-5.5","thinkingLevel":"high"}
            """);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var scans = 0;
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () =>
        {
            scans++;
            return scanner.ScanAllJobsRaw();
        });
        scanner.SetIndexCache(cache);
        Assert.Single(scanner.ScanAllJobs());
        Assert.Equal(1, scans);

        var writer = new CountingWriter();
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, fileWriter: writer);
        using var git = GitProcessTelemetry.BeginRequest("routing-test", NullLogger.Instance);

        Assert.True(Timed("model", () => mutations.SetJobModel("routing", ModelIds.Gpt55, watchPath)));
        Assert.Equal(1, writer.Replacements);
        Assert.Equal(1, scans);
        Assert.Equal(ModelIds.Gpt55, Assert.Single(scanner.ScanAllJobs()).Model);
        Assert.Equal(ModelIds.Gpt55, cache.GetCore("routing", watchPath).Record?.Model);

        Assert.True(Timed("thinking-level", () =>
            mutations.SetJobThinkingLevel("routing", "medium", watchPath)));
        Assert.Equal(2, writer.Replacements);
        Assert.Equal(1, scans);
        Assert.Equal("medium", Assert.Single(scanner.ScanAllJobs()).ThinkingLevel);
        Assert.Equal("medium", cache.GetCore("routing", watchPath).Record?.ThinkingLevel);

        Assert.True(Timed("cli-type", () => mutations.SetJobCliType("routing", "claude", watchPath,
            useOwnSession: true)));
        Assert.Equal(3, writer.Replacements);
        Assert.Equal(1, scans);
        var card = Assert.Single(scanner.ScanAllJobs());
        Assert.Equal("claude", card.CliType);
        Assert.Equal("claude", card.Agent);
        Assert.Equal("claude", cache.GetCore("routing", watchPath).Record?.CliType);
        var taskPath = Path.Combine(folder, "task.json");
        Assert.True(cache.IsCurrentTaskJson(taskPath));
        using var persisted = JsonDocument.Parse(File.ReadAllText(taskPath));
        Assert.True(persisted.RootElement.GetProperty("useOwnSession").GetBoolean());
        Assert.Equal(0, GitProcessTelemetry.CurrentTally()?.Spawns);

        File.AppendAllText(taskPath, " ");
        Assert.False(cache.IsCurrentTaskJson(taskPath));
    }

    [Fact]
    public async Task ConcurrentRoutingWrites_PublishTheLastDurableCardToBoardAndDetail()
    {
        var watchPath = Path.Combine(_root, "concurrent-project");
        var folder = Path.Combine(watchPath, TaskStates.Ready, "routing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            """{"id":"routing","title":"Routing","state":"2-ready","order":10,"cliType":"codex","model":"gpt-5.5","thinkingLevel":"high"}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "concurrent-project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var scans = 0;
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () =>
        {
            Interlocked.Increment(ref scans);
            return scanner.ScanAllJobsRaw();
        });
        scanner.SetIndexCache(cache);
        var original = Assert.Single(scanner.ScanAllJobs());
        var initialGeneration = cache.Generation;
        var writer = new BlockingWriter();
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, fileWriter: writer);
        using var git = GitProcessTelemetry.BeginRequest("routing-concurrent-test", NullLogger.Instance);

        var first = Task.Run(() => mutations.SetJobThinkingLevel("routing", "medium", watchPath));
        try
        {
            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(10)));
            var second = Task.Run(() => mutations.SetJobCliType("routing", "claude", watchPath));
            writer.Release.Set();
            Assert.True(await first);
            Assert.True(await second);
        }
        finally { writer.Release.Set(); }

        var board = Assert.Single(scanner.ScanAllJobs());
        var detail = cache.GetDetailFacts("routing", watchPath).Info;
        var core = cache.GetCore("routing", watchPath).Record;
        Assert.NotNull(detail);
        Assert.NotNull(core);
        Assert.Equal("claude", board.CliType);
        Assert.Equal(board.CliType, detail.CliType);
        Assert.Equal(board.CliType, core.CliType);
        Assert.Equal(board.ThinkingLevel, detail.ThinkingLevel);
        Assert.Equal(board.ThinkingLevel, core.ThinkingLevel);
        using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        Assert.Equal(persisted.RootElement.GetProperty("cliType").GetString(), board.CliType);
        Assert.Equal(persisted.RootElement.GetProperty("thinkingLevel").GetString(), board.ThinkingLevel);
        Assert.Equal(TaskGitSignature.For(original), TaskGitSignature.For(board));
        Assert.Equal(initialGeneration + 2, cache.Generation);
        Assert.Equal(1, scans);
        Assert.Equal(2, writer.Replacements);
        Assert.Equal(0, GitProcessTelemetry.CurrentTally()?.Spawns);
    }

    [Fact]
    public async Task SlowRoutingFolderRead_DoesNotBlockOtherPublicationsOrReplaceANewerCard()
    {
        var watchPath = Path.Combine(_root, "publication-project");
        var firstFolder = Path.Combine(watchPath, TaskStates.Ready, "first");
        var secondFolder = Path.Combine(watchPath, TaskStates.Ready, "second");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);
        File.WriteAllText(Path.Combine(firstFolder, "task.json"),
            """{"id":"first","title":"First","state":"2-ready","order":10,"thinkingLevel":"high"}""");
        File.WriteAllText(Path.Combine(secondFolder, "task.json"),
            """{"id":"second","title":"Second","state":"2-ready","order":20}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "publication-project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
            scanner.ScanAllJobsRaw);
        scanner.SetIndexCache(cache);
        var initial = scanner.ScanAllJobs();
        var oldFirst = Assert.Single(initial, task => task.Id == "first");
        var second = Assert.Single(initial, task => task.Id == "second");
        using var readEntered = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var slowRead = Task.Run(() => cache.PublishRoutingFromFolder(firstFolder, () =>
        {
            readEntered.Set();
            if (!releaseRead.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Routing folder read was not released");
            return oldFirst;
        }));
        try
        {
            Assert.True(readEntered.Wait(TimeSpan.FromSeconds(10)));
            var unrelated = Task.Run(() => cache.PublishRoutingMutation(second));
            await unrelated.WaitAsync(TimeSpan.FromSeconds(3));

            File.WriteAllText(Path.Combine(firstFolder, "task.json"),
                """{"id":"first","title":"First","state":"2-ready","order":10,"thinkingLevel":"medium"}""");
            var newer = scanner.ScanJobFolder(firstFolder,
                new WatchPathEntry { Name = "publication-project", Path = watchPath },
                TaskStates.Ready, bypassMemo: true)!;
            cache.PublishRoutingMutation(newer);
        }
        finally
        {
            releaseRead.Set();
            await slowRead;
        }
        Assert.Equal("medium", Assert.Single(scanner.ScanAllJobs(), task => task.Id == "first").ThinkingLevel);
        Assert.Equal("medium", cache.GetCore("first", watchPath).Record?.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_PublishesIntoWarmBoardWithoutAnotherWorkspaceScan()
    {
        var watchPath = Path.Combine(_root, "create-project");
        Directory.CreateDirectory(watchPath);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "create-project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var scans = 0;
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () =>
        {
            scans++;
            return scanner.ScanAllJobsRaw();
        });
        scanner.SetIndexCache(cache);
        Assert.Empty(scanner.ScanAllJobs());
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        scanner.SetStatsMetadataCache(stats);
        Assert.Empty(stats.AllJobs());
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);

        Assert.Equal("created", Timed("create-first", () => mutations.CreateJob(new CreateTaskRequest
            {
                Id = "created", Title = "Created", WatchPath = watchPath,
            })));

        Assert.Equal("created-two", Timed("create-warm", () => mutations.CreateJob(new CreateTaskRequest
            {
                Id = "created-two", Title = "Created two", WatchPath = watchPath,
            })));
        Assert.Equal(2, scanner.ScanAllJobs().Count);
        Assert.Equal(1, scans);
        Assert.Equal(2, stats.AllJobs().Count);
    }

    [Fact]
    public void DirectPublication_DoesNotHideAnEarlierMutationInvalidation()
    {
        var watchPath = Path.Combine(_root, "invalidation-project");
        var firstFolder = Path.Combine(watchPath, TaskStates.Ready, "first");
        Directory.CreateDirectory(firstFolder);
        File.WriteAllText(Path.Combine(firstFolder, "task.json"),
            """{"id":"first","title":"First","state":"2-ready","order":10}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "invalidation-project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        using var refreshEntered = new ManualResetEventSlim();
        using var releaseRefresh = new ManualResetEventSlim();
        var holdRefresh = false;
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
            scanner.ScanAllJobsRaw,
            beforeRefreshGenerationCapture: () =>
            {
                if (!holdRefresh) return;
                refreshEntered.Set();
                releaseRefresh.Wait(TimeSpan.FromSeconds(10));
            });
        scanner.SetIndexCache(cache);
        Assert.Single(scanner.ScanAllJobs());

        var secondFolder = Path.Combine(watchPath, TaskStates.Ready, "second");
        Directory.CreateDirectory(secondFolder);
        File.WriteAllText(Path.Combine(secondFolder, "task.json"),
            """{"id":"second","title":"Second","state":"2-ready","order":20}""");
        holdRefresh = true;
        cache.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        cache.TryAcquireBoardRead();
        try
        {
            Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(10)));
            var first = scanner.ScanJobFolder(firstFolder,
                new WatchPathEntry { Name = "invalidation-project", Path = watchPath },
                TaskStates.Ready, bypassMemo: true)!;
            cache.PublishRoutingMutation(first);
            var read = Task.Run(scanner.ScanAllJobs);
            Assert.False(read.Wait(TimeSpan.FromMilliseconds(500)),
                "The read must wait for the earlier invalidation's workspace reconciliation.");
            releaseRefresh.Set();
            Assert.Contains(read.GetAwaiter().GetResult(), task => task.Id == "second");
        }
        finally { releaseRefresh.Set(); }
    }

    [Fact]
    public void WatcherMarkerDuringScan_DoesNotEraseTheNewCard()
    {
        var watchPath = Path.Combine(_root, "watcher-project");
        var firstFolder = Path.Combine(watchPath, TaskStates.Ready, "first");
        Directory.CreateDirectory(firstFolder);
        File.WriteAllText(Path.Combine(firstFolder, "task.json"),
            """{"id":"first","title":"First","state":"2-ready","order":10}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "watcher-project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        TaskIndexCache? cache = null;
        string? pendingPath = null;
        cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () =>
        {
            var fresh = scanner.ScanAllJobsRaw();
            if (pendingPath is { } path)
            {
                pendingPath = null;
                cache!.NotifyCoreFileChanged(path);
            }
            return fresh;
        });
        scanner.SetIndexCache(cache);
        Assert.Single(scanner.ScanAllJobs());

        var secondFolder = Path.Combine(watchPath, TaskStates.Ready, "second");
        Directory.CreateDirectory(secondFolder);
        var secondTaskPath = Path.Combine(secondFolder, "task.json");
        File.WriteAllText(secondTaskPath,
            """{"id":"second","title":"Second","state":"2-ready","order":20}""");
        pendingPath = secondTaskPath;
        Assert.Equal(2, cache.ForceRefresh());
        Assert.Contains(scanner.ScanAllJobs(), task => task.Id == "second");
        Task.Delay(300).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private T Timed<T>(string name, Func<T> action)
    {
        var timer = Stopwatch.StartNew();
        var trace = TaskSwitchTrace.Begin(null, null);
        try
        {
            var result = action();
            _output.WriteLine("{0} elapsedMs={1:0.###} trace={2}", name,
                timer.Elapsed.TotalMilliseconds,
                trace.Summary("ok", 200, 0, GitProcessTelemetry.CurrentTally()));
            return result;
        }
        finally { trace.Restore(); }
    }

    private sealed class CountingWriter : IAtomicJsonFileWriter
    {
        private readonly AtomicJsonFileWriter _writer = new();
        public int Replacements { get; private set; }
        public void Write(string path, string content) => _writer.Write(path, content);
        public void ReplaceExisting(string path, string content)
        {
            Replacements++;
            _writer.ReplaceExisting(path, content);
        }
    }

    private sealed class BlockingWriter : IAtomicJsonFileWriter
    {
        private readonly AtomicJsonFileWriter _writer = new();
        private int _replacements;
        public int Replacements => Volatile.Read(ref _replacements);
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Write(string path, string content) => _writer.Write(path, content);
        public void ReplaceExisting(string path, string content)
        {
            _writer.ReplaceExisting(path, content);
            if (Interlocked.Increment(ref _replacements) == 1)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("First routing write was not released");
            }
        }
    }
}
