namespace AgentStudio.Runner;

/// <summary>What the tick does with an armed CLI-recovery auto-resume.</summary>
public enum CliRecoveryResumeAction
{
    /// <summary>Nothing to do on this tick: not armed, no durable auto intent, or the CLI is still down.</summary>
    Wait,

    /// <summary>Restore the operator's durable auto mode and count the attempt.</summary>
    Resume,

    /// <summary>Stop resuming. The runner stays manual until an operator acts.</summary>
    StopProbing,
}

/// <summary>
/// Pure decision for the CLI-recovery auto-resume loop (AGT-2821).
///
/// <para>
/// <b>Why this exists.</b> The spawn-failure pickup pause is bounded per task:
/// after <c>PickupFailureThreshold</c> failed spawns the card returns to
/// <c>2-ready</c> and the runner drops to manual. The recovery probe then
/// restores the operator's auto mode as soon as the CLI answers
/// <c>&lt;cli&gt; --version</c>. When the CLI binary is healthy but the run
/// spawn itself is broken — the durable-worker pipe defect on Stable 0.3.0 —
/// those two bounded mechanisms compose into an unbounded loop: pause, probe,
/// resume, burn the spawn budget again, pause. The card flaps between
/// <c>2-ready</c> and <c>3-progress</c> for as long as the backend runs.
/// </para>
///
/// <para>
/// <b>What it does.</b> It counts consecutive auto-resumes that never produced
/// a started CLI process. Up to <see cref="MaxConsecutiveResumes"/> of them
/// keep the self-healing behaviour for a transient CLI break; the next one
/// stops probing so the runner rests in manual with a visible reason instead
/// of cycling the card. A confirmed process start (the CLI really works again)
/// and an operator mode change both reset the count.
/// </para>
/// </summary>
public static class CliRecoveryResumePolicy
{
    /// <summary>
    /// Consecutive auto-resumes allowed before the runner stops re-arming
    /// itself. Small on purpose: a genuinely healed CLI spawns on the first
    /// resume, so more attempts only repeat the same failed spawn budget.
    /// </summary>
    public const int MaxConsecutiveResumes = 3;

    /// <summary>
    /// Decide the next step. <paramref name="armed"/> is the spawn-failure
    /// pause marker, <paramref name="hasDurableAutoIntent"/> means the
    /// operator's saved mode is an auto mode, <paramref name="cliAvailable"/>
    /// is the result of the CLI availability probe, and
    /// <paramref name="consecutiveResumes"/> counts the resumes since the last
    /// confirmed CLI start or operator mode change.
    /// </summary>
    public static CliRecoveryResumeAction Decide(
        bool armed,
        bool manualMode,
        bool hasDurableAutoIntent,
        bool cliAvailable,
        int consecutiveResumes)
    {
        if (!armed || !manualMode || !hasDurableAutoIntent) return CliRecoveryResumeAction.Wait;
        // The budget outranks the probe: a CLI that answers --version while
        // every run still fails to spawn is exactly the looping case.
        if (consecutiveResumes >= MaxConsecutiveResumes) return CliRecoveryResumeAction.StopProbing;
        return cliAvailable ? CliRecoveryResumeAction.Resume : CliRecoveryResumeAction.Wait;
    }

    /// <summary>
    /// Operator-facing explanation for <see cref="CliRecoveryResumeAction.StopProbing"/>.
    /// It becomes the runner's mode reason, so it names the CLI, the count and
    /// what an operator has to do.
    /// </summary>
    public static string StopProbingReason(string cliType, int consecutiveResumes)
        => $"auto-resume stopped: the {cliType} CLI answers its availability probe, but "
           + $"{consecutiveResumes} restored auto runs in a row could not start a process. "
           + "The runner stays manual until the CLI is repaired and auto mode is switched on again.";
}
