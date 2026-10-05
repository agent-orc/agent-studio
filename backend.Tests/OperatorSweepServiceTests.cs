using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3011: the operator sweep service against the real scanner, attempt
/// authority, project settings, timeline, and decision journal. Only the side
/// effects (moving a card, queueing a run) are faked, so the assertions are
/// about what the service decided and what it durably recorded.
/// </summary>
public sealed class OperatorSweepServiceTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));

    public OperatorSweepServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "operator-sweeps-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task FreshProductFailure_OpensOneFixRound_AndChargesTheSharedBudget()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9001", Sha('a'));
        SettleReview(stack, "AGT-9001", Sha('a'), ReviewTerminalOutcome.ProductFailure);

        var report = await stack.Service.RunOnceAsync();

        Assert.Equal(1, report.Acted);
        var call = Assert.Single(stack.Actions.FixRounds);
        Assert.Contains("automatic round 1 of 4", call.FollowUp);
        var journal = ReviewDecisionLog.ReadAll(_root, Project);
        var reissue = Assert.Single(journal, record => record.Kind == ReviewDecisionKind.Reissue);
        Assert.Equal("operator-sweep:fix-rounds", reissue.FailureKind);
        var receipt = Assert.Single(Receipts(stack, "AGT-9001"));
        Assert.Equal(OperatorSweepKinds.FixRounds, receipt.Details![OperatorSweepTriggers.ReceiptSweepKey]);
        Assert.Equal("1", receipt.Details["roundsUsed"]);

        // Same review again on the next tick: the receipt makes it idempotent.
        var second = await stack.Service.RunOnceAsync();
        Assert.Equal(0, second.Acted);
        Assert.Single(stack.Actions.FixRounds);
        var card = Assert.Single(stack.Service.Project(Project).Cards);
        Assert.Equal(OperatorSweepReasons.AlreadyHandled, Assert.Single(card.Decisions).Reason);
        Assert.Equal(3, card.RoundsRemaining);
    }

    [Fact]
    public async Task ActiveReviewLease_IsNeverRaced()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9002", Sha('b'));
        SettleReview(stack, "AGT-9002", Sha('b'), ReviewTerminalOutcome.ProductFailure);
        // A newer review for the same delivery holds a live lease, the state the
        // lease-authority races of the night-shift scripts came from.
        var taskKey = TaskKey(stack, "AGT-9002");
        var run = stack.Authority.GetTaskProjection(taskKey).CurrentRunAttempt!;
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-001", Sha('b'), run.AttemptId, "requirements", "policy", [], "review-create-leased"))
            .ReviewAttempt!;
        var leased = stack.Authority.ClaimReview(review.AttemptId, "reviewer", "review-host", 600, "review-claim-leased");
        Assert.Equal(AttemptLifecycleState.Leased, leased.ReviewAttempt!.State);

        var report = await stack.Service.RunOnceAsync();

        Assert.Equal(0, report.Acted);
        Assert.Empty(stack.Actions.FixRounds);
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_root, Project), record => record.Kind == ReviewDecisionKind.Reissue);
        Assert.Empty(Receipts(stack, "AGT-9002"));
    }

    [Fact]
    public async Task PendingReviewAttempt_AlsoBlocksTheSweep()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9003", Sha('c'));
        SettleReview(stack, "AGT-9003", Sha('c'), ReviewTerminalOutcome.ProductFailure);
        var taskKey = TaskKey(stack, "AGT-9003");
        var run = stack.Authority.GetTaskProjection(taskKey).CurrentRunAttempt!;
        stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-001", Sha('c'), run.AttemptId, "requirements", "policy", [], "review-create-pending"));

        var facts = new OperatorSweepGuardFacts(false, true, HasActiveReview(stack, taskKey), false, new(0, 4));
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.FixRounds, new OperatorSweepTrigger("review:x", false), facts);

        Assert.Equal(OperatorSweepReasons.ActiveReviewAttempt, decision.Reason);
        await stack.Service.RunOnceAsync();
        Assert.Empty(stack.Actions.FixRounds);
    }

    [Fact]
    public async Task Budget_IsSharedWithTheOrchestrator_AndNotResetByARequeueOrASweep()
    {
        var stack = Build();
        var folder = SeedCard(stack, "AGT-9004", Sha('d'));
        // Two orchestrator reissues, an operator requeue (new epoch), one more
        // orchestrator reissue: the orchestrator's epoch count says 1, the
        // card has spent 3 of 4.
        AppendOrchestratorReissue("AGT-9004");
        AppendOrchestratorReissue("AGT-9004");
        var requeue = new OperatorReviewRequeueService(_root, NullLogger<OperatorReviewRequeueService>.Instance);
        requeue.Apply(folder, "AGT-9004", Project, TaskStates.HumanReview, TaskStates.Ready, "retry", "human");
        AppendOrchestratorReissue("AGT-9004", epoch: 1);
        Assert.Equal(1, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            ReviewDecisionLog.ReadAll(_root, Project), "AGT-9004"));
        SettleReview(stack, "AGT-9004", Sha('d'), ReviewTerminalOutcome.ProductFailure, suffix: "1");

        var first = await stack.Service.RunOnceAsync();

        Assert.Equal(1, first.Acted);
        Assert.Contains("automatic round 4 of 4", Assert.Single(stack.Actions.FixRounds).FollowUp);
        // The orchestrator counts the sweep's round in its own budget.
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            ReviewDecisionLog.ReadAll(_root, Project), "AGT-9004"));

        // Another operator requeue opens a new epoch; the card budget stays spent.
        requeue.Apply(folder, "AGT-9004", Project, TaskStates.HumanReview, TaskStates.Ready, "again", "human");
        SettleReview(stack, "AGT-9004", Sha('d'), ReviewTerminalOutcome.ProductFailure, suffix: "2");

        var second = await stack.Service.RunOnceAsync();

        Assert.Equal(0, second.Acted);
        Assert.Equal(1, second.WaitingForPerson);
        Assert.Single(stack.Actions.FixRounds);
        var projection = stack.Service.Project(Project);
        var waiting = Assert.Single(projection.WaitingForPerson);
        Assert.Equal(OperatorSweepReasons.BudgetExhausted, waiting.Reason);
        Assert.Equal(0, Assert.Single(projection.Cards).RoundsRemaining);
    }

    [Fact]
    public async Task PausedSweep_StaysPausedAcrossARestart()
    {
        var stack = Build();
        stack.Service.SetPaused(Project, OperatorSweepKinds.FixRounds, true, "human:ops", "investigating AGT-9005");
        SeedCard(stack, "AGT-9005", Sha('e'));
        SettleReview(stack, "AGT-9005", Sha('e'), ReviewTerminalOutcome.ProductFailure);

        // A new process: fresh settings service, fresh sweep service, same workspace.
        var restarted = Build();
        var report = await restarted.Service.RunOnceAsync();

        Assert.Equal(0, report.Acted);
        Assert.Empty(restarted.Actions.FixRounds);
        var projection = restarted.Service.Project(Project);
        var status = Assert.Single(projection.Sweeps, sweep => sweep.Sweep == OperatorSweepKinds.FixRounds);
        Assert.True(status.Paused);
        Assert.Equal("human:ops", status.PausedBy);
        Assert.Equal("investigating AGT-9005", status.PauseReason);
        Assert.Equal(OperatorSweepReasons.Paused, Assert.Single(Assert.Single(projection.Cards).Decisions).Reason);

        restarted.Service.SetPaused(Project, OperatorSweepKinds.FixRounds, false, "human:ops", null);
        var resumed = await Build().Service.RunOnceAsync();
        Assert.Equal(1, resumed.Acted);
    }

    [Fact]
    public async Task FailedTick_DoesNotStopTheNextTick()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9006", Sha('f'));
        SettleReview(stack, "AGT-9006", Sha('f'), ReviewTerminalOutcome.ProductFailure);
        stack.GateFacts.Throw = true;

        var failed = await stack.Service.RunOnceAsync();

        Assert.Equal(1, failed.Failed);
        Assert.Contains("integration projection unavailable", failed.Error);
        Assert.Empty(stack.Actions.FixRounds);
        Assert.Equal(OperatorSweepReasons.EvaluationFailed,
            Assert.Single(Assert.Single(stack.Service.Project(Project).Cards).Decisions).Reason);
        var alarm = stack.Service.Project(Project);
        Assert.Equal(OperatorSweepHealthPolicy.Alarm, alarm.Status);
        Assert.All(alarm.Sweeps, sweep => Assert.Contains("integration projection unavailable", sweep.LastRunError));

        stack.GateFacts.Throw = false;
        var next = await stack.Service.RunOnceAsync();

        Assert.Equal(1, next.Acted);
        Assert.Null(next.Error);
        Assert.Equal(OperatorSweepHealthPolicy.Healthy, stack.Service.Project(Project).Status);
        Assert.Single(stack.Actions.FixRounds);
    }

    [Fact]
    public async Task HostedLoop_KeepsTickingAfterATickThrows()
    {
        var runner = new ScriptedRunner();
        var hosted = new OperatorSweepHostedService(runner, NullLogger<OperatorSweepHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        var reached = await runner.ReachedThirdTick.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hosted.StopAsync(CancellationToken.None);

        Assert.True(reached);
        Assert.True(runner.Calls >= 3);
    }

    [Fact]
    public async Task RefusedAction_IsRetriedAndChargesNothing()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9007", Sha('1'));
        SettleReview(stack, "AGT-9007", Sha('1'), ReviewTerminalOutcome.ProductFailure);
        stack.Actions.Refuse = true;

        var refused = await stack.Service.RunOnceAsync();

        Assert.Equal(0, refused.Acted);
        Assert.Empty(Receipts(stack, "AGT-9007"));
        Assert.Empty(ReviewDecisionLog.ReadAll(_root, Project));
        Assert.Equal(OperatorSweepReasons.ActionFailed,
            Assert.Single(Assert.Single(stack.Service.Project(Project).Cards).Decisions).Reason);

        stack.Actions.Refuse = false;
        Assert.Equal(1, (await stack.Service.RunOnceAsync()).Acted);
    }

    [Fact]
    public async Task Salvage_ContinuesAnEscalatedTimeoutOnce()
    {
        var stack = Build();
        var folder = SeedCard(stack, "AGT-9008", Sha('2'), TaskStates.Escalated);
        stack.Timeline.Append(folder, TimelineEventKinds.AgentRunFinished, TimelineActors.Agent, "remote run unknown",
            runId: "run-9008", details: new Dictionary<string, string>
            {
                ["status"] = "unknown",
                ["runAttemptId"] = "run-9008",
                ["salvageBranch"] = "salvage/AGT-9008",
                ["salvageCommitSha"] = Sha('3'),
                ["reason"] = "Timeout",
            });

        Assert.Equal(1, (await stack.Service.RunOnceAsync()).Acted);
        Assert.Equal(0, (await stack.Service.RunOnceAsync()).Acted);

        var salvage = Assert.Single(stack.Actions.Salvages);
        Assert.Equal(Sha('3'), salvage.Salvage.CommitSha);
        Assert.Equal(OperatorSweepKinds.Salvage, Assert.Single(Receipts(stack, "AGT-9008")).Details![OperatorSweepTriggers.ReceiptSweepKey]);
    }

    [Fact]
    public async Task GateTriage_QueuesAProductGateFailure_AndLeavesEnvironmentFailuresToTheLadder()
    {
        var stack = Build();
        SeedCard(stack, "AGT-9009", Sha('4'));
        SeedCard(stack, "AGT-9010", Sha('5'));
        stack.GateFacts.Failures["AGT-9009"] = new OperatorSweepGateFailure(
            Sha('4'), IntegrationGateJournal.PreDevelopBuildGateStep, AcceptedIntegrationFailureCodes.BuildGateFailed,
            "2 tests failed", null, false, AgentStudio.TaskServer.Contracts.RunFailureClass.Product);
        stack.GateFacts.Failures["AGT-9010"] = new OperatorSweepGateFailure(
            Sha('5'), IntegrationGateJournal.PreDevelopBuildGateStep, AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            "node version mismatch", null, true, AgentStudio.TaskServer.Contracts.RunFailureClass.Infrastructure);

        var report = await stack.Service.RunOnceAsync();

        Assert.Equal(1, report.Acted);
        Assert.Equal("AGT-9009", Assert.Single(stack.Actions.GateContinuations).Id);
        var held = Assert.Single(stack.Service.Project(Project).Cards, card => card.JobId == "AGT-9010");
        Assert.Equal(OperatorSweepReasons.EnvironmentFailure, Assert.Single(held.Decisions).Reason);
    }

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["TaskRepository"] = _root,
        }).Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        var authority = new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance);
        var actions = new FakeActions();
        var gateFacts = new FakeGateFacts();
        var service = new OperatorSweepService(
            scanner, authority, settings, timeline, gateFacts, actions, configuration,
            NullLogger<OperatorSweepService>.Instance,
            _clock);
        return new Stack(scanner, timeline, authority, actions, gateFacts, service);
    }

    private string SeedCard(Stack stack, string id, string deliverySha, string state = TaskStates.HumanReview)
    {
        var folder = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = id,
                    title = id,
                    state,
                    order = 1,
                    agent = "codex",
                    cliType = "codex",
                    mode = TaskModes.Coding,
                    projectName = Project,
                    ownerClientId = DefaultClientIdentity.Id,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = id,
            RunAttemptId = "run-" + id,
            AttemptChainId = "chain-" + id,
            Project = Project,
            Repository = _root,
            ResultSha = deliverySha,
            ResultRef = "refs/heads/task/" + id,
            CompletedAtUtc = _clock.GetUtcNow().AddHours(-1),
        });
        stack.Scanner.InvalidateCache();
        return folder;
    }

    private static void SettleReview(
        Stack stack, string id, string deliverySha, ReviewTerminalOutcome outcome, string suffix = "")
    {
        var taskKey = TaskKey(stack, id);
        var run = stack.Authority.GetTaskProjection(taskKey).CurrentRunAttempt;
        if (run is null)
        {
            run = stack.Authority.AcquireRun(taskKey, "PROJ-001", null, "runner", "host", 600, "run-create-" + id).RunAttempt!;
            stack.Authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete-" + id),
                Outcome = "done",
                ResultSha = deliverySha,
            });
        }
        var key = id + suffix;
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-001", deliverySha, run.AttemptId, "requirements", "policy", [], "review-create-" + key))
            .ReviewAttempt!;
        var claimed = stack.Authority.ClaimReview(review.AttemptId, "reviewer", "review-host", 600, "review-claim-" + key)
            .ReviewAttempt!;
        var settled = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + key),
            deliverySha,
            outcome));
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
    }

    private void AppendOrchestratorReissue(string jobId, int epoch = 0)
        => ReviewDecisionLog.Append(_root, new ReviewDecisionRecord(
            _clock.GetUtcNow().UtcDateTime, jobId, Project, ReviewDecisionKind.Reissue, "aspect block", "p", "r", "f")
        {
            AttemptEpoch = epoch,
        });

    private static bool HasActiveReview(Stack stack, string taskKey)
        => stack.Authority.GetTaskProjection(taskKey).ReviewAttempts
            .Any(attempt => attempt.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased);

    private static string TaskKey(Stack stack, string id)
    {
        var job = stack.Scanner.FindJob(id, null)!;
        return job.Key ?? job.TaskKey;
    }

    private IReadOnlyList<TimelineEvent> Receipts(Stack stack, string id)
        => stack.Timeline.ReadAll(stack.Scanner.FindJob(id, _watchPath)!.FolderPath)
            .Where(entry => entry.Kind == TimelineEventKinds.OperatorSweepRoundStarted)
            .ToList();

    private static string Sha(char c) => new(c, 40);

    private sealed record Stack(
        TaskScannerService Scanner,
        TimelineLog Timeline,
        AttemptAuthorityService Authority,
        FakeActions Actions,
        FakeGateFacts GateFacts,
        OperatorSweepService Service);

    private sealed class FakeActions : IOperatorSweepActions
    {
        public bool Refuse { get; set; }
        public List<OperatorSweepFixRound> FixRounds { get; } = [];
        public List<TaskInfo> GateContinuations { get; } = [];
        public List<OperatorSweepSalvageFacts> Salvages { get; } = [];

        public Task<OperatorSweepActionResult> OpenFixRoundAsync(TaskInfo job, OperatorSweepFixRound round, CancellationToken ct)
        {
            if (Refuse) return Task.FromResult(new OperatorSweepActionResult(false, "move refused"));
            FixRounds.Add(round);
            return Task.FromResult(new OperatorSweepActionResult(true, "fix round opened"));
        }

        public Task<OperatorSweepActionResult> ContinueGateFailureAsync(TaskInfo job, string subjectKey, CancellationToken ct)
        {
            GateContinuations.Add(job);
            return Task.FromResult(new OperatorSweepActionResult(true, "gate continuation queued"));
        }

        public Task<OperatorSweepActionResult> ContinueSalvageAsync(TaskInfo job, OperatorSweepSalvageFacts salvage, CancellationToken ct)
        {
            Salvages.Add(salvage);
            return Task.FromResult(new OperatorSweepActionResult(true, "salvage continued"));
        }
    }

    private sealed class FakeGateFacts : IOperatorSweepGateFacts
    {
        public bool Throw { get; set; }
        public Dictionary<string, OperatorSweepGateFailure> Failures { get; } = new(StringComparer.Ordinal);

        public OperatorSweepGateFailure? Read(TaskInfo job)
        {
            if (Throw) throw new IOException("integration projection unavailable");
            return Failures.GetValueOrDefault(job.Id);
        }
    }

    private sealed class ScriptedRunner : IOperatorSweepRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<bool> ReachedThirdTick { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperatorSweepOptions Options { get; } =
            new(true, TimeSpan.FromMilliseconds(20), TimeSpan.Zero, CardRoundBudget.DefaultRoundsPerCard);

        public Task<OperatorSweepTickReport> RunOnceAsync(CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call >= 3) ReachedThirdTick.TrySetResult(true);
            if (call == 1) throw new InvalidOperationException("first tick fails");
            return Task.FromResult(new OperatorSweepTickReport(default, default, 0, 0, 0, 0, 0,
                call == 2 ? "scanner unavailable" : null));
        }
    }
}
