using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The S2 review baseline of Dossier AGT-3400: per-step review minutes and
/// tokens from pipeline executions, the fixed token share f, the aspect share
/// a and the PK5 A wave census. Legacy rows without token fields stay unknown.
/// </summary>
public sealed class ReviewBaselineTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-baseline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Samples_ReadEveryRunAndKeepLegacyTokensUnknown()
    {
        var record = new PipelineExecutionRecord
        {
            JobId = "card-a", Attempt = 2,
            Steps =
            [
                Aspect("aspect-code-quality", "review-2", Now.AddHours(-1), 120_000, 9_000, 400, 1_000) with
                {
                    EarlierRuns =
                    [
                        // A legacy run: a model ran, but the row carries no token fields.
                        new PipelineStepRunSummary
                        {
                            Status = PipelineStepStatus.Passed, Model = "gpt-6-sol",
                            StartedAt = Now.AddHours(-3), CompletedAt = Now.AddHours(-3).AddMinutes(3),
                            DurationMs = 180_000, ExecutionAttemptId = "review-1",
                        },
                    ],
                },
                Tool("review-2", Now.AddHours(-1), 600_000, PipelineStepStatus.Passed),
                Aspect("aspect-requirement-fit", "review-2", Now.AddHours(-1), 0, 0, 0, 0) with
                {
                    Model = null, ExecutionLocation = "carried-over", Verdict = "pass",
                    StartedAt = Now.AddHours(-1), CompletedAt = Now.AddHours(-1),
                },
                new PipelineStepExecution
                {
                    StepId = PipelineCatalogue.OrchestratorDecisionStepId, Kind = StepKind.Orchestrator,
                    Status = PipelineStepStatus.Passed, Model = "gpt-6-sol",
                    StartedAt = Now.AddMinutes(-50), CompletedAt = Now.AddMinutes(-49),
                    DurationMs = 60_000, InputTokens = 3_000, OutputTokens = 200, ExecutionAttemptId = "review-2",
                },
            ],
        };

        var samples = ReviewStepSampleReader.Read("card-a", record, TokenTestServices.Pricing());

        var quality = samples.Where(s => s.Step == "code-quality").OrderBy(s => s.CompletedAt).ToList();
        Assert.Equal(2, quality.Count);
        Assert.Null(quality[0].Tokens);
        Assert.Equal(3, quality[0].WallMinutes);
        Assert.Equal(10_000, quality[1].Tokens!.Prompt);
        var tool = Assert.Single(samples, s => s.Step == ReviewSteps.ToolCommands);
        Assert.Equal(0, tool.Tokens!.Prompt);
        Assert.Equal(10, tool.WallMinutes);
        Assert.DoesNotContain(samples, s => s.Step == ReviewSteps.RequirementFit);
        Assert.Equal(3_000, Assert.Single(samples, s => s.Step == ReviewSteps.Decision).Tokens!.Input);
    }

    [Fact]
    public void StepFold_ReportsPerRoundAndPerCard_AndCountsUnknownRoundsInsteadOfZero()
    {
        var samples = new[]
        {
            Sample("A", "r1", "code-quality", 2, Tokens(1_000)),
            Sample("A", "r2", "code-quality", 4, Tokens(3_000)),
            Sample("B", "r3", "code-quality", 6, null),
            Sample("C", "r4", "code-quality", 8, Tokens(5_000)),
            Sample("A", "r1", ReviewSteps.ToolCommands, 10, Tokens(0)),
        };

        var rows = ReviewStepBaselineFold.Build(samples);

        var quality = Assert.Single(rows, r => r.Step == "code-quality");
        Assert.Equal(ReviewSteps.PackageTier, quality.Tier);
        Assert.Equal(3, quality.Cards);
        Assert.Equal(4, quality.Rounds);
        Assert.Equal(5, quality.WallMinutes.PerRoundMedian);
        Assert.Equal(6, quality.WallMinutes.PerCardMedian);
        Assert.Equal(3, quality.InputTokens.KnownRounds);
        Assert.Equal(1, quality.InputTokens.UnknownRounds);
        Assert.Equal(3_000, quality.InputTokens.PerRoundMedian);
        Assert.Equal(1, quality.InputTokens.UnknownCards);
        Assert.Equal(4_500, quality.InputTokens.PerCardMedian);
        Assert.Equal(9_000, quality.InputTokens.Total);
        Assert.Equal(ReviewSteps.CardCheckTier, rows[0].Tier);
    }

    [Fact]
    public void FixedShare_RecoversTheInterceptShare_AndSkipsUnknownSamples()
    {
        // Prompt = 6,000 fixed tokens + 4 tokens per material line.
        var samples = new[] { 100, 500, 1_000, 2_000 }
            .Select(lines => new FixedShareSample("code-quality", 6_000 + 4L * lines, lines))
            .Append(new FixedShareSample("code-quality", null, 300))
            .Append(new FixedShareSample("code-quality", 7_000, null))
            .Append(new FixedShareSample("requirement-fit", 5_000, 100))
            .ToList();

        var estimates = ReviewFixedShareEstimator.Estimate(samples);

        var quality = Assert.Single(estimates, e => e.Aspect == "code-quality");
        Assert.Equal(4, quality.Samples);
        Assert.Equal(2, quality.UnknownSamples);
        Assert.Equal(6_000, quality.InterceptTokens!.Value, 6);
        Assert.Equal(4, quality.TokensPerLine!.Value, 6);
        Assert.Equal(6_000 / 9_600d, quality.FixedShare!.Value, 6);
        Assert.Equal(1, quality.RSquared!.Value, 6);
        var fit = Assert.Single(estimates, e => e.Aspect == "requirement-fit");
        Assert.Null(fit.FixedShare);
        Assert.Equal(ReviewSteps.CardCheckTier, fit.Tier);
        Assert.Equal(6_000 / 9_600d, ReviewFixedShareEstimator.PackageTierShare(estimates)!.Value, 6);
    }

    [Fact]
    public void WaveCensus_FormsPk5Packages_ByWindowCapAndMaterialBudget()
    {
        var t = Now.AddDays(-1);
        var cards = new[]
        {
            Card("W1", "docs/x/index.html", t, 10, 1_000),
            Card("W2", "docs/x/index.html", t.AddMinutes(30), 10, 1_000),
            Card("W3", "docs/x/index.html", t.AddMinutes(59), 10, 1_000),
            Card("W4", "docs/x/index.html", t.AddMinutes(60), 10, 1_000),
            Card("W5", "docs/x/index.html", t.AddMinutes(61), 10, 1_000), // cap of 4 reached
            Card("W6", "docs/x/index.html", t.AddMinutes(200), 10, 1_000), // outside the window
            Card("Y1", "/docs/y/index.html", t, 150, 8_000),
            Card("Y2", "docs\\y\\index.html", t.AddMinutes(5), 60, 1_000), // 210 files: over budget
            Card("Z1", "docs/z/index.html", null, 1, 10), // check never passed
            Card("N1", null, t, 1, 10),
            Card("N2", "", t, 1, 10),
        };

        var census = ReviewWaveCensus.Build(cards);

        Assert.Equal(11, census.ReviewedCards);
        Assert.Equal(9, census.DossierCards);
        Assert.Equal(9 / 11d, census.DossierShare!.Value, 6);
        Assert.True(census.MeetsWaveShareThreshold);
        Assert.Equal(1, census.DossierCardsWithoutCheckTime);
        Assert.Equal(new[] { "W1", "W2", "W3", "W4" }, census.PackageList[0].Members);
        Assert.Contains(census.PackageList, p => p.Members.SequenceEqual(new[] { "W5" }));
        Assert.Contains(census.PackageList, p => p.Members.SequenceEqual(new[] { "W6" }));
        Assert.Contains(census.PackageList, p => p.Members.SequenceEqual(new[] { "Y1" }));
        Assert.Contains(census.PackageList, p => p.Members.SequenceEqual(new[] { "Y2" }));
        Assert.Equal(5, census.Packages);
        Assert.Equal(1, census.MedianPackageSize);
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [4] = 1 }, census.PackageSizes);
    }

    [Fact]
    public void WaveCensus_UnknownSizeNeitherBlocksNorFillsTheBudget()
    {
        var t = Now.AddDays(-1);
        var census = ReviewWaveCensus.Build(
        [
            Card("A", "docs/w.html", t, 190, 11_000),
            Card("B", "docs/w.html", t.AddMinutes(1), null, null),
            Card("C", "docs/w.html", t.AddMinutes(2), 20, 100),
        ]);

        Assert.Equal(new[] { "A", "B" }, census.PackageList[0].Members);
        Assert.True(census.PackageList[0].SizeUnknown);
        Assert.Equal(new[] { "C" }, census.PackageList[1].Members);
        Assert.Equal(1, census.PackagesWithUnknownSize);
        Assert.Equal(1.5, census.MedianPackageSize);
    }

    [Fact]
    public void AspectShare_UsesCatalogueAspectsOverReviewOther_AndSkipsUnknownCards()
    {
        var cards = new[]
        {
            new ReviewBaselineCard("A", "A", null,
            [
                Sample("A", "r1", "code-quality", 30, Tokens(1)),
                Sample("A", "r1", "requirement-fit", 30, Tokens(1)),
                Sample("A", "r1", ReviewSteps.Grade, 30, Tokens(1)),
            ], ReviewOtherSeconds: 7_200),
            new ReviewBaselineCard("B", "B", null,
                [Sample("B", "r2", "code-quality", 45, Tokens(1))], ReviewOtherSeconds: 3_600),
            new ReviewBaselineCard("C", "C", null,
                [Sample("C", "r3", "code-quality", null, Tokens(1))], ReviewOtherSeconds: 3_600),
            new ReviewBaselineCard("D", "D", null,
                [Sample("D", "r4", "code-quality", 10, Tokens(1))]),
        };

        var share = ReviewBaselineFold.Share(cards);

        Assert.Equal(2, share.Cards);
        Assert.Equal(1, share.UnknownCards);
        Assert.Equal((3_600 + 2_700) / 10_800d, share.Pooled!.Value, 6);
        Assert.Equal((0.5 + 0.75) / 2, share.MedianPerCard!.Value, 6);
    }

    [Fact]
    public void Report_PicksTheCardCheckRoundAndItsMaterialForTheCensus()
    {
        var sha1 = new string('1', 40);
        var sha2 = new string('2', 40);
        var card = new ReviewBaselineCard("A", "AGT-1", "docs/x.html",
        [
            Sample("A", "r1", ReviewSteps.ToolCommands, 5, Tokens(0), passed: false, sha: sha1, at: Now.AddHours(-5)),
            Sample("A", "r2", ReviewSteps.ToolCommands, 5, Tokens(0), sha: sha2, at: Now.AddHours(-2)),
            Sample("A", "r2", ReviewSteps.RequirementFit, 3, Tokens(2_000), sha: sha2, at: Now.AddHours(-1), files: 7),
            Sample("A", "r0", "code-quality", 3, Tokens(2_000), at: Now.AddDays(-40)),
        ], Material: new ReviewMaterialSize(99, 9_999),
            MaterialBySha: new Dictionary<string, ReviewMaterialSize> { [sha2] = new(5, 400) });

        var report = ReviewBaselineFold.Build("Demo", 30, Now, [card]);

        var census = Assert.Single(report.Waves.PackageList);
        Assert.Equal(Now.AddHours(-1), census.FirstCheckAt);
        Assert.Equal(7, census.Files);
        Assert.Equal(400, census.Lines);
        Assert.DoesNotContain(report.Steps, row => row.Step == "code-quality");
        var fit = Assert.Single(report.FixedShare, estimate => estimate.Aspect == ReviewSteps.RequirementFit);
        Assert.Equal(1, fit.Samples);
    }

    [Fact]
    public void MaterialSizes_AccumulateAttributedCommits_AndStayUnknownForAMissingFact()
    {
        var commits = new List<TaskCommitInfo>
        {
            new() { Sha = "c1", ResultSha = "r1" },
            new() { Sha = "old", SupersededBySha = "c2" },
            new() { Sha = "c2" },
        };
        var facts = new Dictionary<string, GitCommitFact>
        {
            ["c1"] = Commit("c1", ("a.cs", 10, 2), ("b.cs", 5, 0)),
            ["c2"] = Commit("c2", ("a.cs", 1, 1), ("c.md", 3, 0)),
        };

        var (card, bySha) = ReviewBaselineService.MaterialSizes(commits, sha => facts.GetValueOrDefault(sha));
        var (missing, _) = ReviewBaselineService.MaterialSizes(
            [.. commits, new TaskCommitInfo { Sha = "c3" }], sha => facts.GetValueOrDefault(sha));

        Assert.Equal(new ReviewMaterialSize(3, 22), card);
        Assert.Equal(new ReviewMaterialSize(2, 17), bySha["r1"]);
        Assert.Equal(new ReviewMaterialSize(3, 22), bySha["c2"]);
        Assert.Null(missing);
        Assert.Null(ReviewBaselineService.MaterialSizes([], _ => null).Card);
    }

    [Fact]
    public void Service_ReadsTaskFolders_DossierPathAndLegacyRows()
    {
        var watchPath = Path.Combine(_root, "tasks");
        var t = Now.AddDays(-2);
        Seed(watchPath, "card-a", "docs/review-gate-packages/index.html", new PipelineExecutionRecord
        {
            JobId = "card-a",
            Steps =
            [
                Tool("ra", t, 300_000, PipelineStepStatus.Passed),
                Aspect("aspect-requirement-fit", "ra", t.AddMinutes(10), 60_000, 4_000, 100, 0),
                Aspect("aspect-code-quality", "ra", t.AddMinutes(12), 120_000, 8_000, 300, 500),
            ],
        });
        Seed(watchPath, "card-b", "docs/review-gate-packages/index.html", new PipelineExecutionRecord
        {
            JobId = "card-b",
            Steps =
            [
                Tool("rb", t.AddMinutes(20), 300_000, PipelineStepStatus.Passed),
                // Legacy aspect row: no token fields at all.
                Aspect("aspect-requirement-fit", "rb", t.AddMinutes(30), 90_000, 0, 0, 0),
            ],
        });
        Seed(watchPath, "card-c", null, new PipelineExecutionRecord
        {
            JobId = "card-c",
            Steps = [Tool("rc", t, 300_000, PipelineStepStatus.Passed)],
        });
        Seed(watchPath, "card-d", "docs/other.html", null);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var service = new ReviewBaselineService(scanner, pipeline, TokenTestServices.Pricing(),
            new ProjectCycleTimeService(scanner, new TimelineLog(NullLogger<TimelineLog>.Instance), pipeline,
                NullLogger<ProjectCycleTimeService>.Instance),
            new GitCardFactReader(), new FakeTimeProvider(new DateTimeOffset(Now)));

        var report = service.Build("Demo", watchPath, 30);

        Assert.Equal(3, report.Waves.ReviewedCards);
        Assert.Equal(2, report.Waves.DossierCards);
        var package = Assert.Single(report.Waves.PackageList);
        Assert.Equal(new[] { "card-a", "card-b" }, package.Members);
        Assert.True(package.SizeUnknown);
        var fit = Assert.Single(report.Steps, row => row.Step == ReviewSteps.RequirementFit);
        Assert.Equal(1, fit.InputTokens.KnownRounds);
        Assert.Equal(1, fit.InputTokens.UnknownRounds);
        Assert.Equal(4_000, fit.InputTokens.PerRoundMedian);
        Assert.Equal(2, fit.WallMinutes.KnownRounds);
        var tools = Assert.Single(report.Steps, row => row.Step == ReviewSteps.ToolCommands);
        Assert.Equal(3, tools.Cards);
        Assert.Equal(5, tools.WallMinutes.PerRoundMedian);
        Assert.All(report.FixedShare, estimate => Assert.Equal(0, estimate.Samples));
    }

    private void Seed(string watchPath, string slug, string? dossier, PipelineExecutionRecord? pipeline)
    {
        var dir = Path.Combine(watchPath, TaskStates.HumanReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var dossierJson = dossier is null ? "" : $",\"dossier\":{{\"path\":\"{dossier}\"}}";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.HumanReview}\",\"order\":1,"
            + $"\"agent\":\"claude\",\"createdAt\":\"{Now.AddDays(-3):o}\"{dossierJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        if (pipeline is not null)
            File.WriteAllText(Path.Combine(dir, PipelineExecutionLog.FileName),
                JsonSerializer.Serialize(pipeline, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static PipelineStepExecution Aspect(string stepId, string round, DateTime completed, long durationMs,
        long input, long output, long cacheRead)
        => new()
        {
            StepId = stepId, Kind = StepKind.Aspect, Status = PipelineStepStatus.Passed, Model = "gpt-6-sol",
            StartedAt = completed.AddMilliseconds(-durationMs), CompletedAt = completed, DurationMs = durationMs,
            InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead,
            ExecutionAttemptId = round, Verdict = "pass",
        };

    private static PipelineStepExecution Tool(string round, DateTime started, long durationMs, PipelineStepStatus status)
        => new()
        {
            StepId = PipelineCatalogue.BuildTestGateStepId, Kind = StepKind.Tool, Status = status,
            StartedAt = started, CompletedAt = started.AddMilliseconds(durationMs), DurationMs = durationMs,
            ExecutionAttemptId = round,
        };

    private static ReviewStepTokens Tokens(long input) => new(input, 0, 0, 0);

    private static ReviewStepSample Sample(string job, string round, string step, double? minutes,
        ReviewStepTokens? tokens, bool passed = true, string? sha = null, DateTime? at = null, int? files = null)
        => new(job, round, step, at ?? Now.AddDays(-1), minutes, tokens, passed, sha, files);

    private static WaveCensusCard Card(string key, string? dossier, DateTime? passedAt, int? files, int? lines)
        => new(key, dossier, passedAt, files, lines);

    private static GitCommitFact Commit(string sha, params (string Path, int Added, int Removed)[] files)
        => new(sha, sha, [], "tree", Now, "author", "subject", new Dictionary<string, string>(),
            files.Select(file => new GitCommitFileFact("M", file.Path, null, file.Added, file.Removed, false)).ToList());
}
