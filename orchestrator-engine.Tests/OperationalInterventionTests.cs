using AgentStudio.OrchestrationDecisions;
using AgentStudio.OrchestratorEngine.OperationalInterventions;
using Xunit;

namespace AgentStudio.OrchestratorEngine.Tests;

public sealed class OperationalInterventionTests
{
    private readonly TestTime _clock = new(new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("host-overload", "lower-host-admission")]
    [InlineData("review-daemon-stuck", "restart-stuck-worker")]
    [InlineData("gate-worker-stuck", "restart-stuck-worker")]
    [InlineData("provider-auth-invalid", "reroute-attempts")]
    [InlineData("batch-gate-stuck", "drop-completed-gate-members")]
    [InlineData("provider-rate-limited", "requeue-from-delivery")]
    [InlineData("deploy-drift", "retry-engine-start")]
    public async Task Stall_event_applies_one_bounded_effect_and_writes_a_linked_receipt(string kind, string action)
    {
        var effects = new FakeEffects();
        var ledger = new FakeLedger();
        var facts = Facts(kind);

        var receipt = await new OperationalInterventionCoordinator(effects, ledger, _clock)
            .HandleAsync(facts, default);

        Assert.NotNull(receipt);
        Assert.Equal(action, receipt.Action);
        Assert.Equal(AutonomyClass.A1, receipt.Autonomy);
        Assert.Equal("applied", receipt.Outcome);
        Assert.Equal("observed-before", receipt.Before);
        Assert.Equal("observed-after", receipt.After);
        Assert.Contains("load 46.7 on 12 cores", receipt.Observation);
        if (kind == "host-overload")
            Assert.Equal("admission 6", effects.Actions[0].After);
        Assert.NotEmpty(receipt.Reason);
        Assert.NotEmpty(receipt.Undo);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(10), receipt.CheckAt);
        Assert.Equal(["AGT-1"], ledger.CardLinks);
        Assert.Single(effects.Actions);
        Assert.Single(ledger.Receipts);
    }

    [Fact]
    public async Task Overload_recovers_only_after_the_observed_load_is_below_the_restore_threshold()
    {
        var effects = new FakeEffects();
        var ledger = new FakeLedger();
        var coordinator = new OperationalInterventionCoordinator(effects, ledger, _clock);
        var initial = Facts("host-overload");
        await coordinator.HandleAsync(initial, default);
        _clock.Advance(TimeSpan.FromMinutes(12));
        var recovered = initial with
        {
            EventId = "event-recovered",
            ConditionCleared = true,
            CurrentAdmission = 6,
            PreviousAdmission = 8,
            Load1 = 6,
        };

        var receipt = await coordinator.HandleAsync(recovered, default);

        Assert.Equal("restore-host-admission", receipt!.Action);
        Assert.Equal("admission 8", effects.Actions[1].After);
        Assert.Contains("restore", receipt.Undo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Follow_up_escalates_once_when_the_condition_persists_and_does_not_loop()
    {
        var effects = new FakeEffects();
        var ledger = new FakeLedger();
        var coordinator = new OperationalInterventionCoordinator(effects, ledger, _clock);
        var facts = Facts("host-overload");
        await coordinator.HandleAsync(facts, default);
        Assert.Null(await coordinator.HandleAsync(facts, default));
        _clock.Advance(TimeSpan.FromMinutes(11));

        var escalation = await coordinator.HandleAsync(facts, default);
        var repeated = await coordinator.HandleAsync(facts, default);

        Assert.Equal("escalate", escalation!.Action);
        Assert.Equal(AutonomyClass.A0, escalation.Autonomy);
        Assert.Equal(2, ledger.Receipts.Count);
        Assert.Single(effects.Actions);
        Assert.Null(repeated);
    }

    [Fact]
    public async Task Two_other_host_actions_in_an_hour_block_a_third()
    {
        var ledger = new FakeLedger();
        ledger.Receipts.Add(Existing("one", _clock.GetUtcNow().AddMinutes(-20)));
        ledger.Receipts.Add(Existing("two", _clock.GetUtcNow().AddMinutes(-5)));
        var effects = new FakeEffects();

        var receipt = await new OperationalInterventionCoordinator(effects, ledger, _clock)
            .HandleAsync(Facts("gate-worker-stuck"), default);

        Assert.Equal("escalate", receipt!.Action);
        Assert.Contains("2 automatic actions", receipt.Reason);
        Assert.Empty(effects.Actions);
    }

    [Theory]
    [InlineData("provider-auth-invalid", "renew-provider-login")]
    [InlineData("toolchain-missing", "add-review-toolchain")]
    [InlineData("long-host-move", "move-work-to-host")]
    public async Task Human_only_changes_are_proposals(string kind, string action)
    {
        var effects = new FakeEffects();
        var receipt = await new OperationalInterventionCoordinator(effects, new FakeLedger(), _clock)
            .HandleAsync(Facts(kind) with { HasHealthyAlternative = false }, default);
        Assert.Equal(action, receipt!.Action);
        Assert.Equal(AutonomyClass.A2, receipt.Autonomy);
        Assert.Equal("propose", receipt.Outcome);
        Assert.Empty(effects.Actions);
    }

    [Fact]
    public async Task Empty_salvage_does_not_create_a_rate_limit_retry()
    {
        var effects = new FakeEffects();
        var receipt = await new OperationalInterventionCoordinator(effects, new FakeLedger(), _clock)
            .HandleAsync(Facts("provider-rate-limited") with { HasLastRealDelivery = false }, default);
        Assert.Equal("escalate", receipt!.Action);
        Assert.Empty(effects.Actions);
    }

    [Fact]
    public async Task Expired_batch_closes_when_no_completed_member_needs_reconciliation()
    {
        var effects = new FakeEffects();
        var receipt = await new OperationalInterventionCoordinator(effects, new FakeLedger(), _clock)
            .HandleAsync(Facts("batch-gate-stuck") with
            {
                CompletedGateMembers = 0,
                GateDeadlinePassed = true,
            }, default);
        Assert.Equal("close-expired-batch", receipt!.Action);
        Assert.Single(effects.Actions);
    }

    [Fact]
    public async Task Effect_failure_is_receipted_and_the_follow_up_escalates_without_retry()
    {
        var effects = new FakeEffects { Fail = true };
        var ledger = new FakeLedger();
        var coordinator = new OperationalInterventionCoordinator(effects, ledger, _clock);
        var facts = Facts("gate-worker-stuck");
        var failed = await coordinator.HandleAsync(facts, default);
        var escalated = await coordinator.HandleAsync(facts, default);

        Assert.StartsWith("failed:", failed!.Outcome);
        Assert.Contains("outcome unknown", failed.After);
        Assert.Equal("escalate", escalated!.Action);
        Assert.Single(effects.Actions);
        Assert.Equal(2, ledger.Receipts.Count);
    }

    private OperationalStallFacts Facts(string kind) => new(
        "event-1", "fingerprint-1", kind, "runner-01", _clock.GetUtcNow(), ["AGT-1"],
        Load1: 46.7, CpuCores: 12, CurrentAdmission: 8,
        HasHealthyAlternative: true, CompletedGateMembers: 3, PendingGateMembers: 19,
        HasLastRealDelivery: true, CorrectedConfigurationVerified: true);

    private static OperationalInterventionReceipt Existing(string id, DateTimeOffset at) =>
        new(id, id, "runner-01", "restart-stuck-worker", AutonomyClass.A1, at,
            at.AddMinutes(10), "observation", "reason", "before", "after", "undo", "applied");

    private sealed class FakeEffects : IOperationalInterventionEffects
    {
        public List<OperationalInterventionDecision> Actions { get; } = [];
        public bool Fail { get; init; }

        public Task<OperationalEffectResult> ApplyAsync(OperationalStallFacts facts,
            OperationalInterventionDecision decision, CancellationToken ct)
        {
            Actions.Add(decision);
            if (Fail) throw new InvalidOperationException("fake target unavailable");
            return Task.FromResult(new OperationalEffectResult(true, "observed-before", "observed-after", "ok"));
        }
    }

    private sealed class FakeLedger : IOperationalInterventionLedger
    {
        public List<OperationalInterventionReceipt> Receipts { get; } = [];
        public IReadOnlyList<string> CardLinks { get; private set; } = [];

        public Task<IReadOnlyList<OperationalInterventionReceipt>> ReadAsync(string hostId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OperationalInterventionReceipt>>(Receipts.ToArray());

        public Task AppendAsync(OperationalInterventionReceipt receipt, IReadOnlyList<string> affectedCards,
            CancellationToken ct)
        {
            Receipts.Add(receipt);
            CardLinks = affectedCards;
            return Task.CompletedTask;
        }
    }

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
