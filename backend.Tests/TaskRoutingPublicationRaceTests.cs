using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3723: a routing write whose folder version moves during its folder
/// read (the watcher echo of the write itself, or a refresh prune) must still
/// publish the durable card, and the next routing write must build on it.
/// The fake clock keeps the 250 ms watcher worker from repairing the facts,
/// so every assertion sees only what the routing write itself published.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class TaskRoutingPublicationRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-routing-race-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WatcherEventDuringRoutingRead_StillPublishesTheWrite()
    {
        var fixture = CreateFixture("""
            {"id":"routing","title":"Routing","state":"2-ready","order":10,"thinkingLevel":"high"}
            """);
        File.WriteAllText(fixture.TaskPath,
            """{"id":"routing","title":"Routing","state":"2-ready","order":10,"thinkingLevel":"medium"}""");
        var reads = 0;

        fixture.Cache.PublishRoutingFromFolder(fixture.Folder, () =>
        {
            if (Interlocked.Increment(ref reads) == 1) fixture.Cache.NotifyCoreFileChanged(fixture.TaskPath);
            return fixture.Scanner.ScanJobFolder(fixture.Folder, fixture.Entry, TaskStates.Ready, bypassMemo: true);
        });

        Assert.Equal("medium", Assert.Single(fixture.Scanner.ScanAllJobs()).ThinkingLevel);
        Assert.Equal("medium", Assert.Single(fixture.Scanner.GetLiveSnapshotWithReferenceIndex().Live).ThinkingLevel);
        Assert.Equal("medium", fixture.Cache.GetCore("routing", fixture.WatchPath).Record?.ThinkingLevel);
        Assert.Equal("medium", fixture.Cache.GetDetailFacts("routing", fixture.WatchPath).Info?.ThinkingLevel);
        Assert.True(fixture.Cache.IsCurrentTaskJson(fixture.TaskPath));
        Assert.Equal(1, fixture.Scans);
    }

    [Fact]
    public void CliTypeThenModelWrite_WithWatcherEventDuringTheRead_KeepsBothAndTheClaimSeesThem()
    {
        var fixture = CreateFixture($$"""
            {"id":"routing","title":"Routing","state":"2-ready","order":10,
             "agent":"claude","cliType":"claude","model":"{{ModelIds.ClaudeOpus55}}","thinkingLevel":"high"}
            """);
        var mutations = Mutations(fixture);

        // The raw watcher event of the cli-type write's own task.json rename
        // arrives while its routing publication reads the folder.
        fixture.Scanner.RaiseWatcherEventOnNextRead();
        Assert.True(mutations.SetJobCliType("routing", CliTypes.Codex, fixture.WatchPath));
        Assert.Equal(0, fixture.Scanner.PendingWatcherEvents);
        var claimAfterCli = Assert.Single(fixture.Scanner.GetLiveSnapshotWithReferenceIndex().Live);
        Assert.Equal(CliTypes.Codex, claimAfterCli.CliType);

        Assert.True(mutations.SetJobModel("routing", ModelIds.Gpt6Sol, fixture.WatchPath));

        using (var persisted = JsonDocument.Parse(File.ReadAllText(fixture.TaskPath)))
        {
            Assert.Equal(CliTypes.Codex, persisted.RootElement.GetProperty("cliType").GetString());
            Assert.Equal(ModelIds.Gpt6Sol, persisted.RootElement.GetProperty("model").GetString());
        }
        var claim = Assert.Single(fixture.Scanner.GetLiveSnapshotWithReferenceIndex().Live);
        Assert.Equal(CliTypes.Codex, claim.CliType);
        Assert.Equal(ModelIds.Gpt6Sol, claim.Model);
        var core = fixture.Cache.GetCore("routing", fixture.WatchPath).Record;
        Assert.Equal(CliTypes.Codex, core?.CliType);
        Assert.Equal(ModelIds.Gpt6Sol, core?.Model);
        Assert.Equal(1, fixture.Scans);
    }

    [Fact]
    public void ThinkingLevelWriteAfterAnUnpublishedCliWrite_ReadsTheFolder()
    {
        var fixture = CreateFixture($$"""
            {"id":"routing","title":"Routing","state":"2-ready","order":10,
             "agent":"claude","cliType":"claude","model":"{{ModelIds.ClaudeOpus55}}","thinkingLevel":"high"}
            """);
        var mutations = Mutations(fixture);
        // A durable write the resident fact has not absorbed yet, as an
        // operator script or a skipped publication leaves it.
        File.WriteAllText(fixture.TaskPath, $$"""
            {"id":"routing","title":"Routing","state":"2-ready","order":10,
             "agent":"codex","cliType":"codex","model":"{{ModelIds.Gpt6Sol}}","thinkingLevel":"high"}
            """);
        Assert.Equal(CliTypes.Claude, fixture.Cache.GetDetailFacts("routing", fixture.WatchPath).Info?.CliType);

        Assert.True(mutations.SetJobModel("routing", ModelIds.Gpt6Sol, fixture.WatchPath));
        Assert.True(mutations.SetJobThinkingLevel("routing", "medium", fixture.WatchPath));

        using var persisted = JsonDocument.Parse(File.ReadAllText(fixture.TaskPath));
        Assert.Equal(CliTypes.Codex, persisted.RootElement.GetProperty("cliType").GetString());
        Assert.Equal(ModelIds.Gpt6Sol, persisted.RootElement.GetProperty("model").GetString());
        Assert.Equal("medium", persisted.RootElement.GetProperty("thinkingLevel").GetString());
        var claim = Assert.Single(fixture.Scanner.GetLiveSnapshotWithReferenceIndex().Live);
        Assert.Equal(CliTypes.Codex, claim.CliType);
        Assert.Equal(ModelIds.Gpt6Sol, claim.Model);
        Assert.Equal("medium", claim.ThinkingLevel);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Fixture CreateFixture(string taskJson)
    {
        var watchPath = Path.Combine(_root, "project");
        var folder = Path.Combine(watchPath, TaskStates.Ready, "routing");
        Directory.CreateDirectory(folder);
        var taskPath = Path.Combine(folder, "task.json");
        File.WriteAllText(taskPath, taskJson);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "project",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        }).Build();
        var scanner = new WatcherRaceScanner(config);
        var fixture = new Fixture(config, scanner, watchPath, folder, taskPath);
        fixture.Cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () =>
        {
            fixture.Scans++;
            return scanner.ScanAllJobsRaw();
        }, timeProvider: new FakeTimeProvider());
        scanner.Cache = fixture.Cache;
        scanner.SetIndexCache(fixture.Cache);
        Assert.Single(scanner.ScanAllJobs());
        Assert.Equal(1, fixture.Scans);
        return fixture;
    }

    private static TaskMutationService Mutations(Fixture fixture) => new(fixture.Scanner,
        new ClientIdentityStore(fixture.Config, NullLogger<ClientIdentityStore>.Instance),
        new ProjectRegistry(fixture.Config, NullLogger<ProjectRegistry>.Instance),
        new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
        NullLogger<TaskMutationService>.Instance);

    private sealed class Fixture(IConfiguration config, WatcherRaceScanner scanner,
        string watchPath, string folder, string taskPath)
    {
        public IConfiguration Config { get; } = config;
        public WatcherRaceScanner Scanner { get; } = scanner;
        public TaskIndexCache Cache { get; set; } = null!;
        public string WatchPath { get; } = watchPath;
        public string Folder { get; } = folder;
        public string TaskPath { get; } = taskPath;
        public WatchPathEntry Entry => new() { Name = "project", Path = WatchPath };
        public int Scans { get; set; }
    }

    /// <summary>Delivers a task.json watcher event in the middle of the next folder read.</summary>
    private sealed class WatcherRaceScanner(IConfiguration config)
        : TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config))
    {
        private int _pending;
        public TaskIndexCache? Cache { get; set; }
        public int PendingWatcherEvents => Volatile.Read(ref _pending);

        public void RaiseWatcherEventOnNextRead() => Interlocked.Exchange(ref _pending, 1);

        public override TaskInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state,
            bool bypassMemo = false)
        {
            if (Interlocked.Exchange(ref _pending, 0) == 1)
                Cache!.NotifyCoreFileChanged(Path.Combine(jobDir, "task.json"));
            return base.ScanJobFolder(jobDir, entry, state, bypassMemo);
        }
    }
}
