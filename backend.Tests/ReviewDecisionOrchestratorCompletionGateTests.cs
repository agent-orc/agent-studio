using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the structured completion gate wired into <c>ProcessDoneAsync</c>.
/// Status prose cannot reopen a TASK_DONE run; explicit terminal/process
/// evidence controls turn and implementation completion, then deterministic
/// checks and structured aspect review control task acceptance.
/// </summary>
public class ReviewDecisionOrchestratorCompletionGateTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public ReviewDecisionOrchestratorCompletionGateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "orch-gate-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task TaskDone_OpenItemsInStatus_DoesNotTreatProseAsStructuredOpenWork()
    {
        SeedReviewJobWithDone("open-items-job",
            status: "## Open Items\n- [ ] Wire the new route into the shell\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "open-items-job");
        Assert.True(Directory.Exists(folder), "status prose should not override a structured TASK_DONE terminal");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"complete\"", pipelineJson);
        Assert.True(aspect.Invocations > 0);
        Assert.True(File.Exists(Path.Combine(folder, CompletionAcceptanceRecord.FileName)));
    }

    [Fact]
    public async Task TaskDone_SuccessButHistoricalBuildFailureProse_ContinuesToDeterministicChecks()
    {
        // ASS-764: the run claims Result: Success while its own Notes report a
        // build failure. The contradiction rule must catch it rather than let
        // auto-review accept it with concerns.
        SeedReviewJobWithDone("contradiction-job",
            status: "Result: Success\n\n## Notes\nFinal build failed with error CS0246.\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "contradiction-job");
        Assert.True(Directory.Exists(folder), "status prose should not replace the deterministic build gate");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"complete\"", pipelineJson);
        Assert.True(aspect.Invocations > 0);
    }

    [Fact]
    public async Task TaskDone_OpenItemsProse_BudgetExhausted_DoesNotOpaqueCountEscalate()
    {
        SeedReviewJobWithDone("escalate-job",
            status: "## Open Items\n- [ ] Finish the migration\n");
        var aspect = new CountingAspect();
        // maxReissues=0 -> the budget is exhausted at the first encounter, so the
        // gate must escalate instead of reissuing.
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 0);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "escalate-job");
        Assert.True(Directory.Exists(folder), "prose must not escalate even when the reissue budget is exhausted");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"complete\"", pipelineJson);
        Assert.True(aspect.Invocations > 0);
    }

    [Fact]
    public async Task TaskDone_CleanCloseOut_PassesGate_AndRunsAspects()
    {
        // Control: a clean close-out records the post-core Orchestrator-Review row
        // as "complete" and falls through to the aspect review + final decision.
        SeedReviewJobWithDone("clean-job",
            status: "## Summary\nDone and verified.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "clean-job");
        Assert.True(Directory.Exists(folder), "a clean accept should promote to 5-human-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"complete\"", pipelineJson);
        Assert.True(aspect.Invocations > 0, "a clean gate must let the aspect review run");
    }

    [Fact]
    public async Task TaskDone_BuildTestGateFails_BudgetLeft_ReissuesBeforeAspects()
    {
        SeedReviewJobWithDone("build-red-job",
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 123,
            "TaskJsonFile.cs(10,20): error CS1061: missing member",
            "dotnet build exit 1", true, false));
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "build-red-job");
        Assert.True(Directory.Exists(folder), "a red deterministic build gate should reissue to 2-ready");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.BuildTestGateStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"fail\"", pipelineJson);
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
        Assert.Equal(0, aspect.Invocations);
    }

    [Fact]
    public async Task CodeFailureAfterFromScratchRetry_ConsumesBudgetAndPersistsCacheDecision()
    {
        const string slug = "build-red-after-clean-retry";
        SeedReviewJobWithDone(slug,
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var cacheDecision = new BuildTestGateDependencyCacheDecision(
            "41ec315875d8dee864f057bf", true, 2_764_800, 83_912_704,
            true, "cached-tree-verification-failure", true, false);
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 123,
            "cached tree failed; cache evicted; clean tree failed",
            "npm run build exit 1; dependency cache: " +
            BuildTestGateRunner.DependencyCacheDecisionSummary(cacheDecision),
            false, true)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            FailureFingerprint = "code:after-clean-retry",
            DependencyCacheDecision = cacheDecision,
        };
        var orchestrator = BuildOrchestrator(
            new CountingAspect().Cli, maxReissues: 1,
            new FakeBuildTestGateRunner(result));

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(folder));
        var decision = Assert.Single(
            ReviewDecisionLog.ReadAll(_workspace, Project),
            item => item.JobId == slug);
        Assert.Equal(ReviewDecisionKind.Reissue, decision.Kind);
        Assert.Equal("code:after-clean-retry", decision.FailureFingerprint);
        Assert.Contains("rerunFromScratch=yes", decision.Reason);
        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("repository=41ec315875d8dee864f057bf", pipelineJson);
        Assert.Contains("evicted=yes", pipelineJson);
    }

    [Fact]
    public async Task AntiChurnEscalationNamesFingerprintAndLastGateCacheDecision()
    {
        const string slug = "build-red-at-ceiling";
        SeedReviewJobWithDone(slug,
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var cacheDecision = new BuildTestGateDependencyCacheDecision(
            "41ec315875d8dee864f057bf", true, 2_764_800, 83_912_704,
            true, "cached-tree-verification-failure", true, false);
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 123, "clean retry failed",
            "npm run build exit 1", false, true)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            FailureFingerprint = "code:stable-fingerprint",
            DependencyCacheDecision = cacheDecision,
        };
        var orchestrator = BuildOrchestrator(
            new CountingAspect().Cli, maxReissues: 0,
            new FakeBuildTestGateRunner(result));

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, slug)));
        var decision = Assert.Single(
            ReviewDecisionLog.ReadAll(_workspace, Project),
            item => item.JobId == slug);
        Assert.Contains("fingerprint=code:stable-fingerprint", decision.Reason);
        Assert.Contains("last gate cache decision:", decision.Reason);
        Assert.Contains("repository=41ec315875d8dee864f057bf", decision.Reason);
        Assert.Contains("rerunFromScratch=yes", decision.Reason);
    }

    [Fact]
    public async Task HistoricalReasonTextFromAnotherAttemptCannotFormDoubleFailure()
    {
        const string slug = "build-new-attempt";
        var currentSha = new string('b', 40);
        SeedReviewJobWithDone(slug,
            "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, currentSha, "lease-new");
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            DateTime.UtcNow.AddMinutes(-5), slug, Project, ReviewDecisionKind.Reissue,
            ReviewDecisionOrchestrator.BuildTestGateReissueReasonPrefix
            + $"attempt=lease-new;subject={currentSha};gate={PipelineCatalogue.BuildTestGateStepId};fingerprint=code:stable",
            "old", "old failure", "old follow-up")
        {
            AttemptChainId = "lease-old",
            GateId = PipelineCatalogue.BuildTestGateStepId,
            SubjectSha = new string('a', 40),
            FailureFingerprint = "code:stable",
            FailureKind = BuildTestGateFailureKind.Code.ToString(),
        });
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 10, "new compile error", "dotnet build exit 1", true, false)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            FailureFingerprint = "code:stable",
        };
        var orchestrator = BuildOrchestrator(
            new CountingAspect().Cli, maxReissues: 3, new FakeBuildTestGateRunner(result));

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, slug)));
        var decisions = ReviewDecisionLog.ReadAll(_workspace, Project).Where(d => d.JobId == slug).ToList();
        Assert.Equal(2, decisions.Count);
        Assert.Equal("lease-new", decisions[^1].AttemptChainId);
        Assert.Equal(currentSha, decisions[^1].SubjectSha);
    }

    [Fact]
    public async Task InfrastructureFailureRetriesSameSubjectWithoutCodingReissue()
    {
        const string slug = "build-infrastructure";
        var sha = new string('c', 40);
        SeedReviewJobWithDone(slug,
            "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, sha, "lease-infra");
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 10,
            "error MSB3027: file is locked", "dotnet build exit n/a", true, false)
        {
            FailureKind = BuildTestGateFailureKind.Lock,
            FailureFingerprint = "lock:stable",
        };
        var gate = new FakeBuildTestGateRunner(result);
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, maxReissues: 3, gate);
        orchestrator.BuildTestGateRetryBackoff = _ => TimeSpan.Zero;

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(3, gate.CallCount);
        Assert.All(gate.Requests, request =>
        {
            Assert.Equal(sha, request.ExpectedSha);
            Assert.Equal("lease-infra", request.AttemptChainId);
        });
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, slug)));
        var decision = Assert.Single(
            ReviewDecisionLog.ReadAll(_workspace, Project),
            item => item.JobId == slug);
        Assert.Equal(ReviewDecisionKind.Escalate, decision.Kind);
        Assert.StartsWith(
            "[auto-review-escalation] "
            + ReviewDecisionOrchestrator.BuildTestGateInfrastructureReasonPrefix,
            decision.Reason);
        Assert.Equal("lease-infra", decision.AttemptChainId);
    }

    [Fact]
    public async Task TaskDone_BuildTestGateGreen_ContinuesToAspects()
    {
        SeedReviewJobWithDone("build-green-job",
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 123, "build passed", "build gate passed", true, false));
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "build-green-job");
        Assert.True(Directory.Exists(folder), "a green deterministic build gate should continue through auto-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.BuildTestGateStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"ok\"", pipelineJson);
        Assert.True(aspect.Invocations > 0, "a green build gate must let the aspect review run");
    }

    [Fact]
    public async Task TaskDone_UnrelatedTestFailure_AdvancesAndPersistsSeparateFinding()
    {
        const string slug = "build-unrelated-red";
        SeedReviewJobWithDone(slug,
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Warn, 0, 25, "baseline red",
            "work-package gate passed with 1 separate non-blocking finding; test-level=work-package; selected=2; full-suite=not-run; omitted=1",
            true, false)
        {
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.WorkPackage,
                DiffInput = ["src/feature.cs"],
                SelectedCandidateIds = ["test-feature"],
                SelectedCommands = ["test-feature", "test-baseline"],
                OmittedTestCommands = ["test-all"],
                Selector = "deterministic+llm",
                SelectorModel = "model-x",
                AdvisorReason = "shared namespace risk",
            },
            Findings = [new BuildTestGateFinding(
                "out-of-work-package-test-failure",
                TestExecutionLevels.Continuous,
                "test-baseline",
                "baseline failed outside the selected work package",
                1,
                "expected 1 but got 2")],
        };
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(
            aspect.Cli, maxReissues: 3, new FakeBuildTestGateRunner(result));

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Assert.True(Directory.Exists(folder), "an unrelated baseline failure must not block the card");
        var findingPath = Path.Combine(folder, "post-steps", "test-findings-1.json");
        Assert.True(File.Exists(findingPath));
        var findingJson = File.ReadAllText(findingPath);
        Assert.Contains("out-of-work-package-test-failure", findingJson);
        Assert.Contains("\"blocking\": false", findingJson);
        var gateLog = File.ReadAllText(Path.Combine(folder, "post-steps", "build-test-gate-1.log"));
        Assert.Contains("\"Level\": \"work-package\"", gateLog);
        Assert.Contains("\"src/feature.cs\"", gateLog);
        var selectionStart = gateLog.IndexOf("--- test-selection.json ---\n", StringComparison.Ordinal)
            + "--- test-selection.json ---\n".Length;
        var selectionEnd = gateLog.IndexOf(
            "\n--- process-evidence.json ---", selectionStart, StringComparison.Ordinal);
        var loggedSelection = JsonSerializer.Deserialize<TestSelectionAudit>(
            gateLog[selectionStart..selectionEnd]);
        Assert.NotNull(loggedSelection);
        Assert.Equal(["test-feature"], loggedSelection!.SelectedCandidateIds);
        Assert.Equal("deterministic+llm", loggedSelection.Selector);
        Assert.Equal("model-x", loggedSelection.SelectorModel);
        Assert.Equal("shared namespace risk", loggedSelection.AdvisorReason);
        Assert.True(aspect.Invocations > 0);
    }

    [Fact]
    public async Task TaskDone_BuildGateRequiresExactSubjectWithoutSharedCheckoutCommandFallback()
    {
        const string slug = "build-worktree-job";
        SeedReviewJobWithDone(slug,
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        SeedGitRepository();
        var worktree = Path.Combine(_workspace, "worktrees", slug);
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        RunGit(_watchPath, "branch", WorktreeTaskLifecycle.BranchFor(slug));
        RunGit(_watchPath, "worktree", "add", worktree, WorktreeTaskLifecycle.BranchFor(slug));

        var buildGate = new FakeBuildTestGateRunner(_ =>
            new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 10,
                "build passed in detached exact-subject workspace", "build gate passed", true, false));
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(_watchPath), Path.GetFullPath(buildGate.LastRequest!.RepositoryPath));
        Assert.True(buildGate.LastRequest.RequireExactSubject);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, slug)),
            "a lock in the shared checkout must not make the task-worktree gate red");
        Assert.True(aspect.Invocations > 0);
    }

    [Fact]
    public void BuildTestGate_DocOnlyDiff_IsSkippedByDiffClassifier()
    {
        Assert.False(BuildTestGateRunner.HasCodeDiff([
            "docs/research/note.md",
            "README.md",
            ".orchestrator/status.md",
        ]));
        Assert.True(BuildTestGateRunner.HasCodeDiff([
            "src/AgentTaskboard.Shared/Models/TaskModels.cs",
        ]));
        Assert.True(BuildTestGateRunner.HasCodeDiff([
            "frontend/src/app/app.component.ts",
        ]));
    }

    private sealed class CountingAspect
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);

        public Task<string> Cli(
            string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        }
    }

    private delegate Task<string> AspectCli(
        string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct);

    private sealed class FakeBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly Func<BuildTestGateRequest, BuildTestGateResult> _result;
        private int _callCount;

        public string? LastRepositoryPath { get; private set; }
        public BuildTestGateRequest? LastRequest { get; private set; }
        public int CallCount => Volatile.Read(ref _callCount);
        public List<BuildTestGateRequest> Requests { get; } = [];

        public FakeBuildTestGateRunner(BuildTestGateResult result)
        {
            _result = _ => result;
        }

        public FakeBuildTestGateRunner(Func<BuildTestGateRequest, BuildTestGateResult> result) => _result = result;

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            LastRepositoryPath = request.RepositoryPath;
            LastRequest = request;
            Interlocked.Increment(ref _callCount);
            lock (Requests) Requests.Add(request);
            return Task.FromResult(_result(request));
        }
    }

    private void SeedGitRepository()
    {
        RunGit(_watchPath, "init", "-q", "-b", "main");
        RunGit(_watchPath, "config", "user.email", "test@example.com");
        RunGit(_watchPath, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_watchPath, "seed.txt"), "seed");
        RunGit(_watchPath, "add", "seed.txt");
        RunGit(_watchPath, "commit", "-q", "-m", "seed");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
    }

    private void SeedReviewJobWithDone(string slug, string status)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing for {slug}.\n");
        File.WriteAllText(Path.Combine(dir, "status.md"), status);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");
    }

    private void WriteRemoteSubject(string slug, string sha, string attemptChainId)
    {
        ReviewSubjectStore.Write(Path.Combine(_watchPath, TaskStates.AutoReview, slug), new ReviewSubjectRecord
        {
            TaskKey = slug,
            RunAttemptId = $"run-{attemptChainId}",
            Project = Project,
            Repository = "https://example.invalid/repo.git",
            ResultSha = sha,
            AttemptChainId = attemptChainId,
            Executor = "remote-test",
            LeaseId = attemptChainId,
            FencingToken = 1,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        AspectCli aspectCli,
        int maxReissues,
        IBuildTestGateRunner? buildGate = null)
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

        return new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: git,
            pipelineLog: pipelineLog,
            lintScssRunner: null,
            buildTestGateRunner: buildGate);
    }
}
