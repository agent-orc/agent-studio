using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2849: the pure decision behind restart recovery for an interrupted
/// pre-develop build gate. The matrix is the point - ancestry alone can never
/// tell a verified merge from an un-gated one, so every row here is about which
/// durable fact is allowed to decide, and which two facts refuse the repair
/// outright.
/// </summary>
public sealed class InterruptedIntegrationGatePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoOpenRecord_DecidesNothing()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([]);

        Assert.Equal(InterruptedGateRecoveryAction.None, decision.Action);
        Assert.Null(decision.RollbackSha);
        Assert.Empty(decision.Requeue);
    }

    [Fact]
    public void UnverifiedMergeStillOnTheBranch_RollsBackToItsPreMergeTip()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.RollBack, decision.Action);
        Assert.Equal("tip0", decision.RollbackSha);
        Assert.Single(decision.Requeue);
        Assert.Single(decision.Unverified);
    }

    /// <summary>
    /// A green receipt for exactly this merge result IS the verdict: the process
    /// died after the gate answered. Rolling that merge back would discard a
    /// verified result and re-spend the gate for nothing.
    /// </summary>
    [Fact]
    public void GreenVerdictForTheExactSha_ResumesInsteadOfRollingBack()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.Green),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Requeue, decision.Action);
        Assert.Equal(InterruptedIntegrationGatePolicy.Reasons.Verified, decision.Reason);
        Assert.Null(decision.RollbackSha);
        Assert.Single(decision.Requeue);
    }

    /// <summary>A red receipt is a verdict too, but the merge must still go.</summary>
    [Fact]
    public void RedVerdictWhoseRollbackNeverRan_StillRollsBack()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.Red),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.RollBack, decision.Action);
        Assert.Equal("tip0", decision.RollbackSha);
    }

    /// <summary>
    /// The incident: later merges sat on top of an un-gated one. The oldest
    /// unverified merge sets the anchor, because resetting to a newer one would
    /// leave the un-gated merge exactly where it was.
    /// </summary>
    [Fact]
    public void StackedRecords_UseTheOldestUnverifiedAnchorAndRequeueEveryCard()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("second", gatedSha: "merge2", anchor: "merge1", verdict: InterruptedGateVerdict.None, at: T0.AddMinutes(11)),
            Entry("first", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None, at: T0),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.RollBack, decision.Action);
        Assert.Equal("tip0", decision.RollbackSha);
        Assert.Equal(["first", "second"], decision.Requeue.Select(entry => entry.JobId));
    }

    /// <summary>
    /// A verified merge that happens to sit above an unverified one is still
    /// rolled back with it - and requeued, so it is merged and gated again rather
    /// than silently dropped.
    /// </summary>
    [Fact]
    public void VerifiedMergeAboveAnUnverifiedOne_IsRequeuedWithTheRollback()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("first", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None, at: T0),
            Entry("second", gatedSha: "merge2", anchor: "merge1", verdict: InterruptedGateVerdict.Green, at: T0.AddMinutes(11)),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.RollBack, decision.Action);
        Assert.Equal("tip0", decision.RollbackSha);
        Assert.Equal(["first", "second"], decision.Requeue.Select(entry => entry.JobId));
    }

    /// <summary>
    /// The record survived a branch that was already repaired by hand. Nothing to
    /// roll back; the card still needs its integration.
    /// </summary>
    [Fact]
    public void MergeNoLongerOnTheBranch_RequeuesWithoutTouchingIt()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None) with
            {
                GatedShaOnBranch = false,
            },
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Requeue, decision.Action);
        Assert.Equal(InterruptedIntegrationGatePolicy.Reasons.NotOnBranch, decision.Reason);
        Assert.Null(decision.RollbackSha);
    }

    /// <summary>
    /// The record was opened before the merge and the branch never moved: the
    /// process died before or during the merge primitive, and there is nothing
    /// unverified on the branch.
    /// </summary>
    [Fact]
    public void RecordWithoutAMergeResultOnAnUnmovedBranch_OnlyRequeues()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: null, anchor: "tip0", verdict: InterruptedGateVerdict.None) with
            {
                GatedShaOnBranch = false,
                AnchorIsBranchTip = true,
            },
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Requeue, decision.Action);
        Assert.Null(decision.RollbackSha);
    }

    /// <summary>
    /// The mirror: the record never recorded its merge result, but the branch did
    /// move. The serialized merge boundary means nothing else could have moved
    /// it, so the branch goes back to the recorded anchor.
    /// </summary>
    [Fact]
    public void RecordWithoutAMergeResultOnAMovedBranch_RollsBackToTheAnchor()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: null, anchor: "tip0", verdict: InterruptedGateVerdict.None) with
            {
                GatedShaOnBranch = false,
                AnchorIsBranchTip = false,
            },
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.RollBack, decision.Action);
        Assert.Equal("tip0", decision.RollbackSha);
    }

    [Fact]
    public void AnchorThatIsNotOnTheBranch_Escalates()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None) with
            {
                AnchorOnBranch = false,
            },
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Escalate, decision.Action);
        Assert.Equal(InterruptedIntegrationGatePolicy.Reasons.AnchorUnreachable, decision.Reason);
        Assert.Empty(decision.Requeue);
    }

    /// <summary>
    /// Resetting below the published tip would take back commits the world has
    /// already seen. That is not a rollback, so the policy refuses it.
    /// </summary>
    [Fact]
    public void AnchorBehindThePublishedTip_Escalates()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: "tip0", verdict: InterruptedGateVerdict.None) with
            {
                AnchorContainsPublishedTip = false,
            },
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Escalate, decision.Action);
        Assert.Equal(InterruptedIntegrationGatePolicy.Reasons.AnchorBehindPublished, decision.Reason);
        Assert.Null(decision.RollbackSha);
    }

    [Fact]
    public void MissingAnchor_Escalates()
    {
        var decision = InterruptedIntegrationGatePolicy.Decide([
            Entry("one", gatedSha: "merge1", anchor: null, verdict: InterruptedGateVerdict.None),
        ]);

        Assert.Equal(InterruptedGateRecoveryAction.Escalate, decision.Action);
        Assert.Equal(InterruptedIntegrationGatePolicy.Reasons.AnchorUnreachable, decision.Reason);
    }

    private static InterruptedGateEntry Entry(
        string jobId,
        string? gatedSha,
        string? anchor,
        InterruptedGateVerdict verdict,
        DateTimeOffset? at = null)
        => new(
            jobId,
            JobFolderPath: "/jobs/" + jobId,
            gatedSha,
            anchor,
            at ?? T0,
            verdict,
            GatedShaOnBranch: gatedSha is not null,
            AnchorOnBranch: anchor is not null,
            AnchorIsBranchTip: false,
            AnchorContainsPublishedTip: true);
}
