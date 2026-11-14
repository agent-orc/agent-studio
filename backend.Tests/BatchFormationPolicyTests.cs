using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3568: repack membership, intent ownership and intent recovery of the formation planner.</summary>
public sealed class BatchFormationPolicyTests
{
    // clock-independent: the pure policy receives explicit instants that meet only each other.
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 59, 0, TimeSpan.Zero);

    [Fact]
    public void Chooses_the_largest_compatible_set_even_when_the_first_card_blocks_two()
    {
        BatchPlannerCard[] cards =
        [
            new("A", ["one.cs", "two.cs"]),
            new("B", ["one.cs"]),
            new("C", ["two.cs"]),
        ];

        var selected = BatchFormationPolicy.LargestCompatible(cards, 8, "separate");

        Assert.Equal(["B", "C"], selected.Select(card => card.TaskKey));
        Assert.Equal(["one.cs"], BatchFormationPolicy.ConflictingFiles(cards[0], cards[1]));
    }

    [Fact]
    public void Adjacent_overlap_allows_shared_files_and_maximum_size_still_applies()
    {
        var cards = Enumerable.Range(0, 3)
            .Select(index => new BatchPlannerCard($"C{index}", ["same.cs"]))
            .ToArray();

        Assert.Equal(["C0", "C1"], BatchFormationPolicy
            .LargestCompatible(cards, 2, "adjacent").Select(card => card.TaskKey));
    }

    [Fact]
    public void Unavailable_diff_conflicts_under_separate_overlap()
        => Assert.Equal(["changed files unavailable"],
            BatchFormationPolicy.ConflictingFiles(new("A", null), new("B", ["b.cs"])));

    [Fact]
    public void Repack_merges_two_small_formed_batches_into_one_successor_that_keeps_the_oldest_place()
    {
        var plan = BatchFormationPolicy.Repack(Facts(
            [Waiting("old", At, "A"), Waiting("new", At.AddMinutes(5), "B")],
            members: [Card("A"), Card("B")], eligible: [Card("C")]));

        Assert.Equal(BatchRepackAction.Replace, plan.Action);
        Assert.Equal(["new", "old"], plan.Retiring.Order(StringComparer.Ordinal));
        Assert.Equal(["A", "B", "C"], plan.SelectedKeys.Order(StringComparer.Ordinal));
        Assert.Equal(At, plan.QueuedSinceUtc);
    }

    [Fact]
    public void Repack_keeps_a_batch_that_already_holds_the_largest_selection_and_annotates_the_rest()
    {
        var plan = BatchFormationPolicy.Repack(Facts(
            [Waiting("full", At, "A", "B"), Waiting("blocked", At.AddMinutes(1), "C")],
            members: [Card("A", "a.cs"), Card("B", "b.cs"), Card("C", "a.cs")], eligible: [],
            maximumSize: 2));

        Assert.Equal(BatchRepackAction.Annotate, plan.Action);
        Assert.Empty(plan.Retiring);
        Assert.Equal("new batch: overlap separate, files a.cs", plan.Annotations["blocked"]);
    }

    [Fact]
    public void Repack_does_nothing_for_one_batch_without_eligible_cards_or_below_minimum_size()
    {
        Assert.Equal(BatchRepackAction.None, BatchFormationPolicy.Repack(Facts(
            [Waiting("only", At, "A", "B")], members: [Card("A"), Card("B")], eligible: [])).Action);
        Assert.Equal(BatchRepackAction.None, BatchFormationPolicy.Repack(Facts(
            [Waiting("only", At, "A")], members: [Card("A")], eligible: [Card("B")],
            minimumSize: 3)).Action);
    }

    [Fact]
    public void Repack_drops_waiting_members_whose_queued_record_is_gone()
    {
        var plan = BatchFormationPolicy.Repack(Facts(
            [Waiting("one", At, "A", "GONE"), Waiting("two", At.AddMinutes(1), "B")],
            members: [Card("A"), Card("B")], eligible: []));

        Assert.Equal(BatchRepackAction.Replace, plan.Action);
        Assert.Equal(["A", "B"], plan.SelectedKeys);
    }

    [Fact]
    public void Repack_exclusion_names_the_overlapping_files_or_the_maximum_size()
    {
        BatchPlannerCard[] selected = [Card("A", "a.cs")];
        Assert.Equal((true, "overlap separate: a.cs"),
            BatchFormationPolicy.RepackExclusion(Card("B", "a.cs"), selected, 4, "separate"));
        Assert.Equal((false, "maximumSize 4"),
            BatchFormationPolicy.RepackExclusion(Card("C", "c.cs"), selected, 4, "separate"));
        Assert.Equal((false, "maximumSize 4"),
            BatchFormationPolicy.RepackExclusion(Card("B", "a.cs"), selected, 4, "adjacent"));
    }

    [Fact]
    public void Intent_is_accepted_when_no_other_owner_holds_its_generations()
    {
        var decision = BatchFormationPolicy.Decide(Intent(BatchIntentKind.Formation, ["A", "B"],
            owners: [Owner("C", "other")]));

        Assert.True(decision.Accepted);
        Assert.Empty(decision.Conflicts);
    }

    [Fact]
    public void Intent_is_refused_when_another_batch_or_open_intent_owns_a_generation()
    {
        var decision = BatchFormationPolicy.Decide(Intent(BatchIntentKind.Formation, ["A", "B"],
            owners: [Owner("B", "concurrent-planner")]));

        Assert.False(decision.Accepted);
        Assert.Equal(BatchFormationPolicy.GenerationOwned, decision.Refusal);
        Assert.Equal("B", Assert.Single(decision.Conflicts).TaskKey);
    }

    [Fact]
    public void Replacement_may_take_the_generations_of_the_batches_it_retires()
    {
        var decision = BatchFormationPolicy.Decide(Intent(BatchIntentKind.Replacement, ["A", "B"],
            owners: [Owner("A", "old"), Owner("B", "old")],
            retiring: [new BatchRetiringFact("old", BatchPhase.Formed, false)]));

        Assert.True(decision.Accepted);
    }

    [Theory]
    [InlineData(BatchPhase.Assembling, false, BatchFormationPolicy.PredecessorNotFormed)]
    [InlineData(BatchPhase.Abandoned, false, BatchFormationPolicy.PredecessorNotFormed)]
    [InlineData(BatchPhase.Formed, true, BatchFormationPolicy.PredecessorAssembling)]
    public void Replacement_retires_only_formed_batches_no_flight_assembles(BatchPhase phase,
        bool assembling, string refusal)
        => Assert.Equal(refusal, BatchFormationPolicy.Decide(Intent(BatchIntentKind.Replacement, ["A"],
            retiring: [new BatchRetiringFact("old", phase, assembling)])).Refusal);

    [Theory]
    [InlineData(BatchPhase.Formed, true)]
    [InlineData(BatchPhase.Green, true)]
    [InlineData(BatchPhase.Published, false)]
    [InlineData(BatchPhase.Abandoned, false)]
    public void Own_flight_replaces_its_batch_in_any_unsettled_phase(BatchPhase phase, bool accepted)
        => Assert.Equal(accepted, BatchFormationPolicy.Decide(Intent(BatchIntentKind.Replacement, ["A"],
            retiring: [new BatchRetiringFact("old", phase, Assembling: true, RetiredByOwnFlight: true)])).Accepted);

    [Fact]
    public void Intent_without_members_is_refused()
        => Assert.Equal(BatchFormationPolicy.NoMembers,
            BatchFormationPolicy.Decide(Intent(BatchIntentKind.Formation, [])).Refusal);

    [Fact]
    public void Interrupted_intent_without_a_durable_successor_is_discarded()
    {
        // Nothing was retired yet, so the predecessors still own their members.
        Assert.Equal(BatchIntentStep.Discard, BatchFormationPolicy.Recover(new(false, null,
            [new BatchRetiringFact("old", BatchPhase.Formed, false)])));
        Assert.Equal(BatchIntentStep.Discard, BatchFormationPolicy.Recover(new(true, BatchPhase.Abandoned,
            [new BatchRetiringFact("old", BatchPhase.Formed, false)])));
    }

    [Theory]
    [InlineData(BatchPhase.Formed)]
    [InlineData(BatchPhase.Abandoned)]
    public void Interrupted_intent_with_a_durable_successor_rolls_forward(BatchPhase predecessor)
        => Assert.Equal(BatchIntentStep.ClaimAndRetire, BatchFormationPolicy.Recover(new(true,
            BatchPhase.Formed, [new BatchRetiringFact("old", predecessor, false)])));

    [Fact]
    public void Interrupted_intent_whose_predecessor_started_first_rolls_back()
    {
        Assert.Equal(BatchIntentStep.RollBack, BatchFormationPolicy.Recover(new(true, BatchPhase.Formed,
            [new BatchRetiringFact("old", BatchPhase.Assembling, false)])));
        Assert.Equal(BatchIntentStep.ClaimAndRetire, BatchFormationPolicy.Recover(new(true, BatchPhase.Formed,
            [new BatchRetiringFact("own", BatchPhase.Green, false, RetiredByOwnFlight: true)])));
        Assert.Equal(BatchIntentStep.RollBack, BatchFormationPolicy.Recover(new(true, BatchPhase.Formed,
            [new BatchRetiringFact("own", BatchPhase.Published, false, RetiredByOwnFlight: true)])));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(BatchPhase.Unknown, false)]
    [InlineData(BatchPhase.Abandoned, false)]
    [InlineData(BatchPhase.Formed, true)]
    [InlineData(BatchPhase.Running, true)]
    public void Predecessors_retire_only_after_the_successor_is_durable(BatchPhase? successor, bool allowed)
        => Assert.Equal(allowed, BatchFormationPolicy.MayRetirePredecessors(successor));

    private static BatchPlannerCard Card(string key, params string[] paths)
        => new(key, paths.Length == 0 ? [key.ToLowerInvariant() + ".cs"] : paths);

    private static BatchRepackWaiting Waiting(string id, DateTimeOffset closedAt, params string[] keys)
        => new(id, keys, closedAt, closedAt);

    private static BatchRepackFacts Facts(BatchRepackWaiting[] waiting, BatchPlannerCard[] members,
        BatchPlannerCard[] eligible, int minimumSize = 2, int maximumSize = 8)
        => new(waiting, members, eligible, minimumSize, maximumSize, "separate");

    private static BatchGenerationOwner Owner(string key, string owner)
        => new(new BatchPendingGeneration(key, "run-1", null), owner);

    private static BatchIntentFacts Intent(BatchIntentKind kind, string[] keys,
        BatchGenerationOwner[]? owners = null, BatchRetiringFact[]? retiring = null)
        => new(kind, "successor",
            keys.Select(key => new BatchPendingGeneration(key, "run-1", "review-" + key)).ToArray(),
            retiring ?? [], owners ?? []);
}
