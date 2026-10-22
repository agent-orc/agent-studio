using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MonolithOrchestrationStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Claim_complete_and_duplicate_completion_apply_once()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claimed = fixture.Store.Claim(Claim("engine_one"));
        Assert.Equal("claimed", claimed.Status);
        Assert.Equal(run.RunId, claimed.Run!.RunId);
        var completion = Complete(claimed);

        var done = await fixture.Store.CompleteAsync(run.RunId, completion, default);
        var replay = await fixture.Store.CompleteAsync(run.RunId, completion, default);

        Assert.Equal("completed", done.Status);
        Assert.Equal(done, replay);
        Assert.Equal(1, fixture.Applier.Count);
        Assert.Single(done.StageResults!);
    }

    [Fact]
    public void Renew_release_and_reclaim_increment_the_fence()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var first = fixture.Store.Claim(Claim("engine_one"));
        var lease = first.Lease!;
        var renewed = fixture.Store.Renew(run.RunId,
            new(lease.EngineId, lease.InstanceId, lease.LeaseId, lease.Fence, 180));
        Assert.True(renewed.ExpiresAt > lease.ExpiresAt);

        var released = fixture.Store.Release(run.RunId,
            new(lease.EngineId, lease.InstanceId, lease.LeaseId, lease.Fence, "retry"));
        Assert.Equal("pending", released.Status);
        var second = fixture.Store.Claim(Claim("engine_two"));
        Assert.Equal(lease.Fence + 1, second.Lease!.Fence);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Renew(run.RunId,
            new(lease.EngineId, lease.InstanceId, lease.LeaseId, lease.Fence)));
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_stale_completion_is_rejected()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var stale = fixture.Store.Claim(Claim("engine_one"));
        _time.Advance(TimeSpan.FromMinutes(3));
        var fresh = fixture.Store.Claim(Claim("engine_two"));

        Assert.Equal(stale.Lease!.Fence + 1, fresh.Lease!.Fence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CompleteAsync(run.RunId, Complete(stale), default));
        Assert.Equal(0, fixture.Applier.Count);
    }

    [Fact]
    public void Expired_claim_is_alertable_without_another_engine_claim()
    {
        using var fixture = new Fixture(_time);
        fixture.Create();
        var claimed = fixture.Store.Claim(Claim("engine_one"));
        var occurrence = fixture.Store.GetOccurrence(claimed.Run!.RunId)!;
        Assert.False(MonolithOrchestrationRecoveryService.ShouldAlert(occurrence,
            _time.GetUtcNow().UtcDateTime));

        _time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal("leased", fixture.Store.Get(claimed.Run.RunId)!.Status);
        Assert.True(MonolithOrchestrationRecoveryService.ShouldAlert(occurrence,
            _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Active_decision_wait_crosses_the_healthy_engine_alert_bound_at_fifteen_minutes()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var occurrence = fixture.Store.GetOccurrence(run.RunId)!;
        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(MonolithOrchestrationRecoveryService.ShouldAlertHealthyLongWait(
            occurrence, _time.GetUtcNow().UtcDateTime));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(MonolithOrchestrationRecoveryService.ShouldAlertHealthyLongWait(
            occurrence, _time.GetUtcNow().UtcDateTime));
        fixture.Store.Supersede(run.RunId, TaskStates.Ready);
        Assert.False(MonolithOrchestrationRecoveryService.ShouldAlertHealthyLongWait(
            fixture.Store.GetOccurrence(run.RunId)!, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task Pending_and_applying_occurrences_recover_after_restart()
    {
        using var fixture = new Fixture(_time);
        var pending = fixture.Create();
        var restarted = fixture.Restart();
        Assert.Equal("pending", restarted.Get(pending.RunId)!.Status);
        var claim = restarted.Claim(Claim("engine_one"));
        fixture.Applier.ThrowNext = true;
        await Assert.ThrowsAsync<IOException>(() =>
            restarted.CompleteAsync(pending.RunId, Complete(claim), default));
        Assert.Equal("applying", restarted.Get(pending.RunId)!.Status);
        Assert.True(restarted.CanBlockCard(pending.RunId, "project", "card", TaskStates.AutoReview));

        var recovered = fixture.Restart();
        await recovered.RecoverApplyingAsync(default);
        Assert.Equal("completed", recovered.Get(pending.RunId)!.Status);
        Assert.False(recovered.CanBlockCard(pending.RunId, "project", "card", TaskStates.AutoReview));
        Assert.Equal("empty", recovered.Claim(Claim("engine_one")).Status);
        Assert.Equal(1, fixture.Applier.Count);
    }

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public async Task Applying_decision_is_superseded_after_the_card_leaves_its_source_lane(string observedState)
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claim = fixture.Store.Claim(Claim("engine_one"));
        var completion = Complete(claim) with { OutputJson = "{\"decision\":\"DegradeWithFollowUp\"}" };
        fixture.Applier.ThrowNext = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CompleteAsync(run.RunId,
            completion, default));
        Assert.True(fixture.Store.CanBlockCard(run.RunId, "project", "card", TaskStates.AutoReview));
        fixture.Applier.CurrentState = observedState;

        var restarted = fixture.Restart();
        await restarted.RecoverApplyingAsync(default);

        Assert.Equal("cancelled", restarted.Get(run.RunId)!.Status);
        Assert.Contains($"observed={observedState}", restarted.GetOccurrence(run.RunId)!.CancellationReason);
        Assert.False(restarted.CanBlockCard(run.RunId, "project", "card", observedState));
        Assert.Empty(restarted.WaitingOccurrences());
        Assert.Equal("empty", restarted.Claim(Claim("engine_one")).Status);
        Assert.Equal(0, fixture.Applier.Count);
        Assert.Equal(observedState, Assert.Single(fixture.Applier.SupersededStates));
        Assert.Equal("cancelled", fixture.Restart().Get(run.RunId)!.Status);
    }

    [Fact]
    public async Task Operator_fix_round_supersedes_an_applying_review_decision_at_the_lane_move()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claim = fixture.Store.Claim(Claim("engine_one"));
        var completion = Complete(claim) with { OutputJson = "{\"decision\":\"DegradeWithFollowUp\"}" };
        fixture.Applier.ThrowNext = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CompleteAsync(run.RunId,
            completion, default));

        var moved = await fixture.Store.WithCardMoveAsync("project", "card",
            TaskStates.AutoReview, TaskStates.Ready,
            () => Task.FromResult(new MoveJobOutcome(MoveJobStatus.Success)), default);

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.Equal("cancelled", fixture.Store.Get(run.RunId)!.Status);
        Assert.False(fixture.Store.CanBlockCard(run.RunId, "project", "card", TaskStates.Ready));
        Assert.Equal(TaskStates.Ready, Assert.Single(fixture.Applier.SupersededStates));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.CompleteAsync(
            run.RunId, completion, default));
        Assert.Equal(0, fixture.Applier.Count);
    }

    [Fact]
    public async Task Malformed_applying_decision_is_settled_once_after_restart()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claim = fixture.Store.Claim(Claim("engine_one"));
        fixture.Applier.ThrowNext = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CompleteAsync(run.RunId,
            Complete(claim), default));
        fixture.Applier.ThrowMalformed = true;
        var recovered = fixture.Restart();
        var malformed = new List<string>();
        await recovered.RecoverApplyingAsync(default,
            onMalformed: (item, _) => malformed.Add(item.Run.RunId));
        await recovered.RecoverApplyingAsync(default,
            onMalformed: (item, _) => malformed.Add(item.Run.RunId));

        Assert.Equal(run.RunId, Assert.Single(malformed));
        Assert.Equal("failed", recovered.Get(run.RunId)!.Status);
        Assert.Contains("unrecoverable-application", recovered.GetOccurrence(run.RunId)!.CancellationReason);
        Assert.False(recovered.CanBlockCard(run.RunId, "project", "card", TaskStates.AutoReview));
        Assert.Empty(recovered.WaitingOccurrences());
        Assert.Equal("failed", fixture.Restart().Get(run.RunId)!.Status);
    }

    [Fact]
    public async Task Refused_operator_move_keeps_the_applying_decision_recoverable()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claim = fixture.Store.Claim(Claim("engine_one"));
        fixture.Applier.ThrowNext = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CompleteAsync(run.RunId,
            Complete(claim), default));

        await fixture.Store.WithCardMoveAsync("project", "card", TaskStates.AutoReview,
            TaskStates.Ready,
            () => Task.FromResult(new MoveJobOutcome(MoveJobStatus.Failure)), default);
        Assert.Equal("applying", fixture.Store.Get(run.RunId)!.Status);
        await fixture.Store.RecoverApplyingAsync(default);
        Assert.Equal("completed", fixture.Store.Get(run.RunId)!.Status);
        Assert.Equal(1, fixture.Applier.Count);
    }

    [Fact]
    public async Task Operator_continue_supersedes_only_decisions_open_when_it_started()
    {
        using var fixture = new Fixture(_time);
        var review = fixture.Create();
        var prior = fixture.Store.OpenLaneDecisionIds("project", "card");
        var later = fixture.Store.Create("project", "card", OrchestrationStage.GateDispatch,
            "{}", "gate:after-continue", TaskStates.AutoReview, "gate_attempt", 2, 3);

        await fixture.Store.SupersedeRunsAsync("project", "card", prior,
            TaskStates.AutoReview, "operator-continue", default);

        Assert.Equal("cancelled", fixture.Store.Get(review.RunId)!.Status);
        Assert.Contains("operator-continue", fixture.Store.GetOccurrence(review.RunId)!.CancellationReason);
        Assert.Equal("pending", fixture.Store.Get(later.RunId)!.Status);
        Assert.Equal(TaskStates.AutoReview, Assert.Single(fixture.Applier.SupersededStates));
    }

    [Fact]
    public async Task Deterministic_refusal_settles_once_and_recovery_does_not_reapply()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claimed = fixture.Store.Claim(Claim("engine_one"));
        fixture.Applier.Refusal = "IntegrationFailed integration-pending";

        var refused = await fixture.Store.CompleteAsync(run.RunId, Complete(claimed), default);
        await fixture.Store.RecoverApplyingAsync(default);
        await fixture.Store.RecoverApplyingAsync(default);

        Assert.Equal("failed", refused.Status);
        Assert.Equal(1, fixture.Applier.Calls);
        var persisted = fixture.Restart().GetOccurrence(run.RunId)!;
        Assert.Equal(1, persisted.ApplicationAttempts);
        Assert.Equal(fixture.Applier.Refusal, persisted.ApplicationFailure);
        Assert.Empty(fixture.Store.WaitingOccurrences());
    }

    [Fact]
    public async Task Transient_application_failure_has_three_attempt_limit()
    {
        using var fixture = new Fixture(_time);
        var run = fixture.Create();
        var claimed = fixture.Store.Claim(Claim("engine_one"));
        fixture.Applier.AlwaysFail = true;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Store.CompleteAsync(run.RunId, Complete(claimed), default));
        await fixture.Store.RecoverApplyingAsync(default);
        await fixture.Store.RecoverApplyingAsync(default);
        await fixture.Store.RecoverApplyingAsync(default);

        Assert.Equal("failed", fixture.Store.Get(run.RunId)!.Status);
        Assert.Equal(MonolithOrchestrationStore.MaximumApplicationAttempts, fixture.Applier.Calls);
        Assert.Equal(MonolithOrchestrationStore.MaximumApplicationAttempts,
            fixture.Restart().GetOccurrence(run.RunId)!.ApplicationAttempts);
    }

    [Fact]
    public void Idempotent_occurrence_creation_does_not_duplicate_pending_work()
    {
        using var fixture = new Fixture(_time);
        var first = fixture.Create();
        var second = fixture.Create();
        Assert.Equal(first.RunId, second.RunId);
        Assert.Single(fixture.Store.List(null, null));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Create("project", "card",
            OrchestrationStage.ReviewDecision, "{\"changed\":true}", "transition_key",
            TaskStates.AutoReview, "attempt", 3, 3));
    }

    [Fact]
    public void Prepared_review_occurrence_survives_restart_and_cannot_be_claimed_before_settlement()
    {
        using var fixture = new Fixture(_time);
        var prepared = fixture.Store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 3, 4, claimable: false);
        Assert.Equal("empty", fixture.Store.Claim(Claim("engine_one")).Status);

        var restarted = fixture.Restart();
        Assert.Equal(prepared.RunId, Assert.Single(restarted.AwaitingActivation()).Run.RunId);
        Assert.Equal("empty", restarted.Claim(Claim("engine_one")).Status);
        restarted.Activate(prepared.RunId);
        Assert.Equal(prepared.RunId, restarted.Claim(Claim("engine_one")).Run!.RunId);
        Assert.Empty(fixture.Restart().AwaitingActivation());
    }

    [Theory]
    [InlineData("review-settlement-failed")]
    [InlineData("review-settlement-rejected")]
    public void Unaccepted_review_has_a_durable_cancellation_receipt_and_can_be_prepared_again(string reason)
    {
        using var fixture = new Fixture(_time);
        var prepared = fixture.Store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 3, 4, claimable: false);

        fixture.Store.CancelUnactivated(prepared.RunId, reason);
        var restarted = fixture.Restart();
        Assert.Equal("cancelled", restarted.Get(prepared.RunId)!.Status);
        var receipt = Assert.Single(restarted.List(null, "cancelled"));
        var persisted = System.Text.Json.JsonSerializer.Deserialize<MonolithOrchestrationOccurrence>(
            File.ReadAllText(Path.Combine(fixture.DirectoryPath, receipt.RunId + ".json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(reason, persisted!.CancellationReason);
        Assert.Empty(restarted.WaitingOccurrences());
        Assert.Equal(prepared.RunId, Assert.Single(restarted.AwaitingActivation()).Run.RunId);
        Assert.Equal("empty", restarted.Claim(Claim("engine_one")).Status);

        var retried = restarted.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{\"reviewOutcome\":\"Pass\"}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 4, 5, claimable: false);
        Assert.Equal(prepared.RunId, retried.RunId);
        Assert.Equal("{\"reviewOutcome\":\"Pass\"}", restarted.Get(retried.RunId)!.PayloadJson);
        Assert.Equal(4, restarted.GetOccurrence(retried.RunId)!.AuthorityFence);
        Assert.Equal(5, restarted.GetOccurrence(retried.RunId)!.AuthorityEpoch);
        restarted.Activate(retried.RunId);
        Assert.Equal(retried.RunId, restarted.Claim(Claim("engine_one")).Run!.RunId);
    }

    [Fact]
    public void Unaccepted_review_retried_with_the_same_report_reopens_the_cancelled_preparation()
    {
        using var fixture = new Fixture(_time);
        var prepared = fixture.Store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 3, 4, claimable: false);
        fixture.Store.CancelUnactivated(prepared.RunId, "review-settlement-rejected");
        var restarted = fixture.Restart();

        var retried = restarted.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 3, 4, claimable: false);

        Assert.Equal(prepared.RunId, retried.RunId);
        Assert.Equal("pending", retried.Status);
        Assert.Null(restarted.GetOccurrence(retried.RunId)!.CancellationReason);
        restarted.Activate(retried.RunId);
        Assert.Equal(retried.RunId, restarted.Claim(Claim("engine_one")).Run!.RunId);
    }

    [Fact]
    public void Superseded_preparation_stays_terminal_after_restart_and_same_key_replay()
    {
        using var fixture = new Fixture(_time);
        var prepared = fixture.Store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:attempt:report", TaskStates.AutoReview,
            "attempt", 3, 4, claimable: false);
        fixture.Store.Supersede(prepared.RunId, TaskStates.Ready);

        var restarted = fixture.Restart();
        Assert.Empty(restarted.AwaitingActivation());
        var replay = restarted.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:attempt:report", TaskStates.AutoReview,
            "attempt", 3, 4, claimable: false);
        Assert.Equal("cancelled", replay.Status);
        Assert.Throws<InvalidOperationException>(() => restarted.Activate(prepared.RunId));
        Assert.Equal("empty", restarted.Claim(Claim("engine_one")).Status);
    }

    [Fact]
    public async Task Periodic_sweep_coalesces_until_the_prior_occurrence_is_applied()
    {
        using var fixture = new Fixture(_time);
        var first = fixture.Store.CreatePeriodic("project", "card",
            OrchestrationStage.OperatorSweep, "{}", "fix:review-one",
            TaskStates.HumanReview, "review_attempt", 3, 4, TimeSpan.FromMinutes(10));
        _time.Advance(TimeSpan.FromMinutes(11));
        var stillPending = fixture.Store.CreatePeriodic("project", "card",
            OrchestrationStage.OperatorSweep, "{}", "fix:review-one",
            TaskStates.HumanReview, "review_attempt", 3, 4, TimeSpan.FromMinutes(10));
        Assert.Equal(first.RunId, stillPending.RunId);
        var claimed = fixture.Store.Claim(new("engine", "instance",
            [OrchestrationStage.OperatorSweep]));
        await fixture.Store.CompleteAsync(first.RunId,
            new("engine", "instance", claimed.Lease!.LeaseId, claimed.Lease.Fence,
                OrchestrationStage.OperatorSweep, OrchestrationAction.Continue,
                "{}", "complete:periodic"), default);
        var next = fixture.Store.CreatePeriodic("project", "card",
            OrchestrationStage.OperatorSweep, "{}", "fix:review-two",
            TaskStates.HumanReview, "review_attempt", 3, 4, TimeSpan.FromMinutes(10));
        Assert.NotEqual(first.RunId, next.RunId);
    }

    private static OrchestrationClaimRequest Claim(string engine)
        => new(engine, "instance_one", [OrchestrationStage.ReviewDecision], 120);

    private static CompleteOrchestrationStageRequest Complete(OrchestrationClaimResponse claim)
        => new(claim.Lease!.EngineId, claim.Lease.InstanceId, claim.Lease.LeaseId,
            claim.Lease.Fence, OrchestrationStage.ReviewDecision,
            OrchestrationAction.Continue, "{}", "completion_key");

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "orchestration-test-" + Guid.NewGuid().ToString("N"));
        public string DirectoryPath => _directory;
        private readonly FakeTimeProvider _time;
        public FakeApplier Applier { get; } = new();
        public MonolithOrchestrationStore Store { get; }

        public Fixture(FakeTimeProvider time)
        {
            _time = time;
            Store = Restart();
        }

        public MonolithOrchestrationStore Restart() => new(_directory, _time, Applier);
        public OrchestrationRunDto Create() => Store.Create("project", "card",
            OrchestrationStage.ReviewDecision, "{}", "transition_key",
            TaskStates.AutoReview, "attempt", 2, 3);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeApplier : IMonolithOrchestrationApplier, IOrchestrationSupersessionAudit
    {
        public int Count { get; private set; }
        public int Calls { get; private set; }
        public bool ThrowNext { get; set; }
        public bool ThrowMalformed { get; set; }
        public string CurrentState { get; set; } = TaskStates.AutoReview;
        public List<string?> SupersededStates { get; } = [];
        public void RecordSuperseded(MonolithOrchestrationOccurrence occurrence, string? observedState)
            => SupersededStates.Add(observedState);
        public bool AlwaysFail { get; set; }
        public string? Refusal { get; set; }
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            Calls++;
            if (Refusal is not null)
                throw new OrchestrationApplicationRefusedException(Refusal);
            if (AlwaysFail)
                throw new IOException("Simulated persistent storage error.");
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new IOException("Simulated crash before authority effect.");
            }
            if (ThrowMalformed)
                throw new InvalidDataException("Frozen decision data is malformed.");
            if (CurrentState != occurrence.ExpectedSourceState)
                throw new OrchestrationSourceStateMismatchException(CurrentState);
            Count++;
            return Task.CompletedTask;
        }
    }
}
