using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Pure policy for a remote attempt whose detached worker disappeared before it
/// recorded a result (AGT-2870).
///
/// <para>
/// The runner detects the loss while it still holds the attempt identity and the
/// fence, so it publishes the worktree under that attempt's generation-scoped
/// salvage ref and names the ref on the lease release. A release that carries
/// such a ref is the same shape as the AGT-2861 timeout: the work exists and
/// only lacks its finishing round, so it gets one automatic continuation per
/// delivery generation and escalates on a repeat.
/// </para>
///
/// <para>
/// A loss without a salvage ref is not continued and not escalated: there is
/// nothing to continue from, and returning the card to Ready is the behaviour
/// the runner and the liveness monitor already produce for a crashed worker.
/// </para>
/// </summary>
public static class LostWorkerContinuationPolicy
{
    /// <summary>The release outcome a runner reports for a lost detached worker.</summary>
    public const string ReleaseOutcome = "worker-lost";

    /// <summary>Recorded on the timeline entry, the saved intent, and the lane transition.</summary>
    public const string ContinuationReason = "lost-worker-with-salvage";

    public static bool IsLostWorkerRelease(string? outcome)
        => string.Equals(
            (outcome ?? string.Empty).Trim(),
            ReleaseOutcome,
            StringComparison.OrdinalIgnoreCase);

    public static RunTimeoutSalvageAction Decide(
        string? outcome,
        bool hasSalvageCommit,
        int automaticContinuationRoundsUsed)
    {
        if (!IsLostWorkerRelease(outcome)) return RunTimeoutSalvageAction.None;
        if (!hasSalvageCommit) return RunTimeoutSalvageAction.None;

        return Math.Max(0, automaticContinuationRoundsUsed)
               < RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds
            ? RunTimeoutSalvageAction.StartContinuation
            : RunTimeoutSalvageAction.Escalate;
    }

    /// <summary>
    /// The escalation sentence for a repeated worker loss. It names the salvage
    /// so the manual recovery needs no journal reading, and the worker's own
    /// last line when the runner captured one.
    /// </summary>
    public static string ComposeEscalationReason(
        string? crashLine,
        RunSalvageReference? salvage,
        int automaticContinuationRoundsUsed)
    {
        var text = "The remote worker was lost before it recorded a result";
        if (salvage is not null) text += $"; salvaged as {salvage.Describe()}";
        var rounds = Math.Max(0, automaticContinuationRoundsUsed);
        if (rounds > 0)
        {
            text += rounds == 1
                ? "; 1 automatic continuation round was already spent"
                : $"; {rounds} automatic continuation rounds were already spent";
        }
        var line = (crashLine ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length > 300) line = line[..300];
        if (line.Length > 0) text += $"; last worker line: {line}";
        return text + ".";
    }
}

