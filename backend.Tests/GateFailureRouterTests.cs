using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3009: the delivery-chain reconciler, the real integration-status lookup
/// over a throwaway repository, and a captured merge-gate log in the card's
/// <c>post-steps</c>. These are the paths that used to park every red gate as
/// an <c>operator-decision</c>; the assertions are about the lane the card ends
/// in and the typed blocker it carries there.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class GateFailureRouterTests : IDisposable
{
    private const string Project = "Fixture";

    /// <summary>A fixed instant: the routing must not depend on the day the test runs.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public GateFailureRouterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-failure-router-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Gate Failure Router Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    [Theory]
    [InlineData("environment-worker-crash.AGT-2724.log", TaskStates.AutoReview)]
    [InlineData("environment-run-budget.AGT-2839.log", TaskStates.AutoReview)]
    [InlineData("environment-transport.derived.log", TaskStates.AutoReview)]
    [InlineData("environment-worker-crash.AGT-2724.log", TaskStates.HumanReview)]
    public async Task EnvironmentClass_NeverReachesHumanReview_AndIsHandedToTheReplayLadder(
        string fixture, string lane)
    {
        var stack = Build();
        var id = SeedRedGate(stack, "worker crash", fixture, lane);

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        Assert.Equal(TaskStates.AutoReview, card.State);
        Assert.Null(card.ParkedBlocker);
        // The durable code now names the class the product decided, so the
        // bounded gate-environment ladder owns the replay.
        var step = stack.Integration.ReadLatestMergeStep(card)!;
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, step.FailureCode);
        Assert.StartsWith("[gate-triage environment]", step.Reason);
        var status = stack.Integration.BuildLookup([card])[card.TaskKey];
        var ladder = GateEnvironmentRetryPolicy.Decide(card, status, reviewPassed: true, attemptsSpent: 0,
            lastAttemptAt: null, failedAt: Now, GateEnvironmentRetryOptions.Default,
            Now);
        Assert.Equal(GateEnvironmentRetryAction.Wait, ladder.Action);

        // Repeat sweeps neither park it nor raise anything.
        await stack.Reconciler.RunOnceAsync();
        Assert.Equal(TaskStates.AutoReview, Card(stack, id).State);
        Assert.Empty(stack.Interventions.List(_watchPath));
    }

    [Fact]
    public async Task EnvironmentClass_WithTheLadderSpent_ParksTyped_NotAsOperatorDecision()
    {
        var stack = Build();
        var id = SeedRedGate(stack, "worker crash", "environment-worker-crash.AGT-2724.log", TaskStates.AutoReview);
        var folder = Card(stack, id).FolderPath;
        var delivery = ReviewSubjectStore.Read(folder)!.ResultSha;
        GateEnvironmentRetryReceipts.RecordParked(stack.Timeline, folder, delivery, 3, "ladder spent");

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        Assert.Equal(TaskStates.Escalated, card.State);
        Assert.Equal(GateFailureParkCategories.EnvironmentExhausted, card.ParkedBlocker?.BlockerType);
    }

    [Fact]
    public async Task UndecidableClass_ParksForAPerson_StatingClassAndMissingEvidence()
    {
        var stack = Build();
        var id = SeedRedGate(stack, "conflict", "undecidable-conflict.AGT-2712.log", TaskStates.AutoReview);

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        Assert.Equal(TaskStates.Escalated, card.State);
        Assert.Equal(GateFailureParkCategories.Undecidable, card.ParkedBlocker?.BlockerType);
        Assert.Contains("class undecidable", card.ParkedBlocker!.Reason);
        Assert.Contains("Missing evidence:", card.ParkedBlocker.Reason);
        Assert.NotEqual(ParkedBlockerCatalog.OperatorDecision, card.ParkedBlocker.BlockerType);
    }

    [Fact]
    public async Task ProductClass_StartsNoCauseCard_AndParksTypedWhenTheFixRoundCannotStart()
    {
        // No TaskRunnerService in this stack, so the fix round cannot start and
        // the bounded fallback parks with the product category.
        var stack = Build();
        var id = SeedRedGate(stack, "product", "product.AGT-2722.log", TaskStates.AutoReview);

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        Assert.Equal(TaskStates.Escalated, card.State);
        Assert.Equal(GateFailureParkCategories.Product, card.ParkedBlocker?.BlockerType);
        Assert.Contains("AutoPushStrategyTests.CompletedPushWorker_PushesQueuedCommitToMain", card.ParkedBlocker!.Reason);
        Assert.Empty(stack.Interventions.List(_watchPath));
        var receipt = GateFailureRouter.ReadReceipt(card.FolderPath)!;
        Assert.Equal("fix-round-could-not-start", receipt.Route!.Reason);
    }

    [Fact]
    public async Task SecondCardWithTheSameFingerprint_OpensACauseCard_AndWaitsOnIt()
    {
        var stack = Build();
        var firstId = SeedRedGate(stack, "first", "shared-cause.AGT-2677.log", TaskStates.AutoReview);

        await stack.Reconciler.RunOnceAsync();

        var first = Card(stack, firstId);
        Assert.Equal(GateFailureParkCategories.Product, first.ParkedBlocker?.BlockerType);
        Assert.Empty(stack.Interventions.List(_watchPath));

        var secondId = SeedRedGate(stack, "second", "shared-cause.AGT-2752.log", TaskStates.AutoReview);
        await stack.Reconciler.RunOnceAsync();

        var cause = Assert.Single(stack.Interventions.List(_watchPath));
        Assert.Equal("gate/shared-cause", cause.FailureClass);
        var second = Card(stack, secondId);
        Assert.Contains(second.Key!, cause.AffectedCards);
        Assert.DoesNotContain(first.Key!, cause.AffectedCards);
        Assert.Contains(
            "batchmovejobtests.endpoint_reportsprogress_continuesafteritemfailure_anddoesnotblockreviewread",
            cause.Signature);
        var causeCard = stack.Scanner.FindJob(cause.FollowUpTaskId, _watchPath)!;
        Assert.Equal(TaskStates.Preparation, causeCard.State);

        Assert.Equal(TaskStates.Escalated, second.State);
        Assert.Equal(GateFailureParkCategories.SharedCause, second.ParkedBlocker?.BlockerType);
        Assert.Contains($"Waiting on {cause.FollowUpKey}", second.ParkedBlocker!.Reason);
        Assert.Contains(cause.FollowUpKey, second.References.BlockedBy);

        // The counter saw both cards under one fingerprint.
        var fingerprint = GateFailureRouter.ReadReceipt(second.FolderPath)!.Triage!.Fingerprint;
        Assert.Equal(new[] { first.Key!, second.Key! }.Order(StringComparer.Ordinal),
            stack.Counter.Cards(fingerprint));
    }

    [Fact]
    public async Task IntegrationBranchClass_AttachesToTheCauseCard_InsteadOfChargingTheCard()
    {
        var stack = Build();
        var id = SeedRedGate(stack, "red baseline", "integration-branch.derived-from-AGT-2752.log", TaskStates.AutoReview);

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        var cause = Assert.Single(stack.Interventions.List(_watchPath));
        Assert.Equal(GateFailureParkCategories.SharedCause, card.ParkedBlocker?.BlockerType);
        Assert.Contains(cause.FollowUpKey, card.References.BlockedBy);
        Assert.Equal(AcceptedIntegrationFailureCodes.BuildGateFailed,
            stack.Integration.ReadLatestMergeStep(card)!.FailureCode);
    }

    [Fact]
    public async Task GateEnvironmentFailureWithARedBaseline_IsTakenAwayFromTheReplayLadder()
    {
        // AGT-2916 types a red-baseline failure as a gate environment failure;
        // replaying it would only re-measure the broken branch.
        var stack = Build();
        var id = SeedRedGate(stack, "red baseline", "integration-branch.derived-from-AGT-2752.log", TaskStates.AutoReview,
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure);

        await stack.Reconciler.RunOnceAsync();

        var card = Card(stack, id);
        Assert.Equal(AcceptedIntegrationFailureCodes.BuildGateFailed,
            stack.Integration.ReadLatestMergeStep(card)!.FailureCode);
        Assert.Equal(GateFailureParkCategories.SharedCause, card.ParkedBlocker?.BlockerType);
    }

    /// <summary>
    /// Creates the card through the mutation service, the way the product does,
    /// then gives it a delivery commit, a passed-review subject, the captured
    /// gate log, and the failed merge step. The returned id doubles as the
    /// card key the assertions use.
    /// </summary>
    private string SeedRedGate(
        Stack stack, string label, string fixture, string lane,
        string failureCode = AcceptedIntegrationFailureCodes.BuildGateFailed)
    {
        var id = stack.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = label,
            WatchPath = _watchPath,
            TargetState = TaskStates.AutoReview,
            PromptMarkdown = $"Implement {label}.",
        })!;
        Assert.NotNull(id);
        var created = stack.Scanner.FindJob(id, _watchPath)!;
        var key = created.Key ?? id;
        var folder = created.FolderPath;

        Git(_repo, "checkout", "-q", "-b", "task/" + key, "develop");
        File.WriteAllText(Path.Combine(_repo, key + ".txt"), key + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + key);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var taskJson = Path.Combine(folder, "task.json");
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(taskJson))!;
        void Set(string name, object value) => fields[name] = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Set("state", lane);
        Set("phase", LifecyclePhases.Integrating);
        Set("mode", TaskModes.Coding);
        Set("projectName", Project);
        Set("commit", Commit(deliverySha));
        Set("commits", new[] { Commit(deliverySha) });
        File.WriteAllText(taskJson, JsonSerializer.Serialize(fields,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Directory.CreateDirectory(Path.Combine(folder, "post-steps"));
        File.WriteAllText(Path.Combine(folder, "post-steps", "pre-develop-build-gate-1.log"),
            GateFailureTriagePolicyTests.Fixture(fixture));
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = key,
            RunAttemptId = "run-" + key,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + key,
            AttemptChainId = "chain-" + key,
            IntegrationBranch = "develop",
            CompletedAtUtc = Now.AddMinutes(-5),
        });
        var failedAt = Now.UtcDateTime.AddMinutes(-1);
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        stack.Pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = failedAt,
            CompletedAt = failedAt,
            Verdict = failureCode == AcceptedIntegrationFailureCodes.BuildGateFailed
                ? "gate-failed" : "gate-environment-failure",
            Reason = "The build gate blocked the merge into develop: see post-steps/pre-develop-build-gate-1.log.",
            FailureCode = failureCode,
        });
        stack.Scanner.InvalidateCache();
        return id;
    }

    private TaskInfo Card(Stack stack, string id)
    {
        stack.Scanner.InvalidateCache();
        return stack.Scanner.FindJob(id, _watchPath)!;
    }

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["DeliveryChain:Guarded"] = "true",
        }).Build();
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_watchPath, Project, DefaultWorkspace.Id);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance,
            integrationStatus: integration, configuration: configuration);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance, integrationStatus: integration,
            configuration: configuration);
        var interventions = new FailureInterventionService(mutations, scanner, timeline,
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
            NullLogger<FailureInterventionService>.Instance,
            new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
            pipelineLog: pipeline);
        var counter = new InMemoryFingerprintCounter();
        var router = new GateFailureRouter(integration, pipeline, settings, timeline, configuration,
            NullLogger<GateFailureRouter>.Instance, counter, interventions);
        var reconciler = new DeliveryChainReconciler(scanner, integration, transitions, configuration,
            NullLogger<DeliveryChainReconciler>.Instance, router);
        return new Stack(scanner, mutations, timeline, pipeline, integration, interventions, counter, reconciler);
    }

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = Now,
        attribution = "automatic",
        confidence = 1,
    };

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Gate failure router test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        TimelineLog Timeline,
        PipelineExecutionLog Pipeline,
        TaskIntegrationStatusService Integration,
        FailureInterventionService Interventions,
        InMemoryFingerprintCounter Counter,
        DeliveryChainReconciler Reconciler);

    /// <summary>The Task Server store's contract: idempotent per report key, distinct cards per fingerprint.</summary>
    private sealed class InMemoryFingerprintCounter : IGateFailureFingerprintCounter
    {
        private readonly Dictionary<string, (string Fingerprint, string Card)> _events = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Cards(string fingerprint)
            => _events.Values.Where(item => item.Fingerprint == fingerprint)
                .Select(item => item.Card).Distinct().Order(StringComparer.Ordinal).ToArray();

        public Task<IReadOnlyList<string>?> RecordAndReadCardsAsync(
            string fingerprint, string cardKey, string reportKey, CancellationToken ct)
        {
            _events.TryAdd(reportKey, (fingerprint, cardKey));
            return Task.FromResult<IReadOnlyList<string>?>(Cards(fingerprint));
        }
    }
}
