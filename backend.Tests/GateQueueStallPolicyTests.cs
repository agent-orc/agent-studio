using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3219: the queue overrun rule, the backlog assessment the planners read,
/// and the once-per-episode stall event on an injected clock.
/// </summary>
public sealed class GateQueueStallPolicyTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(9_327_000);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-gate-queue-stall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("machine-gate-queue", "queue", TaskStates.AutoReview, true, GateQueueOverrunAction.Requeue)]
    [InlineData("machine-gate-queue", "queue", TaskStates.AutoReview, false, GateQueueOverrunAction.Settle)]
    [InlineData("machine-gate-queue", "queue", TaskStates.HumanReview, true, GateQueueOverrunAction.Settle)]
    [InlineData("gate-run", "verification", TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    [InlineData("machine-gate-queue", "preparation", TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    [InlineData(null, null, TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    public void Only_a_queue_phase_overrun_of_the_current_review_is_requeued(
        string? budget, string? phase, string lane, bool current, GateQueueOverrunAction expected)
        => Assert.Equal(expected, GateQueueStallPolicy.Decide(budget, phase, lane, current));

    [Fact]
    public void Sixteen_cards_behind_two_slots_are_a_backlog_and_idle_configured_slots_are_advised()
    {
        var facts = new GateQueueFacts(16, 2, 2, 4, TimeSpan.FromMinutes(90),
            TimeSpan.FromMinutes(35), Budget);

        var assessment = GateQueueStallPolicy.Assess(facts);

        Assert.True(assessment.Backlogged);
        Assert.Equal(TimeSpan.FromMinutes(35 * 8), assessment.EstimatedDrain);
        Assert.Equal(4, assessment.SlotsNeeded);
        Assert.Equal(2, assessment.IdleConfiguredSlots);
        Assert.Contains("2 configured gate slot(s) are idle", assessment.Advisory);

        var drained = GateQueueStallPolicy.Assess(facts with { QueueDepth = 4, ConfiguredSlots = 2 });
        Assert.False(drained.Backlogged);
        Assert.Null(drained.Advisory);
        Assert.Null(GateQueueStallPolicy.Assess(facts with { ConfiguredSlots = 2 }).Advisory);
    }

    [Fact]
    public void Release_requires_an_escalated_card_with_a_current_passed_review_and_a_queue_overrun()
    {
        Assert.True(GateQueueStallPolicy.Releasable(TaskStates.Escalated, "machine-gate-queue", "queue", true, true));
        Assert.False(GateQueueStallPolicy.Releasable(TaskStates.AutoReview, "machine-gate-queue", "queue", true, true));
        Assert.False(GateQueueStallPolicy.Releasable(TaskStates.Escalated, "gate-run", "verification", true, true));
        Assert.False(GateQueueStallPolicy.Releasable(TaskStates.Escalated, "machine-gate-queue", "queue", false, true));
        Assert.False(GateQueueStallPolicy.Releasable(TaskStates.Escalated, "machine-gate-queue", "queue", true, false));
    }

    [Fact]
    public void Monitor_raises_one_event_per_queue_episode()
    {
        var store = new BatchGateStore(_root);
        var monitor = new GateQueueStallMonitor(store, _clock);
        monitor.Waiting("demo", "request-a", 2, 2);
        _clock.Advance(TimeSpan.FromMinutes(40));
        monitor.Waiting("demo", "request-b", 2, 2);
        var facts = monitor.Facts("demo", queuedMembers: 3, Budget, configuredSlots: 2);

        Assert.Equal(5, facts.QueueDepth);
        Assert.Equal(TimeSpan.FromMinutes(40), facts.OldestWait);
        Assert.Equal(2, facts.SlotsBusy);
        Assert.Equal(TimeSpan.FromMinutes(30), facts.TypicalGateDuration);

        var raised = Assert.IsType<GateQueueStallEvent>(monitor.QueueOverrun("demo", "AGT-3213", facts));
        Assert.Equal(_clock.GetUtcNow(), raised.RaisedAtUtc);
        Assert.Equal((long)TimeSpan.FromMinutes(90).TotalMilliseconds, raised.EstimatedDrainMs);
        Assert.Null(monitor.QueueOverrun("demo", "AGT-3214", facts));
        Assert.False(monitor.Observe("demo", queuedMembers: 1));

        monitor.Left("request-a");
        monitor.Left("request-b");
        Assert.True(monitor.Observe("demo", queuedMembers: 0));
        _clock.Advance(TimeSpan.FromHours(3));
        var next = Assert.IsType<GateQueueStallEvent>(monitor.QueueOverrun("demo", "AGT-3215", facts));

        Assert.Equal([raised.EpisodeId, next.EpisodeId],
            store.ListQueueStalls("demo").Select(stall => stall.EpisodeId));
    }

    [Fact]
    public void Look_ahead_planner_closes_a_batch_instead_of_a_per_task_gate_behind_a_backlog()
    {
        var now = _clock.GetUtcNow();
        var input = new BatchPlanInput(now,
            [Card("RISKY", .9, now.AddMinutes(-1)), Card("A", .05, now), Card("B", .05, now)], [],
            2, 6, 20, 45, ["urgent"], "balanced", "adjacent");

        Assert.Equal("per-task", BatchGateLookAheadPlanner.Plan(input).Decision);
        var backlogged = BatchGateLookAheadPlanner.Plan(input with { GateBacklog = true });
        Assert.Equal("close", backlogged.Decision);
        Assert.Equal(BatchGateLookAheadPlanner.BacklogReason, backlogged.Reason);
        Assert.Equal(["A", "B"], backlogged.ProposedMembers);
    }

    [Fact]
    public void Look_ahead_planner_waits_for_a_partner_instead_of_a_lone_per_task_gate_behind_a_backlog()
    {
        var now = _clock.GetUtcNow();
        var input = new BatchPlanInput(now.AddMinutes(30), [Card("A", .05, now)],
            [new BatchPlanUpcoming("B", "review", now.AddMinutes(40), .05, [])],
            2, 6, 20, 45, ["urgent"], "balanced", "adjacent");

        Assert.Equal("per-task", BatchGateLookAheadPlanner.Plan(input).Decision);
        var backlogged = BatchGateLookAheadPlanner.Plan(input with { GateBacklog = true });
        Assert.Equal("wait", backlogged.Decision);
        Assert.Equal(now.AddMinutes(40), backlogged.CloseAtUtc);
    }

    [Fact]
    public void Threshold_policy_closes_at_pressure_size_and_holds_a_lone_card_behind_a_backlog()
    {
        var now = _clock.GetUtcNow();
        var options = new BatchGateFormationOptions(Enabled: true, CloseSize: 4, PressureSize: 2);
        var pair = new[] { Subject("a", now.AddMinutes(-1), 1), Subject("b", now, 2) };

        Assert.Null(BatchGatePolicy.Form(pair, Scope, Sha, options, now).Manifest);
        Assert.NotNull(BatchGatePolicy.Form(pair, Scope, Sha, options, now, gateBacklog: true).Manifest);

        var lone = new[] { Subject("a", now.AddMinutes(-30), 1) };
        Assert.True(BatchGatePolicy.Form(lone, Scope, Sha, options, now).UsePerTaskGate);
        var held = BatchGatePolicy.Form(lone, Scope, Sha, options, now, reviewQueueLength: 1, gateBacklog: true);
        Assert.False(held.UsePerTaskGate);
        Assert.Null(held.Manifest);
    }

    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");

    private static BatchGateSubject Subject(string key, DateTimeOffset reviewed, long sequence) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/" + key, Sha, "run-1", 1, 1, Sha,
        true, true, true, true, false, null, true, reviewed, sequence);

    private static BatchPlanCard Card(string key, double risk, DateTimeOffset now)
        => new(key, now.AddMinutes(-1), risk, [], []);
}
