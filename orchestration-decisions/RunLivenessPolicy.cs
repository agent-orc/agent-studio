namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Pure decision core for Run-Liveness Slice A (see
/// <c>docs/concepts/run-liveness-and-slot-semantics.md</c>). Answers one
/// question for a single <c>3-progress</c> card: given whether it still has a
/// live run-heartbeat, whether its core agent run already finished, and how
/// long it has been silent, what must the runner do so that
/// <b>no zombie survives 60s</b>?
///
/// <para>
/// Invariant (concept Rule 4): every <c>3-progress</c> card needs a live
/// run-heartbeat. The heartbeat source is PHASE-AWARE - during execution the
/// live CLI process is the heartbeat; during post-processing the post-step
/// executor is. A card whose owning run process is gone is a zombie and must
/// leave <c>3-progress</c> within 60s.
/// </para>
///
/// <para>
/// The recovery is also phase-aware, which is why this is not a single "demote
/// everything" rule:
/// <list type="bullet">
///   <item><b>Execution interrupted</b> (no heartbeat, core run never finished -
///   belegt AGT-2006): demote to <c>2-ready</c> with reason
///   <see cref="RunLivenessReasons.ProcessLost"/> so a fresh run retries the
///   same task. The caller clears the session-resume pointer so the retry does
///   not walk into the "No conversation found" / "no rollout found" launch-fail
///   chain.</item>
///   <item><b>Post-processing interrupted</b> (no heartbeat but the agent run
///   already finished - belegt AGT-1932, the run finished AND merged and only
///   post-processing died with the backend): re-trigger post-processing with
///   reason <see cref="RunLivenessReasons.PostProcessingLost"/> instead of
///   re-running the finished agent, which would waste the completed run.</item>
/// </list>
/// </para>
///
/// <para>
/// Kept pure (no I/O, no clock) so the whole invariant is locked by
/// fixture-based unit tests, the same discipline
/// <c>StrandedRunBackstop</c> and <c>RunOutcomePolicy</c> follow. The
/// caller (<c>RunLivenessMonitor</c>) gathers the facts from disk and executes
/// the verdict.
/// </para>
/// </summary>
public static class RunLivenessPolicy
{
    /// <summary>
    /// Decide what to do with one <c>3-progress</c> card.
    /// </summary>
    public static RunLivenessDecision Decide(RunLivenessFacts facts)
    {
        // A live run-heartbeat owns the card: the runner's own active-run latch,
        // a still-alive CLI process, or a live owning-runner lease (a foreign
        // backend sharing the workspace). Healthy - never touch it.
        if (facts.HasLiveRunHeartbeat)
            return new RunLivenessDecision(
                RunLivenessAction.Healthy,
                RunLivenessReasons.HeartbeatPresent,
                "a live run-heartbeat owns this 3-progress card (active-run latch, live CLI process, or a live owning-runner lease)");

        // A card may intentionally remain in 3-progress without a coding CLI
        // only when that fact is explicit on the card. These phases do not own
        // an execution slot; their dedicated continuation/timeout path owns the
        // wake-up. This is the visible-wait half of the 60-second invariant.
        if (facts.HasVisibleWaitingState)
            return new RunLivenessDecision(
                RunLivenessAction.VisibleWait,
                RunLivenessReasons.VisibleWait,
                "no live coding process, but the card carries an explicit loop-waiting or steer-pending phase");

        // No heartbeat, but the card only just went silent. During uptime a card
        // can sit heartbeat-less for a beat between the lane move and the run
        // claim/lock; do not demote inside that window. At boot the grace is 0,
        // so a genuinely crashed run (silent for its whole crash) is adopted
        // immediately. The caller re-checks on the next tick, so a real zombie
        // still leaves 3-progress well inside the 60s budget.
        if (facts.SecondsSinceActivity < facts.GraceSeconds)
            return new RunLivenessDecision(
                RunLivenessAction.WithinGrace,
                RunLivenessReasons.WithinGrace,
                $"no run-heartbeat but only {facts.SecondsSinceActivity:F0}s since last activity (< {facts.GraceSeconds:F0}s grace); re-checked next tick before any demotion");

        // No heartbeat past the grace: this run is lost. Phase-aware recovery.
        if (facts.CoreRunFinished)
            return new RunLivenessDecision(
                RunLivenessAction.RetriggerPostProcessing,
                RunLivenessReasons.PostProcessingLost,
                "the agent run already finished (agent_run_finished); only post-processing lost its heartbeat - re-triggering post-processing instead of re-running the completed agent");

        return new RunLivenessDecision(
            RunLivenessAction.DemoteToReady,
            RunLivenessReasons.ProcessLost,
            "no run-heartbeat and the agent run never finished - the run process is lost; demoting to 2-ready so a fresh run retries the same task");
    }
}

/// <summary>
/// The three run-bound facts <see cref="RunLivenessPolicy.Decide"/> needs. All
/// are gathered by the caller from durable on-disk state so the policy stays a
/// pure function.
/// </summary>
/// <param name="HasLiveRunHeartbeat">
/// True when a live run-heartbeat owns the card: the runner's active-run latch
/// holds it, a tracked CLI process for it is still running, or a live
/// (unexpired / live-pid) owning-runner lease is stamped on it.
/// </param>
/// <param name="CoreRunFinished">
/// True when a durable signal says the core agent run already finished
/// (an <c>agent_run_finished</c> timeline row, a surviving completion marker,
/// or <c>phase == post-processing-running</c>). Distinguishes AGT-1932
/// (finished, only post-processing died) from AGT-2006 (execution interrupted).
/// </param>
/// <param name="SecondsSinceActivity">
/// Seconds since the last run-produced activity (max over run-log mtimes and
/// the stable <c>enteredLaneAt</c> stamp). Floors the demotion so a just-moved
/// card is not judged before its run has had a chance to claim its heartbeat.
/// </param>
/// <param name="GraceSeconds">
/// The silence a card is allowed before a missing heartbeat counts as
/// process-lost. Zero at boot (adopt immediately); a small window during uptime.
/// </param>
public sealed record RunLivenessFacts(
    bool HasLiveRunHeartbeat,
    bool CoreRunFinished,
    double SecondsSinceActivity,
    double GraceSeconds,
    bool HasVisibleWaitingState = false);

/// <summary>The pure verdict: what to do plus a taxonomy code and a human reason.</summary>
public sealed record RunLivenessDecision(
    RunLivenessAction Action,
    string ReasonCode,
    string Detail);

/// <summary>The four possible run-liveness verdicts for a <c>3-progress</c> card.</summary>
public enum RunLivenessAction
{
    /// <summary>A live run-heartbeat owns the card; leave it alone.</summary>
    Healthy,
    /// <summary>No CLI slot is held, but an explicit waiting phase makes the state honest.</summary>
    VisibleWait,
    /// <summary>No heartbeat yet but too fresh to judge; re-check next tick.</summary>
    WithinGrace,
    /// <summary>Execution interrupted (process-lost): demote to <c>2-ready</c> and clear the resume pointer.</summary>
    DemoteToReady,
    /// <summary>Core run finished, only post-processing died: re-trigger post-processing.</summary>
    RetriggerPostProcessing,
}

/// <summary>Stable taxonomy codes carried on a <see cref="RunLivenessDecision"/> and in the audit log.</summary>
public static class RunLivenessReasons
{
    /// <summary>A live run-heartbeat is present; the card is healthy.</summary>
    public const string HeartbeatPresent = "heartbeat-present";
    public const string VisibleWait = "visible-wait";
    /// <summary>No heartbeat but the card is inside the liveness grace window.</summary>
    public const string WithinGrace = "within-grace";
    /// <summary>The run process is lost and the agent run never finished (demote to 2-ready).</summary>
    public const string ProcessLost = "process-lost";
    /// <summary>The agent run finished; only post-processing lost its heartbeat (re-trigger post-processing).</summary>
    public const string PostProcessingLost = "post-processing-lost";
}
