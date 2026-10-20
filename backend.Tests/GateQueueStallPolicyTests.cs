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
    [InlineData("gate-run", "verification", TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    [InlineData("machine-gate-queue", "preparation", TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    [InlineData(null, null, TaskStates.AutoReview, true, GateQueueOverrunAction.Settle)]
    // A result of a superseded generation or a card outside Auto Review never
    // settles, whatever its verdict: the 19./20.10.2026 incident parked such
    // cards from Ready, In Progress and Human Review.
    [InlineData("machine-gate-queue", "queue", TaskStates.AutoReview, false, GateQueueOverrunAction.Supersede)]
    [InlineData("machine-gate-queue", "queue", TaskStates.Ready, true, GateQueueOverrunAction.Supersede)]
    [InlineData("machine-gate-queue", "queue", TaskStates.Progress, true, GateQueueOverrunAction.Supersede)]
    [InlineData("machine-gate-queue", "queue", TaskStates.HumanReview, true, GateQueueOverrunAction.Supersede)]
    [InlineData("gate-run", "verification", TaskStates.AutoReview, false, GateQueueOverrunAction.Supersede)]
    [InlineData("gate-run", "verification", TaskStates.Ready, true, GateQueueOverrunAction.Supersede)]
    [InlineData(null, null, TaskStates.AutoReview, false, GateQueueOverrunAction.Supersede)]
    [InlineData(null, null, null, true, GateQueueOverrunAction.Supersede)]
    public void Only_the_current_review_in_auto_review_settles_and_only_its_queue_overrun_is_requeued(
        string? budget, string? phase, string? lane, bool current, GateQueueOverrunAction expected)
        => Assert.Equal(expected, GateQueueStallPolicy.Decide(budget, phase, lane, current));

    [Theory]
    [InlineData(0, GateQueueOverrunAction.Requeue)]
    [InlineData(3, GateQueueOverrunAction.Requeue)]
    [InlineData(4, GateQueueOverrunAction.EscalateCapacity)]
    [InlineData(5, GateQueueOverrunAction.EscalateCapacity)]
    public void A_review_generation_escalates_as_gate_capacity_once_its_queue_wait_reaches_the_cap(
        int budgetsWaited, GateQueueOverrunAction expected)
    {
        var cap = GateQueueStallPolicy.QueueWaitCap(Budget);

        Assert.Equal(Budget * GateQueueStallPolicy.QueueWaitCapBudgets, cap);
        Assert.Equal(expected, GateQueueStallPolicy.Decide("machine-gate-queue", "queue",
            TaskStates.AutoReview, true, Budget * budgetsWaited, cap));
        // The cap never turns an execution overrun or a stale review into a capacity park.
        Assert.Equal(GateQueueOverrunAction.Settle, GateQueueStallPolicy.Decide("gate-run", "verification",
            TaskStates.AutoReview, true, Budget * budgetsWaited, cap));
        Assert.Equal(GateQueueOverrunAction.Supersede, GateQueueStallPolicy.Decide("machine-gate-queue", "queue",
            TaskStates.AutoReview, false, Budget * budgetsWaited, cap));
    }

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

    private static readonly GateQueueReleaseFacts Parked = new(TaskStates.Escalated, "machine-gate-queue", "queue",
        CurrentGeneration: true, ReviewPassed: true, TaskStates.AutoReview, "escalated", "auto-review-escalation",
        PendingIntent: false);

    [Fact]
    public void Release_requires_an_escalated_card_with_a_current_passed_review_and_a_queue_overrun()
    {
        Assert.True(GateQueueStallPolicy.Releasable(Parked));
        Assert.True(GateQueueStallPolicy.Releasable(Parked with { EscalationCategory = "gate-capacity" }));
        Assert.Equal(GateQueueReleaseRefusal.NotEscalated,
            GateQueueStallPolicy.ReleaseRefusal(Parked with { Lane = TaskStates.AutoReview }));
        Assert.Equal(GateQueueReleaseRefusal.NotQueueOverrun,
            GateQueueStallPolicy.ReleaseRefusal(Parked with { BudgetName = "gate-run", BudgetPhase = "verification" }));
        Assert.Equal(GateQueueReleaseRefusal.ReviewNotCurrent,
            GateQueueStallPolicy.ReleaseRefusal(Parked with { CurrentGeneration = false }));
        Assert.Equal(GateQueueReleaseRefusal.ReviewNotCurrent,
            GateQueueStallPolicy.ReleaseRefusal(Parked with { ReviewPassed = false }));
    }

    [Theory]
    [InlineData(TaskStates.Ready, "escalated", "auto-review-escalation")]
    [InlineData(TaskStates.Progress, "escalated", "auto-review-escalation")]
    [InlineData(TaskStates.HumanReview, "escalated", "auto-review-escalation")]
    [InlineData(null, null, null)]
    [InlineData(TaskStates.AutoReview, "operator-move", null)]
    [InlineData(TaskStates.AutoReview, "escalated", "agent-needs-input")]
    public void Release_refuses_a_card_that_was_not_escalated_out_of_auto_review_by_the_fallback(
        string? from, string? cause, string? category)
        => Assert.Equal(GateQueueReleaseRefusal.NotFallbackEscalation, GateQueueStallPolicy.ReleaseRefusal(
            Parked with { EscalatedFromLane = from, EscalationCause = cause, EscalationCategory = category }));

    [Fact]
    public void Release_refuses_a_card_with_a_pending_follow_up()
        => Assert.Equal(GateQueueReleaseRefusal.PendingIntent,
            GateQueueStallPolicy.ReleaseRefusal(Parked with { PendingIntent = true }));

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
    public void Open_episode_survives_a_restart_and_reminds_every_interval_until_the_queue_drains()
    {
        var store = new BatchGateStore(_root);
        var facts = new GateQueueFacts(6, 2, 2, 2, Budget, TimeSpan.FromMinutes(35), Budget);
        var opened = Assert.IsType<GateQueueStallEvent>(
            new GateQueueStallMonitor(store, _clock).QueueOverrun("demo", "AGT-3213", facts));

        // A new process reads the open episode from the store.
        var restarted = new GateQueueStallMonitor(store, _clock);
        Assert.Equal(opened.EpisodeId, restarted.OpenEpisode("demo")?.EpisodeId);
        _clock.Advance(GateQueueStallPolicy.StallReminderInterval - TimeSpan.FromMinutes(1));
        Assert.Null(restarted.QueueOverrun("demo", "AGT-3214", facts));
        Assert.Null(restarted.Remind("demo", facts));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var reminder = Assert.IsType<GateQueueStallEvent>(restarted.Remind("demo", facts));
        Assert.Equal(opened.EpisodeId, reminder.EpisodeId);
        Assert.Equal(2, reminder.Raise);
        Assert.Equal(opened.RaisedAtUtc, reminder.EpisodeOpenedAtUtc);
        Assert.Null(restarted.Remind("demo", facts));
        _clock.Advance(GateQueueStallPolicy.StallReminderInterval);
        Assert.Equal(3, restarted.QueueOverrun("demo", "AGT-3215", facts)?.Raise);

        Assert.True(restarted.Observe("demo", queuedMembers: 0));
        Assert.Null(new GateQueueStallMonitor(store, _clock).OpenEpisode("demo"));
        Assert.Null(restarted.Remind("demo", facts));
        Assert.Equal([1, 2, 3], store.ListQueueStalls("demo").Select(stall => stall.Raise));
    }

    [Fact]
    public void Restored_waiters_keep_an_open_episode_open_after_a_restart_until_they_no_longer_wait()
    {
        var store = new BatchGateStore(_root);
        var facts = new GateQueueFacts(1, 2, 2, 2, Budget, TimeSpan.FromMinutes(35), Budget);
        Assert.NotNull(new GateQueueStallMonitor(store, _clock).QueueOverrun("demo", "AGT-3213", facts));
        var overranAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5));

        // The new process has no live waiter; the ledger restores the request.
        var restarted = new GateQueueStallMonitor(store, _clock);
        restarted.Restore("demo", "review-a", overranAt);
        Assert.False(restarted.Observe("demo", queuedMembers: 0));
        Assert.Equal(TimeSpan.FromMinutes(5), restarted.Facts("demo", 0, Budget, 2).OldestWait);

        // The resumed gate reports its wait and keeps the restored start.
        _clock.Advance(TimeSpan.FromMinutes(1));
        restarted.Waiting("demo", "review-a", 2, 2);
        restarted.PruneRestored(_ => false);
        Assert.Equal(TimeSpan.FromMinutes(6), restarted.Facts("demo", 0, Budget, 2).OldestWait);
        restarted.Left("review-a");

        // A restored request whose gate no longer waits is dropped, and the queue drains.
        restarted.Restore("demo", "review-b", overranAt);
        restarted.PruneRestored(key => key != "review-b");
        Assert.True(restarted.Observe("demo", queuedMembers: 0));
    }

    [Fact]
    public void Stall_history_keeps_the_newest_events_per_project_and_reads_only_those()
    {
        var store = new BatchGateStore(_root);
        var legacy = Path.Combine(_root, "queue-stalls", "legacy-episode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{}");
        for (var raise = 1; raise <= BatchGateStore.RetainedQueueStalls + 5; raise++)
        {
            store.RecordQueueStall(Stall("demo", raise));
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        store.RecordQueueStall(Stall("other", 1));

        var kept = store.ListQueueStalls("demo");
        Assert.Equal(Enumerable.Range(6, BatchGateStore.RetainedQueueStalls), kept.Select(stall => stall.Raise));
        Assert.Equal([54, 55], store.ListQueueStalls("demo", take: 2).Select(stall => stall.Raise));
        Assert.Single(store.ListQueueStalls("other"));
        Assert.False(File.Exists(legacy));
    }

    private GateQueueStallEvent Stall(string project, int raise)
        => new("episode-" + project, project, _clock.GetUtcNow(), 3, 1000, 2, 2, 2000, 9_327_000, 0, null,
            "AGT-3213", raise);

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
    public void Look_ahead_planner_ages_a_skipped_high_risk_card_out_to_its_own_gate()
    {
        var now = _clock.GetUtcNow();
        var risky = new BatchPlanCard("RISKY", now.AddMinutes(-39), .9, [], []);
        var input = new BatchPlanInput(now, [risky, Card("A", .05, now), Card("B", .05, now)], [],
            2, 6, 20, 45, ["urgent"], "balanced", "adjacent", GateBacklog: true);

        Assert.Equal("close", BatchGateLookAheadPlanner.Plan(input).Decision);
        var aged = BatchGateLookAheadPlanner.Plan(input with { NowUtc = now.AddMinutes(1) });
        Assert.Equal("per-task", aged.Decision);
        Assert.Equal(["RISKY"], aged.ProposedMembers);
    }

    [Fact]
    public void Look_ahead_planner_stops_waiting_for_a_partner_once_the_lone_card_aged_out()
    {
        var now = _clock.GetUtcNow();
        var input = new BatchPlanInput(now.AddMinutes(38), [Card("A", .05, now)],
            [new BatchPlanUpcoming("B", "review", now.AddMinutes(50), .05, [])],
            2, 6, 20, 45, ["urgent"], "balanced", "adjacent", GateBacklog: true);

        Assert.Equal("wait", BatchGateLookAheadPlanner.Plan(input).Decision);
        Assert.Equal("per-task", BatchGateLookAheadPlanner.Plan(input with { NowUtc = now.AddMinutes(39) }).Decision);
    }

    [Fact]
    public void Look_ahead_planner_does_not_wait_for_a_partner_whose_eta_has_passed()
    {
        var now = _clock.GetUtcNow();
        var overdue = new BatchPlanInput(now.AddMinutes(30), [Card("A", .05, now)],
            [new BatchPlanUpcoming("B", "review", now.AddMinutes(29), .05, [])],
            2, 6, 20, 45, ["urgent"], "balanced", "adjacent", GateBacklog: true);

        var decision = BatchGateLookAheadPlanner.Plan(overdue);

        Assert.Equal("per-task", decision.Decision);
        // Before its deadline, an overdue ETA no longer counts as a due partner either.
        var early = BatchGateLookAheadPlanner.Plan(overdue with { NowUtc = now.AddMinutes(10),
            Upcoming = [new BatchPlanUpcoming("B", "review", now.AddMinutes(5), .05, [])], IdleGateHost = true });
        Assert.Equal("per-task", early.Decision);
    }

    [Fact]
    public void Threshold_policy_closes_at_pressure_size_and_holds_a_lone_card_behind_a_backlog()
    {
        var now = _clock.GetUtcNow();
        var options = new BatchGateFormationOptions(Enabled: true, CloseSize: 4, PressureSize: 2);
        var pair = new[] { Subject("a", now.AddMinutes(-1), 1), Subject("b", now, 2) };

        Assert.Null(BatchGatePolicy.Form(pair, Scope, Sha, options, now).Manifest);
        Assert.NotNull(BatchGatePolicy.Form(pair, Scope, Sha, options, now, gateBacklog: true).Manifest);

        // Past the 15 minute default deadline, inside the age-out bound of two deadlines.
        var lone = new[] { Subject("a", now.AddMinutes(-20), 1) };
        Assert.True(BatchGatePolicy.Form(lone, Scope, Sha, options, now).UsePerTaskGate);
        var held = BatchGatePolicy.Form(lone, Scope, Sha, options, now, reviewQueueLength: 1, gateBacklog: true);
        Assert.False(held.UsePerTaskGate);
        Assert.Null(held.Manifest);

        // After two deadlines the lone card takes its own gate.
        var deadline = TimeSpan.FromMinutes(options.DeadlineMinutes);
        var aged = new[] { Subject("a", now - deadline * BatchGateLookAheadPlanner.BacklogAgeOutDeadlines, 1) };
        Assert.True(BatchGatePolicy.Form(aged, Scope, Sha, options, now, reviewQueueLength: 1, gateBacklog: true)
            .UsePerTaskGate);
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
