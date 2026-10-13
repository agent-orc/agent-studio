using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the two halves of the "Auto-review always parallel + orchestrator
/// final verdict" feature:
/// <list type="number">
/// <item>The read-only parallel review pool (Req 1 / ADR-0052): several DONE
/// tasks are reviewed concurrently rather than one-at-a-time, because an
/// aspect review writes no repo files.</item>
/// <item>The orchestrator final-verdict pipeline step (Req 2): after the
/// parallel aspects the orchestrator records ONE aggregated verdict into
/// <c>pipeline-execution.json</c> as the distinct
/// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> step.</item>
/// </list>
/// The aspect CLI is stubbed so the tests assert the orchestrator's wiring,
/// not what a real model said.
/// </summary>
public class OrchestratorParallelReviewAndDecisionStepTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public OrchestratorParallelReviewAndDecisionStepTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "orch-parallel-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task TaskDone_AllAspectsPass_RecordsAcceptFinalVerdictStep()
    {
        // Req 2: a clean accept must leave the orchestrator-decision step in
        // pipeline-execution.json with verdict "accept" - before this feature
        // the catalogue defined the step but nothing ever recorded it, so it
        // stayed Pending forever.
        SeedReviewJobWithDone("accept-job");
        var orchestrator = BuildOrchestrator(PassStub);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "accept-job");
        Assert.True(Directory.Exists(folder), "accept should promote the task to 5-human-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorDecisionStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"accept\"", pipelineJson);
    }

    [Fact]
    public async Task TaskDone_AspectBlocks_RecordsReissueFinalVerdictStep()
    {
        // Req 2: a blocking aspect aggregates to a single "reissue" final
        // verdict recorded on the post-move folder (2-ready), distinct from the
        // per-aspect rows that drove it.
        SeedReviewJobWithDone("block-job");
        var orchestrator = BuildOrchestrator(BlockStub);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "block-job");
        Assert.True(Directory.Exists(folder), "a blocking aspect verdict should reissue to 2-ready");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorDecisionStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
    }

    [Fact]
    public async Task TaskDone_GradeBNamesDeficiencies_CouncilReissuesWithTargetedHandoff()
    {
        SeedReviewJobWithDone("council-job");
        var gradeReply = """
            The implementation is close, but concrete gaps remain.
            [[CODE_REVIEW_FINDING: text=Dark-theme colors are incorrect; fix them and provide both-theme screenshots.]]
            [[CODE_REVIEW_FINDING: text=Upload rejection lacks focused test evidence; add the missing regression test.]]
            [[CODE_REVIEW_GRADE: grade=B; summary=Two concrete gaps remain.]]
            [[TASK_DONE]]
            """;
        var orchestrator = BuildOrchestrator(PassStub, gradeReply: gradeReply);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "council-job");
        Assert.True(Directory.Exists(folder), "named grade findings should start a new round in 2-ready");

        var followUp = File.ReadAllText(Path.Combine(folder, "orchestrator-follow-up.md"));
        Assert.Contains("Dark-theme colors are incorrect", followUp);
        Assert.Contains("Upload rejection lacks focused test evidence", followUp);
        Assert.DoesNotContain("The implementation is close", followUp);

        var reviewFile = Assert.Single(Directory.EnumerateFiles(folder, "code-review-grade-*.md"));
        var reaction = AgentStudio.Review.CouncilReviewReactionStore.Read(folder, Path.GetFileName(reviewFile));
        Assert.NotNull(reaction);
        Assert.Equal(AgentStudio.OrchestrationDecisions.CouncilReactionDisposition.Reissue, reaction!.Disposition);
        Assert.Equal(2, reaction.Assessments.Count);
        Assert.True(reaction.StartsNewRound);
        var reactionJson = File.ReadAllText(
            AgentStudio.Review.CouncilReviewReactionStore.PathFor(folder, Path.GetFileName(reviewFile)));
        Assert.Contains("\"disposition\": \"Reissue\"", reactionJson);
        Assert.Contains("\"action\": \"FixNextRound\"", reactionJson);

        var decision = Assert.Single(ReviewDecisionLog.ReadAll(_workspace, Project));
        Assert.Equal(ReviewDecisionKind.Reissue, decision.Kind);
        Assert.NotNull(decision.CouncilReaction);
    }

    [Fact]
    public async Task TwoDoneJobs_AreReviewedConcurrentlyInReadOnlyPool()
    {
        // Req 1: two DONE tasks must be in auto-review at the same time. The
        // probe blocks each aspect call until BOTH jobs have an aspect in
        // flight, then asserts the high-water mark of concurrent jobs was 2.
        // A sequential regression can never reach 2 (job B does not start until
        // job A fully drains) - the probe's bounded wait makes that fail fast
        // rather than hang.
        SeedReviewJobWithDone("alpha-job");
        SeedReviewJobWithDone("bravo-job");

        var probe = new ConcurrencyProbe(expectDistinct: 2, new[] { "alpha-job", "bravo-job" });
        var orchestrator = BuildOrchestrator(probe.AspectCli, maxParallelReviews: 4);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(probe.MaxConcurrentJobs >= 2,
            $"expected the two DONE reviews to overlap in the read-only pool, but the high-water mark was {probe.MaxConcurrentJobs}");

        // Both reach the accept verdict and promote to 5-human-review.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "alpha-job")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "bravo-job")));
    }

    [Fact]
    public async Task SingleSlot_StillReviewsBothJobs_OneAtATime()
    {
        // MaxParallelReviews=1 is the sequential special case (ParallelSlotPolicy
        // admits one read-only task, serialises the rest). Both jobs are still
        // reviewed in the one tick - the pool drains them in sequence.
        SeedReviewJobWithDone("first-job");
        SeedReviewJobWithDone("second-job");
        var orchestrator = BuildOrchestrator(PassStub, maxParallelReviews: 1);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "first-job")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "second-job")));
    }

    private static readonly AspectCli PassStub =
        (_, _, _, _, _, _) => Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

    private static readonly AspectCli BlockStub =
        (_, _, _, _, _, _) => Task.FromResult("[[ASPECT_VERDICT: status=block; summary=defect]]\n[[TASK_DONE]]");

    private delegate Task<string> AspectCli(
        string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct);

    private void SeedReviewJobWithDone(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing for {slug}.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        AspectCli aspectCli,
        int maxParallelReviews = 4,
        string? gradeReply = null)
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
            ["ReviewDecisionOrchestrator:MaxParallelReviews"] = maxParallelReviews.ToString(),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        aspectRunner.CliRunner = (aspectId, cli, model, prompt, timeout, ct) =>
            aspectCli(aspectId, cli, model, prompt, timeout, ct);

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
        AgentStudio.Review.CodeReviewStepService? codeReviewStep = null;
        if (gradeReply is not null)
        {
            codeReviewStep = new AgentStudio.Review.CodeReviewStepService(
                prompts, NullLogger<AgentStudio.Review.CodeReviewStepService>.Instance);
            codeReviewStep.CliRunner = (_, _, _, _, _) => Task.FromResult(gradeReply);
        }

        return new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: null,
            pipelineLog: pipelineLog,
            lintScssRunner: null,
            codeReviewStep: codeReviewStep);
    }

    /// <summary>
    /// Aspect-CLI stub that proves job-level concurrency. Each aspect call
    /// identifies its owning job from the prompt (the job id appears in every
    /// aspect prompt), bumps a per-job in-flight counter, and parks until
    /// <paramref name="expectDistinct"/> distinct jobs are concurrently in
    /// flight. <see cref="MaxConcurrentJobs"/> records the high-water mark so
    /// the test can assert the reviews actually overlapped.
    /// </summary>
    private sealed class ConcurrencyProbe
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _inFlightByJob = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectDistinct;
        private readonly IReadOnlyList<string> _jobIds;

        public int MaxConcurrentJobs { get; private set; }

        public ConcurrencyProbe(int expectDistinct, IReadOnlyList<string> jobIds)
        {
            _expectDistinct = expectDistinct;
            _jobIds = jobIds;
        }

        public async Task<string> AspectCli(
            string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
        {
            var jobId = _jobIds.FirstOrDefault(id => prompt.Contains(id, StringComparison.Ordinal)) ?? "unknown";
            bool reached;
            lock (_gate)
            {
                _inFlightByJob.TryGetValue(jobId, out var n);
                _inFlightByJob[jobId] = n + 1;
                var distinct = _inFlightByJob.Count(kv => kv.Value > 0);
                if (distinct > MaxConcurrentJobs) MaxConcurrentJobs = distinct;
                reached = distinct >= _expectDistinct;
            }
            if (reached) _allArrived.TrySetResult(true);

            // Bounded so a sequential regression fails the >= 2 assertion fast
            // instead of deadlocking the test run.
            await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));

            lock (_gate)
            {
                _inFlightByJob[jobId] = _inFlightByJob[jobId] - 1;
            }
            return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";
        }
    }
}
