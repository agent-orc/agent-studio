namespace AgentStudio.OrchestrationDecisions;

/// <summary>What a gate request whose budget ran out does next (AGT-3219).</summary>
public enum GateQueueOverrunAction
{
    /// <summary>The verdict is the card's: the existing settlement path escalates or passes it.</summary>
    Settle,

    /// <summary>
    /// The request never reached a machine slot. The card stays in Auto Review
    /// and its gate request returns to the batch queue.
    /// </summary>
    Requeue,

    /// <summary>
    /// The review generation spent its total queue wait cap without reaching a
    /// slot. The card escalates under <see cref="GateQueueStallPolicy.GateCapacityCategory"/>:
    /// the gate host lacks capacity, the code was not judged.
    /// </summary>
    EscalateCapacity,
}

/// <summary>
/// Facts of the machine gate queue for one project scope. The host counts
/// <paramref name="QueueDepth"/> as gate requests that wait for a slot or for
/// their batch, and <paramref name="TypicalGateDuration"/> from recent gate
/// executions. <paramref name="ConfiguredSlots"/> is the gate capacity the host
/// configuration offers; it can exceed the slots the queue currently uses.
/// </summary>
public sealed record GateQueueFacts(
    int QueueDepth, int SlotsBusy, int SlotsTotal, int ConfiguredSlots,
    TimeSpan OldestWait, TimeSpan TypicalGateDuration, TimeSpan QueueBudget);

/// <summary>
/// Pure reading of <see cref="GateQueueFacts"/>. <see cref="Backlogged"/> is set
/// when the slots in use cannot drain the queue inside the queue budget.
/// <see cref="IdleConfiguredSlots"/> and <see cref="Advisory"/> name configured
/// capacity the queue does not use; the host only reports it.
/// </summary>
public sealed record GateQueueAssessment(
    bool Backlogged, TimeSpan EstimatedDrain, int SlotsNeeded,
    int IdleConfiguredSlots, string? Advisory);

/// <summary>
/// AGT-3219: a gate budget spent in the queue phase is an infrastructure wait,
/// not a card failure. The rule decides whether such a result settles the card,
/// whether the queue is backlogged so the planner prefers batches, and whether
/// an escalated card can be released back to the queue.
/// </summary>
public static class GateQueueStallPolicy
{
    /// <summary>The budget name the machine gate stamps on a queue wait overrun.</summary>
    public const string QueueBudgetName = "machine-gate-queue";

    /// <summary>The budget phase of a wait for a machine slot.</summary>
    public const string QueuePhase = "queue";

    /// <summary>Escalation category of a card that spent its queue wait cap.</summary>
    public const string GateCapacityCategory = "gate-capacity";

    /// <summary>
    /// Queue budgets one review generation may spend waiting for a slot across
    /// all its requeues before it escalates as a gate capacity problem.
    /// </summary>
    public const int QueueWaitCapBudgets = 4;

    /// <summary>An open stall episode raises its event again after this interval.</summary>
    public static readonly TimeSpan StallReminderInterval = TimeSpan.FromHours(4);

    /// <summary>Total queue wait cap of one review generation for a queue budget.</summary>
    public static TimeSpan QueueWaitCap(TimeSpan queueBudget)
        => queueBudget > TimeSpan.Zero ? queueBudget * QueueWaitCapBudgets : TimeSpan.MaxValue;

    /// <summary>
    /// True for a budget the request spent before it held a slot. Execution
    /// phases (preparation, verification) and an unknown phase are not.
    /// </summary>
    public static bool IsQueueOverrun(string? budgetName, string? budgetPhase)
        => string.Equals(budgetPhase, QueuePhase, StringComparison.Ordinal)
           && string.Equals(budgetName, QueueBudgetName, StringComparison.Ordinal);

    /// <summary>
    /// A queue overrun of the current review generation of a card in Auto
    /// Review is requeued until <paramref name="totalQueueWait"/>, the queue
    /// wait of this generation including this overrun, reaches
    /// <paramref name="queueWaitCap"/>; then it escalates as a capacity problem.
    /// Every other result keeps its settlement path, so an execution-phase
    /// overrun still escalates.
    /// </summary>
    public static GateQueueOverrunAction Decide(string? budgetName, string? budgetPhase,
        string? lane, bool currentGeneration, TimeSpan totalQueueWait = default,
        TimeSpan? queueWaitCap = null)
    {
        if (!IsQueueOverrun(budgetName, budgetPhase) || !currentGeneration
            || !string.Equals(lane, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
            return GateQueueOverrunAction.Settle;
        return queueWaitCap is { } cap && totalQueueWait >= cap
            ? GateQueueOverrunAction.EscalateCapacity
            : GateQueueOverrunAction.Requeue;
    }

    /// <summary>
    /// Estimated drain is the number of slot rounds the queue needs times one
    /// typical gate run. The queue is backlogged when that exceeds the queue
    /// budget, so a request at its tail would overrun while it waits.
    /// </summary>
    public static GateQueueAssessment Assess(GateQueueFacts facts)
    {
        var slots = Math.Max(1, facts.SlotsTotal);
        var depth = Math.Max(0, facts.QueueDepth);
        var typical = facts.TypicalGateDuration > TimeSpan.Zero ? facts.TypicalGateDuration : TimeSpan.Zero;
        var rounds = (depth + slots - 1) / slots;
        var drain = TimeSpan.FromTicks(typical.Ticks * rounds);
        var backlogged = depth > 0 && facts.QueueBudget > TimeSpan.Zero && drain > facts.QueueBudget;
        var needed = backlogged
            ? (int)Math.Ceiling(depth * typical.TotalMilliseconds
                / Math.Max(1d, facts.QueueBudget.TotalMilliseconds))
            : 0;
        var idle = Math.Max(0, facts.ConfiguredSlots - slots);
        var advisory = backlogged && idle > 0
            ? $"{idle} configured gate slot(s) are idle while {depth} gate request(s) wait; "
              + $"draining needs about {Math.Max(needed, slots + 1)} slot(s). "
              + "Raise the gate slots in use to the configured capacity to drain the queue."
            : null;
        return new GateQueueAssessment(backlogged, drain, needed, idle, advisory);
    }

    /// <summary>
    /// One stall event per queue episode: an episode opens at the first queue
    /// overrun and stays open until the queue has drained.
    /// </summary>
    public static bool RaisesStallEvent(bool episodeOpen) => !episodeOpen;

    /// <summary>
    /// An episode that stays open raises its event again once
    /// <see cref="StallReminderInterval"/> passed since its last event, so a
    /// sustained backlog does not go quiet after the first event.
    /// </summary>
    public static bool RemindsStall(DateTimeOffset lastRaisedAtUtc, DateTimeOffset nowUtc)
        => nowUtc - lastRaisedAtUtc >= StallReminderInterval;

    /// <summary>The episode closes once no request waits for a slot and none is queued.</summary>
    public static bool EpisodeDrained(int queueDepth) => queueDepth <= 0;

    /// <summary>
    /// An escalated card returns to the queue only when its escalation was a
    /// queue overrun of the review that is still current and passed.
    /// </summary>
    public static bool Releasable(string? lane, string? budgetName, string? budgetPhase,
        bool currentGeneration, bool reviewPassed)
        => string.Equals(lane, TaskStates.Escalated, StringComparison.OrdinalIgnoreCase)
           && IsQueueOverrun(budgetName, budgetPhase) && currentGeneration && reviewPassed;
}
