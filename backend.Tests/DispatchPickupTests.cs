using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: all expiry observations are frozen fixture instants.
public sealed class DispatchPickupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-pickup-" + Guid.NewGuid().ToString("N"));
    private DispatchStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Dispatch:StoragePath"] = _root }).Build());
    private static DispatchPlan Plan() => new("p", DispatchStrategies.OnePerArea, "v1", "area-warden",
        [new(1, "A", 0, false, false, DispatchCandidateState.Eligible), new(2, "B", 0, false, false, DispatchCandidateState.Eligible)],
        [], [], [], [], [new(1, DispatchSlotAction.Admit, "A", "first-slot", false, [])]);

    [Theory]
    [InlineData("local")]
    [InlineData("remote-claim")]
    public void Both_paths_record_admission_and_serialization_without_losing_the_admitting_decision(string path)
    {
        var store = Store();
        var lease = store.AcquireLease("p", "area-warden", 0, Now, TimeSpan.FromHours(1), true, true, true)!;
        var record = new DispatchShadowRecordV1("evaluation", "decision", "facts", Now, path, Plan(), false);
        Assert.True(store.WriteLive(new(record, lease.Fence, []), Now));
        var admitted = DispatchPickupPolicy.Decide(record.Plan, "A", Now, true, new HashSet<string>());
        var serialized = DispatchPickupPolicy.Decide(record.Plan, "B", Now, true, new HashSet<string>());
        Assert.True(admitted.Admit);
        Assert.False(serialized.Admit);
        Assert.Equal("A", serialized.WaitsFor);
        store.AppendRunDecision("p", new(record.DecisionId, "A", Now, path, true, admitted.Reason, null));
        store.BindRun("p", "A", record.DecisionId, "attempt");
        store.AppendRunDecision("p", new("later-serialize", "A", Now.AddMinutes(1), path, false, "area-busy", "B"));
        Assert.Equal("decision", Store().AdmittingDecision("p", "A"));
        Assert.Equal(3, Store().ReadRunDecisions("p").Count);
        Assert.False(Assert.Single(Store().ReadDispatcherLog("p")).Shadow);
        Assert.True(Store().VerifyDispatcherLog("p"));
    }

    [Fact]
    public void A_hold_names_its_blocker_and_releases_at_the_exact_expiry()
    {
        var plan = Plan() with { Holds = [new("A", "area-busy", "C", Now, Now.AddHours(1))] };
        var held = DispatchPickupPolicy.Decide(plan, "A", Now, true, new HashSet<string>());
        Assert.False(held.Admit);
        Assert.Equal("C", held.WaitsFor);
        Assert.Equal(Now.AddHours(1), held.ExpiresAt);
        Assert.True(DispatchPickupPolicy.Decide(plan, "A", Now.AddHours(1), true, new HashSet<string>()).Admit);
    }

    [Fact]
    public void Plan_edges_gate_until_the_named_target_is_satisfied()
    {
        var plan = Plan() with { DependenciesToAdd = [new("A", "C", "dispatch:area-warden", "chain-overlap")] };
        Assert.Equal("C", DispatchPickupPolicy.Decide(plan, "A", Now, true, new HashSet<string>()).WaitsFor);
        Assert.True(DispatchPickupPolicy.Decide(plan, "A", Now, true, new HashSet<string> { "C" }).Admit);
        Assert.False(DispatchPickupPolicy.Decide(plan, "A", Now, false, new HashSet<string> { "C" }).Admit);
    }

    [Fact]
    public void Restart_and_lease_replacement_cannot_admit_a_stale_plan()
    {
        var store = Store();
        var lease = store.AcquireLease("p", "area-warden", 0, Now, TimeSpan.FromHours(1), true, true, true)!;
        var record = new DispatchShadowRecordV1("eval", "decision", "facts", Now, "local", Plan(), false);
        Assert.True(store.WriteLive(new(record, lease.Fence, []), Now));
        Assert.Equal("decision", Store().ReadLive("p")!.Record.DecisionId);
        Assert.False(store.WriteLive(new(record, lease.Fence, []), Now.AddHours(1)));
        Assert.True(store.ReleaseLease("p", lease.Persona, lease.Fence, Now));
        Assert.NotNull(store.AcquireLease("p", "surveyor", lease.Fence, Now, TimeSpan.FromHours(1), true, true, true));
        Assert.False(store.WriteLive(new(record, lease.Fence, []), Now));
    }

    [Theory]
    [InlineData("off", "B,A")]
    [InlineData("shadow", "B,A")]
    [InlineData("live", "A,B")]
    public void Both_order_paths_apply_only_live_positions_without_writing_owner_order(string mode, string expected)
    {
        var cards = new[] { new TaskInfo { Id = "a", Key = "A", ProjectName = "p", Order = 2 },
            new TaskInfo { Id = "b", Key = "B", ProjectName = "p", Order = 1 } };
        ProjectSettings Settings(string _) => new() { DispatchMode = mode };
        Assert.Equal(expected.Split(','), LaneSortApplier.Sort(cards, TaskStates.Ready, Settings, _ => Plan()).Select(c => c.Key));
        Assert.Equal(expected.Split(','), LaneSortApplier.PickupOrder(cards, Settings, _ => Plan()).Select(c => c.Key));
        Assert.Equal(new[] { 2, 1 }, cards.Select(c => c.Order));
    }

    [Fact]
    public void Skipped_unroutable_heads_do_not_reserve_slots_and_a_resume_never_overlaps_itself()
    {
        var forecast = new DispatchForecast(["backend/Features/Tasks/A.cs"], ["backend/Features/Tasks"], DispatchForecastConfidence.High);
        var facts = new DispatchFacts("p", Now,
            [new("A", 999, Now, [], true, forecast, []), new("B", 999, Now.AddMinutes(-1), [], true, forecast, [])],
            [new("B", false, forecast, [])], 1, DispatchLedgerSummary.Empty, [], PickupCandidates: ["B"]);
        var plan = DispatchPlanPolicy.Plan(facts, new(DispatchStrategies.OnePerArea, "v1"));
        Assert.Equal("B", Assert.Single(plan.Admitted));
        Assert.Equal(new[] { "A", "B" }, plan.Order.Select(c => c.CardKey));
    }

    [Fact]
    public void Exclusive_marks_survive_reevaluation_only_while_the_card_remains_present()
    {
        var mark = new DispatchExclusiveMark("A", "over-module-limit", "four modules");
        var retained = DispatchPickupPolicy.PreserveExclusive(Plan(), [mark], new HashSet<string> { "A" });
        Assert.Equal(mark, Assert.Single(retained.ExclusiveMarks));
        Assert.Empty(DispatchPickupPolicy.PreserveExclusive(Plan(), [mark], new HashSet<string>()).ExclusiveMarks);
    }

    [Fact]
    public void A_plan_edge_survives_its_predecessor_leaving_Ready_until_integration()
    {
        var edge = new DispatchDependencyEdge("A", "C", "dispatch:packer", "chain-overlap");
        var facts = new DispatchFacts("p", Now, [new("A", 999, Now, [], true, DispatchForecast.Unknown, [])],
            [new("C", false, DispatchForecast.Unknown, [])], 1, DispatchLedgerSummary.Empty, [],
            ActiveDependencies: [edge]);
        var held = DispatchPlanPolicy.Plan(facts, DispatchStrategies.Starter(DispatchStrategies.LaneEntry));
        Assert.Equal(edge, Assert.Single(held.DependenciesToAdd));
        Assert.Empty(held.Admitted);
        var released = DispatchPlanPolicy.Plan(facts with { SatisfiedDependencies = ["C"] },
            DispatchStrategies.Starter(DispatchStrategies.LaneEntry));
        Assert.Empty(released.DependenciesToAdd);
        Assert.Equal("A", Assert.Single(released.Admitted));
    }

    [Fact]
    public void A_skipped_head_keeps_its_named_hold_while_another_card_is_considered()
    {
        var forecast = new DispatchForecast(["backend/Features/Tasks/A.cs"], ["backend/Features/Tasks"], DispatchForecastConfidence.High);
        var ready = Enumerable.Range(1, 4).Select(i => new DispatchReadyCard("A" + i, 999,
            Now.AddMinutes(-i), [], true, i == 1 ? forecast : DispatchForecast.Unknown, [])).ToArray();
        var original = new DispatchHold("A1", "area-busy", "C", Now.AddMinutes(-1), Now.AddHours(1));
        var facts = new DispatchFacts("p", Now, ready, [new("C", false, forecast, [])], 1,
            DispatchLedgerSummary.Empty, [], ActiveHolds: [original], PickupCandidates: ["A2"]);
        var plan = DispatchPlanPolicy.Plan(facts, DispatchStrategies.Starter(DispatchStrategies.OnePerArea));
        Assert.Equal(original, Assert.Single(plan.Holds));
        Assert.DoesNotContain(plan.Releases, r => r.CardKey == "A1");
        Assert.Equal("A2", Assert.Single(plan.Admitted));
    }

    [Fact]
    public void The_run_timeline_preserves_the_admitting_decision_id()
    {
        var timeline = RunTimelineBuilder.Build([new SessionEvent { Ts = Now.UtcDateTime, Kind = "start",
            DispatchDecisionId = "decision" }], [], Now.UtcDateTime);
        Assert.Equal("decision", Assert.Single(timeline.Runs).DispatchDecisionId);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
