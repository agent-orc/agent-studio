using AgentStudio.Runner;
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

        var recovered = fixture.Restart();
        await recovered.RecoverApplyingAsync(default);
        Assert.Equal("completed", recovered.Get(pending.RunId)!.Status);
        Assert.Equal(1, fixture.Applier.Count);
    }

    [Fact]
    public void Idempotent_occurrence_creation_does_not_duplicate_pending_work()
    {
        using var fixture = new Fixture(_time);
        var first = fixture.Create();
        var second = fixture.Create();
        Assert.Equal(first.RunId, second.RunId);
        Assert.Single(fixture.Store.List(null, null));
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
        Assert.Empty(restarted.AwaitingActivation());
        Assert.Equal("empty", restarted.Claim(Claim("engine_one")).Status);

        var retried = restarted.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review-report:review_attempt:report_key", TaskStates.AutoReview,
            "review_attempt", 3, 4, claimable: false);
        Assert.Equal(prepared.RunId, retried.RunId);
        restarted.Activate(retried.RunId);
        Assert.Equal(retried.RunId, restarted.Claim(Claim("engine_one")).Run!.RunId);
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

    private sealed class FakeApplier : IMonolithOrchestrationApplier
    {
        public int Count { get; private set; }
        public bool ThrowNext { get; set; }
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new IOException("Simulated crash before authority effect.");
            }
            Count++;
            return Task.CompletedTask;
        }
    }
}
