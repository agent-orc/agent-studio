using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the post-aspect Evidence-Gate wired into <c>ProcessDoneAsync</c>
/// (ASS-764). After the parallel aspect review, a DONE run that left no visual
/// proof for a UI/bug task, or whose <c>tests-and-evidence</c> aspect is not
/// clean, must NOT be accepted-with-concerns: it is reissued to 2-ready with a
/// verification demand (or escalated to 5-human-review once the reissue budget
/// is spent). Unlike the static <see cref="CompletionGate"/>, this gate runs
/// AFTER the aspects, so the aspect CLI is invoked before it fires.
/// </summary>
public class ReviewDecisionOrchestratorEvidenceGateTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    private const string CleanStatus = "## Summary\nDone and verified.\n\nResult: Success\n\n## Open Items\nNone\n";

    public ReviewDecisionOrchestratorEvidenceGateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "orch-evidence-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task BugTask_NoVisualEvidence_AspectsPass_ReissuesWithEvidenceDemand()
    {
        // The ASS-764 core: a bug task claims success, every aspect passes, but
        // the run left no screenshot/e2e proof. The gate must reissue with an
        // evidence demand rather than accept it.
        SeedReviewJobWithDone("no-evidence-bug", CleanStatus, taskType: TaskTypes.Bug);
        var invocations = 0;
        var orchestrator = BuildOrchestrator(
            _ => { Interlocked.Increment(ref invocations); return PassVerdict; }, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "no-evidence-bug");
        Assert.True(Directory.Exists(folder), "a bug task with no visual evidence should reissue to 2-ready");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "no-evidence-bug")));

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorDecisionStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);

        // The gate is post-aspect: the aspect CLI ran before it fired.
        Assert.True(invocations > 0, "evidence gate must run AFTER the aspect review");

        // The reissue follow-up demands visual proof.
        var followUp = File.ReadAllText(Path.Combine(folder, "orchestrator-follow-up.md"));
        Assert.Contains("screenshot", followUp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BugTask_WhitespaceReviewEvidence_AspectsPass_ReissuesWithEvidenceDemand()
    {
        // A review-evidence file that exists but contains no real entry must
        // fail closed, same as a missing artifact. The gate runs after aspects,
        // then upgrades the residual accept to a verification-demanding reissue.
        SeedReviewJobWithDone("whitespace-evidence-bug", CleanStatus, taskType: TaskTypes.Bug, reviewEvidenceLog: " \r\n\t\r\n");
        var invocations = 0;
        var orchestrator = BuildOrchestrator(
            _ => { Interlocked.Increment(ref invocations); return PassVerdict; }, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "whitespace-evidence-bug");
        Assert.True(Directory.Exists(folder), "whitespace-only review evidence should not satisfy the evidence gate");
        Assert.True(invocations > 0, "evidence gate must run AFTER the aspect review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);

        var followUp = File.ReadAllText(Path.Combine(folder, "orchestrator-follow-up.md"));
        Assert.Contains("screenshot", followUp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestsAndEvidenceConcern_WithVisualEvidence_ReissuesNotAcceptWithConcerns()
    {
        // Requirement 2: the tests-and-evidence aspect raises a concern (failing
        // build / missing test). Even with a screenshot on disk, that concern is
        // blocking - reissue, never accept-with-concerns into 5-human-review.
        SeedReviewJobWithDone("te-concern", CleanStatus, taskType: TaskTypes.Chore, withScreenshot: true);
        var orchestrator = BuildOrchestrator(aspect => aspect switch
        {
            "tests-and-evidence" => "[[ASPECT_VERDICT: status=concerns; summary=Build failed with error CS0246; no regression test.]]\n[[TASK_DONE]]",
            _ => PassVerdict,
        }, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "te-concern");
        Assert.True(Directory.Exists(folder), "an unclean tests-and-evidence aspect must reissue, not accept-with-concerns");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "te-concern")));

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
        Assert.DoesNotContain("accept-with-concerns", pipelineJson);
    }

    [Fact]
    public async Task RequirementFitGoalMissConcern_ReissuesNotAcceptWithConcerns()
    {
        // A clear "goal not met" signal is too strong for ordinary
        // accept-with-concerns. The solution-quality gate uses the shared
        // reissue budget and sends the card back with concrete feedback.
        SeedReviewJobWithDone("goal-miss", CleanStatus, taskType: TaskTypes.Chore, withScreenshot: true);
        var orchestrator = BuildOrchestrator(aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=concerns; summary=Core task goal not met, the change only updates comments.]]\n[[TASK_DONE]]",
            _ => PassVerdict,
        }, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "goal-miss");
        Assert.True(Directory.Exists(folder), "a clear goal-miss concern must reissue, not accept-with-concerns");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "goal-miss")));

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
        Assert.DoesNotContain("accept-with-concerns", pipelineJson);

        var followUp = File.ReadAllText(Path.Combine(folder, "orchestrator-follow-up.md"));
        Assert.Contains("Core task goal not met", followUp);
        Assert.Contains("Do not redo already-complete work", followUp);
    }

    [Fact]
    public async Task BugTask_WithScreenshot_AspectsPass_PromotesToHumanReview()
    {
        // Control / green path: a bug task that DID ship a screenshot and passes
        // every aspect flows normally to 5-human-review.
        SeedReviewJobWithDone("verified-bug", CleanStatus, taskType: TaskTypes.Bug, withScreenshot: true);
        var orchestrator = BuildOrchestrator(_ => PassVerdict, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "verified-bug");
        Assert.True(Directory.Exists(folder), "a verified bug fix should promote to 5-human-review");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "verified-bug")));

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"accept\"", pipelineJson);
    }

    [Fact]
    public async Task BugTask_NoVisualEvidence_BudgetExhausted_EscalatesToHumanReview()
    {
        SeedReviewJobWithDone("escalate-bug", CleanStatus, taskType: TaskTypes.Bug);
        // maxReissues=0 -> budget exhausted at first encounter; the gate escalates.
        var orchestrator = BuildOrchestrator(_ => PassVerdict, maxReissues: 0);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Escalated, "escalate-bug");
        Assert.True(Directory.Exists(folder), "missing evidence with no budget left should escalate to 5e-escalated");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorDecisionStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"escalate\"", pipelineJson);
    }

    [Fact]
    public async Task AspectBlock_BudgetExhausted_EscalatesInsteadOfReissuingToReady()
    {
        // ASS-794 loop-breaker on the multi-aspect BLOCK path. A finished task
        // (clean close-out: Open Items None, Result Success) is BLOCKed by an
        // aspect, but the shared reissue budget is already spent (maxReissues=0).
        // The block path must NOT loop back to 2-ready; it escalates to
        // 5-human-review so the card cannot pendulum 2-ready <-> run forever.
        SeedReviewJobWithDone("loop-block", CleanStatus, taskType: TaskTypes.Chore);
        var orchestrator = BuildOrchestrator(aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=block; summary=Verified a doc but made no code changes.]]\n[[TASK_DONE]]",
            _ => PassVerdict,
        }, maxReissues: 0);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "loop-block")),
            "a blocked task with no reissue budget left must escalate to 5e-escalated, not reissue");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "loop-block")),
            "the loop-breaker must prevent a pendulum back to 2-ready");

        var pipelineJson = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.Escalated, "loop-block", PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorDecisionStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"escalate\"", pipelineJson);
    }

    private const string PassVerdict = "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";

    private void SeedReviewJobWithDone(
        string slug,
        string status,
        string taskType,
        bool withScreenshot = false,
        string? reviewEvidenceLog = null)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\",\"taskType\":\"{taskType}\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing for {slug}.\n");
        File.WriteAllText(Path.Combine(dir, "status.md"), status);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");

        if (withScreenshot)
        {
            var shots = Path.Combine(dir, "results", "playwright", "fix-spec");
            Directory.CreateDirectory(shots);
            File.WriteAllBytes(Path.Combine(shots, "after.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }

        if (reviewEvidenceLog is not null)
        {
            var results = Path.Combine(dir, "results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "review-evidence.jsonl"), reviewEvidenceLog);
        }
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(Func<string, string> aspectStub, int maxReissues)
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
            ["ReviewDecisionOrchestrator:MaxParallelReviews"] = "4",
            ["ReviewDecisionOrchestrator:MaxAutoReissueAttempts"] = maxReissues.ToString(),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        aspectRunner.CliRunner = (aspectId, _, _, _, _, _) => Task.FromResult(aspectStub(aspectId));

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

        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);

        return new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: null,
            pipelineLog: pipelineLog,
            lintScssRunner: null);
    }
}
