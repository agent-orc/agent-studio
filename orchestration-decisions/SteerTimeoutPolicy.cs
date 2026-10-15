namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Pure decision core for Run-Liveness Slice B - the steer-timeout invariant
/// (see <c>docs/concepts/run-liveness-and-slot-semantics.md</c>, Rule 2).
/// Answers one question for a single <c>3-progress</c> card that is waiting on
/// an unanswered steer / <c>[[TASK_NEEDS_INPUT]]</c> question: given how long it
/// has waited, its configured timeout, and whether the task context yields an
/// unambiguous answer, what must the runner
/// do so that <b>no steered card waits indefinitely</b>?
///
/// <para>
/// Belegt (2026-07-10): three cards (2062/2067/2068) hung in parallel ~5 hours
/// on steer questions whose answer was already knowable from the branch state
/// ("is this already implemented?" - their work was long since merged). The
/// runs waited unbounded because the NeedsInput wait had no timeout; the loss
/// was invisible because no lane moved. Slice A demotes a card whose PROCESS is
/// gone; a steered card is different - it is waiting on purpose, so it is
/// excluded from the Slice A heartbeat check and needs its own bounded wait.
/// </para>
///
/// <para>
/// The recovery is deliberately two-way, mirroring the concept's Rule 2:
/// <list type="bullet">
///   <item><b>Unambiguous</b> (the answer is derivable from prompt.md / the
///   task context, e.g. the branch-state check for the 2067 case): auto-answer
///   the question and let the run continue, reason
///   <see cref="SteerTimeoutReasons.AutoAnswered"/>.</item>
///   <item><b>Ambiguous</b> (no confident answer): route the card to a normal
///   <c>blocked</c> escalation with a clear reason, reason
///   <see cref="SteerTimeoutReasons.SteerUnanswered"/> - never an endless
///   wait.</item>
/// </list>
/// </para>
///
/// <para>
/// Kept pure (no I/O, no clock) so the whole invariant is locked by
/// fixture-based unit tests - the same discipline <see cref="RunLivenessPolicy"/>
/// follows. The caller (<c>SteerTimeoutMonitor</c>) gathers the facts from the
/// durable steer-pending marker on disk, computes the auto-answer via a
/// resolver, and executes the verdict.
/// </para>
/// </summary>
public static class SteerTimeoutPolicy
{
    /// <summary>
    /// Decide what to do with one waiting <c>3-progress</c> steer card.
    /// </summary>
    public static SteerTimeoutDecision Decide(SteerTimeoutFacts facts)
    {
        // Still inside the bounded wait. The card shows its "waiting for answer
        // since mm:ss" pill; the caller re-checks on the next sweep. The
        // boundary is inclusive (>= timeout acts) so a card cannot sit forever
        // at exactly the timeout.
        if (facts.SecondsWaiting < facts.TimeoutSeconds)
            return new SteerTimeoutDecision(
                SteerTimeoutAction.KeepWaiting,
                SteerTimeoutReasons.WithinTimeout,
                null,
                $"waited {facts.SecondsWaiting:F0}s of the {facts.TimeoutSeconds:F0}s steer timeout; still waiting for an answer");

        // Timed out. Prefer an unambiguous auto-answer from the task context;
        // fall back to a clear blocked escalation. Either way the wait ends now.
        if (facts.HasConfidentAutoAnswer && !string.IsNullOrWhiteSpace(facts.AutoAnswerText))
            return new SteerTimeoutDecision(
                SteerTimeoutAction.AutoAnswer,
                SteerTimeoutReasons.AutoAnswered,
                facts.AutoAnswerText,
                $"steer unanswered for {facts.SecondsWaiting:F0}s (> {facts.TimeoutSeconds:F0}s); the answer is unambiguous from the task context - auto-answering so the run continues instead of waiting");

        return new SteerTimeoutDecision(
            SteerTimeoutAction.RouteBlocked,
            SteerTimeoutReasons.SteerUnanswered,
            null,
            string.IsNullOrWhiteSpace(facts.AmbiguityReason)
                ? $"steer unanswered for {facts.SecondsWaiting:F0}s (> {facts.TimeoutSeconds:F0}s) and the answer is not derivable from the task context; routing to blocked + human escalation rather than waiting indefinitely"
                : $"steer unanswered for {facts.SecondsWaiting:F0}s (> {facts.TimeoutSeconds:F0}s); {facts.AmbiguityReason}; routing to blocked + human escalation rather than waiting indefinitely");
    }
}

/// <summary>
/// The facts <see cref="SteerTimeoutPolicy.Decide"/> needs, all gathered by the
/// caller from durable on-disk state (the steer-pending marker) plus the
/// resolver's verdict, so the policy stays a pure function.
/// </summary>
/// <param name="SecondsWaiting">Seconds since the steer question started waiting (marker <c>WaitStartedAt</c>).</param>
/// <param name="TimeoutSeconds">The bounded wait this card is allowed before the timeout fires. Default 120s; per-card / config override.</param>
/// <param name="HasConfidentAutoAnswer">True when the resolver produced an unambiguous answer from prompt.md / the task context.</param>
/// <param name="AutoAnswerText">The answer to feed back as a Continue when <paramref name="HasConfidentAutoAnswer"/>.</param>
/// <param name="AmbiguityReason">Optional human reason why no confident answer was found (surfaced in the blocked escalation).</param>
public sealed record SteerTimeoutFacts(
    double SecondsWaiting,
    double TimeoutSeconds,
    bool HasConfidentAutoAnswer,
    string? AutoAnswerText,
    string? AmbiguityReason);

/// <summary>The pure verdict: what to do, a taxonomy code, the answer (when auto-answering), and a human reason.</summary>
public sealed record SteerTimeoutDecision(
    SteerTimeoutAction Action,
    string ReasonCode,
    string? AnswerText,
    string Detail);

/// <summary>The three possible steer-timeout verdicts for a waiting <c>3-progress</c> card.</summary>
public enum SteerTimeoutAction
{
    /// <summary>Still inside the bounded wait; leave it, re-check next sweep.</summary>
    KeepWaiting,
    /// <summary>Timed out and the answer is unambiguous: feed it back as a Continue so the run resumes.</summary>
    AutoAnswer,
    /// <summary>Timed out and no confident answer: route to blocked + normal human escalation.</summary>
    RouteBlocked,
}

/// <summary>Stable taxonomy codes carried on a <see cref="SteerTimeoutDecision"/> and in the audit log / timeline.</summary>
public static class SteerTimeoutReasons
{
    /// <summary>Still inside the bounded steer wait; card keeps its "waiting for answer" pill.</summary>
    public const string WithinTimeout = "within-timeout";
    /// <summary>Timed out; the answer was unambiguous and auto-fed as a Continue.</summary>
    public const string AutoAnswered = "auto-answered";
    /// <summary>Timed out; no confident answer, routed to blocked + human escalation.</summary>
    public const string SteerUnanswered = "steer-unanswered";
}
