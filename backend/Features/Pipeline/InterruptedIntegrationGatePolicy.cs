namespace AgentStudio.Pipeline;

/// <summary>What the durable gate verdict for the exact merge result says.</summary>
public enum InterruptedGateVerdict
{
    /// <summary>No applicable receipt for this exact SHA: the gate never reached a verdict.</summary>
    None,

    /// <summary>A green receipt for exactly this merge result exists; the merge is verified.</summary>
    Green,

    /// <summary>A red receipt for exactly this merge result exists; the merge is rejected.</summary>
    Red,
}

/// <summary>What startup recovery must do with one integration branch.</summary>
public enum InterruptedGateRecoveryAction
{
    /// <summary>Nothing was left in flight for this branch.</summary>
    None,

    /// <summary>The branch is trustworthy as it stands; only the cards must be integrated again.</summary>
    Requeue,

    /// <summary>The branch carries un-gated merges and must go back to <see cref="InterruptedGateDecision.RollbackSha"/> first.</summary>
    RollBack,

    /// <summary>The branch cannot be repaired without destroying work; a human decides.</summary>
    Escalate,
}

/// <summary>
/// One interrupted gate as seen from the current process: the durable journal
/// entry plus the Git facts the recovery read for it.
/// </summary>
/// <param name="GatedShaOnBranch">The merge result is reachable from the current branch tip.</param>
/// <param name="AnchorOnBranch">The recorded pre-merge anchor is reachable from the current branch tip, so the branch can be reset onto it.</param>
/// <param name="AnchorIsBranchTip">The branch never moved past the anchor; there is nothing to roll back for this entry.</param>
/// <param name="AnchorContainsPublishedTip">
/// The published <c>origin/&lt;branch&gt;</c> tip is reachable from the anchor,
/// or the repository has no published integration branch at all. False means a
/// reset would drop commits the world has already seen.
/// </param>
public sealed record InterruptedGateEntry(
    string JobId,
    string JobFolderPath,
    string? GatedSha,
    string? Anchor,
    DateTimeOffset StartedAt,
    InterruptedGateVerdict Verdict,
    bool GatedShaOnBranch,
    bool AnchorOnBranch,
    bool AnchorIsBranchTip,
    bool AnchorContainsPublishedTip);

/// <param name="RollbackSha">Commit the integration branch is reset to; null unless the action is <see cref="InterruptedGateRecoveryAction.RollBack"/>.</param>
/// <param name="Requeue">Jobs whose integration must run again; empty for <see cref="InterruptedGateRecoveryAction.Escalate"/>.</param>
/// <param name="Unverified">The interrupted entries whose merge never reached a verdict, oldest first.</param>
public sealed record InterruptedGateDecision(
    InterruptedGateRecoveryAction Action,
    string Reason,
    string? RollbackSha,
    IReadOnlyList<InterruptedGateEntry> Requeue,
    IReadOnlyList<InterruptedGateEntry> Unverified);

/// <summary>
/// Pure decision behind restart recovery for an interrupted pre-develop build
/// gate (AGT-2849).
///
/// <para>
/// The gated subject is the merge result, so the merge exists before the verdict
/// does. Ancestry on the integration branch therefore proves presence, never
/// verification: an un-gated merge left behind by a dead process looks exactly
/// like a merge that passed. Only a durable receipt for that exact SHA
/// distinguishes them, and this policy is the place that insists on it.
/// </para>
/// <para>
/// One decision per integration branch, not per card, because the branch is the
/// shared object: rolling one card's merge back while a later merge sits on top
/// of it is not a rollback, it is a rewrite of somebody else's delivery. The
/// oldest unverified merge therefore sets the anchor and every affected card is
/// integrated again on top of the repaired branch.
/// </para>
/// <para>
/// Two facts can refuse a repair outright: an anchor that is not on the branch
/// any more (the graph moved beyond what this record describes) and an anchor
/// that does not contain the published tip (resetting would un-publish commits
/// that are already on origin). Both escalate rather than guess.
/// </para>
/// </summary>
public static class InterruptedIntegrationGatePolicy
{
    public static class Reasons
    {
        public const string Nothing = "no-interrupted-gate";
        public const string Verified = "interrupted-gate-verdict-durable";
        public const string NotOnBranch = "interrupted-merge-absent-from-branch";
        public const string RollBack = "interrupted-merge-rolled-back";
        public const string AnchorUnreachable = "interrupted-gate-anchor-unreachable";
        public const string AnchorBehindPublished = "interrupted-gate-anchor-behind-published-tip";
    }

    /// <summary>
    /// Decides the repair for one integration branch from every in-flight gate
    /// record that names it.
    /// </summary>
    public static InterruptedGateDecision Decide(IReadOnlyList<InterruptedGateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return new InterruptedGateDecision(
                InterruptedGateRecoveryAction.None,
                Reasons.Nothing,
                RollbackSha: null,
                Requeue: [],
                Unverified: []);
        }

        var ordered = entries.OrderBy(entry => entry.StartedAt).ToList();
        var unverified = ordered.Where(NeedsRollback).ToList();
        if (unverified.Count == 0)
        {
            return new InterruptedGateDecision(
                InterruptedGateRecoveryAction.Requeue,
                ordered.All(entry => entry.Verdict == InterruptedGateVerdict.Green)
                    ? Reasons.Verified
                    : Reasons.NotOnBranch,
                RollbackSha: null,
                Requeue: ordered,
                Unverified: []);
        }

        var oldest = unverified[0];
        if (string.IsNullOrWhiteSpace(oldest.Anchor) || !oldest.AnchorOnBranch)
        {
            return new InterruptedGateDecision(
                InterruptedGateRecoveryAction.Escalate,
                Reasons.AnchorUnreachable,
                RollbackSha: null,
                Requeue: [],
                Unverified: unverified);
        }

        if (!oldest.AnchorContainsPublishedTip)
        {
            return new InterruptedGateDecision(
                InterruptedGateRecoveryAction.Escalate,
                Reasons.AnchorBehindPublished,
                RollbackSha: null,
                Requeue: [],
                Unverified: unverified);
        }

        return new InterruptedGateDecision(
            InterruptedGateRecoveryAction.RollBack,
            Reasons.RollBack,
            oldest.Anchor,
            ordered,
            unverified);
    }

    /// <summary>
    /// True when this entry's merge is both unverified and still on the branch.
    /// A green receipt for the exact SHA verifies it; an entry whose merge is not
    /// in the branch graph describes a branch that was already repaired. An entry
    /// that never recorded a merge result counts as unverified as soon as the
    /// branch moved past its anchor, because the serialized merge boundary means
    /// nothing else could have moved it.
    /// </summary>
    private static bool NeedsRollback(InterruptedGateEntry entry)
    {
        if (entry.Verdict == InterruptedGateVerdict.Green) return false;
        return string.IsNullOrWhiteSpace(entry.GatedSha)
            ? !entry.AnchorIsBranchTip
            : entry.GatedShaOnBranch;
    }
}
