namespace AgentStudio.Runner;

/// <summary>
/// Deterministic loop-breaker for the auto-review reissue cycle (ASS-794,
/// Epic-776 orchestrator hardening). It sits on the multi-aspect BLOCK path in
/// <c>ProcessDoneAsync</c>, the one reissue branch that did not previously
/// enforce the shared reissue budget and could therefore pendulum a card
/// between <c>2-ready</c> and the run loop forever.
///
/// <para>
/// The failure it stops (verified on ASS-794): an already-finished task is
/// re-issued, the re-run finds nothing left to do and produces NO new
/// commit/diff, the aspect reviewers read the unchanged (or +0/-0) diff as
/// "work missing" and BLOCK, which reissues again, which re-runs empty again -
/// an endless loop on a task whose own close-out was clean the whole time.
/// </para>
///
/// <para>
/// Two rules, applied in order:
/// </para>
/// <list type="number">
/// <item>
///   <b>Empty-diff accept.</b> When a card that was already re-issued at least
///   once comes back with an EMPTY follow-up diff (the latest run committed
///   nothing) while its own close-out is acceptable (open items none, build /
///   tests green - the static <see cref="CompletionGate"/> passed), accept it.
///   The empty follow-up diff is evidence that nothing is open, not that nothing
///   was done; the prior run already landed the work. Accept routes to
///   <c>5-human-review</c> (ADR-0025), so a human still has the final say.
/// </item>
/// <item>
///   <b>Hard budget.</b> Once the shared per-task reissue budget is spent, never
///   loop back to <c>2-ready</c> again. Escalate to <c>5-human-review</c> with
///   the blocking concerns surfaced instead of penduluming.
/// </item>
/// </list>
///
/// <para>
/// Pure and side-effect-free so the policy can be pinned by unit tests, the same
/// pattern <see cref="CompletionGate"/>, <see cref="EvidenceGate"/>, and
/// <see cref="RunQuarantineBreaker"/> follow. The caller owns the disk-facing
/// inputs (the empty-diff probe and the completion-gate verdict).
/// </para>
/// </summary>
public static class ReissueLoopBreaker
{
    public const int DefaultIdenticalBlockRounds = 2;

    public enum LoopBreakAction
    {
        /// <summary>No loop-break; fall through to the normal reissue.</summary>
        None,

        /// <summary>
        /// Accept the run: a re-issued card produced an empty follow-up diff while
        /// its own close-out was already acceptable. The empty diff confirms
        /// nothing is open.
        /// </summary>
        AcceptEmptyDiff,

        /// <summary>
        /// Escalate to human review: the shared reissue budget is spent, so the
        /// card must not loop back to ready again.
        /// </summary>
        Escalate,
    }

    public sealed record Decision
    {
        public LoopBreakAction Action { get; init; } = LoopBreakAction.None;
        public string Reason { get; init; } = "Reissue budget intact; no loop-break.";
        public string Cause { get; init; } = "none";

        public bool BreaksLoop => Action != LoopBreakAction.None;
    }

    /// <summary>
    /// Decide whether to break the reissue loop before a would-be reissue.
    /// </summary>
    /// <param name="priorReissues">
    /// Count of reissues already recorded for this task (shared across
    /// NEEDS_INPUT / NOOP / aspect / lint reissues). A value of 0 means this is
    /// the first review pass, so the empty-diff rule cannot apply yet (there is
    /// no "follow-up" run to compare against).
    /// </param>
    /// <param name="maxReissues">Configured reissue budget for the task.</param>
    /// <param name="emptyFollowupDiff">
    /// True when the latest re-run produced NO new commit (HEAD unchanged across
    /// the run). The caller computes this from the run timeline; an indeterminate
    /// probe must pass <c>false</c> so the empty-diff accept never fires on a
    /// guess (the budget rule still breaks the loop).
    /// </param>
    /// <param name="stateAcceptable">
    /// True when the run's own close-out is acceptable: open items none, build /
    /// tests green - i.e. the static completion gate passed.
    /// </param>
    public static Decision Evaluate(
        int priorReissues,
        int maxReissues,
        bool emptyFollowupDiff,
        bool stateAcceptable)
    {
        // Rule 1: empty follow-up diff on an already-reissued, clean card -> accept.
        // This takes precedence over the budget rule: an empty clean re-run should
        // be accepted (low human burden), not escalated, even when the budget is
        // also spent.
        if (priorReissues >= 1 && emptyFollowupDiff && stateAcceptable)
        {
            return new Decision
            {
                Action = LoopBreakAction.AcceptEmptyDiff,
                Cause = "empty-followup-diff",
                Reason =
                    $"Re-run after {priorReissues} prior reissue(s) produced no new commit/diff and the close-out is clean " +
                    "(open items none, build/tests green); the empty follow-up diff confirms nothing is open. " +
                    "Accepting instead of reissuing again.",
            };
        }

        // Rule 2: budget spent -> escalate, never reissue back to 2-ready again.
        if (priorReissues >= maxReissues)
        {
            return new Decision
            {
                Action = LoopBreakAction.Escalate,
                Cause = "reissue-budget-exhausted",
                Reason =
                    $"Reissue budget spent ({priorReissues} of {maxReissues} reissue(s) used); " +
                    "escalating to human review instead of reissuing again to avoid a 2-ready <-> run loop.",
            };
        }

        return new Decision();
    }
}
