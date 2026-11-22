using Microsoft.Extensions.Configuration;
using AgentStudio.Git;
using AgentStudio.Projects;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationConflictSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    private static IntegrationConflictEntry Row(string id, string card, int day = 0, string target = "develop") =>
        new(1, id, "Project", card, target, "delivery", "base", "tip", Start.AddDays(day).AddHours(1),
            Start, 3600, 2, [new("Host/Program.cs", 2, ["AGT-2"]), new("other.cs", 1, ["AGT-2"])], "parked", Start);

    [Fact]
    public void RatesUseUniqueIntegratedCardsAndPairsCountOnceAcrossFiles()
    {
        var row = Row("one", "AGT-1");
        var summary = ProjectIntegrationConflictSummary.Build("Project", Start, Start.AddDays(2),
            [row, row, Row("two", "AGT-3", 1)],
            [new("AGT-2", "develop", Start.AddHours(2)), new("AGT-2", "develop", Start.AddHours(3)),
             new("AGT-4", "develop", Start.AddDays(1)), new("AGT-5", "develop", Start.AddDays(1)),
             new("AGT-6", "develop", Start.AddDays(1))]).Summary;
        Assert.Equal(2, summary.Conflicts);
        Assert.Equal(4, summary.IntegratedCards);
        Assert.Equal(50d, summary.Per100IntegratedCards);
        Assert.All(summary.TopFiles, file => Assert.Equal(2, file.Count));
        Assert.Equal(2, summary.CardPairs.Count);
        Assert.All(summary.CardPairs, pair => Assert.Equal(1, pair.Count));
        Assert.Equal(100d, summary.Daily[0].Per100IntegratedCards);
        Assert.Equal(100d / 3, summary.Daily[1].Per100IntegratedCards);
        Assert.Equal(summary.Conflicts, summary.Daily.Sum(day => day.Conflicts));
        Assert.Equal(summary.IntegratedCards, summary.Daily.Sum(day => day.IntegratedCards));
    }

    [Fact]
    public void RangeIsHalfOpenTargetIsScopedAndMissingDenominatorIsNull()
    {
        var response = ProjectIntegrationConflictSummary.Build("Project", Start, Start.AddDays(1),
            [Row("one", "AGT-1"), Row("later", "AGT-3", 1), Row("main", "AGT-1", target: "main")],
            [new("old", "develop", Start.AddDays(-1)), new("old", "develop", Start.AddHours(2)),
             new("other", "main", Start.AddHours(1))], "develop");
        Assert.Single(response.Records);
        Assert.Equal(0, response.Summary.IntegratedCards);
        Assert.Null(response.Summary.Per100IntegratedCards);
        Assert.Null(Assert.Single(response.Summary.Daily).Per100IntegratedCards);
    }

    [Fact]
    public void LedgerSurvivesReopeningDeduplicatesStagesAndPreservesDetectionTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "conflict-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new IntegrationConflictLedger(root);
            var row = Row("one", "AGT-1");
            store.Record(row);
            store.Record(row with { DetectedAtUtc = Start.AddHours(9) });
            store.Resolve("Project", "AGT-1", "delivery", "develop", "agent-round");
            var read = Assert.Single(new IntegrationConflictLedger(root).Read("project"));
            Assert.Equal(row.DetectedAtUtc, read.DetectedAtUtc);
            Assert.Equal("agent-round", read.Resolution);
            Assert.Empty(store.Read("Other project"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void IntegrationReceiptsUseRecordedTargetAndIgnoreUnknownPublicationTargets()
    {
        var task = new TaskInfo { Id = "card", Key = "AGT-1" };
        var timeline = new[]
        {
            new TimelineEvent { Kind = TimelineEventKinds.IntegrationSucceeded, Ts = Start.UtcDateTime,
                Details = new() { ["integrationBranch"] = "main" } },
        };
        var lane = new[]
        {
            new IntegrationLaneEvent("Project", "card", 1, Start, "published", Reason: "batch:known"),
            new IntegrationLaneEvent("Project", "card", 2, Start, "published", Reason: "batch:missing"),
        };
        var cards = ProjectIntegrationConflictEndpoints.ReadIntegrations(task, timeline, lane,
            new Dictionary<string, string> { ["known"] = "release" }).ToArray();
        Assert.Equal(["main", "release"], cards.Select(card => card.Target));
    }

    [Fact]
    public void RecoveryStagesCanAddFilesWithoutReplacingFirstObservationOrPreviousResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "conflict-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new IntegrationConflictLedger(root);
            var row = Row("one", "AGT-1") with { Files = [new("first.cs", 2, ["AGT-2"])] };
            store.Record(row);
            store.Resolve("Project", "AGT-1", "delivery", "develop", "union");
            store.Record(row with { Files = [new("first.cs", 1, []), new("second.cs", 1, ["AGT-3"])] });
            var read = Assert.Single(store.Read("Project"));
            Assert.Equal(2, read.Files.Count);
            Assert.Equal(2, read.Files[0].HunkCount);
            Assert.Equal("union", read.Resolution);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReceiptProjectionReusesUnchangedFilesAndRefreshesOnNewIntegration()
    {
        var root = Path.Combine(Path.GetTempPath(), "conflict-receipts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var task = new TaskInfo { Id = "card", Key = "AGT-1", FolderPath = root };
            var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
            var batches = new BatchGateStore(Path.Combine(root, "batches"));
            var cache = new IntegrationReceiptCache();
            timeline.Append(root, new TimelineEvent { Kind = TimelineEventKinds.IntegrationSucceeded,
                Ts = Start.UtcDateTime, Details = new() { ["integrationBranch"] = "develop" } });
            var first = cache.Read(task, timeline, batches);
            Assert.Single(first);
            Assert.Same(first, cache.Read(task, timeline, batches));
            timeline.Append(root, new TimelineEvent { Kind = TimelineEventKinds.IntegrationSucceeded,
                Ts = Start.AddHours(1).UtcDateTime, Details = new() { ["integrationBranch"] = "main" } });
            Assert.Equal(2, cache.Read(task, timeline, batches).Count);
            IntegrationLaneEvents.Append(root, "Project", "card", "published", sequence: 1, reason: "batch:missing");
            Assert.Equal(2, cache.Read(task, timeline, batches).Count);
            Assert.Equal(0, batches.ManifestListings);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LedgerRangeReadFiltersHalfOpenRangeAndTargetAndRefreshesResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "conflict-range-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new IntegrationConflictLedger(root);
            ledger.Record(Row("one", "AGT-1"));
            ledger.Record(Row("later", "AGT-1", 1));
            ledger.Record(Row("main", "AGT-1", target: "main"));
            Assert.Single(ledger.Read("Project", Start, Start.AddDays(1), "develop"));
            ledger.Resolve("Project", "AGT-1", "delivery", "develop", "agent-round");
            Assert.Equal("agent-round", Assert.Single(ledger.Read("Project", Start.AddDays(1), Start.AddDays(2), "develop")).Resolution);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ProjectTaskReadAvoidsTheGlobalDiskWalkAndIncludesArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "conflict-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (project, state) in new[] { ("alpha", TaskStates.Completed), ("alpha", TaskStates.Archive), ("beta", TaskStates.Completed) })
            {
                var folder = Path.Combine(root, project, state, project + state);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
                { title = project, state, fixture = false }));
            }
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "alpha", ["WatchPaths:0:Path"] = Path.Combine(root, "alpha"),
                ["WatchPaths:1:Name"] = "beta", ["WatchPaths:1:Path"] = Path.Combine(root, "beta"),
            }).Build();
            var scanner = new TaskScannerService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SummaryGenerationService>.Instance, config));
            var tasks = scanner.ScanAutomationJobsWithArchive(Path.Combine(root, "alpha"));
            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, task => Assert.Equal("alpha", task.ProjectName));
            Assert.Contains(tasks, task => task.State == TaskStates.Archive);
            Assert.Equal(0, scanner.RawScanCalls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
