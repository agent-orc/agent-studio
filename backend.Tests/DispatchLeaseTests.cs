using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Configuration;
using AgentStudio.Runner;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every scheduling window, expiry and evidence timestamp is supplied explicitly.
public sealed class DispatchLeaseTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-lease-" + Guid.NewGuid().ToString("N"));
    private DispatchStore Store => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Dispatch:StoragePath"] = _root }).Build());
    private static DispatchChallenger Candidate(string id = "area-warden", string strategy = DispatchStrategies.OnePerArea)
        => new(id, strategy, new(60, 65, 100, 2, 0, false, 0, null, null, false));
    private static DispatchLeaseFacts Facts(string? wave = "one") => new(At, wave, "queue-keeper",
        [Candidate()], ["queue-keeper", "area-warden", "surveyor", "packer"], [], new("", 0, null, null, false));

    [Fact]
    public void Windows_alternate_once_per_project_wave_and_retain_assignment_when_revisited()
    {
        var first = DispatchLeasePolicy.Decide(new(), Facts());
        Assert.Equal("queue-keeper", first.Persona);
        Assert.Equal("area-warden", first.State.Challenger);
        var repeated = DispatchLeasePolicy.Decide(first.State, Facts() with { Now = At.AddHours(2) });
        Assert.Equal(first.State, repeated.State);
        var second = DispatchLeasePolicy.Decide(repeated.State, Facts("two"));
        Assert.Equal("area-warden", second.Persona);
        var oldWave = DispatchLeasePolicy.Decide(second.State, Facts());
        Assert.Equal("queue-keeper", oldWave.Persona);
        Assert.Equal(2, oldWave.State.Windows.Count);
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(new(), Facts("two")).Persona);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Paused_assignment_survives_a_new_window_and_owner_controls(bool stopped, bool pinned)
    {
        var state = new DispatchLeaseState("area-warden", Stopped: stopped, Pinned: pinned ? "area-warden" : null);
        var paused = Facts() with { CurrentPersona = "area-warden", Available = ["queue-keeper"], Challengers = [] };
        var fallback = DispatchLeasePolicy.Decide(state, paused);
        Assert.Equal("queue-keeper", fallback.Persona);
        Assert.Equal("assigned-persona-unavailable", fallback.Reason);
        var resumed = DispatchLeasePolicy.Decide(fallback.State, paused with
            { CurrentPersona = "queue-keeper", Available = ["queue-keeper", "area-warden"] });
        Assert.Equal("area-warden", resumed.Persona);
        Assert.Equal(stopped, resumed.State.Stopped);
        Assert.Equal(state.Pinned, resumed.State.Pinned);
    }

    [Fact]
    public void Missing_wave_uses_utc_day_without_advancing_on_each_pick()
    {
        var first = DispatchLeasePolicy.Decide(new(), Facts(null) with { Now = At.ToOffset(TimeSpan.FromHours(12)) });
        Assert.Equal("day:2026-11-20", first.Window);
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(first.State, Facts(null)).Persona);
        Assert.Equal("area-warden", DispatchLeasePolicy.Decide(first.State, Facts(null) with { Now = At.AddDays(1) }).Persona);
    }

    [Theory]
    [InlineData(99, 2, 65, true)]
    [InlineData(100, 1, 65, true)]
    [InlineData(100, 2, 64.99, true)]
    [InlineData(100, 2, 65, false)]
    public void Thin_shadow_failed_gate_or_small_margin_cannot_enter(int cards, int waves, double value, bool gate)
    {
        var candidate = Candidate();
        candidate = candidate with { Facts = candidate.Facts with
            { ShadowIntegratedCards = cards, ShadowWaves = waves, ChallengerValue = value, GatePassed = gate } };
        var result = DispatchLeasePolicy.Decide(new(), Facts() with { Challengers = [candidate] });
        Assert.Null(result.State.Challenger);
    }

    [Fact]
    public void Area_warden_is_first_and_packer_remains_shadow_even_when_it_scores_highest()
    {
        var packer = Candidate("packer", DispatchStrategies.PackageThenChain) with { Facts = Candidate().Facts with { ChallengerValue = 100 } };
        var surveyor = Candidate("surveyor", DispatchStrategies.FileOverlap);
        Assert.Equal("area-warden", DispatchLeasePolicy.Decide(new(), Facts() with { Challengers = [packer, surveyor, Candidate()] }).State.Challenger);
        Assert.Null(DispatchLeasePolicy.Decide(new(), Facts() with { Challengers = [packer, surveyor] }).State.Challenger);
    }

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, false)]
    public void Champion_changes_only_after_four_settled_winning_pairs(int rounds, bool lose, bool promoted)
    {
        var state = DispatchLeasePolicy.Decide(new(), Facts()).State;
        for (var i = 1; i < rounds * 2; i++) state = DispatchLeasePolicy.Decide(state, Facts("wave-" + i)).State;
        var results = state.Windows.Select((w, i) => new DispatchLiveWaveResult(w.Key, w.Epoch, w.Persona,
            w.Persona == "queue-keeper" ? 60 : lose && i == 3 ? 59 : 65, true)).ToArray();
        var result = DispatchLeasePolicy.Decide(state, Facts("next") with { Results = results, Challengers = [] });
        Assert.Equal(promoted ? "area-warden" : "queue-keeper", result.State.Champion);
        if (promoted) Assert.Null(result.State.Challenger);
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(state, Facts("next") with
            { Results = results.Select(r => r with { Complete = false }).ToArray(), Challengers = [] }).State.Champion);
    }

    [Fact]
    public void Promotion_does_not_admit_a_second_challenger_using_the_previous_champions_scores()
    {
        var state = DispatchLeasePolicy.Decide(new(), Facts()).State;
        for (var i = 1; i < 8; i++) state = DispatchLeasePolicy.Decide(state, Facts("wave-" + i)).State;
        var results = state.Windows.Select(w => new DispatchLiveWaveResult(w.Key, w.Epoch, w.Persona,
            w.Persona == "queue-keeper" ? 60 : 70, true)).ToArray();
        var next = DispatchLeasePolicy.Decide(state, Facts("next") with
            { Results = results, Challengers = [Candidate("surveyor", DispatchStrategies.FileOverlap)] });
        Assert.Equal("area-warden", next.State.Champion);
        Assert.Null(next.State.Challenger);
    }

    [Fact]
    public void Guard_is_t1_importance_70_and_owner_undo_is_fenced_by_guard_id_and_sample()
    {
        var state = new DispatchLeaseState("area-warden", Pinned: "area-warden", Stopped: true);
        var facts = Facts() with { CurrentPersona = "area-warden", Guard = new("sample", 20, 15, 5, true) };
        var fallback = DispatchLeasePolicy.Decide(state, facts);
        Assert.Equal("queue-keeper", fallback.Persona);
        Assert.Equal("T1", fallback.AuthorityTier);
        Assert.Equal(70, fallback.FeedImportance);
        Assert.NotNull(fallback.State.Guard);
        Assert.False(DispatchLeasePolicy.Decide(fallback.State, facts with { OwnerAction = "undo-guard", ExpectedGuard = "stale" }).Allowed);
        var undone = DispatchLeasePolicy.Decide(fallback.State, facts with
            { CurrentPersona = "queue-keeper", OwnerAction = "undo-guard", ExpectedGuard = fallback.State.Guard.Id });
        Assert.Equal("area-warden", undone.Persona);
        Assert.Null(undone.State.Guard);
        Assert.Equal("area-warden", DispatchLeasePolicy.Decide(undone.State, facts).Persona);
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(undone.State, facts with
            { Guard = facts.Guard with { Digest = "new-sample" } }).Persona);
        Assert.Null(DispatchLeasePolicy.Decide(state, facts with { Guard = facts.Guard with { SameCards = false } }).State.Guard);
        Assert.Null(DispatchLeasePolicy.Decide(state, facts with { Guard = facts.Guard with { Cards = 19 } }).State.Guard);
        Assert.Null(DispatchLeasePolicy.Decide(state, facts with { Guard = facts.Guard with { LiveConflictsPer100 = 14.99 } }).State.Guard);
        var alternating = DispatchLeasePolicy.Decide(new("area-warden"), facts);
        var restored = DispatchLeasePolicy.Decide(alternating.State, facts with { OwnerAction = "undo-guard",
            ExpectedGuard = alternating.State.Guard!.Id, CurrentPersona = "queue-keeper" });
        Assert.False(restored.State.Stopped);
        Assert.Null(restored.State.Pinned);
        Assert.Equal("area-warden", restored.Persona);
    }

    [Fact]
    public void Owner_can_pin_or_stop_but_cannot_pin_an_unauthorized_persona_or_disable_guard()
    {
        var first = DispatchLeasePolicy.Decide(new(), Facts());
        var pinned = DispatchLeasePolicy.Decide(first.State, Facts() with { OwnerAction = "pin", OwnerPersona = "area-warden" });
        Assert.Equal("area-warden", pinned.Persona);
        Assert.Equal("area-warden", DispatchLeasePolicy.Decide(pinned.State, Facts("two")).Persona);
        Assert.True(pinned.State.Stopped);
        Assert.False(DispatchLeasePolicy.Decide(first.State, Facts() with { OwnerAction = "pin", OwnerPersona = "unknown" }).Allowed);
        var stopped = DispatchLeasePolicy.Decide(first.State, Facts() with { OwnerAction = "stop" });
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(stopped.State, Facts("two")).Persona);
        var guarded = DispatchLeasePolicy.Decide(pinned.State, Facts() with
            { CurrentPersona = "area-warden", Guard = new("s", 20, 20, 0, true) });
        Assert.Equal("queue-keeper", DispatchLeasePolicy.Decide(guarded.State, Facts() with
            { OwnerAction = "pin", OwnerPersona = "area-warden" }).Persona);
    }

    [Fact]
    public void State_and_fence_survive_restart_and_stale_writers_cannot_overwrite_owner_control()
    {
        var store = Store;
        var first = DispatchLeasePolicy.Decide(new(), Facts());
        var lease = store.ApplyLeaseDecision("p", 0, first, At, "platform")!;
        Assert.Equal(1, lease.Fence);
        Assert.Equal("area-warden", Store.ReadLease("P")!.Rollout!.Challenger);
        Assert.Null(Store.ReadLease("other"));
        var pinned = DispatchLeasePolicy.Decide(first.State, Facts() with { OwnerAction = "pin", OwnerPersona = "area-warden" });
        var next = Store.ApplyLeaseDecision("p", 1, pinned, At, "owner")!;
        Assert.Equal("owner", next.Actor);
        Assert.Equal(2, next.Fence);
        Assert.Null(store.ApplyLeaseDecision("p", 1, first, At, "platform"));
        Assert.Null(store.AcquireLease("p", "queue-keeper", 2, At.AddDays(2), TimeSpan.FromHours(1), true, true, true));
        Assert.Equal("area-warden", Store.ReadLease("p")!.Persona);
    }

    [Fact]
    public void Gateway_and_engine_roundtrip_keep_the_same_pure_lease_decision()
    {
        var occurrence = new DispatchLeaseOccurrence(new(), Facts(), 0, null);
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.DispatchLease, "lease:p", DispatchLease: occurrence);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var frozen = AiOperatorDecisionGateway.ReadPayload(json);
        var decision = AiOperatorDecisionPolicy.Decide(frozen);
        var gateway = new AiOperatorDecisionGateway(new ConfigurationBuilder().Build());
        var monolith = gateway.Submit(new("p", "dispatch:p", "dispatch-lease", payload, null)).MonolithDecision!;
        Assert.Equal(AiOperatorDisposition.Act, decision.Disposition);
        Assert.Equal(AutonomyClass.A1, decision.Autonomy);
        Assert.Equal(monolith.DispatchLease!.Persona, decision.DispatchLease!.Persona);
        Assert.Equal("area-warden", decision.DispatchLease.State.Challenger);
        var receipt = AiOperatorDecisionGateway.ReadDecision(JsonSerializer.Serialize(new { operatorDecision = decision },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(decision.DispatchLease.Persona, receipt.DispatchLease!.Persona);
        var state = decision.DispatchLease.State;
        var heldPayload = payload with { DispatchLease = occurrence with { State = state,
            LeaseExpiresAt = At.AddHours(24), Facts = Facts() with { CurrentPersona = "queue-keeper" } } };
        Assert.Equal(AiOperatorDisposition.Hold, gateway.Preview(heldPayload).Disposition);
    }

    [Fact]
    public void Card_keeps_original_admission_and_conflict_belongs_to_later_card_even_if_earlier_card_merges_last()
    {
        var store = Store;
        var baseline = DispatchPlanPolicy.Plan(new("p", At, [], [], 1, DispatchLedgerSummary.Empty, [], Persona: "queue-keeper"), DispatchStrategies.Starter(DispatchStrategies.LaneEntry));
        store.AppendShadow(new("e", "earlier", "digest", At, "local", baseline, false));
        store.AppendShadow(new("f", "later", "digest", At.AddMinutes(1), "remote-claim", baseline with { Persona = "area-warden" }, false));
        store.AppendRunDecision("p", new("earlier", "A", At, "local", true, "admit", null));
        store.BindRun("p", "A", "earlier", "attempt-a");
        store.AppendRunDecision("p", new("later", "B", At.AddMinutes(1), "remote-claim", true, "admit", null));
        store.BindRun("p", "B", "later", "attempt-b");
        store.ApplyLeaseDecision("p", 0, DispatchLeasePolicy.Decide(new(), Facts()), At, "platform");
        Assert.Equal("earlier", Store.AdmittingDecision("p", "A"));
        var conflict = new DispatchConflictRecordV1("c", "p", "A", "merge", "base", "tip", ["a.cs"], ["B"], At.AddHours(1))
            { DispatchDecisionIds = new Dictionary<string, string?> { ["A"] = "earlier", ["B"] = "later" } };
        Assert.True(store.AppendConflict(conflict));
        var recorded = Assert.Single(Store.ReadConflicts("p"));
        Assert.Equal("B", recorded.AdmittedLaterCard);
        Assert.Equal("area-warden", recorded.PersonaId);
        Assert.Equal("live", recorded.PersonaMode);
        Assert.Equal("later", Store.ConflictAdmission("p", recorded)!.DecisionId);
        Assert.False(store.AppendConflict(conflict with { At = At.AddDays(1) }));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
