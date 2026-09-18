namespace AgentRunner;

/// <summary>
/// Durable bookkeeping for a finalization (result transfer, completion
/// recording, hand-back) that could not reach the Task Server. It is the only
/// state the daemon poll loop needs to re-drive the very same attempt later,
/// which is why it is persisted inside the slot instead of being held in the
/// memory of the worker task that failed.
/// </summary>
/// <param name="Attempts">Number of finalization attempts that ended in a transport fault.</param>
/// <param name="PendingSinceUtc">When the first of those attempts failed.</param>
/// <param name="NextAttemptAtUtc">Earliest instant the poll loop may re-drive.</param>
/// <param name="LastReason">Short transport reason of the most recent failure.</param>
/// <param name="Teardown">
/// The secured delivery of this attempt, when teardown already succeeded before
/// the transport fault. A re-drive finds the worktree gone and would otherwise
/// assemble a completion without the delivery it already pushed.
/// </param>
public sealed record PendingFinalization(
    int Attempts,
    DateTime PendingSinceUtc,
    DateTime NextAttemptAtUtc,
    string LastReason,
    WorktreeTeardownResult? Teardown = null);

/// <summary>What the daemon poll loop should do with one persisted slot.</summary>
public enum FinalizationRetryAction
{
    /// <summary>Not a deferred finalization: the poll loop leaves the slot alone.</summary>
    Ignore,

    /// <summary>The backoff window has not elapsed yet.</summary>
    Wait,

    /// <summary>The Task Server still does not answer; keep waiting without burning an attempt.</summary>
    ServerUnreachable,

    /// <summary>Re-drive the finalization now with the same idempotent steps startup reconciliation uses.</summary>
    Redrive,
}

/// <summary>
/// Pure decision for AGT-2869: a finalization that failed because the Task
/// Server was restarting is retried from the persisted slot, not abandoned
/// until the next daemon restart.
///
/// <para>
/// The ladder is deliberately short (15 s, 30 s, then every 60 s): a deploy
/// restart is over in minutes, and the worker's result is already on disk, so
/// the cost of one more poll is a single HTTP call. The run timeout only marks
/// the point from which the wait stops being ordinary and is journaled as such -
/// it never stops the retries, because the result must reach the server
/// eventually.
/// </para>
/// </summary>
public static class FinalizationRetryPolicy
{
    /// <summary>The slot phase a deferred finalization stays in.</summary>
    public const string Phase = "finalizing";

    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SecondDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SteadyDelay = TimeSpan.FromSeconds(60);

    /// <summary>Backoff after <paramref name="attempts"/> failed finalization attempts.</summary>
    public static TimeSpan DelayAfter(int attempts) => attempts switch
    {
        <= 1 => FirstDelay,
        2 => SecondDelay,
        _ => SteadyDelay,
    };

    /// <summary>Records one more failed finalization attempt and schedules the next one.</summary>
    public static PendingFinalization Schedule(
        PendingFinalization? previous,
        string reason,
        WorktreeTeardownResult? teardown,
        DateTime nowUtc)
    {
        var attempts = (previous?.Attempts ?? 0) + 1;
        return new PendingFinalization(
            attempts,
            previous?.PendingSinceUtc ?? nowUtc,
            nowUtc.Add(DelayAfter(attempts)),
            string.IsNullOrWhiteSpace(reason) ? "transport fault" : reason.Trim(),
            teardown ?? previous?.Teardown);
    }

    /// <summary>
    /// Pushes the next attempt out while a re-drive is in flight. Without it a
    /// re-drive that dies on something other than a transport fault would be
    /// due again on the very next poll tick, and the loop would spin.
    /// </summary>
    public static PendingFinalization HoldFor(PendingFinalization pending, DateTime nowUtc)
        => pending with { NextAttemptAtUtc = nowUtc.Add(DelayAfter(pending.Attempts + 1)) };

    /// <summary>
    /// Whether this slot is a deferred finalization the poll loop must re-drive
    /// now. Every input is an observation the caller has already made, so the
    /// decision itself stays free of process, filesystem, and network access.
    /// </summary>
    public static FinalizationRetryAction Decide(
        string? phase,
        PendingFinalization? finalization,
        bool durableResultReady,
        bool slotIsActive,
        bool serverAnswered,
        DateTime nowUtc)
    {
        if (finalization is null) return FinalizationRetryAction.Ignore;
        if (!string.Equals(phase, Phase, StringComparison.Ordinal)) return FinalizationRetryAction.Ignore;
        if (slotIsActive) return FinalizationRetryAction.Ignore;
        // Without the worker's durable result there is nothing to deliver, and
        // startup reconciliation - not this loop - owns releasing that slot.
        if (!durableResultReady) return FinalizationRetryAction.Ignore;
        if (nowUtc < finalization.NextAttemptAtUtc) return FinalizationRetryAction.Wait;
        return serverAnswered
            ? FinalizationRetryAction.Redrive
            : FinalizationRetryAction.ServerUnreachable;
    }

    /// <summary>
    /// True once the wait has outlived the run timeout. Retries continue, but
    /// the journal says so, because from here on an operator is looking at a
    /// delivery that is late rather than at a deploy that is still running.
    /// </summary>
    public static bool BeyondRunTimeout(
        PendingFinalization finalization,
        TimeSpan runTimeout,
        DateTime nowUtc)
        => runTimeout > TimeSpan.Zero
           && nowUtc - finalization.PendingSinceUtc > runTimeout;
}
