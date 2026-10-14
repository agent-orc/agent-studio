using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the regression-radar post-step: the pure outcome mapping
/// (<see cref="ReviewDecisionOrchestrator.MapRegressionRadarOutcome"/>) and
/// the orchestrator recording the step into <c>pipeline-execution.json</c>
/// so the Overview pipeline lists it with a status + duration. The radar is
/// reporting-only - it never reissues - so the e2e test asserts the step is
/// recorded but the job still promotes to 5-human-review on a clean review.
///
/// <para>
/// A canned <see cref="RegressionRadarResult"/> is fed through the
/// <see cref="ReviewDecisionOrchestrator.RegressionRadarAnalyzer"/> test seam
/// so the test stays deterministic and needs neither git nor a session
/// timeline; the real <c>RegressionRadarService</c> classification is covered
/// by <c>RegressionRadarServiceTests</c>.
/// </para>
/// </summary>
public class RegressionRadarPostStepTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public RegressionRadarPostStepTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "regression-radar-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MapOutcome_Error_RecordsSkipped()
    {
        var (status, verdict, reason) = ReviewDecisionOrchestrator.MapRegressionRadarOutcome(
            new RegressionRadarResult { Error = "No commit range available" });
        Assert.Equal(PipelineStepStatus.Skipped, status);
        Assert.Equal("n/a", verdict);
        Assert.Equal("No commit range available", reason);
    }

    [Fact]
    public void MapOutcome_NoSpecChanges_RecordsPassedClean()
    {
        var (status, verdict, _) = ReviewDecisionOrchestrator.MapRegressionRadarOutcome(
            new RegressionRadarResult { BaselineSha = "a", HeadSha = "b", TotalSpecChanges = 0 });
        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Equal("clean", verdict);
    }

    [Theory]
    [InlineData(SpecChangeCategory.Intended, "intended")]
    [InlineData(SpecChangeCategory.AtRisk, "at-risk")]
    [InlineData(SpecChangeCategory.Drift, "drift")]
    public void MapOutcome_WithChanges_PassesWithCategoryVerdict(SpecChangeCategory category, string expectedVerdict)
    {
        // The radar never blocks: every analysis that ran records as Passed
        // with the worst category carried in the verdict token. The reason
        // line summarises the per-category counts for the record.
        var (status, verdict, reason) = ReviewDecisionOrchestrator.MapRegressionRadarOutcome(
            new RegressionRadarResult
            {
                OverallStatus = category,
                TotalSpecChanges = 3,
                IntendedCount = 1,
                AtRiskCount = 1,
                DriftCount = 1,
            });
        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Equal(expectedVerdict, verdict);
        Assert.Contains("3 spec change(s)", reason);
    }

    [Fact]
    public async Task RadarDrift_RecordsStepInPipelineExecution_AndStillPromotesToReview()
    {
        // Clean aspect verdicts + a drift radar result: the job promotes to
        // 5-human-review (radar is reporting-only) and the radar step lands in
        // pipeline-execution.json as Passed with a "drift" verdict + a duration.
        SeedReviewJobWithDone("radar-drift");
        var orchestrator = BuildOrchestrator(new RegressionRadarResult
        {
            OverallStatus = SpecChangeCategory.Drift,
            TotalSpecChanges = 2,
            IntendedCount = 1,
            AtRiskCount = 0,
            DriftCount = 1,
            BaselineSha = "aaaaaaa",
            HeadSha = "bbbbbbb",
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var reviewFolder = Path.Combine(_watchPath, TaskStates.HumanReview, "radar-drift");
        Assert.True(Directory.Exists(reviewFolder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "radar-drift")));

        // Read the record back as the typed model rather than asserting on the
        // raw JSON: the on-disk writer serialises the status enum as a number,
        // so the Overview's "status + duration" is verified through the field
        // values the API surface (and the FE) actually consume.
        var record = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance).Read(reviewFolder);
        Assert.NotNull(record);
        var radarStep = record!.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.RegressionRadarStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Passed, radarStep.Status);
        Assert.Equal("drift", radarStep.Verdict);
        Assert.True(radarStep.CompletedAt.HasValue);
        Assert.True(radarStep.DurationMs >= 0);
    }

    [Fact]
    public async Task RadarCondition_DoesNotRunAnalyzer_WhenConditionDoesNotMatch()
    {
        // taskType must be non-"feature" so the radar condition does NOT match,
        // but NOT "bug": a bug card trips the EvidenceGate (ASS-764, bugs require
        // visual proof) which blocks promotion to 5-human-review and the radar
        // step record never reaches the review folder this test reads back.
        SeedReviewJobWithDone("radar-condition", taskType: "chore");
        var analyzerCalled = false;
        var orchestrator = BuildOrchestrator(
            new RegressionRadarResult
            {
                OverallStatus = SpecChangeCategory.Drift,
                TotalSpecChanges = 1,
                BaselineSha = "aaaaaaa",
                HeadSha = "bbbbbbb",
            },
            settings =>
            {
                settings.SetPipelineStep(Project, PipelineCatalogue.RegressionRadarStepId, new PipelineStepSetting
                {
                    Condition = new PipelineStepCondition
                    {
                        When = PipelineStepConditions.TaskType,
                        Value = "feature",
                    },
                });
            });
        orchestrator.RegressionRadarAnalyzer = (_, _) =>
        {
            analyzerCalled = true;
            return new RegressionRadarResult
            {
                OverallStatus = SpecChangeCategory.Drift,
                TotalSpecChanges = 1,
                BaselineSha = "aaaaaaa",
                HeadSha = "bbbbbbb",
            };
        };

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.False(analyzerCalled);
        var reviewFolder = Path.Combine(_watchPath, TaskStates.HumanReview, "radar-condition");
        var record = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance).Read(reviewFolder);
        Assert.NotNull(record);
        var radarStep = record!.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.RegressionRadarStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Skipped, radarStep.Status);
        Assert.Equal("off", radarStep.Verdict);
        Assert.Equal("post-step disabled by config or condition", radarStep.Reason);
    }

    private void SeedReviewJobWithDone(string slug, string? taskType = null)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var taskTypeJson = string.IsNullOrWhiteSpace(taskType) ? "" : $",\"taskType\":\"{taskType}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"{taskTypeJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        RegressionRadarResult cannedResult,
        Action<ProjectSettingsService>? configureSettings = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
            ["ReviewDecisionOrchestrator:AspectsEnabled"] = "true",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        // Every aspect passes so the only thing left to record is the radar.
        aspectRunner.CliRunner = (_, _, _, _, _, _) =>
            Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var mutations = new TaskMutationService(
            scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        configureSettings?.Invoke(settings);
        var transitions = new TaskTransitionService(scanner, stateMachine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);

        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: null,
            pipelineLog: pipelineLog,
            // The radar post-step reads its run condition from the project
            // settings; production injects this via DI. Without it _projectSettings
            // is null and the condition is never consulted (the radar runs
            // unconditionally), so the per-task condition gating can't be
            // exercised. Pass the same configured instance the test mutates.
            projectSettings: settings);
        orchestrator.RegressionRadarAnalyzer = (_, _) => cannedResult;
        return orchestrator;
    }
}
