using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the ASS-563 lint-scss post-step end to end: config-layer
/// resolution, the orchestrator's lint-scss reissue branch, the
/// infinite-spin escalation guard, and the pipeline-execution.json /
/// post-steps log artefacts the FE reads to render the verdict badge.
///
/// <para>
/// A <see cref="FakeLintScssRunner"/> stands in for <c>npx stylelint</c>
/// so the tests stay deterministic and don't depend on a Node toolchain.
/// The runner itself is covered separately by
/// <see cref="LintScssRunner_NoFrontend_ReturnsSkipped"/>; the rest of
/// the suite exercises the orchestrator's verdict-handling, which is
/// where every reissue / escalate / observability decision lives.
/// </para>
/// </summary>
public class LintScssPostStepTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public LintScssPostStepTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "lint-scss-tests-" + Guid.NewGuid().ToString("N"));
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
    public void ConfigResolver_BuiltInDefault_IsWarn()
    {
        // No config layers populated -> built-in Warn so the gate is
        // observable from day 1 but never reissues until a project opts in.
        var folder = Path.Combine(_workspace, "no-config");
        Directory.CreateDirectory(folder);
        var mode = PostStepConfigResolver.Resolve(
            new ConfigurationBuilder().Build(), folder, PipelineCatalogue.LintScssStepId);
        Assert.Equal(PostStepMode.Warn, mode);
    }

    [Fact]
    public void ConfigResolver_ProjectDefault_AppliesWhenNoJobOverride()
    {
        var folder = Path.Combine(_workspace, "project-only");
        Directory.CreateDirectory(folder);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"PostSteps:{PipelineCatalogue.LintScssStepId}:DefaultMode"] = "fail"
        }).Build();
        var mode = PostStepConfigResolver.Resolve(config, folder, PipelineCatalogue.LintScssStepId);
        Assert.Equal(PostStepMode.Fail, mode);
    }

    [Fact]
    public void ConfigResolver_JobOverride_BeatsProjectDefault()
    {
        var folder = Path.Combine(_workspace, "job-override");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"job-override\",\"postSteps\":{{\"{PipelineCatalogue.LintScssStepId}\":\"off\"}}}}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"PostSteps:{PipelineCatalogue.LintScssStepId}:DefaultMode"] = "fail"
        }).Build();
        var mode = PostStepConfigResolver.Resolve(config, folder, PipelineCatalogue.LintScssStepId);
        Assert.Equal(PostStepMode.Off, mode);
    }

    [Fact]
    public void ConfigResolver_JobOverride_AcceptsBareStepIdSuffix()
    {
        // task.json author conveniences: "lint-scss" instead of
        // "post-lint-scss" so the per-task override stays terse.
        var folder = Path.Combine(_workspace, "bare-suffix");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            "{\"id\":\"bare\",\"postSteps\":{\"lint-scss\":\"fail\"}}");
        var mode = PostStepConfigResolver.Resolve(
            new ConfigurationBuilder().Build(), folder, PipelineCatalogue.LintScssStepId);
        Assert.Equal(PostStepMode.Fail, mode);
    }

    [Fact]
    public void ConfigResolver_TaskTypeMode_BeatsProjectDefault_ButLosesToJobOverride()
    {
        // Per spec layer 2 (taskTypeDefaults.feature.postSteps.lint-scss)
        // sits between job-level override and project-level default.
        var folder = Path.Combine(_workspace, "task-type");
        Directory.CreateDirectory(folder);
        var middle = PostStepConfigResolver.Resolve(folder, PipelineCatalogue.LintScssStepId,
            taskTypeMode: PostStepMode.Fail, projectMode: PostStepMode.Warn);
        Assert.Equal(PostStepMode.Fail, middle);

        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"x\",\"postSteps\":{{\"{PipelineCatalogue.LintScssStepId}\":\"off\"}}}}");
        var jobWins = PostStepConfigResolver.Resolve(folder, PipelineCatalogue.LintScssStepId,
            taskTypeMode: PostStepMode.Fail, projectMode: PostStepMode.Warn);
        Assert.Equal(PostStepMode.Off, jobWins);
    }

    [Fact]
    public void ConfigResolver_UnparseableJobOverride_FallsThroughToNextLayer()
    {
        var folder = Path.Combine(_workspace, "garbled");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"g\",\"postSteps\":{{\"{PipelineCatalogue.LintScssStepId}\":\"yolo\"}}}}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"PostSteps:{PipelineCatalogue.LintScssStepId}:DefaultMode"] = "fail"
        }).Build();
        var mode = PostStepConfigResolver.Resolve(config, folder, PipelineCatalogue.LintScssStepId);
        Assert.Equal(PostStepMode.Fail, mode);
    }

    [Fact]
    public async Task LintScssRunner_NoFrontend_ReturnsSkipped()
    {
        // Production safety net: a watched project without a frontend/
        // tree skips silently rather than spinning the agent on a
        // never-fixable failure.
        var repo = Path.Combine(_workspace, "no-frontend-repo");
        Directory.CreateDirectory(repo);
        var runner = new LintScssRunner(NullLogger<LintScssRunner>.Instance);
        var result = await runner.RunAsync(repo, PostStepMode.Fail, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(LintScssVerdict.Skipped, result.Verdict);
        Assert.Null(result.ExitCode);
        Assert.Contains("no frontend/", result.Reason);
    }

    [Fact]
    public async Task LintScssRunner_ModeOff_SkipsWithoutTouchingFilesystem()
    {
        // mode=off short-circuits before any directory check; even a
        // bogus path returns Skipped.
        var runner = new LintScssRunner(NullLogger<LintScssRunner>.Instance);
        var result = await runner.RunAsync(
            Path.Combine(_workspace, "does-not-exist"), PostStepMode.Off,
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(LintScssVerdict.Skipped, result.Verdict);
        Assert.Equal("mode=off", result.Reason);
    }

    [Fact]
    public void LintScssRunner_WindowsCommand_PassesGlobWithoutLiteralQuotes()
    {
        var process = new System.Diagnostics.ProcessStartInfo();

        LintScssRunner.AddStylelintArguments(process, isWindows: true);

        Assert.Equal(
            ["/d", "/s", "/c", "npx", "stylelint", "src/**/*.scss"],
            process.ArgumentList);
        Assert.DoesNotContain("\"", process.ArgumentList[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LintScssFail_InFailMode_ReissuesToReadyAndJournalsLintReason()
    {
        // Happy path: aspects pass, lint-scss fails, mode=fail -> job
        // ends up in 2-ready with the reissue tag + the truncated
        // stylelint output captured in both the follow-up file and the
        // decision-journal Reason.
        SeedReviewJobWithDone("lint-broken");
        var fakeLint = new FakeLintScssRunner(
            new LintScssResult(LintScssVerdict.Fail, ExitCode: 2, DurationMs: 1234,
                Output: "src/app/foo.scss\n  3:1 ✖ Disallowed hex color \"#fff\"",
                Reason: "stylelint exit 2"));
        var orchestrator = BuildOrchestratorWithLint(fakeLint, lintMode: "fail");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Lane move: 4-auto-review -> 2-ready (the lint-scss reissue path).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "lint-broken")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "lint-broken")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "lint-broken")));

        var followUp = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.Ready, "lint-broken", "orchestrator-follow-up.md"));
        Assert.Contains("lint-scss post-step found stylelint errors", followUp);
        Assert.Contains("Disallowed hex color", followUp);

        // pipeline-execution.json must have the lint-scss step recorded
        // as Failed so the FE badge can render "fail".
        var pipelineJson = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.Ready, "lint-broken",
                AgentStudio.Pipeline.PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.LintScssStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"fail\"", pipelineJson);

        // Truncated lint output is persisted under post-steps/ so the
        // operator can expand it from the timeline.
        var postStepsDir = Path.Combine(_watchPath, TaskStates.Ready, "lint-broken", "post-steps");
        Assert.True(Directory.Exists(postStepsDir));
        var logFiles = Directory.GetFiles(postStepsDir, "lint-scss-*.log");
        Assert.Single(logFiles);
        Assert.Contains("Disallowed hex color", File.ReadAllText(logFiles[0]));

        // Decision journal: one Reissue entry with the lint-scss prefix
        // so the next failure can find it via the infinite-spin counter.
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
        Assert.Equal(ReviewDecisionKind.Reissue, records[0].Kind);
        Assert.StartsWith(ReviewDecisionOrchestrator.LintScssReissueReasonPrefix, records[0].Reason);
    }

    [Fact]
    public async Task LintScssFail_InWarnMode_FallsThroughToAcceptAsDone()
    {
        // mode=warn: lint-scss verdict is Warn, not Fail, so the
        // orchestrator skips the reissue branch and lets the existing
        // accept-as-done path promote to 5-human-review.
        SeedReviewJobWithDone("lint-warn-only");
        var fakeLint = new FakeLintScssRunner(
            new LintScssResult(LintScssVerdict.Warn, ExitCode: 2, DurationMs: 100,
                Output: "src/app/bar.scss\n  4:1 ✖ something",
                Reason: "stylelint exit 2"));
        var orchestrator = BuildOrchestratorWithLint(fakeLint, lintMode: "warn");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "lint-warn-only")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "lint-warn-only")));

        // Step verdict still recorded as Warn so the FE can render the
        // amber pill even though no reissue happened.
        var pipelineJson = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.HumanReview, "lint-warn-only",
                AgentStudio.Pipeline.PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"warn\"", pipelineJson);
    }

    [Fact]
    public async Task LintScssFail_TwiceInARow_EscalatesToHumanReview()
    {
        // ASS-46 infinite-spin guard: a Reissue record with the
        // LintScssReissueReasonPrefix already in the journal means the
        // agent had its one shot. A second fail must escalate to
        // 5-human-review instead of reissuing again.
        SeedReviewJobWithDone("lint-double-fail");

        // Pre-seed a prior lint-scss reissue so the counter triggers.
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            JobId: "lint-double-fail",
            Project: Project,
            Kind: ReviewDecisionKind.Reissue,
            Reason: ReviewDecisionOrchestrator.LintScssReissueReasonPrefix + "stylelint exit 2",
            Prompt: "(deterministic lint-scss post-step)",
            Response: "(prior run output)",
            FollowUp: "(prior follow-up)"));

        var fakeLint = new FakeLintScssRunner(
            new LintScssResult(LintScssVerdict.Fail, ExitCode: 2, DurationMs: 100,
                Output: "still broken",
                Reason: "stylelint exit 2"));
        var orchestrator = BuildOrchestratorWithLint(fakeLint, lintMode: "fail");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "lint-double-fail")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "lint-double-fail")));

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(2, records.Count); // pre-seed + escalate
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
        Assert.Contains("lint-scss failed twice", records[^1].Reason);
    }

    [Theory]
    [InlineData("warn")]
    [InlineData("fail")]
    public async Task RemoteRequiredLint_DefersWithoutAcceptingOrReissuing(string lintMode)
    {
        const string slug = "remote-lint-wait";
        SeedReviewJobWithDone(slug);
        Directory.CreateDirectory(Path.Combine(_watchPath, "frontend"));
        File.WriteAllText(Path.Combine(_watchPath, "frontend", "angular.json"), "{}");
        var orchestrator = BuildOrchestratorWithLint(new RemoteRequiredLintScssRunner(), lintMode);

        var result = await orchestrator.ProcessCardAsync(_workspace, Project, slug, _watchPath, CancellationToken.None);

        Assert.Equal(PostProcessingCardStatus.Deferred, result.Status);
        Assert.Equal(RemoteExecutionRequirement.Code, result.Reason);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, slug)));
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspace, Project));
        var pipelineJson = File.ReadAllText(Path.Combine(_watchPath, TaskStates.AutoReview, slug, PipelineExecutionLog.FileName));
        Assert.Contains(RemoteExecutionRequirement.Code, pipelineJson);
    }

    [Fact]
    public async Task RemoteRequiredLint_DisabledStepStillSkips()
    {
        var result = await new RemoteRequiredLintScssRunner().RunAsync(
            _watchPath, PostStepMode.Off, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(LintScssVerdict.Skipped, result.Verdict);
        Assert.Null(result.InfrastructureFailureCode);
    }

    private void SeedReviewJobWithDone(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");
    }

    private ReviewDecisionOrchestrator BuildOrchestratorWithLint(
        ILintScssRunner lintRunner,
        string lintMode)
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
            [$"PostSteps:{PipelineCatalogue.LintScssStepId}:DefaultMode"] = lintMode,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        // Aspect stub: every aspect passes so the lint-scss branch is the
        // only verdict left to decide on.
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
        var transitions = new TaskTransitionService(scanner, stateMachine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);

        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: null,
            pipelineLog: pipelineLog,
            lintScssRunner: lintRunner);
        return orchestrator;
    }

    /// <summary>
    /// Test seam: returns a canned <see cref="LintScssResult"/> on every
    /// invocation so the rest of the test asserts what the orchestrator
    /// did with that verdict, not what stylelint actually said.
    /// </summary>
    private sealed class FakeLintScssRunner : ILintScssRunner
    {
        private readonly LintScssResult _canned;
        public FakeLintScssRunner(LintScssResult canned) { _canned = canned; }
        public Task<LintScssResult> RunAsync(
            string repositoryPath, PostStepMode mode, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(_canned);
    }
}
