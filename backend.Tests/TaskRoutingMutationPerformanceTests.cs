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
}
