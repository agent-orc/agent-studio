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
/// AGT-2824: the bounded ladder that finally performs the retry a gate
/// environment failure already promises. Every test drives the real merge runner
/// against a throwaway repo, so the assertions are about what actually landed in
/// the integration branch and what the card records, not about a mock.
/// <para>
/// The load-bearing invariant is that a retry costs no review: attempt authority
/// must hold exactly the same review attempts after a replay as before it.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class GateEnvironmentRetryServiceTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public GateEnvironmentRetryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-environment-retry-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Gate Environment Retry Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task Sweep_WhenTheFirstRungIsDue_IntegratesWithoutSpendingANewReview()
    {
        var stack = Build();
        var delivery = SeedGateEnvironmentFailure(stack, "due", failedMinutesAgo: 6);
        var reviewsBefore = stack.Authority.GetTaskProjection(stack.TaskKey("due")).ReviewAttempts.Count;

        var sweep = await stack.Retries.RunOnceAsync();

        Assert.Equal(1, sweep.Retried);
        Assert.Equal(0, sweep.Parked);
        Assert.Equal(1, stack.Gate.Invocations);
        Assert.True(IsAncestor(delivery, "develop"), "the replayed merge did not land the delivery in develop");

        // The whole point of the card: no second review slot is burned.
        Assert.Equal(
            reviewsBefore,
            stack.Authority.GetTaskProjection(stack.TaskKey("due")).ReviewAttempts.Count);

        var receipt = Assert.Single(Receipts(stack, "due"));
        Assert.Equal(delivery, receipt.Details![GateEnvironmentRetryReceipts.DeliveryShaKey]);
        Assert.Equal(GateEnvironmentRetrySources.Sweep, receipt.Details[GateEnvironmentRetryReceipts.SourceKey]);
        Assert.Equal("1", receipt.Details[GateEnvironmentRetryReceipts.RungKey]);
    }

    [Fact]
    public async Task Sweep_BeforeTheFirstRungIsDue_WaitsAndSpendsNothing()
    {
        var stack = Build();
        SeedGateEnvironmentFailure(stack, "early", failedMinutesAgo: 1);

        var sweep = await stack.Retries.RunOnceAsync();

        Assert.Equal(1, sweep.Candidates);
        Assert.Equal(1, sweep.Waiting);
        Assert.Equal(0, sweep.Retried);
        Assert.Equal(0, stack.Gate.Invocations);
        Assert.Empty(Receipts(stack, "early"));
    }

    [Fact]
    public async Task Sweep_AfterTheLastRung_ParksWithAReasonNamingTheEnvironmentFailure()
    {
        var stack = Build();
        var delivery = SeedGateEnvironmentFailure(stack, "spent", failedMinutesAgo: 600);
        for (var rung = 1; rung <= 3; rung++) SeedRetryReceipt(stack, "spent", delivery, rung);

        var sweep = await stack.Retries.RunOnceAsync();

        Assert.Equal(1, sweep.Parked);
        Assert.Equal(0, sweep.Retried);
        Assert.Equal(0, stack.Gate.Invocations);

        var job = stack.Scanner.FindJob("spent", _watchPath)!;
        var status = stack.Integration.BuildLookup([job])[job.TaskKey];
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, status.Failure!.Code);
        Assert.Contains(
            "Parked after 3 automatic gate-environment retries",
            status.Failure.Reason,
            StringComparison.Ordinal);
        // The original gate evidence survives inside the parked reason.
        Assert.Contains("does not match .nvmrc", status.Failure.Reason, StringComparison.Ordinal);

        // Idempotent: the next sweep must not append a second parked receipt.
        var second = await stack.Retries.RunOnceAsync();
        Assert.Equal(0, second.Parked);
        Assert.Single(
            stack.Timeline.ReadAll(job.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationGateEnvironmentParked);
    }

    [Fact]
    public async Task Sweep_WithoutAPassedReviewForTheDeliverySha_NeverReplays()
    {
        var stack = Build();
        SeedGateEnvironmentFailure(stack, "unreviewed", failedMinutesAgo: 600, passedReview: false);

        var sweep = await stack.Retries.RunOnceAsync();

        Assert.Equal(0, sweep.Candidates);
        Assert.Equal(0, stack.Gate.Invocations);
        Assert.Empty(Receipts(stack, "unreviewed"));
    }

    [Fact]
    public async Task Sweep_ForABuildGateFailure_IsNotItsBusiness()
    {
        var stack = Build();
        SeedGateEnvironmentFailure(
            stack,
            "product",
            failedMinutesAgo: 600,
            failureCode: AcceptedIntegrationFailureCodes.BuildGateFailed);

        var sweep = await stack.Retries.RunOnceAsync();

        Assert.Equal(0, sweep.Candidates);
        Assert.Equal(0, stack.Gate.Invocations);
    }

    [Fact]
    public async Task RetryNow_ReplaysImmediatelyWhileTheLadderIsStillBackingOff()
    {
        var stack = Build();
        var delivery = SeedGateEnvironmentFailure(stack, "operator", failedMinutesAgo: 1);
        var job = stack.Scanner.FindJob("operator", _watchPath)!;
        var reviewsBefore = stack.Authority.GetTaskProjection(job.TaskKey).ReviewAttempts.Count;

        var result = await stack.Retries.RetryNowAsync(job);

        Assert.Equal(GateEnvironmentRetryStatus.Replayed, result.Status);
        Assert.Equal(delivery, result.DeliverySha);
        Assert.True(result.Outcome!.Value.IsSuccessfulIntegration());
        Assert.True(IsAncestor(delivery, "develop"));
        Assert.Equal(reviewsBefore, stack.Authority.GetTaskProjection(job.TaskKey).ReviewAttempts.Count);
        Assert.Equal(
            GateEnvironmentRetrySources.Operator,
            Assert.Single(Receipts(stack, "operator")).Details![GateEnvironmentRetryReceipts.SourceKey]);
    }

    [Fact]
    public async Task RetryNow_WhileASweepReplayIsRunning_IsRefusedAndDoesNotSpendASecondRung()
    {
        using var gateEntered = new SemaphoreSlim(0, 1);
        using var releaseGate = new SemaphoreSlim(0, 1);
        var stack = Build(onGateRun: () =>
        {
            gateEntered.Release();
            releaseGate.Wait(TimeSpan.FromSeconds(30));
        });
        SeedGateEnvironmentFailure(stack, "concurrent", failedMinutesAgo: 600);
        var job = stack.Scanner.FindJob("concurrent", _watchPath)!;

        // The scripted gate blocks synchronously, and the runner reaches it
        // without an intervening await, so the sweep needs its own thread.
        var sweep = Task.Run(() => stack.Retries.RunOnceAsync());
        Assert.True(await gateEntered.WaitAsync(TimeSpan.FromSeconds(30)), "the sweep never entered the gate");

        var concurrent = await stack.Retries.RetryNowAsync(job);
        releaseGate.Release();
        var completed = await sweep;

        Assert.Equal(GateEnvironmentRetryStatus.AlreadyRunning, concurrent.Status);
        Assert.Equal(1, completed.Retried);
        // One rung, one merge: the operator click must not double-spend the
        // budget or race a second merge into the integration branch.
        Assert.Single(Receipts(stack, "concurrent"));
        Assert.Equal(1, stack.Gate.Invocations);
    }

    [Fact]
    public async Task RetryNow_AfterTheLadderParked_RestartsTheBoundedLadder()
    {
        var stack = Build(gateEnvironmentFails: true);
        var delivery = SeedGateEnvironmentFailure(stack, "unpark", failedMinutesAgo: 600);
        for (var rung = 1; rung <= 3; rung++) SeedRetryReceipt(stack, "unpark", delivery, rung);
        Assert.Equal(1, (await stack.Retries.RunOnceAsync()).Parked);

        var job = stack.Scanner.FindJob("unpark", _watchPath)!;
        var result = await stack.Retries.RetryNowAsync(job);

        Assert.Equal(GateEnvironmentRetryStatus.Replayed, result.Status);
        // An operator replay is not a ladder rung; it restarts the ladder.
        Assert.Equal(0, result.Rung);
        Assert.Equal(MergeIntoIntegrationOutcome.GateEnvironmentFailure, result.Outcome);

        // The operator retry means "the host is fixed": the automatic ladder
        // starts over instead of parking again on the very next sweep.
        var afterUnpark = await stack.Retries.RunOnceAsync();
        Assert.Equal(0, afterUnpark.Parked);
        Assert.Equal(1, afterUnpark.Waiting);
    }

    [Fact]
    public async Task RetryNow_ForACardWithoutAGateEnvironmentFailure_IsRefused()
    {
        var stack = Build();
        SeedGateEnvironmentFailure(
            stack,
            "conflicted",
            failedMinutesAgo: 600,
            failureCode: AcceptedIntegrationFailureCodes.MergeConflict);
        var job = stack.Scanner.FindJob("conflicted", _watchPath)!;

        var result = await stack.Retries.RetryNowAsync(job);

        Assert.Equal(GateEnvironmentRetryStatus.NotApplicable, result.Status);
        Assert.Equal(0, stack.Gate.Invocations);
    }

    [Fact]
    public async Task RetryNow_WithoutAPassedReviewForTheDeliverySha_IsRefused()
    {
        var stack = Build();
        SeedGateEnvironmentFailure(stack, "no-review", failedMinutesAgo: 600, passedReview: false);
        var job = stack.Scanner.FindJob("no-review", _watchPath)!;

        var result = await stack.Retries.RetryNowAsync(job);

        Assert.Equal(GateEnvironmentRetryStatus.NoPassedReview, result.Status);
        Assert.Equal(0, stack.Gate.Invocations);
    }

    [Fact]
    public void Receipts_AreScopedToTheDeliveryShaTheyWereWrittenFor()
    {
        var stack = Build();
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "ledger");
        Directory.CreateDirectory(folder);
        var current = new string('a', 40);
        var previous = new string('b', 40);
        GateEnvironmentRetryReceipts.RecordRetry(
            stack.Timeline, folder, previous, GateEnvironmentRetrySources.Sweep, 1, "older delivery");
        GateEnvironmentRetryReceipts.RecordRetry(
            stack.Timeline, folder, current, GateEnvironmentRetrySources.Sweep, 1, "current delivery");

        var ledger = GateEnvironmentRetryReceipts.Read(stack.Timeline, folder, current);

        // A new delivery is a new review and therefore a new ladder.
        Assert.Equal(1, ledger.AttemptsSpent);
        Assert.False(ledger.Parked);
        Assert.NotNull(ledger.LastAttemptAt);
    }

    private IReadOnlyList<TimelineEvent> Receipts(Stack stack, string id)
    {
        var job = stack.Scanner.FindJob(id, _watchPath);
        return job is null
            ? []
            : stack.Timeline.ReadAll(job.FolderPath)
                .Where(entry => entry.Kind == TimelineEventKinds.IntegrationGateEnvironmentRetried)
                .ToList();
    }

    private void SeedRetryReceipt(Stack stack, string id, string deliverySha, int rung)
        => GateEnvironmentRetryReceipts.RecordRetry(
            stack.Timeline,
            stack.Scanner.FindJob(id, _watchPath)!.FolderPath,
            deliverySha,
            GateEnvironmentRetrySources.Sweep,
            rung,
            $"Seeded rung {rung}.");

    /// <summary>
    /// A card parked in Human Review exactly as an integrate-on-delivery gate
    /// environment failure leaves it: the delivery is on its own branch, develop
    /// is untouched, the merge step carries the typed failure code, and attempt
    /// authority holds the review that passed for this delivery SHA.
    /// </summary>
    private string SeedGateEnvironmentFailure(
        Stack stack,
        string id,
        int failedMinutesAgo,
        bool passedReview = true,
        string failureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + id);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = "AGT-2811",
                    title = id,
                    state = TaskStates.HumanReview,
                    order = 1,
                    agent = "codex",
                    cliType = "codex",
                    mode = TaskModes.Coding,
                    projectName = Project,
                    ownerClientId = DefaultClientIdentity.Id,
                    commit = Commit(deliverySha),
                    commits = new[] { Commit(deliverySha) },
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: Awaiting acceptance.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-2811",
            RunAttemptId = "run-" + id,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + id,
            AttemptChainId = "chain-" + id,
            IntegrationBranch = "develop",
            CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-failedMinutesAgo - 1),
        });

        var failedAt = DateTime.UtcNow.AddMinutes(-failedMinutesAgo);
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        stack.Pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = failedAt,
            CompletedAt = failedAt,
            Verdict = failureCode,
            VerdictSummary = "The build gate rejected the merged result.",
            Reason = "The build gate blocked the merge into develop: "
                     + "Tool 'node' version v24.18.0 does not match .nvmrc. develop was rolled back "
                     + "and nothing was pushed; gate environment: the build/test gate failed before "
                     + "verification could run and will be retried.",
            FailureCode = failureCode,
        });

        if (passedReview) SeedPassedReview(stack, id, deliverySha);
        return deliverySha;
    }

    private void SeedPassedReview(Stack stack, string id, string deliverySha)
    {
        var taskKey = stack.TaskKey(id);
        var run = stack.Authority.AcquireRun(
            taskKey, "PROJ-001", null, "runner", "host", 600, "run-create-" + id).RunAttempt!;
        stack.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete-" + id),
            Outcome = "done",
            ResultSha = deliverySha,
        });
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-001", deliverySha, run.AttemptId,
            "requirements", "policy", [], "review-create-" + id)).ReviewAttempt!;
        var claimed = stack.Authority.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 600, "review-claim-" + id).ReviewAttempt!;
        var settled = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + id),
            deliverySha,
            ReviewTerminalOutcome.Pass));
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
    }

    private Stack Build(
        bool gateEnvironmentFails = false,
        Action? onGateRun = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
        }).Build();

        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var gate = new ScriptedBuildTestGateRunner(gateEnvironmentFails, onGateRun);
        var runner = new MergeIntoDevelopRunner(
            git,
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gate));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var authority = new AttemptAuthorityService(
            configuration, NullLogger<AttemptAuthorityService>.Instance);
        var retries = new GateEnvironmentRetryService(
            scanner,
            settings,
            integration,
            pipeline,
            runner,
            timeline,
            authority,
            new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance),
            configuration,
            NullLogger<GateEnvironmentRetryService>.Instance);
        return new Stack(scanner, timeline, pipeline, integration, authority, gate, retries, _watchPath);
    }

    private bool IsAncestor(string sha, string branch)
        => RunGit(_repo, "merge-base", "--is-ancestor", sha, branch) == 0;

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    private static string Git(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = Run(cwd, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static int RunGit(string cwd, params string[] args) => Run(cwd, args).Code;

    private static (string Out, string Err, int Code) Run(string cwd, params string[] args)
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
        return (stdout, stderr, process.ExitCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Gate environment retry test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        TimelineLog Timeline,
        PipelineExecutionLog Pipeline,
        TaskIntegrationStatusService Integration,
        AttemptAuthorityService Authority,
        ScriptedBuildTestGateRunner Gate,
        GateEnvironmentRetryService Retries,
        string WatchPath)
    {
        public string TaskKey(string id) => Scanner.FindJob(id, WatchPath)!.TaskKey;
    }

    /// <summary>
    /// Green by default; optionally reproduces the exact CAC-18 environment
    /// failure (a toolchain version mismatch before test discovery).
    /// </summary>
    private sealed class ScriptedBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly bool _environmentFails;
        private readonly Action? _duringRun;

        public ScriptedBuildTestGateRunner(bool environmentFails, Action? duringRun)
        {
            _environmentFails = environmentFails;
            _duringRun = duringRun;
        }

        public int Invocations { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            _duringRun?.Invoke();
            var result = _environmentFails
                ? new BuildTestGateResult(
                    BuildTestGateVerdict.Fail,
                    1,
                    5,
                    string.Empty,
                    "Tool 'node' version v24.18.0 does not match .nvmrc",
                    false,
                    false)
                {
                    ExpectedSha = request.ExpectedSha,
                    FailureKind = BuildTestGateFailureKind.Environment,
                }
                : new BuildTestGateResult(
                    BuildTestGateVerdict.Ok,
                    0,
                    5,
                    string.Empty,
                    "replayed gate passed",
                    true,
                    false)
                {
                    ExpectedSha = request.ExpectedSha,
                };
            return Task.FromResult(result);
        }
    }
}
