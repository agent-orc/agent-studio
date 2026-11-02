using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3407: a lone card takes its own gate before its deadline only on an
/// idle gate host, and an authority refuses a per-task decision computed
/// before a second eligible card arrived. Pure decision tests with explicit
/// timestamps; the repository keeps decision tests in backend.Tests.
/// </summary>
public sealed class BatchGateIdleHostPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, false, 0, 0, 3, true)]
    [InlineData(true, false, 0, 2, 3, true)]
    [InlineData(false, false, 0, 0, 3, false)]
    [InlineData(true, true, 0, 0, 3, false)]
    [InlineData(true, false, 1, 0, 3, false)]
    [InlineData(true, false, 0, 3, 3, false)]
    [InlineData(true, false, 3, 3, 3, false)]
    public void Gate_host_is_idle_only_without_batch_per_task_gate_or_full_slots(
        bool loadClear, bool batchRunning, int perTaskGates, int slotsBusy, int slotsTotal, bool expected)
        => Assert.Equal(expected, BatchGateLookAheadPlanner.GateHostIdle(
            loadClear, batchRunning, perTaskGates, slotsBusy, slotsTotal));

    [Fact]
    public void Lone_card_behind_a_busy_gate_host_waits_until_its_deadline()
    {
        var input = Input([Card("A", minutesAgo: 1)]);

        var waiting = BatchGateLookAheadPlanner.Plan(input);
        var due = BatchGateLookAheadPlanner.Plan(input with { NowUtc = Now.AddMinutes(19) });

        Assert.Equal("wait", waiting.Decision);
        Assert.Equal("waiting for target size or deadline", waiting.Reason);
        Assert.Equal(Now.AddMinutes(19), waiting.CloseAtUtc);
        Assert.Equal(["A"], waiting.ProposedMembers);
        Assert.Equal("per-task", due.Decision);
        Assert.Equal(["A"], due.ProposedMembers);
    }

    [Fact]
    public void Lone_card_on_an_idle_gate_host_takes_its_own_gate_at_once()
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input([Card("A", minutesAgo: 1)])
            with { IdleGateHost = true });

        Assert.Equal("per-task", plan.Decision);
        Assert.Equal(["A"], plan.ProposedMembers);
    }

    [Fact]
    public void Behind_a_gate_backlog_a_lone_card_waits_for_a_due_partner_until_it_ages_out()
    {
        var partner = new BatchPlanUpcoming("B", "review", Now.AddMinutes(10), .08, []);
        var pastDeadline = Input([Card("A", minutesAgo: 25)], [partner]) with { GateBacklog = true };
        var agedOut = pastDeadline with
        {
            Waiting = [Card("A", minutesAgo: 20 * BatchGateLookAheadPlanner.BacklogAgeOutDeadlines)],
        };

        var held = BatchGateLookAheadPlanner.Plan(pastDeadline);
        var released = BatchGateLookAheadPlanner.Plan(agedOut);

        Assert.Equal("wait", held.Decision);
        Assert.Equal(Now.AddMinutes(10), held.CloseAtUtc);
        Assert.Equal("per-task", released.Decision);
        Assert.Equal(["A"], released.ProposedMembers);
    }

    [Theory]
    [InlineData("adjacent", "a.cs", "b.cs")]
    [InlineData("separate", "a.cs", "b.cs")]
    [InlineData("adjacent", "shared.cs", "shared.cs")]
    public void Two_old_cards_returned_together_close_as_one_batch_when_their_paths_allow_it(
        string overlap, string pathA, string pathB)
    {
        // Both returned to the queue in the same tick, two deadlines after review.
        var plan = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", minutesAgo: 180, pathA), Card("B", minutesAgo: 179, pathB)]) with
        {
            Overlap = overlap, GateBacklog = true,
        });

        Assert.Equal("close", plan.Decision);
        Assert.Equal("deadline reached", plan.Reason);
        Assert.Equal(["A", "B"], plan.ProposedMembers);
    }

    [Fact]
    public void Two_old_cards_whose_paths_overlap_under_separate_take_their_own_gates_oldest_first()
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input(
            [Card("B", minutesAgo: 179, "shared.cs"), Card("A", minutesAgo: 180, "shared.cs")]) with
        {
            Overlap = "separate", GateBacklog = true,
        });

        Assert.Equal("per-task", plan.Decision);
        Assert.Equal(["A"], plan.ProposedMembers);
    }

    [Fact]
    public void Every_decision_names_the_waiting_cards_it_considered()
    {
        Assert.Equal([], BatchGateLookAheadPlanner.Plan(Input([])).Considered);
        Assert.Equal(["A"], BatchGateLookAheadPlanner.Plan(Input([Card("A", 1)])).Considered);
        Assert.Equal(["B", "A"], BatchGateLookAheadPlanner.Plan(Input(
            [Card("B", 1), Card("A", 2)])).Considered);
        Assert.Equal(["A", "B"], BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", 2, risk: .9), Card("B", 1)])).Considered);
    }

    [Fact]
    public void Per_task_decision_from_a_snapshot_without_the_second_eligible_card_is_refused()
    {
        // 23:30 on 1 November: the engine planned on A alone; B became eligible a tick later.
        var stale = BatchGateLookAheadPlanner.Plan(Input([Card("A", 1)]) with { IdleGateHost = true });
        var current = Facts(Input([Card("A", 2), Card("B", 1)]));

        Assert.Equal("per-task", stale.Decision);
        Assert.False(BatchPlanDecisionPolicy.Admissible(current, stale, out var error));
        Assert.Equal("per-task decision did not consider every eligible card", error);
        Assert.Equal("close", BatchPlanDecisionPolicy.Decide(current).Decision.Decision);
    }

    [Fact]
    public void Per_task_decision_that_considered_every_eligible_card_is_admissible()
    {
        var facts = Facts(Input([Card("A", 2, risk: .9), Card("B", 1)]));
        var decision = BatchGateLookAheadPlanner.Plan(facts.Snapshot);

        Assert.Equal("per-task", decision.Decision);
        Assert.True(BatchPlanDecisionPolicy.Admissible(facts, decision, out var error), error);
    }

    [Fact]
    public void Per_task_decision_for_the_only_eligible_card_is_admissible_whatever_it_considered()
    {
        var decision = new BatchPlanDecision("per-task", "idle gate host", null, ["A"], 2, [],
            Considered: []);

        Assert.True(BatchPlanDecisionPolicy.Admissible(Facts(Input([Card("A", 1)])), decision, out var error),
            error);
    }

    [Fact]
    public void Per_task_decision_of_an_older_decider_without_considered_cards_stays_admissible()
    {
        var decision = new BatchPlanDecision("per-task", "idle gate host", null, ["A"], 2, []);

        Assert.True(BatchPlanDecisionPolicy.Admissible(
            Facts(Input([Card("A", 2), Card("B", 1)])), decision, out var error), error);
    }

    [Fact]
    public void Close_decision_is_not_refused_for_a_card_that_arrived_after_its_snapshot()
    {
        var close = BatchGateLookAheadPlanner.Plan(Input([Card("A", 2), Card("B", 1)]));

        Assert.Equal("close", close.Decision);
        Assert.True(BatchPlanDecisionPolicy.Admissible(
            Facts(Input([Card("A", 3), Card("B", 2), Card("C", 1)])), close, out var error), error);
    }

    private static BatchPlanFacts Facts(BatchPlanInput snapshot)
        => new("project", "scope", snapshot, null, "off");

    private static BatchPlanInput Input(IReadOnlyList<BatchPlanCard> waiting,
        IReadOnlyList<BatchPlanUpcoming>? upcoming = null) => new(
        Now, waiting, upcoming ?? [], 2, 6, 20, 45, ["urgent"], "balanced", "adjacent");

    private static BatchPlanCard Card(string key, int minutesAgo, params string[] paths)
        => Card(key, minutesAgo, .08, paths);

    private static BatchPlanCard Card(string key, int minutesAgo, double risk, params string[] paths)
        => new(key, Now.AddMinutes(-minutesAgo), risk, paths, []);
}
