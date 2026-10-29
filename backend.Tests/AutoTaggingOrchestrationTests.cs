using System.Diagnostics;
using AgentStudio.Areas;
using AgentStudio.Pipeline;
using AgentStudio.Tags;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
    private readonly RejectableWriter _writer = new();
    private readonly ProjectDocsService _docs;
    private readonly AutoTaggingService _service;
    private readonly ProjectRegistry _registry;
    private readonly WorkspaceArtifactPushQueue _pushQueue = new();
    private readonly TagMaintenanceWorkspace _persistence;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    public AutoTaggingOrchestrationTests()
    {
        var watch = Path.Combine(_root, "project");
        Directory.CreateDirectory(watch);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(watch, ".gitignore"), "/*\n!/docs/\n!/.gitignore\n!/foreign.txt\n");
        File.WriteAllText(Path.Combine(watch, "foreign.txt"), "base");
        Git("add", ".gitignore", "foreign.txt");
        Git("commit", "-q", "-m", "test: initialize checkout");
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
        _registry = registry;
        _mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, fileWriter: _writer);
        new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance).EnsureStateFoldersAndMigrate();
        _settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var areas = new AreaRegistryService(new TagRegistryService(NullLogger<TagRegistryService>.Instance, _config), _settings);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config);
        _docs = new ProjectDocsService(_scanner, registry, NullLogger<ProjectDocsService>.Instance,
            git: git, fileWriter: _writer);
        var catalogue = new WorkbenchCatalogueService(_scanner, registry, git, configuration: _config);
        var repositoryMutations = new ManagedRepositoryMutationService(git, _settings, _pushQueue);
        _persistence = new TagMaintenanceWorkspace(_scanner, _mutations, null!, areas, null!,
            catalogue, new WorkbenchTagService(catalogue, areas, git), _docs, registry, repositoryMutations);
        _workspace.Writer = _persistence.Write;
        _service = new AutoTaggingService(_workspace, _classifier,
            new TagGoldenSetEvaluator(new FakeGoldenClassifier(), _config), _settings, _scanner,
            areas,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance), _config, _clock);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WikiAutoTagCommitsOnlyArticleAndQueuesPush()
    {
        AddWiki("new.md");
        var before = int.Parse(Git("rev-list", "--count", "HEAD"));

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        Assert.Equal("tagged", Assert.Single(report.Items).Status);
        Assert.Equal(before + 1, int.Parse(Git("rev-list", "--count", "HEAD")));
        Assert.Equal("chore(wiki): set tags for docs/new.md", Git("log", "-1", "--format=%s"));
        Assert.Equal("docs/new.md", Git("show", "--pretty=format:", "--name-only", "HEAD"));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
        Assert.True(_pushQueue.Reader.TryRead(out var push));
        Assert.Equal(Git("rev-parse", "HEAD"), push!.Sha);
        Assert.False(_pushQueue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WikiProposalLeavesArticleUnchangedAndRecordsReasonForDirtyCheckout()
    {
        AddWiki("dirty.md");
        var article = Path.Combine(Watch(), "docs", "dirty.md");
        var before = File.ReadAllText(article);
        File.WriteAllText(Path.Combine(Watch(), "foreign.txt"), "foreign change");

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        var proposal = Assert.Single(report.Items);
        Assert.Equal("tags-proposed", proposal.Status);
        Assert.Contains("pre-existing changes", proposal.Reason);
        Assert.Equal(before, File.ReadAllText(article));
        Assert.False(_pushQueue.Reader.TryRead(out _));
        Assert.Equal(proposal.Reason, Assert.Single(_service.Read(Project)).Reason);
    }

    [Fact]
    public async Task LowConfidenceWikiProposalWritesNoFile()
    {
        AddWiki("uncertain.md");
        _classifier.Confidence["uncertain.md"] = 0.4;
        var article = Path.Combine(Watch(), "docs", "uncertain.md");
        var before = File.ReadAllText(article);

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        Assert.Equal("tags-proposed", Assert.Single(report.Items).Status);
        Assert.Equal(before, File.ReadAllText(article));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
        Assert.False(_pushQueue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WikiRepositoryLockKeepsClassificationAsProposal()
    {
        AddWiki("locked.md");
        var article = Path.Combine(Watch(), "docs", "locked.md");
        var before = File.ReadAllText(article);
        File.WriteAllText(Path.Combine(Watch(), ".git", "index.lock"), "held");

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        var proposal = Assert.Single(report.Items);
        Assert.Equal("tags-proposed", proposal.Status);
        Assert.Contains("Repository lock exists", proposal.Reason);
        Assert.Equal(before, File.ReadAllText(article));
        Assert.False(_pushQueue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task MissingCheckoutKeepsClassificationAsProposal()
    {
        AddWiki("missing.md");
        var moved = Path.Combine(_root, "detached-project");
        Directory.Move(Watch(), moved);
        var article = Path.Combine(moved, "docs", "missing.md");
        var before = File.ReadAllText(article);

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        var proposal = Assert.Single(report.Items);
        Assert.Equal("tags-proposed", proposal.Status);
        Assert.Contains("checkout is unavailable", proposal.Reason);
        Assert.Equal(before, File.ReadAllText(article));
        Assert.False(_pushQueue.Reader.TryRead(out _));
    }

    [Fact]
    public void WikiMaintenanceMergeAndRetireEachCommitOnlyTheArticle()
    {
        AddWiki("maintenance.md");
        var empty = TagMaintenancePolicy.Encode(Array.Empty<string>());
        var first = TagMaintenancePolicy.Encode(new[] { "execution-and-runner" });
        var second = TagMaintenancePolicy.Encode(new[] { "observation" });

        Assert.True(_persistence.Write(new("wiki", Project, "maintenance.md", empty, first)));
        Assert.True(_persistence.Write(new("wiki", Project, "maintenance.md", first, second)));
        Assert.True(_persistence.Write(new("wiki", Project, "maintenance.md", second, empty)));

        Assert.Equal("docs/maintenance.md", Git("show", "--pretty=format:", "--name-only", "HEAD"));
        Assert.Equal("chore(wiki): set tags for docs/maintenance.md", Git("log", "-1", "--format=%s"));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
        var pushes = 0;
        while (_pushQueue.Reader.TryRead(out _)) pushes++;
        Assert.Equal(3, pushes);
    }

    [Fact]
    public async Task WikiSourceBranchKeepsClassificationAsProposal()
    {
        AddWiki("source.md");
        var project = _registry.EnsureProjectForStorage(Watch(), Project, DefaultWorkspace.Id);
        _registry.SetWikiSourceBranch(project.Id, "main");
        var article = Path.Combine(Watch(), "docs", "source.md");
        var before = File.ReadAllText(article);

        var report = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);

        var proposal = Assert.Single(report.Items);
        Assert.Equal("tags-proposed", proposal.Status);
        Assert.Contains("Wiki source", proposal.Reason);
        Assert.Equal(before, File.ReadAllText(article));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
        Assert.False(_pushQueue.Reader.TryRead(out _));
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
        Assert.Null(_scanner.FindJob("uncertain", Watch())!.TaggingStatus);
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

    [Theory]
    [InlineData("card")]
    [InlineData("dossier")]
    [InlineData("wiki")]
    public async Task NormalBackfillsLeaveExistingProposalsPendingAndClassifyOnlyNewItems(string kind)
    {
        void Add(string id)
        {
            if (kind == "card") AddCard(id);
            else if (kind == "dossier") AddDossier(id);
            else AddWiki(id + ".md");
        }

        var proposedId = kind == "wiki" ? "proposed.md" : "proposed";
        var freshId = kind == "wiki" ? "fresh.md" : "fresh";
        Add("proposed");
        _classifier.Confidence[proposedId] = 0.4;
        await _service.BackfillAsync(Project, apply: true, CancellationToken.None);
        var first = Assert.Single(_service.Read(Project));
        Assert.Equal("tags-proposed", first.Status);
        Assert.Equal(2, _classifier.Calls.Count);
        Assert.Single(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));

        Add("fresh");
        var dryRun = await _service.BackfillAsync(Project, apply: false, CancellationToken.None);
        Assert.Equal(1, dryRun.Classified);
        Assert.Equal(freshId, Assert.Single(dryRun.Items).Id);
        Assert.DoesNotContain(_classifier.Calls.Skip(2), call => call.Id == proposedId);

        var applied = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.Equal(1, applied.Classified);
        Assert.Equal(freshId, Assert.Single(applied.Items).Id);
        var stillProposed = _service.Read(Project).Single(x => x.Id == proposedId);
        Assert.Equal(first.Status, stillProposed.Status);
        Assert.Equal(first.Tags, stillProposed.Tags);
        Assert.Equal(first.At, stillProposed.At);
        var calls = _classifier.Calls.Count;
        var writes = _workspace.Writes.Count;
        var repeated = await NewService().BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.Equal(0, repeated.Classified);
        Assert.Empty(repeated.Items);
        Assert.Equal(calls, _classifier.Calls.Count);
        Assert.Equal(writes, _workspace.Writes.Count);
        Assert.Equal(2, new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()).Count);
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
    public async Task ClassificationFailureBacksOffForOneDay()
    {
        var worker = NewWorker();
        await worker.ScanOnceAsync(CancellationToken.None);
        AddCard("failed-classification");
        _classifier.Fail = true;

        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Single(_classifier.Calls);
        Assert.Empty(_service.Read(Project));
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Single(_classifier.Calls);
        var restarted = await NewService().BackfillAsync(Project, apply: false, CancellationToken.None);
        Assert.Equal(1, restarted.Deferred);
        Assert.Single(_classifier.Calls);

        _classifier.Fail = false;
        _clock.Advance(TimeSpan.FromHours(24));
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, _classifier.Calls.Count);
        Assert.Equal("tagged", Assert.Single(_service.Read(Project)).Status);
    }

    [Fact]
    public async Task ChangedCardContentClearsFailureBackoff()
    {
        var worker = NewWorker();
        await worker.ScanOnceAsync(CancellationToken.None);
        AddCard("changed-content");
        _classifier.Fail = true;
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Single(_classifier.Calls);

        _classifier.Fail = false;
        _workspace.Items[0] = _workspace.Items[0] with { Text = "Revised card body" };
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, _classifier.Calls.Count);
        Assert.Equal("tagged", Assert.Single(_service.Read(Project)).Status);
    }

    [Fact]
    public async Task HourlyCapDefersRemainingCardsUntilWindowExpires()
    {
        _config["AutoTag:HourlyCallLimit"] = "1";
        var worker = NewWorker();
        await worker.ScanOnceAsync(CancellationToken.None);
        AddCard("first");
        AddCard("second");

        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Single(_classifier.Calls);
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Single(_classifier.Calls);

        _clock.Advance(TimeSpan.FromHours(1));
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, _classifier.Calls.Count);
        Assert.Equal(2, _service.Read(Project).Count);
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

    [Theory]
    [InlineData("card")]
    [InlineData("wiki")]
    public async Task FailedTagWriteDoesNotRecordSuccessOrPreventRetry(string kind)
    {
        if (kind == "card") AddCard("failed-write");
        else AddWiki("failed-write.md");
        _workspace.RejectWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));

        Assert.Empty(_service.Read(Project));
        Assert.Null(_service.ReadReport(Project));
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
        if (kind == "card")
            Assert.Null(_scanner.FindJob("failed-write", Watch())!.TaggingStatus);

        _workspace.RejectWrites = false;
        var retried = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.Single(retried.Items);
        Assert.Equal("tagged", _service.Read(Project).Single().Status);
    }

    [Fact]
    public async Task CardDisappearingDuringApplyDoesNotRecordSuccess()
    {
        AddCard("disappearing");
        var folder = _scanner.FindJob("disappearing", Watch())!.FolderPath;
        _workspace.BeforeWrite = () =>
        {
            Directory.Delete(folder, recursive: true);
            _scanner.InvalidateCache();
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));

        Assert.Empty(_service.Read(Project));
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
    }

    [Theory]
    [InlineData("card", 0.95)]
    [InlineData("wiki-md", 0.95)]
    [InlineData("wiki-html", 0.95)]
    public async Task StorageFailureLeavesTagsAndStatusUnchangedAndRetryCompletes(string kind, double confidence)
    {
        var id = kind == "card" ? "atomic-card" : "atomic-article." + (kind == "wiki-md" ? "md" : "html");
        if (kind == "card") AddCard(id); else AddWiki(id);
        _classifier.Confidence[id] = confidence;
        var path = kind == "card"
            ? Path.Combine(_scanner.FindJob(id, Watch())!.FolderPath, "task.json")
            : Path.Combine(Watch(), "docs", id);
        var before = File.ReadAllText(path);
        _writer.Attempts.Clear();
        _writer.Reject = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(_writer.Attempts);
        Assert.Empty(_workspace.Items.Single().Tags);
        Assert.Empty(_service.Read(Project));
        Assert.Null(_service.ReadReport(Project));
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "logs", "timeline.jsonl")));

        _writer.Reject = false;
        var applied = await _service.BackfillAsync(Project, apply: true, CancellationToken.None);
        var expectedStatus = confidence >= 0.8 ? "tagged" : "tags-proposed";
        Assert.Equal(expectedStatus, Assert.Single(applied.Items).Status);
        Assert.Equal(expectedStatus, Assert.Single(_service.Read(Project)).Status);
        Assert.Equal(2, _writer.Attempts.Count);
        string[] tags;
        if (kind == "card")
        {
            var card = _scanner.FindJob(id, Watch())!;
            Assert.Equal(expectedStatus, card.TaggingStatus);
            tags = [.. card.Tags ?? []];
        }
        else
        {
            var content = _docs.ReadWikiFile(Project, id)!.Content;
            Assert.Equal(expectedStatus, ProjectDocsService.ReadTaggingStatus(content));
            tags = ProjectDocsService.FrontmatterTags(content);
            Assert.Contains("Article body", content);
        }
        Assert.Equal(confidence >= 0.8 ? new[] { "execution-and-runner" } : [], tags);
        Assert.Single(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
    }

    [Fact]
    public void StandaloneCardStatusWriterReportsStorageFailure()
    {
        AddCard("status-write");
        _writer.Reject = true;
        Assert.False(_mutations.SetTaggingStatus("status-write", "tagged", Watch()));
        Assert.Null(_scanner.FindJob("status-write", Watch())!.TaggingStatus);
        _writer.Reject = false;
        Assert.True(_mutations.SetTaggingStatus("status-write", "tagged", Watch()));
        Assert.Equal("tagged", _scanner.FindJob("status-write", Watch())!.TaggingStatus);
    }

    [Theory]
    [InlineData("card", "timeline", 0.95)]
    [InlineData("card", "state", 0.95)]
    [InlineData("card", "activity", 0.95)]
    [InlineData("card", "report", 0.95)]
    [InlineData("wiki", "state", 0.95)]
    [InlineData("wiki", "activity", 0.95)]
    [InlineData("wiki", "report", 0.95)]
    [InlineData("dossier", "state", 0.95)]
    [InlineData("dossier", "activity", 0.95)]
    [InlineData("dossier", "report", 0.95)]
    public async Task PostWriteFailureResumesAfterRestartWithoutReclassificationOrDuplicateEvents(
        string kind, string boundary, double confidence)
    {
        var id = kind == "card" ? "recover-card" : kind == "dossier" ? "recover-dossier" : "recover-wiki.md";
        if (kind == "card") AddCard(id);
        else if (kind == "dossier") AddDossier(id);
        else AddWiki(id);
        _classifier.Confidence[id] = confidence;
        var state = Path.Combine(_root, "auto-tag", TagMaintenancePolicy.Fingerprint(Project) + ".json");
        var task = kind == "card" ? _scanner.FindJob(id, Watch()) : null;
        var blocked = boundary switch
        {
            "timeline" => Path.Combine(task!.FolderPath, "logs", "timeline.jsonl"),
            "activity" => Path.Combine(Watch(), ".orchestrator", "orchestrator.jsonl"),
            "report" => Path.ChangeExtension(state, ".report.json"),
            _ => state,
        };
        _workspace.BeforeWrite = () => Directory.CreateDirectory(blocked);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        Assert.Single(_workspace.Writes);
        var calls = _classifier.Calls.Count;
        Directory.Delete(blocked);
        _workspace.BeforeWrite = null;

        // A fresh coordinator has no in-memory knowledge of the failed call.
        var resumed = NewService();
        var report = await resumed.BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.Equal(calls, _classifier.Calls.Count);
        Assert.Single(report.Items);
        Assert.Equal(confidence < 0.8 ? "tags-proposed" : "tagged", report.Items[0].Status);
        Assert.Equal(report.Items[0].Status, Assert.Single(resumed.Read(Project)).Status);
        Assert.Single(resumed.ReadReport(Project)!.Items);
        Assert.False(resumed.HasPendingApply(Project));
        Assert.Single(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
        if (task != null)
            Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(task.FolderPath));
    }

    [Fact]
    public async Task FailedPendingRecordPreventsEveryItemWrite()
    {
        AddCard("unstarted");
        File.WriteAllText(Path.Combine(_root, "auto-tag"), "blocked directory");
        await Assert.ThrowsAnyAsync<IOException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        Assert.Empty(_workspace.Writes);
        Assert.Null(_scanner.FindJob("unstarted", Watch())!.TaggingStatus);
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
    }

    [Fact]
    public async Task CrashAfterItemReplacementReplaysTheSavedBatchAndPreservesItsReport()
    {
        AddCard("first");
        AddWiki("second.md");
        _workspace.AfterWrite = () => throw new IOException("Crash immediately after replacement.");
        await Assert.ThrowsAsync<IOException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        Assert.Single(_workspace.Writes);
        Assert.Empty(_service.Read(Project));
        Assert.True(_service.HasPendingApply(Project));
        var calls = _classifier.Calls.Count;
        _workspace.AfterWrite = null;
        var resumed = NewService();
        var report = await resumed.BackfillAsync(Project, apply: true, CancellationToken.None);
        Assert.Equal(calls, _classifier.Calls.Count);
        Assert.Equal(2, _workspace.Writes.Count);
        Assert.Equal(2, report.Classified);
        Assert.Equal(2, report.CountsPerArea["execution-and-runner"]);
        Assert.Equal(2, resumed.Read(Project).Count);
        Assert.Equal(2, new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()).Count);
        Assert.False(resumed.HasPendingApply(Project));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreationSweepRecoversPendingBookkeepingAndHonorsOptOut(bool existedAtBaseline)
    {
        if (existedAtBaseline) AddCard("pending");
        await NewWorker().ScanOnceAsync(CancellationToken.None);
        if (!existedAtBaseline) AddCard("pending");
        var blocked = Path.Combine(Watch(), ".orchestrator", "orchestrator.jsonl");
        _workspace.BeforeWrite = () => Directory.CreateDirectory(blocked);
        await Assert.ThrowsAsync<IOException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        var calls = _classifier.Calls.Count;
        _workspace.BeforeWrite = null;
        Directory.Delete(blocked);
        var resumed = NewService();
        var worker = new AutoTagCreationWorker(resumed, _workspace, _scanner, _config,
            NullLogger<AutoTagCreationWorker>.Instance);
        _settings.SetAutoTag(Project, false);
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.True(resumed.HasPendingApply(Project));
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
        _settings.SetAutoTag(Project, true);
        await worker.ScanOnceAsync(CancellationToken.None);
        await worker.ScanOnceAsync(CancellationToken.None);
        Assert.False(resumed.HasPendingApply(Project));
        Assert.Equal(calls, _classifier.Calls.Count);
        Assert.Single(resumed.ReadReport(Project)!.Items);
        Assert.False(resumed.HasPendingApply(Project));
        Assert.Single(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
    }

    [Fact]
    public async Task DryRunLeavesPendingApplyUntouched()
    {
        AddCard("pending");
        var task = _scanner.FindJob("pending", Watch())!;
        var blocked = Path.Combine(task.FolderPath, "logs", "timeline.jsonl");
        _workspace.BeforeWrite = () => Directory.CreateDirectory(blocked);
        await Assert.ThrowsAsync<IOException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        var pending = Path.Combine(_root, "auto-tag", TagMaintenancePolicy.Fingerprint(Project) + ".pending.json");
        var saved = File.ReadAllText(pending);
        _workspace.BeforeWrite = null;
        Directory.Delete(blocked);
        await NewService().BackfillAsync(Project, apply: false, CancellationToken.None);
        Assert.Equal(saved, File.ReadAllText(pending));
        Assert.Empty(_service.Read(Project));
        Assert.Empty(new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(task.FolderPath));
        Assert.Empty(new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(Watch()));
        Assert.Single((await NewService().BackfillAsync(Project, apply: true, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task RecoveryRefusesToOverwriteInterveningTagEdits()
    {
        AddCard("edited");
        _workspace.RejectWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BackfillAsync(Project, apply: true, CancellationToken.None));
        _workspace.RejectWrites = false;
        _mutations.SetJobTags("edited", ["security"], Watch());
        _workspace.Items[0] = _workspace.Items[0] with { Tags = ["security"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().BackfillAsync(Project, apply: true, CancellationToken.None));
        Assert.Equal(["security"], _scanner.FindJob("edited", Watch())!.Tags);
        Assert.Empty(_service.Read(Project));
        Assert.True(_service.HasPendingApply(Project));
        Assert.Single(_classifier.Calls);
    }

    private AutoTaggingService NewService() => new(_workspace, _classifier,
        new TagGoldenSetEvaluator(new FakeGoldenClassifier(), _config), _settings, _scanner,
        new AreaRegistryService(new TagRegistryService(NullLogger<TagRegistryService>.Instance, _config), _settings),
        new TimelineLog(NullLogger<TimelineLog>.Instance),
        new OrchestratorLog(NullLogger<OrchestratorLog>.Instance), _config, _clock);

    private void AddDossier(string id)
    {
        var dir = Path.Combine(Watch(), "docs", "operations", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Classification Dossier</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), TagMaintenancePolicy.Encode(new
        {
            schemaVersion = 1, id, title = "Classification Dossier", summary = "Classification context",
            entrypoint = "index.html", status = "active", phase = "testing", updatedAt = "2026-09-13T10:00:00Z",
        }));
        _workspace.Items.Add(new(Project, "dossier", id, "Classification Dossier", [], "Context", true));
        Git("add", "docs/operations/" + id);
        Git("commit", "-q", "-m", "test: add dossier");
    }

    private void AddWiki(string id)
    {
        Directory.CreateDirectory(Path.Combine(Watch(), "docs"));
        var content = id.EndsWith(".html", StringComparison.Ordinal)
            ? "<!doctype html><html><head><title>Article</title></head><body>Article body</body></html>"
            : "# Article\n\nArticle body";
        File.WriteAllText(Path.Combine(Watch(), "docs", id), content);
        Git("add", "docs/" + id);
        Git("commit", "-q", "-m", "test: add wiki article");
        Assert.True(_docs.PreloadWikiContent(Project));
        _workspace.Items.Add(new(Project, "wiki", id, "Article", [], content, true));
    }

    private sealed class RejectableWriter : IAtomicJsonFileWriter
    {
        private readonly AtomicJsonFileWriter _inner = new();
        public bool Reject { get; set; }
        public List<string> Attempts { get; } = [];
        public void Write(string path, string content) => ReplaceExisting(path, content);
        public void ReplaceExisting(string path, string content)
        {
            Attempts.Add(content);
            // Reject the write containing status. A split tags/status implementation
            // would already have changed the file before reaching this failure.
            if (Reject && (content.Contains("taggingStatus", StringComparison.Ordinal)
                || content.Contains("agent-studio-tagging-status", StringComparison.Ordinal)))
                throw new IOException("Injected status persistence failure.");
            _inner.ReplaceExisting(path, content);
        }
    }

    private AutoTagCreationWorker NewWorker() => new(_service, _workspace, _scanner, _config,
        NullLogger<AutoTagCreationWorker>.Instance);
    private AutoTagBackfillQueue NewQueue() => new(_service, _scanner, _config,
        NullLogger<AutoTagBackfillQueue>.Instance);
    private string Watch() => Path.Combine(_root, "project");
    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Watch(), RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
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
        public bool RejectWrites { get; set; }
        public Action? BeforeWrite { get; set; }
        public Action? AfterWrite { get; set; }
        public Func<TagMaintenanceChange, string?, bool> Writer { get; set; } = null!;
        public IReadOnlyList<string> Projects() => [Project];
        public TagMaintenanceSnapshot Capture(string project) => new([], ["execution-and-runner"],
            new() { ["execution-and-runner"] = [] }, Items.ToList());
        public string CreateCard(string project, TagMaintenanceDecision decision) => throw new NotSupportedException();
        public string Read(TagMaintenanceChange change) => TagMaintenancePolicy.Encode(
            Items.Single(i => i.Kind == change.Kind && i.Id == change.Id).Tags);
        public bool Write(TagMaintenanceChange change, string? taggingStatus = null)
        {
            BeforeWrite?.Invoke();
            if (RejectWrites) return false;
            if (!Writer(change, taggingStatus)) return false;
            Writes.Add(change);
            var index = Items.FindIndex(x => x.Kind == change.Kind && x.Id == change.Id);
            Items[index] = Items[index] with { Tags = System.Text.Json.JsonSerializer.Deserialize<string[]>(change.After)! };
            AfterWrite?.Invoke();
            return true;
        }
    }

    private sealed class FakeClassifier : IAutoTagClassifier
    {
        public bool Fail { get; set; }
        public Dictionary<string, double> Confidence { get; } = new(StringComparer.Ordinal);
        public List<(string Id, string Thinking)> Calls { get; } = [];
        public Task<TagClassificationPrediction> ClassifyAsync(string project, TagMaintenanceItem item,
            TagMaintenanceSnapshot snapshot, string thinkingLevel, CancellationToken ct)
        {
            Calls.Add((item.Id, thinkingLevel));
            if (Fail) throw new InvalidOperationException("Injected classification failure.");
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
