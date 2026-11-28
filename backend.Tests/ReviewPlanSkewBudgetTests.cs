using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class ReviewPlanSkewBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plan-skew-budget-" + Guid.NewGuid().ToString("N"));
    private string Watch => Path.Combine(_root, "project");
    private const string Project = "skew";
    private const string SourceId = "SKW-1";
    private static readonly string PlanSha = new('a', 40);

    [Fact]
    public void Legacy_reopen_receipts_and_new_reservations_charge_once_per_attempt()
    {
        var rows = Enumerable.Range(1, 4).SelectMany(index => new[]
        {
            Row(TimelineEventKinds.CardRoundBudgetDisposition, "attempt-" + index),
            Row(TimelineEventKinds.QualityLoopReopened, "attempt-" + index),
        }).ToArray();
        Assert.Equal(4, CardRoundBudget.CountRoundsUsed([], rows, "card"));
        Assert.Equal(4, CardRoundBudget.CountRoundsUsed([], rows.Where(row =>
            row.Kind == TimelineEventKinds.QualityLoopReopened), "card"));
        Assert.Equal(4, CardRoundBudget.CountRoundsUsed([], rows.Where(row =>
            row.Kind == TimelineEventKinds.CardRoundBudgetDisposition), "card"));
        Assert.Equal(0, CardRoundBudget.CountRoundsUsed([], [Row(TimelineEventKinds.CardRoundBudgetDisposition,
            "spent", CardRoundBudgetAction.DegradeToFollowUp)], "card"));
    }

    [Fact]
    public void New_delivery_and_plan_identities_share_the_allowance_across_restarts()
    {
        Directory.CreateDirectory(Watch);
        using var timeline = Timeline();
        for (var index = 0; index < 6; index++)
        {
            var entry = Entry(index, new CardRoundBudgetState(0, 4));
            entry = entry with { Continuation = entry.Continuation! with
                { SkewPlan = entry.Continuation.SkewPlan! with { CommitSha = (index + 10).ToString("x").PadLeft(40, '0') } } };
            var first = ReviewPlanSkewAllowance.Reserve(Watch, entry, timeline)!;
            using var restartedTimeline = Timeline();
            var replay = ReviewPlanSkewAllowance.Reserve(Watch, entry, restartedTimeline)!;
            Assert.Equal(first, replay);
            Assert.Equal(index < 4 ? CardRoundBudgetAction.OpenRound : CardRoundBudgetAction.DegradeToFollowUp,
                first.Action);
            Assert.Equal(Math.Min(index + 1, 4), first.Budget.Used);
        }
        Assert.Equal(4, CardRoundBudget.CountRoundsUsed([], timeline.ReadAll(Watch), SourceId));
        Assert.Equal(6, timeline.ReadAll(Watch).Count);
    }

    [Fact]
    public void Legacy_settlement_recovers_the_plan_pin_from_the_executor_workspace_proof()
    {
        Directory.CreateDirectory(Watch);
        using var timeline = Timeline();
        var entry = Entry(1, new(0, 4));
        entry = entry with { Continuation = entry.Continuation! with { SkewPlan = null } };
        var reservation = ReviewPlanSkewAllowance.Reserve(Watch, entry, timeline)!;
        Assert.Equal(CardRoundBudgetAction.OpenRound, reservation.Action);
        Assert.Equal(PlanSha, reservation.PlanCommitSha);
        Assert.Equal(PlanSha, ReviewPlanSkewAllowance.ReadPlan(Watch)!.CommitSha);
    }

    [Fact]
    public void Successful_review_releases_only_the_pin_and_retains_spent_allowance()
    {
        Directory.CreateDirectory(Watch);
        using var timeline = Timeline();
        ReviewPlanSkewAllowance.Reserve(Watch, Entry(1, new(0, 1)), timeline);
        Assert.NotNull(ReviewPlanSkewAllowance.ReadPlan(Watch));
        ReviewPlanSkewAllowance.ReleasePlan(Watch);
        Assert.Null(ReviewPlanSkewAllowance.ReadPlan(Watch));
        Assert.Equal(CardRoundBudgetAction.DegradeToFollowUp,
            ReviewPlanSkewAllowance.Reserve(Watch, Entry(2, new(0, 1)), timeline)!.Action);
    }

    [Fact]
    public void Prior_shared_rounds_are_spent_before_the_first_skew_reservation()
    {
        Directory.CreateDirectory(Watch);
        using var timeline = Timeline();
        var last = ReviewPlanSkewAllowance.Reserve(Watch, Entry(1, new(3, 4)), timeline)!;
        Assert.Equal(CardRoundBudgetAction.OpenRound, last.Action);
        Assert.Equal(4, last.Budget.Used);
        var next = ReviewPlanSkewAllowance.Reserve(Watch, Entry(2, new(4, 4)), timeline)!;
        Assert.Equal(CardRoundBudgetAction.DegradeToFollowUp, next.Action);
    }

    [Fact]
    public void Replay_cannot_rebind_the_reservation_to_another_plan_delivery_or_settlement()
    {
        Directory.CreateDirectory(Watch);
        using var timeline = Timeline();
        var entry = Entry(1, new(0, 4));
        ReviewPlanSkewAllowance.Reserve(Watch, entry, timeline);
        Assert.Throws<IOException>(() => ReviewPlanSkewAllowance.Reserve(Watch,
            entry with { IdempotencyKey = "different" }, timeline));
        Assert.Throws<IOException>(() => ReviewPlanSkewAllowance.Reserve(Watch,
            entry with { Continuation = entry.Continuation! with
                { SkewPlan = new ReviewPlanSkewPlan(new string('f', 40), "profile", "brief") } }, timeline));
        Assert.Throws<IOException>(() => ReviewPlanSkewAllowance.Reserve(Watch,
            entry with { Continuation = entry.Continuation! with
                { Base = entry.Continuation.Base! with { CommitSha = new string('e', 40) } } }, timeline));
    }

    [Fact]
    public async Task Crash_after_reservation_repairs_the_lane_and_replay_spends_no_second_round()
    {
        var stack = Build();
        var task = Seed(stack.Scanner);
        var entry = Entry(1, new(0, 1));
        ReviewPlanSkewAllowance.Reserve(task.FolderPath, entry, stack.Timeline);
        // A new service set has only the durable reservation and the source lane.
        var restarted = Build();
        Assert.True(await Apply(restarted, task, entry));
        var ready = restarted.Scanner.FindJob(SourceId, Watch)!;
        Assert.Equal(TaskStates.Ready, ready.State);
        Assert.Equal(entry.Continuation!.Base, ContinuationBaseStore.Read(ready.FolderPath));
        Assert.True(await Apply(restarted, ready, entry));
        Assert.Equal(1, CardRoundBudget.CountRoundsUsed([], restarted.Timeline.ReadAll(ready.FolderPath), SourceId));
        Assert.Single(restarted.Timeline.ReadAll(ready.FolderPath), row => row.Kind == TimelineEventKinds.QualityLoopReopened);
        Assert.Null(ReviewConcernRoundStore.Read(ready.FolderPath));
    }

    [Fact]
    public async Task Crash_after_lane_move_finishes_without_moving_again_or_charging_twice()
    {
        var stack = Build();
        var task = Seed(stack.Scanner);
        var entry = Entry(1, new(0, 1));
        ReviewPlanSkewAllowance.Reserve(task.FolderPath, entry, stack.Timeline);
        Assert.True(ContinuationBaseStore.Save(task.FolderPath, entry.Continuation!.Base!));
        var move = await stack.Transitions.MoveAsync(task.Id, TaskStates.Ready, Watch, CancellationToken.None,
            authorityWrite: new AttemptWriteReference(entry.AttemptId, 1, 1, "continuation:" + entry.IdempotencyKey),
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, move.Status);
        var restarted = Build();
        Assert.True(await Apply(restarted, restarted.Scanner.FindJob(SourceId, Watch)!, entry));
        var ready = restarted.Scanner.FindJob(SourceId, Watch)!;
        Assert.Contains(PlanSha, File.ReadAllText(Path.Combine(ready.FolderPath, "prompt.md")));
        Assert.Equal(1, CardRoundBudget.CountRoundsUsed([], restarted.Timeline.ReadAll(ready.FolderPath), SourceId));
    }

    [Fact]
    public async Task Exhaustion_quarantines_delivery_and_creates_one_runnable_repair_without_parent_dependency()
    {
        var stack = Build();
        var task = Seed(stack.Scanner);
        var entry = Entry(1, new(4, 4));
        Assert.True(await Apply(stack, task, entry));
        var source = stack.Scanner.FindJob(SourceId, Watch)!;
        Assert.Equal(TaskStates.Escalated, source.State);
        Assert.True(await Apply(Build(), source, entry));
        var repair = Assert.Single(stack.Scanner.ScanAllJobs(), job => job.CreationSource == "review-plan-skew-repair");
        Assert.Equal(TaskStates.Ready, repair.State);
        Assert.Empty(repair.References.DependsOn);
        Assert.Contains(source.Key ?? source.Id, repair.References.FollowUpOf);
        Assert.Contains(entry.Continuation!.Base!.Ref, File.ReadAllText(Path.Combine(repair.FolderPath, "prompt.md")));
        Assert.Contains("immutable-proof", File.ReadAllText(Path.Combine(source.FolderPath, "delivery.txt")));
        Assert.Null(ContinuationBaseStore.Read(source.FolderPath));
        var receipt = Assert.Single(stack.Timeline.ReadAll(source.FolderPath), row => row.Kind == TimelineEventKinds.OrchestratorEscalated);
        Assert.Equal("DegradeToFollowUp", receipt.Details!["disposition"]);
        Assert.Equal(repair.Key ?? repair.Id, receipt.Details["repairTaskKey"]);
        Assert.Equal(0, CardRoundBudget.CountRoundsUsed([], stack.Timeline.ReadAll(source.FolderPath), source.Id));
    }

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    public async Task Human_lane_holds_do_not_charge_or_create_a_repair(string lane)
    {
        var stack = Build();
        var task = Seed(stack.Scanner, lane);
        Assert.False(await Apply(stack, task, Entry(1, new(4, 4))));
        Assert.Null(ReviewPlanSkewAllowance.ReadPlan(task.FolderPath));
        Assert.Single(stack.Scanner.ScanAllJobs());
        Assert.Equal(lane, stack.Scanner.FindJob(SourceId, Watch)!.State);
    }

    [Fact]
    public async Task Ownership_loss_before_projection_has_no_effect()
    {
        var stack = Build();
        var task = Seed(stack.Scanner);
        Assert.False(await RemoteReviewContinuationProjection.ApplyAsync(task, Entry(1, new(0, 4)),
            stack.Scanner, stack.Transitions, stack.Mutations, stack.Timeline, NullLogger.Instance,
            () => false, CancellationToken.None));
        Assert.Null(ReviewPlanSkewAllowance.ReadPlan(task.FolderPath));
        Assert.Equal(TaskStates.AutoReview, stack.Scanner.FindJob(task.Id, Watch)!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepared_delivery_reviews_the_pinned_plan_or_explicitly_replaces_invalidated_inputs(bool invalidate)
    {
        var stack = Build();
        var task = Seed(stack.Scanner);
        ReviewPlanSkewAllowance.Reserve(task.FolderPath, Entry(1, new(0, 1)), stack.Timeline);
        var authority = new AttemptAuthorityService(stack.Configuration, NullLogger<AttemptAuthorityService>.Instance);
        var result = new string('d', 40);
        var run = authority.AcquireRun(SourceId, "repo", null, "runner", "host", 60, "run").RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new(run.AttemptId, run.LastFence, run.AuthorityEpoch, "result"), Outcome = "done", ResultSha = result,
        }).Accepted);
        var newPlanSha = new string('f', 40);
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("verify", "build-tests", "sh", ["-lc", "echo verified"])],
            ["build-tests"], LibraryVersion: Contract.ReviewLibraryStepPolicy.Version,
            IntegrationRef: "origin/develop", PlanCommitSha: newPlanSha,
            BuildProfileFingerprint: invalidate ? "changed-profile" : "profile", BriefSha256: "brief");
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(SourceId, "repo", result,
            run.AttemptId, "requirements", "policy", [], "new-review", Plan: plan)).ReviewAttempt!;
        string? builtAt = null;
        Assert.True(V1ReviewPlaneEndpoints.PreparePlanSkewReview(task, review, authority, stack.Timeline, commit =>
        {
            builtAt = commit;
            return plan with { PlanCommitSha = commit };
        }));
        var claimed = authority.ClaimReview(review.AttemptId, "reviewer", "host", 60, "claim").ReviewAttempt!;
        Assert.Equal(invalidate ? newPlanSha : PlanSha, builtAt);
        Assert.Equal(builtAt, claimed.Subject.Plan!.PlanCommitSha);
        Assert.Equal(result, claimed.Subject.ExpectedResultSha);
        Assert.True(Contract.ReviewLibraryStepPolicy.ValidPlan(claimed.Subject.Plan, result));
        Assert.Equal(invalidate ? 1 : 0, stack.Timeline.ReadAll(task.FolderPath).Count(row =>
            row.Details?.GetValueOrDefault("cause") == "review-plan-skew-plan-replaced"));
        Assert.Equal(CardRoundBudgetAction.DegradeToFollowUp,
            ReviewPlanSkewAllowance.Reserve(task.FolderPath, Entry(2, new(0, 1)), stack.Timeline)!.Action);
    }

    private static TimelineEvent Row(string kind, string attempt, CardRoundBudgetAction action = CardRoundBudgetAction.OpenRound)
        => new() { Kind = kind, Details = new() { ["cause"] = "review-plan-skew", ["reviewAttemptId"] = attempt,
            ["disposition"] = action.ToString() } };

    private static RemoteReviewSettlementEntry Entry(int index, CardRoundBudgetState budget)
    {
        var delivery = index.ToString("x").PadLeft(40, '0');
        var attempt = "review-" + index;
        var resultRef = Contract.FencedGitRefs.ImmutableResult("run-" + index, 1, delivery);
        var report = new Contract.ReviewReportRequest("executor", "instance", "lease", 1, "settlement-" + index,
            Contract.ReviewOutcomeKinds.DeliveryPredatesReviewPlan, null, "Plan inputs are newer than the delivery.",
            new Contract.ReviewWorkspaceProofDto("repo", delivery, delivery, PlanSha, false, false, "workspace", "namespace", IntegrationTipSha: PlanSha),
            new Contract.ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", "10.0", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [], 1);
        return new RemoteReviewSettlementEntry
        {
            AttemptId = attempt, TaskKey = SourceId, IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report), Report = report, ReceivedAtUtc = DateTime.UnixEpoch,
            Continuation = new(RemoteReviewContinuationKind.PlanMergeRound,
                $"Prepare the delivery against plan commit {PlanSha}.", [], 0, budget,
                Base: ReviewFixRoundBase.Decide(attempt, resultRef, delivery, DateTime.UnixEpoch),
                SkewPlan: new(PlanSha, "profile", "brief")),
        };
    }

    private TaskInfo Seed(TaskScannerService scanner, string lane = TaskStates.AutoReview)
    {
        var folder = TaskStorageLayout.JobDir(Watch, 1, SourceId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = SourceId, key = SourceId, title = "Source", state = lane, agent = "codex", cliType = "codex",
            ownerClientId = DefaultClientIdentity.Id, projectName = Project,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Implement source.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: delivered");
        File.WriteAllText(Path.Combine(folder, "delivery.txt"), "immutable-proof");
        scanner.InvalidateCache();
        return scanner.FindJob(SourceId, Watch)!;
    }

    private sealed record Stack(IConfiguration Configuration, TaskScannerService Scanner, TaskMutationService Mutations,
        TaskTransitionService Transitions, TimelineLog Timeline);

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["WatchPaths:0:Name"] = Project, ["WatchPaths:0:Path"] = Watch,
        }).Build();
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = Timeline();
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        states.EnsureStateFoldersAndMigrate();
        var clients = new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(scanner, clients,
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        return new(configuration, scanner, mutations,
            new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance,
                timeline: timeline), timeline);
    }

    private static TimelineLog Timeline() => new(NullLogger<TimelineLog>.Instance);
    private static Task<bool> Apply(Stack stack, TaskInfo task, RemoteReviewSettlementEntry entry)
        => RemoteReviewContinuationProjection.ApplyAsync(task, entry, stack.Scanner, stack.Transitions, stack.Mutations,
            stack.Timeline, NullLogger.Instance, () => true, CancellationToken.None);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
