using AgentStudio.Areas;
using AgentStudio.Tags;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoTaggingOrchestrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "auto-tag-orchestration-" + Guid.NewGuid().ToString("N"));
    private const string Project = "Tag test project";
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ProjectSettingsService _settings;
    private readonly FakeWorkspace _workspace = new();
    private readonly FakeClassifier _classifier = new();
    private readonly AutoTaggingService _service;

    public AutoTaggingOrchestrationTests()
    {
        var watch = Path.Combine(_root, "project");
        Directory.CreateDirectory(watch);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = watch,
            ["TagMaintenance:GoldenSetPath"] = Path.Combine(_root, "no-golden-set.json"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance).EnsureStateFoldersAndMigrate();
        _settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var areas = new AreaRegistryService(new TagRegistryService(NullLogger<TagRegistryService>.Instance, _config), _settings);
        _service = new AutoTaggingService(_workspace, _classifier,
            new TagGoldenSetEvaluator(new FakeGoldenClassifier(), _config), _settings, _scanner,
            _mutations, null!, null!, areas,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance), _config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task BackfillDryRunAndApplyKeepLowConfidenceAsAProposalAndWriteFeed()
    {
        AddCard("accepted");
        AddCard("uncertain");
        _classifier.Confidence["accepted"] = 0.94;
        _classifier.Confidence["uncertain"] = 0.45;

        var dryRun = await _service.BackfillAsync(Project, apply: false, CancellationToken.None);
        Assert.False(dryRun.Applied);
        Assert.Equal(2, dryRun.Classified);
        Assert.Single(dryRun.LowConfidence);
        Assert.Empty(_workspace.Writes);
        Assert.Empty(_service.Read(Project));
        Assert.Null(_scanner.FindJob("accepted", Watch())!.TaggingStatus);

        var applied = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.True(applied.Applied);
        Assert.Equal(2, applied.Classified);
        Assert.Single(_workspace.Writes);
        Assert.Equal("accepted", _workspace.Writes[0].Id);
        Assert.Equal("tagged", _service.Read(Project).Single(x => x.Id == "accepted").Status);
        Assert.Equal("tags-proposed", _service.Read(Project).Single(x => x.Id == "uncertain").Status);
        Assert.Equal("tagged", _scanner.FindJob("accepted", Watch())!.TaggingStatus);
        Assert.Equal("tags-proposed", _scanner.FindJob("uncertain", Watch())!.TaggingStatus);
        var feed = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch());
        Assert.Contains(feed, x => x.Summary.Contains("Auto-tagged card accepted", StringComparison.Ordinal));
        Assert.Contains(feed, x => x.Summary.Contains("Tags proposed for card uncertain", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_scanner.FindJob("accepted", Watch())!.FolderPath,
            "logs", "timeline.jsonl")));
        var cardPath = Path.Combine(_scanner.FindJob("accepted", Watch())!.FolderPath, "task.json");
        var card = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cardPath))!.AsObject();
        card["taggingStatus"] = "future-status";
        File.WriteAllText(cardPath, card.ToJsonString());
        _scanner.InvalidateCache();
        Assert.Null(_scanner.FindJob("accepted", Watch())!.TaggingStatus);
    }

    [Fact]
    public async Task CreationScanRespectsOptOutAndClassifiesNewCardsAfterDurableBaseline()
    {
        AddCard("existing");
        var worker = NewWorker();
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Empty(_classifier.Calls);

        _settings.SetAutoTag(Project, false);
        AddCard("opted-out");
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Empty(_classifier.Calls);

        _settings.SetAutoTag(Project, true);
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Contains(_classifier.Calls, x => x.Id == "opted-out");
        Assert.DoesNotContain(_classifier.Calls, x => x.Id == "existing");
        Assert.Equal("tagged", _scanner.FindJob("opted-out", Watch())!.TaggingStatus);
    }

    [Fact]
    public async Task BackfillQueueResumesAQueuedJobAfterRestart()
    {
        var first = NewQueue();
        var queued = first.Enqueue(Project, apply: false);
        var resumed = NewQueue();
        await resumed.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (resumed.Read(Project, queued.Id)?.Status != "completed")
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, timeout.Token);
            }
            Assert.False(_service.ReadReport(Project)!.Applied);
        }
        finally { await resumed.StopAsync(CancellationToken.None); }
    }

    private AutoTagCreationWorker NewWorker() => new(_service, _workspace, _scanner, _config,
        NullLogger<AutoTagCreationWorker>.Instance);
    private AutoTagBackfillQueue NewQueue() => new(_service, _config,
        NullLogger<AutoTagBackfillQueue>.Instance);
    private string Watch() => Path.Combine(_root, "project");
    private void AddCard(string id)
    {
        var created = _mutations.CreateJob(new CreateTaskRequest
        {
            Id = id, Title = id, WatchPath = Watch(), Agent = "claude",
        });
        Assert.Equal(id, created);
        _workspace.Items.Add(new(Project, "card", id, id, [], "Card body for " + id, true));
    }

    private sealed class FakeWorkspace : ITagMaintenanceWorkspace
    {
        public List<TagMaintenanceItem> Items { get; } = [];
        public List<TagMaintenanceChange> Writes { get; } = [];
        public IReadOnlyList<string> Projects() => [Project];
        public TagMaintenanceSnapshot Capture(string project) => new([], ["execution-and-runner"],
            new() { ["execution-and-runner"] = [] }, Items.ToList());
        public string CreateCard(string project, TagMaintenanceDecision decision) => throw new NotSupportedException();
        public string Read(TagMaintenanceChange change) => throw new NotSupportedException();
        public bool Write(TagMaintenanceChange change)
        {
            Writes.Add(change);
            var index = Items.FindIndex(x => x.Kind == change.Kind && x.Id == change.Id);
            Items[index] = Items[index] with { Tags = ["execution-and-runner"] };
            return true;
        }
    }

    private sealed class FakeClassifier : IAutoTagClassifier
    {
        public Dictionary<string, double> Confidence { get; } = new(StringComparer.Ordinal);
        public List<(string Id, string Thinking)> Calls { get; } = [];
        public Task<TagClassificationPrediction> ClassifyAsync(string project, TagMaintenanceItem item,
            TagMaintenanceSnapshot snapshot, string thinkingLevel, CancellationToken ct)
        {
            Calls.Add((item.Id, thinkingLevel));
            return Task.FromResult(new TagClassificationPrediction
            {
                Kind = item.Kind, Id = item.Id, Tags = ["execution-and-runner"],
                Confidence = Confidence.GetValueOrDefault(item.Id, 0.95),
            });
        }
    }

    private sealed class FakeGoldenClassifier : ITagGoldenSetClassifier
    {
        public string Model => "test";
        public Task<IReadOnlyList<TagClassificationPrediction>> ClassifyAsync(string project,
            IReadOnlyList<TagGoldenSetItem> items, TagMaintenanceSnapshot context,
            string thinkingLevel, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagClassificationPrediction>>([]);
    }
}
