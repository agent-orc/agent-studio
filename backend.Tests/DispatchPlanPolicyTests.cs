using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the pure policy receives an explicit now; fixture instants meet only each other.

/// <summary>
/// DS-3 (Dossier docs/dispatcher-personas): the five starter strategies of
/// <see cref="DispatchPlanPolicy"/> on small fixtures, and the hard
/// constraints every strategy keeps: pins and urgent first within a priority
/// class, dependsOn, exclusive runs, the slot count and the hold bounds.
/// </summary>
public sealed class DispatchPlanPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Hub = "backend/Host/Program.cs";

    public static TheoryData<string> AllStrategies => new()
    {
        DispatchStrategies.LaneEntry, DispatchStrategies.OnePerArea, DispatchStrategies.FileOverlap,
        DispatchStrategies.LedgerLearned, DispatchStrategies.PackageThenChain,
    };

    public static TheoryData<string> ForecastStrategies => new()
    {
        DispatchStrategies.OnePerArea, DispatchStrategies.FileOverlap,
        DispatchStrategies.LedgerLearned, DispatchStrategies.PackageThenChain,
    };

    // ---------------------------------------------------------------- hard constraints

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void Pins_then_urgent_lead_each_priority_class_and_classes_keep_their_order(string strategy)
    {
        var plan = Plan(strategy, Facts(
        [
            Card("AGT-1", minutes: 1),
            Card("AGT-2", minutes: 5),
            Card("AGT-3", minutes: 2, urgent: true),
            Card("AGT-4", minutes: 0, order: 2),
            Card("AGT-5", minutes: 9, order: 1),
            Card("AGT-6", minutes: 9, priorityClass: 1, order: 0),
            Card("AGT-7", minutes: 9, priorityClass: -1),
        ], freeSlots: 0));

        Assert.Equal(["AGT-7", "AGT-5", "AGT-4", "AGT-3", "AGT-2", "AGT-1", "AGT-6"],
            plan.Order.Select(entry => entry.CardKey));
        Assert.Equal(Enumerable.Range(1, 7), plan.Order.Select(entry => entry.Position));
        Assert.Empty(plan.Slots);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void A_card_with_an_undelivered_dependency_never_takes_a_slot(string strategy)
    {
        var plan = Plan(strategy, Facts(
        [
            Card("AGT-2", minutes: 5, dependenciesSatisfied: false, dependsOn: ["AGT-9"]),
            Card("AGT-1", minutes: 1),
        ], freeSlots: 2));

        Assert.Equal(DispatchCandidateState.WaitsForDependency, plan.Order[0].State);
        Assert.Equal(["AGT-1"], plan.Admitted);
        Assert.Equal(DispatchSlotAction.Serialize, plan.Slots[1].Action);
        Assert.Equal(DispatchReasonCodes.NoCandidate, plan.Slots[1].ReasonCode);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void An_exclusive_in_flight_card_holds_back_every_candidate(string strategy)
    {
        var plan = Plan(strategy, Facts(
            [Card("AGT-2", minutes: 2, files: ["runner/A.cs"]), Card("AGT-1", minutes: 1, files: ["docs/x.md"])],
            freeSlots: 2,
            inFlight: [InFlight("AGT-9", exclusive: true)]));

        Assert.Empty(plan.Admitted);
        Assert.All(plan.Slots, slot =>
        {
            Assert.Equal(DispatchReasonCodes.ExclusiveRunning, slot.ReasonCode);
            Assert.Equal(["AGT-2", "AGT-1"], slot.PassedOver.Select(item => item.CardKey));
            Assert.All(slot.PassedOver, item => Assert.Equal("AGT-9", item.With));
        });
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void An_exclusive_candidate_waits_for_the_project_to_drain_and_then_runs_alone(string strategy)
    {
        var busy = Plan(strategy, Facts([Card("AGT-1", minutes: 1, exclusive: true)], freeSlots: 1,
            inFlight: [InFlight("AGT-9", files: ["docs/other.md"])]));
        Assert.Equal(DispatchReasonCodes.ExclusiveWaitsForDrain, busy.Slots[0].PassedOver.Single().ReasonCode);

        var idle = Plan(strategy, Facts(
            [Card("AGT-2", minutes: 2, exclusive: true), Card("AGT-1", minutes: 1, files: ["docs/x.md"])], freeSlots: 3));
        Assert.Equal(["AGT-2"], idle.Admitted);
        Assert.True(idle.Slots[0].RunsAlone);
        Assert.Equal(DispatchReasonCodes.RunAlone, idle.Slots[0].ReasonCode);
        Assert.All(idle.Slots.Skip(1), slot => Assert.Equal(DispatchReasonCodes.ExclusiveRunning, slot.ReasonCode));
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void The_plan_never_fills_more_than_the_free_slots(string strategy)
    {
        var cards = Enumerable.Range(1, 8)
            .Select(index => Card($"AGT-{index}", minutes: index, files: [$"area{index}/x.sh"], scope: [$"tools/t{index}"]))
            .ToArray();
        var plan = Plan(strategy, Facts(cards, freeSlots: 3));

        Assert.Equal(3, plan.Slots.Count);
        Assert.Equal(["AGT-8", "AGT-7", "AGT-6"], plan.Admitted);
        Assert.Empty(Plan(strategy, Facts(cards, freeSlots: -1)).Slots);
    }

    [Theory]
    [MemberData(nameof(ForecastStrategies))]
    public void Every_serialize_carries_a_reason_code_and_the_passed_over_candidates(string strategy)
    {
        var plan = Plan(strategy, Facts(
            [Card("AGT-2", minutes: 2, files: ["runner/A.cs"]), Card("AGT-1", minutes: 1, files: ["runner/A.cs"])],
            freeSlots: 1,
            inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])]));

        var slot = Assert.Single(plan.Slots);
        Assert.Equal(DispatchSlotAction.Serialize, slot.Action);
        Assert.False(string.IsNullOrWhiteSpace(slot.ReasonCode));
        Assert.Equal(["AGT-2", "AGT-1"], slot.PassedOver.Select(item => item.CardKey));
        Assert.All(slot.PassedOver, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.ReasonCode));
            Assert.Equal("AGT-9", item.With);
        });
        Assert.All(plan.Holds, hold =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hold.ReasonCode));
            Assert.Equal("AGT-9", hold.WaitsFor);
        });
    }

    [Fact]
    public void The_in_flight_window_compares_with_cards_that_finished_coding_but_did_not_integrate()
    {
        var cards = new[] { Card("AGT-1", minutes: 1, files: ["runner/A.cs"], scope: ["runner"]) };
        var awaiting = InFlight("AGT-9", holdsSlot: false, files: ["runner/A.cs"], scope: ["runner"]);

        foreach (var strategy in new[] { DispatchStrategies.LaneEntry, DispatchStrategies.FileOverlap })
        {
            var inFlight = Plan(strategy, Facts(cards, freeSlots: 1, inFlight: [awaiting]));
            Assert.Empty(inFlight.Admitted);
            var running = Plan(strategy, Facts(cards, freeSlots: 1, inFlight: [awaiting], window: DispatchOverlapWindow.Running));
            Assert.Equal(["AGT-1"], running.Admitted);
        }
    }

    [Fact]
    public void A_read_only_card_is_admitted_beside_anything_but_an_exclusive_run()
    {
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(
            [Card("AGT-1", minutes: 1, readOnly: true)], freeSlots: 1,
            inFlight: [InFlight("AGT-9")]));
        Assert.Equal(DispatchReasonCodes.ReadOnly, plan.Slots[0].ReasonCode);
    }

    // ---------------------------------------------------------------- holds

    [Fact]
    public void A_blocked_card_is_held_with_reason_waits_for_and_a_24_hour_expiry()
    {
        var plan = Plan(DispatchStrategies.FileOverlap, Facts(Filler(
            Card("AGT-1", minutes: 1, files: ["runner/A.cs"])), freeSlots: 0,
            inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])]));

        var hold = Assert.Single(plan.Holds);
        Assert.Equal(("AGT-1", DispatchReasonCodes.FileOverlap, "AGT-9"), (hold.CardKey, hold.ReasonCode, hold.WaitsFor));
        Assert.Equal(Now, hold.PlacedAtUtc);
        Assert.Equal(Now.AddHours(24), hold.ExpiresAtUtc);
        Assert.Equal(0, hold.Renewals);
    }

    [Fact]
    public void A_hold_is_never_longer_than_24_hours_even_when_the_parameter_asks_for_more()
    {
        var strategy = new DispatchStrategy(DispatchStrategies.FileOverlap, "test",
            new Dictionary<string, string> { [DispatchStrategies.HoldHours] = "48", [DispatchStrategies.MaxHeldShare] = "0.9" });
        Assert.Equal(2, DispatchStrategies.Validate(strategy).Count);

        var cards = Enumerable.Range(1, 4)
            .Select(index => Card($"AGT-{index}", minutes: index, files: ["runner/A.cs"])).ToArray();
        var plan = DispatchPlanPolicy.Plan(Facts(cards, freeSlots: 0, inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])]), strategy);

        var hold = Assert.Single(plan.Holds);
        Assert.Equal(Now.AddHours(24), hold.ExpiresAtUtc);
    }

    [Fact]
    public void At_most_a_quarter_of_Ready_is_held_and_holds_follow_the_plan_order()
    {
        var cards = Enumerable.Range(1, 9)
            .Select(index => Card($"AGT-{index}", minutes: index, files: ["runner/A.cs"])).ToArray();
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(cards, freeSlots: 0,
            inFlight: [InFlight("AGT-90", files: ["runner/A.cs"])],
            activeHolds: [new DispatchHold("AGT-1", DispatchReasonCodes.AreaBusy, "AGT-90", Now.AddHours(-1), Now.AddHours(23))]));

        Assert.Equal(["AGT-9", "AGT-8"], plan.Holds.Select(hold => hold.CardKey));
        Assert.Contains(new DispatchRelease("AGT-1", DispatchReasonCodes.HoldShareCap, false), plan.Releases);
    }

    [Fact]
    public void An_expired_hold_is_renewed_once_and_then_becomes_a_decision_request()
    {
        var blocked = Filler(Card("AGT-1", minutes: 1, files: ["runner/A.cs"]));
        var inFlight = new[] { InFlight("AGT-9", files: ["runner/A.cs"]) };
        var expired = new DispatchHold("AGT-1", DispatchReasonCodes.FileOverlap, "AGT-9", Now.AddHours(-25), Now.AddHours(-1));

        var renewed = Plan(DispatchStrategies.FileOverlap, Facts(blocked, freeSlots: 0, inFlight: inFlight, activeHolds: [expired]));
        var hold = Assert.Single(renewed.Holds);
        Assert.Equal((1, Now.AddHours(24), Now.AddHours(-25)), (hold.Renewals, hold.ExpiresAtUtc, hold.PlacedAtUtc));

        var exhausted = Plan(DispatchStrategies.FileOverlap, Facts(blocked, freeSlots: 0, inFlight: inFlight,
            activeHolds: [expired with { Renewals = 1 }]));
        Assert.Empty(exhausted.Holds);
        Assert.Equal(new DispatchRelease("AGT-1", DispatchReasonCodes.HoldLimitReached, true), Assert.Single(exhausted.Releases));

        var requested = Plan(DispatchStrategies.FileOverlap, Facts(blocked, freeSlots: 0, inFlight: inFlight,
            decisionRequested: ["AGT-1"]));
        Assert.Empty(requested.Holds);
    }

    [Fact]
    public void Holds_are_released_when_the_blocker_clears_the_card_starts_or_leaves_Ready()
    {
        DispatchHold Held(string key) => new(key, DispatchReasonCodes.FileOverlap, "AGT-9", Now.AddHours(-2), Now.AddHours(22));
        var plan = Plan(DispatchStrategies.FileOverlap, Facts(
            [Card("AGT-2", minutes: 2, files: ["runner/A.cs"]), Card("AGT-1", minutes: 1, files: ["docs/x.md"])],
            freeSlots: 1,
            activeHolds: [Held("AGT-1"), Held("AGT-2"), Held("AGT-3")]));

        Assert.Equal(["AGT-2"], plan.Admitted);
        Assert.Equal(
            [
                new DispatchRelease("AGT-1", DispatchReasonCodes.BlockerCleared, false),
                new DispatchRelease("AGT-2", DispatchReasonCodes.Admitted, false),
                new DispatchRelease("AGT-3", DispatchReasonCodes.LeftReady, false),
            ],
            plan.Releases);
    }

    // ---------------------------------------------------------------- Queue keeper

    [Fact]
    public void Queue_keeper_admits_on_free_slots_with_unknown_scope_optimistic_and_never_holds_chains_or_marks()
    {
        var cards = Filler(
            Card("AGT-3", minutes: 3, files: ["runner/A.cs", "a/1/x", "b/2/x", "c/3/x", "d/4/x"], source: Wave("w1", 2, prior: 40)),
            Card("AGT-2", minutes: 2, files: ["runner/A.cs"], source: Wave("w1", 1)));
        var plan = Plan(DispatchStrategies.LaneEntry, Facts(cards, freeSlots: 2, inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])]));

        Assert.Equal(["AGT-3", "AGT-2"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.ParallelOptimistic, plan.Slots[0].ReasonCode);
        Assert.Empty(plan.Holds);
        Assert.Empty(plan.DependenciesToAdd);
        Assert.Empty(plan.ExclusiveMarks);
        Assert.Equal("queue-keeper", plan.Persona);
    }

    [Fact]
    public void Queue_keeper_serializes_two_declared_scopes_that_overlap_by_prefix()
    {
        var plan = Plan(DispatchStrategies.LaneEntry, Facts(
            [Card("AGT-2", minutes: 2, scope: ["backend/Features/Tasks/Acceptance"]), Card("AGT-1", minutes: 1, scope: ["docs"])],
            freeSlots: 1, inFlight: [InFlight("AGT-9", scope: ["backend/Features/Tasks"])]));

        Assert.Equal(["AGT-1"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.ParallelDisjoint, plan.Slots[0].ReasonCode);
        Assert.Equal(new DispatchPassOver("AGT-2", DispatchReasonCodes.ScopeOverlap, "AGT-9", "backend/features/tasks"),
            Assert.Single(plan.Slots[0].PassedOver));
    }

    // ---------------------------------------------------------------- Area warden

    [Fact]
    public void Area_warden_never_runs_two_cards_of_one_module_and_skips_ahead_to_fill_the_slot()
    {
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(
        [
            Card("AGT-3", minutes: 3, files: ["frontend/src/app/features/board/a.ts"]),
            Card("AGT-2", minutes: 2, files: ["frontend/src/app/features/board/b.ts"]),
            Card("AGT-1", minutes: 1, files: ["backend/Features/Runner/C.cs"]),
        ], freeSlots: 2));

        Assert.Equal(["AGT-3", "AGT-1"], plan.Admitted);
        Assert.Equal(new DispatchPassOver("AGT-2", DispatchReasonCodes.AreaBusy, "AGT-3", "frontend/src/app/features/board"),
            Assert.Single(plan.Slots[1].PassedOver));
    }

    [Fact]
    public void Area_warden_runs_one_card_of_unknown_area_at_a_time()
    {
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(
            [Card("AGT-3", minutes: 3), Card("AGT-2", minutes: 2), Card("AGT-1", minutes: 1, files: ["runner/A.cs"])],
            freeSlots: 3));

        Assert.Equal(["AGT-3", "AGT-1"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.UnknownAreaBusy, plan.Slots[1].PassedOver.Single().ReasonCode);
        Assert.Equal(DispatchReasonCodes.UnknownAreaBusy, plan.Slots[2].ReasonCode);
    }

    [Fact]
    public void Area_warden_ignores_hub_files_when_it_derives_areas()
    {
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(
        [
            Card("AGT-2", minutes: 2, files: [Hub, "backend/Features/Deploy/A.cs"]),
            Card("AGT-1", minutes: 1, files: [Hub, "backend/Features/Runner/B.cs"]),
        ], freeSlots: 2));

        Assert.Equal(["AGT-2", "AGT-1"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.ParallelDisjoint, plan.Slots[1].ReasonCode);
    }

    [Fact]
    public void Area_warden_marks_a_card_over_three_modules_exclusive_within_two_marks_per_project()
    {
        string[] Wide(string tag) =>
            [$"backend/Features/{tag}A/x.cs", $"backend/Features/{tag}B/x.cs", $"backend/Features/{tag}C/x.cs", $"runner/{tag}.cs"];
        var cards = new[]
        {
            Card("AGT-3", minutes: 3, files: Wide("P")),
            Card("AGT-2", minutes: 2, files: Wide("Q")),
            Card("AGT-1", minutes: 1, files: ["backend/Features/One/x.cs", "backend/Features/Two/x.cs", "runner/A.cs"]),
        };

        var plan = Plan(DispatchStrategies.OnePerArea, Facts(cards, freeSlots: 2));
        Assert.Equal(["AGT-3", "AGT-2"], plan.ExclusiveMarks.Select(mark => mark.CardKey));
        Assert.All(plan.ExclusiveMarks, mark => Assert.Equal(DispatchReasonCodes.OverModuleLimit, mark.ReasonCode));
        Assert.Equal(["AGT-3"], plan.Admitted);
        Assert.True(plan.Slots[0].RunsAlone);

        var capped = Plan(DispatchStrategies.OnePerArea, Facts(cards, freeSlots: 2, activeMarks: ["AGT-70"]));
        Assert.Equal(["AGT-3"], capped.ExclusiveMarks.Select(mark => mark.CardKey));
    }

    [Fact]
    public void Area_warden_uses_forecast_modules_when_the_forecast_names_them()
    {
        var plan = Plan(DispatchStrategies.OnePerArea, Facts(
        [
            Card("AGT-2", minutes: 2, files: ["runner/A.cs"], modules: ["deploy"]),
            Card("AGT-1", minutes: 1, files: ["tools/B.sh"], modules: ["deploy"]),
        ], freeSlots: 2));
        Assert.Equal(["AGT-2"], plan.Admitted);
    }

    // ---------------------------------------------------------------- Surveyor

    [Fact]
    public void Surveyor_serializes_only_on_a_shared_non_hub_file()
    {
        var plan = Plan(DispatchStrategies.FileOverlap, Facts(
        [
            Card("AGT-4", minutes: 4, files: [Hub, "backend/Features/Deploy/A.cs"]),
            Card("AGT-3", minutes: 3, files: [Hub, "backend/Features/Deploy/B.cs"]),
            Card("AGT-2", minutes: 2, files: ["backend/Features/Deploy/A.cs"]),
            Card("AGT-1", minutes: 1, files: ["docs/x.md"]),
        ], freeSlots: 3));

        Assert.Equal(["AGT-4", "AGT-3", "AGT-1"], plan.Admitted);
        Assert.Equal(new DispatchPassOver("AGT-2", DispatchReasonCodes.FileOverlap, "AGT-4", "backend/Features/Deploy/A.cs"),
            plan.Slots[2].PassedOver.Single());
    }

    [Fact]
    public void Surveyor_falls_back_to_module_level_for_a_low_confidence_forecast()
    {
        var plan = Plan(DispatchStrategies.FileOverlap, Facts(
        [
            Card("AGT-2", minutes: 2, files: ["backend/Features/Deploy/A.cs"]),
            Card("AGT-1", minutes: 1, files: ["backend/Features/Deploy/B.cs"], confidence: DispatchForecastConfidence.Low),
        ], freeSlots: 2));

        Assert.Equal(["AGT-2"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.AreaBusy, plan.Slots[1].PassedOver.Single().ReasonCode);
    }

    [Fact]
    public void Surveyor_admits_an_unknown_forecast_optimistically()
    {
        var plan = Plan(DispatchStrategies.FileOverlap, Facts([Card("AGT-1", minutes: 1)], freeSlots: 1,
            inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])]));
        Assert.Equal(DispatchReasonCodes.ParallelOptimistic, plan.Slots[0].ReasonCode);
    }

    // ---------------------------------------------------------------- Historian

    [Fact]
    public void Historian_with_an_empty_ledger_plans_exactly_like_the_Surveyor()
    {
        var facts = Facts(
        [
            Card("AGT-4", minutes: 4, files: ["backend/Features/Deploy/A.cs"], source: Wave("w1", 1)),
            Card("AGT-3", minutes: 3, files: ["backend/Features/Deploy/A.cs"], source: Wave("w1", 2)),
            Card("AGT-2", minutes: 2, files: ["backend/Features/Deploy/B.cs"], confidence: DispatchForecastConfidence.Low),
            Card("AGT-1", minutes: 1, files: ["docs/x.md"]),
        ], freeSlots: 2, inFlight: [InFlight("AGT-9", files: ["docs/x.md"])]);

        var surveyor = Plan(DispatchStrategies.FileOverlap, facts);
        var historian = Plan(DispatchStrategies.LedgerLearned, facts);
        Assert.Equal(surveyor.Order, historian.Order);
        Assert.Equal(surveyor.Holds, historian.Holds);
        Assert.Equal(surveyor.DependenciesToAdd, historian.DependenciesToAdd);
        Assert.Equal(surveyor.Slots.Select(Shape), historian.Slots.Select(Shape));
    }

    [Fact]
    public void Historian_lets_one_card_at_a_time_touch_a_module_hot_within_14_days()
    {
        DispatchFacts WithHot(DateTimeOffset lastConflict) => Facts(
        [
            Card("AGT-2", minutes: 2, files: ["backend/Features/Deploy/A.cs"]),
            Card("AGT-1", minutes: 1, files: ["backend/Features/Deploy/B.cs"]),
        ], freeSlots: 2, ledger: new DispatchLedgerSummary(
            [new DispatchHotModule("backend/Features/Deploy", lastConflict)], [], []));

        var hot = Plan(DispatchStrategies.LedgerLearned, WithHot(Now.AddDays(-13)));
        Assert.Equal(["AGT-2"], hot.Admitted);
        Assert.Equal(new DispatchPassOver("AGT-1", DispatchReasonCodes.HotModule, "AGT-2", "backend/Features/Deploy"),
            hot.Slots[1].PassedOver.Single());

        var cold = Plan(DispatchStrategies.LedgerLearned, WithHot(Now.AddDays(-15)));
        Assert.Equal(["AGT-2", "AGT-1"], cold.Admitted);
        Assert.Equal(["AGT-2", "AGT-1"], Plan(DispatchStrategies.FileOverlap, WithHot(Now.AddDays(-1))).Admitted);
    }

    [Fact]
    public void Historian_never_runs_two_cards_that_collided_together_again()
    {
        var plan = Plan(DispatchStrategies.LedgerLearned, Facts(
            [Card("AGT-1", minutes: 1, files: ["docs/x.md"])], freeSlots: 1,
            inFlight: [InFlight("AGT-9", files: ["runner/A.cs"])],
            ledger: new DispatchLedgerSummary([], [new DispatchCollidedPair("agt-9", "AGT-1")], [])));

        Assert.Empty(plan.Admitted);
        Assert.Equal(DispatchReasonCodes.CollidedPair, plan.Slots[0].ReasonCode);
    }

    [Fact]
    public void Historian_chains_a_source_whose_earlier_wave_had_more_than_five_partners_per_card()
    {
        var cards = new[]
        {
            Card("AGT-3", minutes: 0, files: ["backend/Features/Deploy/A.cs"], source: Wave("dp", 2, kind: "playthrough")),
            Card("AGT-2", minutes: 0, files: ["backend/Features/Deploy/A.cs"], source: Wave("dp", 1, kind: "playthrough")),
            Card("AGT-1", minutes: 0, files: ["backend/Features/Tasks/A.cs"], source: Wave("rv", 1, kind: "review-findings")),
            Card("AGT-0", minutes: 0, files: ["backend/Features/Tasks/A.cs"], source: Wave("rv", 2, kind: "review-findings")),
        };
        var ledger = new DispatchLedgerSummary([], [],
            [new DispatchSourceHistory("playthrough", 18.8), new DispatchSourceHistory("review-findings", 1.3)]);

        var plan = Plan(DispatchStrategies.LedgerLearned, Facts(cards, freeSlots: 0, ledger: ledger, persona: "historian-1"));
        Assert.Equal(new DispatchDependencyEdge("AGT-3", "AGT-2", "dispatch:historian-1", DispatchReasonCodes.ChainDenseSource),
            Assert.Single(plan.DependenciesToAdd));
    }

    // ---------------------------------------------------------------- Packer

    [Fact]
    public void Packer_chains_an_overlapping_cluster_of_one_wave_in_source_order()
    {
        var plan = Plan(DispatchStrategies.PackageThenChain, Facts(
        [
            Card("AGT-14", minutes: 0, files: ["backend/Features/Deploy/A.cs"], source: Wave("dp", 4)),
            Card("AGT-13", minutes: 0, files: ["backend/Features/Deploy/A.cs", "runner/B.cs"], source: Wave("dp", 3)),
            Card("AGT-12", minutes: 0, files: ["docs/y.md"], source: Wave("dp", 2)),
            Card("AGT-11", minutes: 0, files: ["runner/B.cs"], source: Wave("dp", 1)),
            Card("AGT-10", minutes: 0, files: ["runner/B.cs"], source: Wave("in", 1)),
        ], freeSlots: 4));

        Assert.Equal(
            [
                new DispatchDependencyEdge("AGT-13", "AGT-11", "dispatch:packer", DispatchReasonCodes.ChainOverlap),
                new DispatchDependencyEdge("AGT-14", "AGT-13", "dispatch:packer", DispatchReasonCodes.ChainOverlap),
            ],
            plan.DependenciesToAdd);
        Assert.Equal(DispatchCandidateState.WaitsForDependency, plan.Order.Single(entry => entry.CardKey == "AGT-14").State);
        Assert.Equal(["AGT-12", "AGT-11"], plan.Admitted);
        Assert.Equal(DispatchReasonCodes.FileOverlap,
            plan.Slots[2].PassedOver.Single(item => item.CardKey == "AGT-10").ReasonCode);
    }

    [Fact]
    public void Packer_never_chains_a_pinned_or_urgent_card_or_closes_a_cycle_with_existing_edges()
    {
        var plan = Plan(DispatchStrategies.PackageThenChain, Facts(
        [
            Card("AGT-4", minutes: 0, files: ["runner/B.cs"], source: Wave("dp", 4), order: 1),
            Card("AGT-3", minutes: 0, files: ["runner/B.cs"], source: Wave("dp", 3), urgent: true),
            Card("AGT-2", minutes: 0, files: ["runner/B.cs"], source: Wave("dp", 2)),
            Card("AGT-1", minutes: 0, files: ["runner/B.cs"], source: Wave("dp", 1), dependsOn: ["AGT-2"]),
        ], freeSlots: 0));

        Assert.Empty(plan.DependenciesToAdd);
    }

    [Fact]
    public void Packer_runs_a_card_alone_when_its_source_prior_exceeds_25_code_files()
    {
        var plan = Plan(DispatchStrategies.PackageThenChain, Facts(
        [
            Card("AGT-2", minutes: 2, files: ["runner/A.cs"], source: Wave("in", 1, prior: 26)),
            Card("AGT-1", minutes: 1, files: ["docs/x.md"], source: Wave("rv", 1, prior: 5)),
        ], freeSlots: 2));

        var mark = Assert.Single(plan.ExclusiveMarks);
        Assert.Equal(("AGT-2", DispatchReasonCodes.OverSizePrior), (mark.CardKey, mark.ReasonCode));
        Assert.Equal(["AGT-2"], plan.Admitted);
        Assert.True(plan.Slots[0].RunsAlone);
    }

    // ---------------------------------------------------------------- strategies as data

    [Fact]
    public void The_catalogue_holds_the_five_starter_strategies_as_valid_data()
    {
        Assert.Equal(
            [("lane-entry-v1", "queue-keeper"), ("one-per-area-v1", "area-warden"), ("file-overlap-v1", "surveyor"),
             ("ledger-learned-v1", "historian"), ("package-then-chain-v1", "packer")],
            DispatchStrategies.Catalogue.Select(definition => (definition.Id, definition.Persona)));
        foreach (var definition in DispatchStrategies.Catalogue)
        {
            var strategy = DispatchStrategies.Starter(definition.Id);
            Assert.Equal(DispatchStrategy.SchemaV1, strategy.Schema);
            Assert.Equal(DispatchStrategies.StarterRevision, strategy.Revision);
            Assert.Empty(DispatchStrategies.Validate(strategy));
        }
    }

    [Fact]
    public void An_unknown_strategy_or_parameter_is_refused()
    {
        Assert.Contains("unknown strategy id 'random-v1'",
            DispatchStrategies.Validate(new DispatchStrategy("random-v1", "1")));
        Assert.Throws<ArgumentException>(() =>
            DispatchPlanPolicy.Plan(Facts([], freeSlots: 1), new DispatchStrategy("random-v1", "1")));
        Assert.Single(DispatchStrategies.Validate(new DispatchStrategy(DispatchStrategies.LaneEntry, "1",
            new Dictionary<string, string> { [DispatchStrategies.HoldHours] = "4" })));
        Assert.Single(DispatchStrategies.Validate(new DispatchStrategy(DispatchStrategies.FileOverlap, "1", Schema: "dispatch-strategy.v2")));
    }

    [Theory]
    [InlineData("frontend/src/app/features/board/card/card.component.ts", "frontend/src/app/features/board")]
    [InlineData("frontend/src/app/models/task.model.ts", "frontend/src/app/models")]
    [InlineData("frontend/e2e/deploy/area.spec.ts", "frontend/e2e/deploy")]
    [InlineData("backend/Features/Runner/ProjectRunner.cs", "backend/Features/Runner")]
    [InlineData("backend/Host/Program.cs", "backend/Host")]
    [InlineData("orchestration-decisions/DispatchPlanPolicy.cs", "orchestration-decisions")]
    [InlineData("runner\\GitWorkspace.cs", "runner")]
    [InlineData("README.md", "README.md")]
    public void The_module_map_reduces_a_path_to_its_module(string path, string module)
        => Assert.Equal(module, DispatchPlanPolicy.ModuleOf(path));

    [Fact]
    public void The_policy_is_deterministic_for_the_same_facts()
    {
        var facts = Facts(
            [Card("AGT-2", minutes: 2, files: ["runner/A.cs"]), Card("AGT-1", minutes: 1, files: ["runner/A.cs"])],
            freeSlots: 2, inFlight: [InFlight("AGT-9", files: ["docs/z.md"])]);
        foreach (var definition in DispatchStrategies.Catalogue)
        {
            var first = Plan(definition.Id, facts);
            var second = Plan(definition.Id, facts);
            Assert.Equal(first.Order, second.Order);
            Assert.Equal(first.Holds, second.Holds);
            Assert.Equal(first.Slots.Select(Shape), second.Slots.Select(Shape));
        }
    }

    // ---------------------------------------------------------------- fixtures

    private static string Shape(DispatchSlotDecision slot)
        => $"{slot.Slot}:{slot.Action}:{slot.CardKey}:{slot.ReasonCode}:{slot.RunsAlone}:"
           + string.Join(",", slot.PassedOver.Select(item => $"{item.CardKey}/{item.ReasonCode}/{item.With}/{item.Detail}"));

    private static DispatchPlan Plan(string strategy, DispatchFacts facts)
        => DispatchPlanPolicy.Plan(facts, DispatchStrategies.Starter(strategy));

    private static DispatchReadyCard Card(string key, int minutes, string[]? files = null, string[]? modules = null,
        string[]? scope = null, int order = DispatchReadyCard.UnpinnedOrder, int priorityClass = 0, bool urgent = false,
        bool exclusive = false, bool readOnly = false, bool dependenciesSatisfied = true, string[]? dependsOn = null,
        DispatchForecastConfidence? confidence = null, DispatchSourceWave? source = null)
        => new(key, order, Now.AddMinutes(-60 + minutes), dependsOn ?? [], dependenciesSatisfied,
            Forecast(files, modules, confidence), scope ?? [], exclusive, readOnly, priorityClass, urgent, source);

    private static DispatchInFlightCard InFlight(string key, bool holdsSlot = true, string[]? files = null,
        string[]? scope = null, bool exclusive = false)
        => new(key, holdsSlot, Forecast(files, null, null), scope ?? [], exclusive);

    private static DispatchForecast Forecast(string[]? files, string[]? modules, DispatchForecastConfidence? confidence)
        => files is null && modules is null
            ? DispatchForecast.Unknown
            : new DispatchForecast(files ?? [], modules ?? [], confidence ?? DispatchForecastConfidence.High);

    private static DispatchSourceWave Wave(string id, int position, int prior = 5, string kind = "playthrough")
        => new(id, kind, position, prior);

    /// <summary>The given cards plus unrelated read-only cards, so a quarter of Ready allows one hold.</summary>
    private static DispatchReadyCard[] Filler(params DispatchReadyCard[] cards)
        => [.. cards, .. Enumerable.Range(0, 4 - cards.Length % 4)
            .Select(index => Card($"DOC-{index}", minutes: -30 - index, readOnly: true, dependenciesSatisfied: false))];

    private static DispatchFacts Facts(IReadOnlyList<DispatchReadyCard> ready, int freeSlots,
        DispatchInFlightCard[]? inFlight = null, DispatchLedgerSummary? ledger = null,
        DispatchOverlapWindow window = DispatchOverlapWindow.InFlight, DispatchHold[]? activeHolds = null,
        string[]? activeMarks = null, string[]? decisionRequested = null, string? persona = null)
        => new("agent-studio", Now, ready, inFlight ?? [], freeSlots, ledger ?? DispatchLedgerSummary.Empty, [Hub],
            window, activeHolds, activeMarks, decisionRequested, persona);
}
