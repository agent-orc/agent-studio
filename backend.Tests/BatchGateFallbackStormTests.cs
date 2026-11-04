using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3466: on 3 November 2026 a restart dissolved a formed batch, its
/// members and later every newly reviewed card took a per-task gate, and
/// thirteen gates ran on a host with five slots. Pure decision and store tests
/// of the three rules that stop it: queued pre-check partners hold a lone card
/// for the next batch, per-task gates leave the batch slot free, and the state
/// names why a member is a fallback.
/// </summary>
public sealed class BatchGateFallbackStormTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 11, 0, 0, TimeSpan.Zero);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-fallback-storm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(1, 2, "wait")]
    [InlineData(5, 2, "wait")]
    [InlineData(0, 2, "per-task")]
    [InlineData(1, 3, "per-task")]
    [InlineData(2, 3, "wait")]
    public void Lone_card_past_its_deadline_waits_for_queued_pre_check_partners(
        int queuedPartners, int minimumSize, string expected)
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input([Card("A", minutesAgo: 25)]) with
        {
            QueuedPartners = queuedPartners, MinimumSize = minimumSize,
        });

        Assert.Equal(expected, plan.Decision);
        Assert.Equal(["A"], plan.ProposedMembers);
        if (expected == "wait")
        {
            Assert.Equal(BatchGateLookAheadPlanner.QueuedPartnersReason, plan.Reason);
            Assert.Null(plan.CloseAtUtc);
        }
    }

    [Fact]
    public void Operator_close_now_and_urgent_cards_keep_their_own_gate_despite_queued_partners()
    {
        var input = Input([Card("A", minutesAgo: 25)]) with { QueuedPartners = 3 };

        Assert.Equal("per-task", BatchGateLookAheadPlanner.Plan(input with { CloseNow = true }).Decision);
        Assert.Equal("per-task", BatchGateLookAheadPlanner.Plan(input with
        {
            Waiting = [new BatchPlanCard("A", Now.AddMinutes(-25), .08, [], ["urgent"])],
        }).Decision);
    }

    [Fact]
    public void A_high_risk_card_is_not_held_for_partners_it_could_never_join()
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input([Card("A", minutesAgo: 25, risk: .9)])
            with { QueuedPartners = 3 });

        Assert.Equal("per-task", plan.Decision);
        Assert.Equal("oldest card exceeds batch risk limit", plan.Reason);
    }

    [Fact]
    public void Plan_without_eligible_cards_waits_while_cards_wait_for_their_pre_check()
    {
        // The incident's plan read `idle` with no proposed members while 14 cards were pending.
        var held = BatchGateLookAheadPlanner.Plan(Input([]) with { QueuedPartners = 14 });
        var idle = BatchGateLookAheadPlanner.Plan(Input([]));

        Assert.Equal("wait", held.Decision);
        Assert.Equal(BatchGateLookAheadPlanner.QueuedPartnersReason, held.Reason);
        Assert.Empty(held.ProposedMembers);
        Assert.Equal("idle", idle.Decision);
        Assert.True(BatchPlanDecisionPolicy.Admissible(
            new BatchPlanFacts("project", "scope", Input([]) with { QueuedPartners = 14 }, null, "off"),
            held, out var error), error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void Threshold_planner_holds_a_lone_card_past_its_deadline_for_queued_partners(
        int queuedPartners, bool held)
    {
        var options = new BatchGateFormationOptions(Enabled: true, Scope: "code");
        var formed = BatchGatePolicy.Form([Subject("a", minute: -25)], Scope, Sha, options, Now,
            queuedPartners: queuedPartners);

        Assert.Null(formed.Manifest);
        Assert.Equal(!held, formed.UsePerTaskGate);
        Assert.Equal(["a"], formed.EligibleKeys);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(50, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(3, 5, true)]
    [InlineData(4, 5, false)]
    [InlineData(13, 5, false)]
    public void Per_task_gates_leave_one_slot_of_the_gate_host_to_the_batch(
        int perTaskGates, int slotsTotal, bool free)
        => Assert.Equal(free, BatchGateLookAheadPlanner.PerTaskGateSlotFree(perTaskGates, slotsTotal));

    [Fact]
    public void Store_keeps_the_first_cause_of_a_review_generation_until_it_returns_to_the_queue()
    {
        var store = new BatchGateStore(_root);
        var planned = Cause("review-1", PerTaskFallbackRules.PlannerPerTask, "threshold plan", Now);
        var resumed = Cause("review-1", PerTaskFallbackRules.InterruptedGate, "restart", Now.AddMinutes(5));

        Assert.True(store.RecordFallbackCause(planned));
        Assert.False(store.RecordFallbackCause(resumed));
        Assert.True(store.RecordFallbackCause(Cause("review-2", PerTaskFallbackRules.BatchPaused,
            "batch paused", Now.AddMinutes(1))));
        Assert.Equal([planned.Rule, PerTaskFallbackRules.BatchPaused],
            store.ListFallbackCauses("project").Select(cause => cause.Rule));

        store.ClearFallbackCause("project", "review-1");

        Assert.Equal(["review-2"], store.ListFallbackCauses("project").Select(cause => cause.ReviewAttemptId));
        Assert.True(store.RecordFallbackCause(resumed));
        Assert.Empty(store.ListFallbackCauses("other"));
    }

    private static BatchGateFallbackCause Cause(string attempt, string rule, string @event, DateTimeOffset at)
        => new("project", "AGT-1", attempt, "run-1", rule, @event, null, at);

    private static BatchGateSubject Subject(string key, int minute = 0) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Sha, "run-1", 1, 1, Sha,
        true, true, true, true, false, null, false,
        Now.AddMinutes(minute), 1);

    private static BatchPlanInput Input(IReadOnlyList<BatchPlanCard> waiting) => new(
        Now, waiting, [], 2, 6, 20, 45, ["urgent"], "balanced", "adjacent");

    private static BatchPlanCard Card(string key, int minutesAgo, double risk = .08)
        => new(key, Now.AddMinutes(-minutesAgo), risk, [], []);
}
