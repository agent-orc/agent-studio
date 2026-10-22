using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Completion-backfill sidecar (<c>.metadata/cycle-time-backfill.json</c>):
/// tolerant parsing, completion-source precedence (ledger, lane-entry, then
/// backfill), the lead-time-only shape of a backfilled row, aggregate and
/// coverage separation, and the file-backed service path including sidecar
/// re-generation without a restart.
/// </summary>
public sealed class CycleTimeBackfillTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cycle-time-backfill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- sidecar parsing ----------------------------------------------------

    [Fact]
    public void Parse_ReadsEntries_NormalizesToUtc_AndDefaultsConfidence()
    {
        var entries = CycleTimeBackfillSidecar.Parse("""
            {
              "version": 1,
              "generatedAt": "2026-08-25T10:00:00Z",
              "entries": {
                "ASS-324": { "completedAt": "2026-05-11T14:30:00+02:00", "source": "git-archive-move", "confidence": "medium", "commit": "478f0000" },
                "ASS-325": { "completedAt": "2026-05-12T09:00:00Z", "source": "status-mtime" }
              }
            }
            """);

        Assert.Equal(2, entries.Count);
        var a = entries["ass-324"]; // key lookup is case-insensitive
        Assert.Equal(new DateTime(2026, 5, 11, 12, 30, 0), a.CompletedAt);
        Assert.Equal("git-archive-move", a.Source);
        Assert.Equal("medium", a.Confidence);
        Assert.Equal("478f0000", a.Commit);
        Assert.Equal("low", entries["ASS-325"].Confidence);
        Assert.Null(entries["ASS-325"].Commit);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"entries\":[]}")]
    public void Parse_InvalidDocument_YieldsNoEntries(string json)
    {
        Assert.Empty(CycleTimeBackfillSidecar.Parse(json));
    }

    [Fact]
    public void Parse_SkipsInvalidEntries_KeepsValidOnes()
    {
        var entries = CycleTimeBackfillSidecar.Parse("""
            {
              "entries": {
                "BAD-DATE": { "completedAt": "yesterday-ish", "source": "status-mtime" },
                "NO-SOURCE": { "completedAt": "2026-05-12T09:00:00Z" },
                "NOT-OBJECT": 42,
                "GOOD-1": { "completedAt": "2026-05-12T09:00:00Z", "source": "task-entered-lane", "confidence": "medium" }
              }
            }
            """);

        Assert.Single(entries);
        Assert.Equal("task-entered-lane", entries["GOOD-1"].Source);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(CycleTimeBackfillSidecar.Load(Path.Combine(_root, "nope", "cycle-time-backfill.json")));
    }

    // ---- completion-source precedence --------------------------------------

    private static readonly CycleTimeBackfillEntry ArchiveMove =
        new(T0.AddDays(3), "git-archive-move", "medium", "abc123");

    [Fact]
    public void Analyze_LedgerCompletion_IgnoresBackfill()
    {
        var task = Task("with-ledger", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddHours(3));
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddHours(2), TaskStates.HumanReview, TaskStates.Completed),
            Lane(T0.AddHours(3), TaskStates.Completed, TaskStates.Archive),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, null, ArchiveMove).Row;

        Assert.NotNull(row);
        Assert.Equal(TaskCycleTimeAnalyzer.LedgerCompletionSource, row!.CompletionSource);
        Assert.Equal(T0.AddHours(2), row.CompletedAt);
        Assert.NotNull(row.Stages);
        Assert.DoesNotContain(row.DataGaps, g => g.StartsWith(TaskCycleTimeAnalyzer.BackfilledGapPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_LaneEntryFallback_BeatsBackfill()
    {
        var task = Task("in-completed", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(5));

        var row = TaskCycleTimeAnalyzer.Analyze(task, [], null, ArchiveMove).Row;

        Assert.NotNull(row);
        Assert.Equal(TaskCycleTimeAnalyzer.LaneEntryCompletionSource, row!.CompletionSource);
        Assert.Equal(T0.AddHours(5), row.CompletedAt);
    }

    [Fact]
    public void Analyze_ArchivedWithoutCompletion_UsesBackfill_LeadTimeOnly()
    {
        var task = Task("legacy", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddDays(4));

        var row = TaskCycleTimeAnalyzer.Analyze(task, [], null, ArchiveMove).Row;

        Assert.NotNull(row);
        Assert.Equal(TaskCycleTimeAnalyzer.BackfillCompletionSource, row!.CompletionSource);
        Assert.Equal(T0.AddDays(3), row.CompletedAt);
        Assert.Equal(3 * 24 * 3600, row.LeadTimeSeconds);
        Assert.Null(row.Stages);
        Assert.Null(row.CycleTimeSeconds);
        Assert.Null(row.FirstClaimedAt);
        Assert.Equal(0, row.CodingRuns);
        Assert.Equal(0, row.IntegrationAttempts);
        Assert.Null(row.IntegrationOutcome);
        Assert.Contains("backfilled:git-archive-move", row.DataGaps);
        Assert.DoesNotContain("no-ledger", row.DataGaps);
        Assert.Empty(row.Transitions!);
    }

    [Fact]
    public void Analyze_ArchivedHistoricalParent_DoesNotUseBackfillOrLedgerCompletion()
    {
        var task = Task("old-parent", TaskStates.Archive, T0, T0.AddDays(1)) with { Kind = "epic" };
        var events = new[] { Lane(T0.AddDays(1), TaskStates.Completed, TaskStates.Archive) };

        var result = TaskCycleTimeAnalyzer.Analyze(task, events, null, ArchiveMove);

        Assert.Null(result.Row);
        Assert.Equal(TaskCycleAnalysis.ExcludedRetiredContainer, result.ExclusionReason);
    }

    [Fact]
    public void Analyze_ArchivedWithoutCompletion_WithoutBackfill_StaysExcluded()
    {
        var task = Task("legacy", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddDays(4));

        var analysis = TaskCycleTimeAnalyzer.Analyze(task, [], null);

        Assert.Null(analysis.Row);
        Assert.Equal(TaskCycleAnalysis.ExcludedNoCompletion, analysis.ExclusionReason);
    }

    [Fact]
    public void Analyze_Backfill_KeepsPartialLedgerAsClaimAnchorAndTransitions()
    {
        var task = Task("partial", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddDays(4));
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddHours(1), TaskStates.Ready, TaskStates.Progress),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, null, ArchiveMove).Row;

        Assert.NotNull(row);
        Assert.Equal(TaskCycleTimeAnalyzer.BackfillCompletionSource, row!.CompletionSource);
        Assert.Null(row.Stages);
        Assert.Single(row.Transitions!);
        // agent_run_started is absent; the lane change into Progress is not an
        // agent-run row, so the claim anchor stays unknown rather than guessed.
        Assert.Null(row.CycleTimeSeconds);
    }

    // ---- aggregation and coverage ------------------------------------------

    [Fact]
    public void BuildResponse_BackfilledRows_CountInLeadTimeAndCoverageOnly()
    {
        var now = T0.AddDays(30);
        var evidencedRow = EvidencedRow("real", completedAt: now.AddDays(-1), leadTime: 3600, outcome: "Merged");
        var backfilledRow = BackfilledRow("legacy", completedAt: now.AddDays(-2), leadTime: 7200);
        var analyses = new List<TaskCycleAnalysis>
        {
            new(evidencedRow, null),
            new(backfilledRow, null),
            new(null, TaskCycleAnalysis.ExcludedNoCompletion),
        };

        var response = ProjectCycleTimeService.BuildResponse("Demo", null, null, "all", now, null, analyses);

        Assert.Equal(2, response.Coverage.TasksInWindow);
        Assert.Equal(1, response.Coverage.TasksBackfilled);
        Assert.Equal(1, response.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Equal(0, response.Coverage.TasksWithoutLedger);

        var lead = response.Aggregates.Single(a => a.Stage == CycleTimeStages.LeadTime);
        Assert.Equal(2, lead.Count);
        Assert.Equal((3600 + 7200) / 2.0, lead.P50);

        // Stage, count, and outcome aggregates stay evidenced-only.
        var coding = response.Aggregates.Single(a => a.Stage == CycleTimeStages.Coding);
        Assert.Equal(1, coding.Count);
        var runs = response.Aggregates.Single(a => a.Stage == CycleTimeStages.CodingRuns);
        Assert.Equal(1, runs.Count);
        Assert.Single(response.IntegrationOutcomes);
        Assert.Equal("Merged", response.IntegrationOutcomes[0].Outcome);

        // Both rows are in the drill-down, the backfilled one without stages.
        Assert.Equal(2, response.Tasks.Count);
        Assert.Null(response.Tasks.Single(t => t.TaskId == "legacy").Stages);
    }

    // ---- file-backed service ------------------------------------------------

    [Fact]
    public void Service_UsesSidecar_AndPicksUpARegeneratedSidecarWithoutRestart()
    {
        var watchPath = Path.Combine(_root, "projects", "demo");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, state));
        var now = DateTime.UtcNow;
        var created = now.AddDays(-12);

        // Legacy shape: archived, no ledger at all.
        SeedJob(watchPath, TaskStates.Archive, "legacy-task", created, created.AddDays(1), [], null);
        // Ledger-complete control task: its sidecar entry must be ignored.
        SeedJob(watchPath, TaskStates.Archive, "ledgered-task", created, created.AddDays(2),
        [
            Created(created, TaskStates.Ready),
            Lane(created.AddDays(1), TaskStates.HumanReview, TaskStates.Completed),
            Lane(created.AddDays(2), TaskStates.Completed, TaskStates.Archive),
        ], null);
        SeedJob(watchPath, TaskStates.Archive, "old-parent", created, created.AddDays(3),
            [], null, kind: "epic");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var service = new ProjectCycleTimeService(
            scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<ProjectCycleTimeService>.Instance);

        // Without a sidecar the legacy task stays excluded.
        var before = service.Build("Demo", "all");
        Assert.NotNull(before);
        Assert.Single(before!.Tasks);
        Assert.Equal(2, before.Coverage.TasksInProject);
        Assert.Equal(1, before.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Equal(0, before.Coverage.TasksBackfilled);

        // The backfill tool writes the sidecar; a later build (fresh window key,
        // so the short project TTL does not serve the stale run) picks it up via
        // the per-task memo's sidecar stamp - no restart, no memo reset.
        var legacyCompleted = created.AddDays(1).ToUniversalTime();
        WriteSidecar(watchPath, $$"""
            {
              "version": 1,
              "entries": {
                "legacy-task": { "completedAt": "{{legacyCompleted:o}}", "source": "git-archive-move", "confidence": "medium", "commit": "abc123" },
                "ledgered-task": { "completedAt": "{{created.AddDays(9).ToUniversalTime():o}}", "source": "status-mtime", "confidence": "low" },
                "old-parent": { "completedAt": "{{created.AddDays(3).ToUniversalTime():o}}", "source": "status-mtime", "confidence": "low" }
              }
            }
            """);

        var after = service.Build("Demo", "30d");
        Assert.NotNull(after);
        Assert.Equal(2, after!.Tasks.Count);
        Assert.Equal(2, after.Coverage.TasksInProject);
        Assert.DoesNotContain(after.Tasks, task => task.TaskId == "old-parent");
        Assert.Equal(0, after.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Equal(1, after.Coverage.TasksBackfilled);

        var legacy = after.Tasks.Single(t => t.TaskId == "legacy-task");
        Assert.Equal(TaskCycleTimeAnalyzer.BackfillCompletionSource, legacy.CompletionSource);
        Assert.Equal(legacyCompleted, legacy.CompletedAt);
        Assert.Null(legacy.Stages);
        Assert.Contains("backfilled:git-archive-move", legacy.DataGaps);

        // The ledger-complete task keeps its ledger completion.
        var ledgered = after.Tasks.Single(t => t.TaskId == "ledgered-task");
        Assert.Equal(TaskCycleTimeAnalyzer.LedgerCompletionSource, ledgered.CompletionSource);

        // Lead time includes the backfilled row; coding runs do not.
        Assert.Equal(2, after.Aggregates.Single(a => a.Stage == CycleTimeStages.LeadTime).Count);
        Assert.Equal(1, after.Aggregates.Single(a => a.Stage == CycleTimeStages.CodingRuns).Count);

        // A re-generated sidecar (new evidence) is honoured on the next
        // uncached build: the per-task memo invalidates on the file stamp.
        var revised = created.AddDays(3).ToUniversalTime();
        WriteSidecar(watchPath, $$"""
            {
              "version": 1,
              "entries": {
                "legacy-task": { "completedAt": "{{revised:o}}", "source": "git-completed-move", "confidence": "high", "commit": "def456" }
              }
            }
            """);

        var revisedBuild = service.Build("Demo", "365d");
        Assert.NotNull(revisedBuild);
        var revisedRow = revisedBuild!.Tasks.Single(t => t.TaskId == "legacy-task");
        Assert.Equal(revised, revisedRow.CompletedAt);
        Assert.Contains("backfilled:git-completed-move", revisedRow.DataGaps);
    }

    // ---- helpers ------------------------------------------------------------

    private static void WriteSidecar(string watchPath, string json)
    {
        var dir = Path.Combine(watchPath, CycleTimeBackfillSidecar.MetadataDirName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, CycleTimeBackfillSidecar.FileName);
        File.WriteAllText(path, json);
        // The memo compares (length, mtime); keep rewrites distinguishable even
        // when the test runs fast enough for identical wall-clock stamps.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 60)));
    }

    private static TaskInfo Task(string id, string state, DateTime createdAt, DateTime enteredLaneAt) => new()
    {
        Id = id,
        TaskKey = "DEMO::" + id,
        Key = "DEM-" + id,
        Title = id + " title",
        State = state,
        CreatedAt = createdAt,
        EnteredLaneAt = enteredLaneAt,
    };

    private static TaskCycleTime EvidencedRow(string id, DateTime completedAt, double leadTime, string? outcome) =>
        new(id, "DEM-" + id, id, TaskStates.Archive, @"C:\demo", completedAt.AddSeconds(-leadTime), completedAt.AddSeconds(-leadTime + 60),
            completedAt, TaskCycleTimeAnalyzer.LedgerCompletionSource,
            new CycleTimeStageSeconds { QueueWait = 60, Coding = leadTime - 60 },
            0, leadTime, leadTime - 60, 1, 1, 0, outcome is null ? 0 : 1, outcome,
            outcome is null ? null : "pre-human-review", [], 0, []);

    private static TaskCycleTime BackfilledRow(string id, DateTime completedAt, double leadTime) =>
        new(id, "DEM-" + id, id, TaskStates.Archive, @"C:\demo", completedAt.AddSeconds(-leadTime), null,
            completedAt, TaskCycleTimeAnalyzer.BackfillCompletionSource,
            null, 0, leadTime, null, 0, 0, 0, 0, null, null,
            ["backfilled:git-archive-move"], 0, []);

    private static TimelineEvent Created(DateTime at, string targetState) => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.PromptCreated,
        Actor = TimelineActors.System,
        Summary = "created",
        Details = new Dictionary<string, string> { ["targetState"] = targetState },
    };

    private static TimelineEvent Lane(DateTime at, string from, string to) => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.LaneChanged,
        Actor = "system",
        Summary = $"{from} -> {to}",
        Details = new Dictionary<string, string> { ["from"] = from, ["to"] = to },
    };

    private static void SeedJob(
        string watchPath,
        string state,
        string slug,
        DateTime createdAt,
        DateTime enteredLaneAt,
        IEnumerable<TimelineEvent> events,
        PipelineExecutionRecord? pipeline,
        string? kind = null)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var kindField = kind is null ? "" : $",\"kind\":\"{kind}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"createdAt\":\"{createdAt:o}\",\"enteredLaneAt\":\"{enteredLaneAt:o}\"{kindField}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        foreach (var evt in events) timeline.Append(dir, evt);
        if (pipeline is not null)
        {
            File.WriteAllText(Path.Combine(dir, PipelineExecutionLog.FileName),
                System.Text.Json.JsonSerializer.Serialize(pipeline, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
    }
}
