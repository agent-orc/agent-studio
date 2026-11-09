using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchPhaseTransitionPolicyTests
{
    private static readonly IReadOnlyDictionary<string, string> NoReplays = new Dictionary<string, string>();

    // The batch gate's phase changes, as the pilot, the assembler and the
    // recoveries write them (AGT-3552 research map).
    public static TheoryData<BatchPhase?, BatchPhase> LegalChanges => new()
    {
        { null, BatchPhase.Formed },
        { BatchPhase.Formed, BatchPhase.Formed },
        { BatchPhase.Formed, BatchPhase.Assembling },
        { BatchPhase.Formed, BatchPhase.Paused },
        { BatchPhase.Formed, BatchPhase.Abandoned },
        { BatchPhase.Assembling, BatchPhase.Assembling },
        { BatchPhase.Assembling, BatchPhase.Assembled },
        { BatchPhase.Assembling, BatchPhase.Paused },
        { BatchPhase.Assembling, BatchPhase.Abandoned },
        { BatchPhase.Assembled, BatchPhase.Running },
        { BatchPhase.Assembled, BatchPhase.Paused },
        { BatchPhase.Assembled, BatchPhase.Abandoned },
        { BatchPhase.Running, BatchPhase.Green },
        { BatchPhase.Running, BatchPhase.Red },
        { BatchPhase.Running, BatchPhase.Paused },
        { BatchPhase.Running, BatchPhase.Abandoned },
        { BatchPhase.Green, BatchPhase.Publishing },
        { BatchPhase.Green, BatchPhase.Paused },
        { BatchPhase.Green, BatchPhase.Abandoned },
        { BatchPhase.Publishing, BatchPhase.Published },
        { BatchPhase.Publishing, BatchPhase.Paused },
        { BatchPhase.Publishing, BatchPhase.Abandoned },
        { BatchPhase.Red, BatchPhase.Paused },
        { BatchPhase.Red, BatchPhase.Abandoned },
        { BatchPhase.Paused, BatchPhase.Paused },
        { BatchPhase.Paused, BatchPhase.Abandoned },
    };

    [Fact]
    public void Only_the_listed_changes_are_legal()
    {
        var legal = LegalChanges.Select(row => ((BatchPhase?)row[0], (BatchPhase)row[1]!)).ToHashSet();
        foreach (var from in Enum.GetValues<BatchPhase>().Cast<BatchPhase?>().Append(null))
        foreach (var to in Enum.GetValues<BatchPhase>())
            Assert.True(BatchPhaseTransitionPolicy.IsLegal(from, to) == legal.Contains((from, to)),
                $"{from?.ToString() ?? "none"} -> {to}");
    }

    [Theory]
    [MemberData(nameof(LegalChanges))]
    public void A_legal_change_takes_the_next_version(BatchPhase? from, BatchPhase to)
    {
        var decision = BatchPhaseTransitionPolicy.Decide(Facts(from, to, version: from is null ? 0 : 4));

        Assert.True(decision.Accepted, decision.Refusal);
        Assert.Equal(from is null ? 1 : 5, decision.Version);
    }

    [Fact]
    public void Published_and_abandoned_batches_never_change_again()
    {
        Assert.True(BatchPhaseTransitionPolicy.IsTerminal(BatchPhase.Published));
        Assert.True(BatchPhaseTransitionPolicy.IsTerminal(BatchPhase.Abandoned));
        Assert.Equal(BatchPhaseTransitionPolicy.IllegalTransition,
            BatchPhaseTransitionPolicy.Decide(Facts(BatchPhase.Abandoned, BatchPhase.Paused)).Refusal);
        Assert.Equal(BatchPhaseTransitionPolicy.IllegalTransition,
            BatchPhaseTransitionPolicy.Decide(Facts(BatchPhase.Formed, BatchPhase.Red)).Refusal);
    }

    [Fact]
    public void A_superseded_coordinator_and_a_stale_version_are_refused()
    {
        var stale = BatchPhaseTransitionPolicy.Decide(Facts(BatchPhase.Running, BatchPhase.Green,
            currentFence: 7, requestedFence: 6));
        var raced = BatchPhaseTransitionPolicy.Decide(Facts(BatchPhase.Formed, BatchPhase.Abandoned,
            version: 3, expectedVersion: 2));

        Assert.Equal(BatchPhaseTransitionPolicy.StaleFence, stale.Refusal);
        Assert.Equal(BatchPhaseTransitionPolicy.StaleVersion, raced.Refusal);
        Assert.Equal(3, raced.Version);
        Assert.True(BatchPhaseTransitionPolicy.Decide(Facts(BatchPhase.Formed, BatchPhase.Abandoned,
            version: 3, expectedVersion: 3, requestedFence: 9)).Accepted);
    }

    [Fact]
    public void A_terminal_phase_settles_each_claim_by_its_replay()
    {
        BatchMemberClaim[] members = [.. new[] { "admitted", "conflict", "unattributable", "deferred", "unreplayed" }
            .Select(key => new BatchMemberClaim(key, "run-" + key, "review-" + key, BatchMemberDisposition.Claimed))];
        var replays = new Dictionary<string, string>
        {
            ["admitted"] = "admitted", ["conflict"] = "conflict",
            ["unattributable"] = "unattributable", ["deferred"] = "cascade-deferred",
        };

        var published = BatchPhaseTransitionPolicy.Dispositions(BatchPhase.Published, members, replays)
            .ToDictionary(item => item.TaskKey, item => item.Disposition);
        var abandoned = BatchPhaseTransitionPolicy.Dispositions(BatchPhase.Abandoned, members, replays)
            .ToDictionary(item => item.TaskKey, item => item.Disposition);
        var running = BatchPhaseTransitionPolicy.Dispositions(BatchPhase.Running, members, replays);

        Assert.Equal(BatchMemberDisposition.Published, published["admitted"]);
        Assert.Equal(BatchMemberDisposition.Ejected, published["conflict"]);
        Assert.Equal(BatchMemberDisposition.Ejected, published["unattributable"]);
        Assert.Equal(BatchMemberDisposition.Released, published["deferred"]);
        Assert.Equal(BatchMemberDisposition.Claimed, published["unreplayed"]);
        Assert.Equal(BatchMemberDisposition.Released, abandoned["admitted"]);
        Assert.Equal(BatchMemberDisposition.Ejected, abandoned["conflict"]);
        Assert.Equal(BatchMemberDisposition.Released, abandoned["unreplayed"]);
        Assert.All(running, member => Assert.Equal(BatchMemberDisposition.Claimed, member.Disposition));
    }

    [Fact]
    public void A_live_batch_has_no_findings()
        => Assert.Empty(BatchGateInvariantPolicy.Assess(new BatchInvariantFacts("batch", null, false, true, [])));

    [Fact]
    public void A_manifest_without_a_record_is_an_orphaned_formation()
    {
        var finding = Assert.Single(BatchGateInvariantPolicy.Assess(
            new BatchInvariantFacts("batch", null, false, false, [])));

        Assert.Equal(BatchInvariantKind.OrphanedFormedBatch, finding.Kind);
        Assert.Equal(BatchInvariantRepair.InitializeFormed, finding.Repair);
    }

    [Fact]
    public void A_formed_batch_claims_a_queued_member_and_retires_when_a_member_is_unclaimable()
    {
        var partial = BatchGateInvariantPolicy.Assess(Formed(
            Member("a", BatchMemberOwnership.ThisBatch), Member("b", BatchMemberOwnership.Queued)));
        var gone = BatchGateInvariantPolicy.Assess(Formed(
            Member("a", BatchMemberOwnership.Queued), Member("b", BatchMemberOwnership.ThisBatch, pending: false)));
        var taken = BatchGateInvariantPolicy.Assess(Formed(
            Member("a", BatchMemberOwnership.ThisBatch), Member("b", BatchMemberOwnership.Elsewhere)));

        var claim = Assert.Single(partial);
        Assert.Equal(("b", BatchInvariantRepair.ClaimMember), (claim.TaskKey, claim.Repair));
        Assert.Equal(["a", "b"], gone.Select(item => item.TaskKey!).Order());
        Assert.All(gone, item => Assert.Equal(BatchInvariantRepair.AbandonBatch, item.Repair));
        Assert.Equal(BatchInvariantRepair.AbandonBatch, Assert.Single(taken).Repair);
        Assert.Empty(BatchGateInvariantPolicy.Assess(Formed(
            Member("a", BatchMemberOwnership.ThisBatch), Member("b", BatchMemberOwnership.ThisBatch))));
    }

    [Fact]
    public void A_formed_diagnostic_child_outside_its_isolation_is_retired()
        => Assert.Equal(BatchInvariantRepair.AbandonBatch, Assert.Single(BatchGateInvariantPolicy.Assess(
            new BatchInvariantFacts("child", BatchPhase.Formed, true, false,
                [Member("a", BatchMemberOwnership.Elsewhere)]))).Repair);

    [Fact]
    public void Abandoned_and_red_batches_return_members_they_still_own()
    {
        BatchInvariantMemberFact[] members =
        [
            Member("owned", BatchMemberOwnership.ThisBatch),
            Member("requeued", BatchMemberOwnership.Queued),
            Member("settled", BatchMemberOwnership.ThisBatch, pending: false),
            Member("gating", BatchMemberOwnership.ThisBatch, live: true),
        ];

        var abandoned = Assert.Single(BatchGateInvariantPolicy.Assess(
            new BatchInvariantFacts("batch", BatchPhase.Abandoned, false, false, members)));
        var red = Assert.Single(BatchGateInvariantPolicy.Assess(
            new BatchInvariantFacts("batch", BatchPhase.Red, false, false, members)));

        Assert.Equal((BatchInvariantKind.TerminalBatchOwnsMember, "owned", BatchInvariantRepair.ReleaseMember),
            (abandoned.Kind, abandoned.TaskKey, abandoned.Repair));
        Assert.Equal((BatchInvariantKind.TerminalBatchOwnsMember, "owned", BatchInvariantRepair.PauseBatch),
            (red.Kind, red.TaskKey, red.Repair));
        Assert.Empty(BatchGateInvariantPolicy.Assess(
            new BatchInvariantFacts("child", BatchPhase.Red, true, false, members)));
    }

    [Fact]
    public void A_published_batch_reports_a_queued_published_member()
    {
        var finding = Assert.Single(BatchGateInvariantPolicy.Assess(new BatchInvariantFacts(
            "batch", BatchPhase.Published, false, false,
            [
                Member("queued", BatchMemberOwnership.ReleasedByThisBatch, BatchMemberDisposition.Published),
                Member("released", BatchMemberOwnership.ReleasedByThisBatch, BatchMemberDisposition.Published, pending: false),
                Member("engine", BatchMemberOwnership.ThisBatch, BatchMemberDisposition.Published, live: true),
                Member("conflict", BatchMemberOwnership.ThisBatch, BatchMemberDisposition.Ejected),
            ])));

        Assert.Equal((BatchInvariantKind.PublishedBatchUnresolvedMember, "queued", BatchInvariantRepair.None),
            (finding.Kind, finding.TaskKey, finding.Repair));
    }

    private static BatchInvariantFacts Formed(params BatchInvariantMemberFact[] members)
        => new("batch", BatchPhase.Formed, false, false, members);

    private static BatchInvariantMemberFact Member(string key, BatchMemberOwnership ownership,
        BatchMemberDisposition disposition = BatchMemberDisposition.Claimed, bool pending = true, bool live = false)
        => new(key, "review-" + key, disposition, pending, ownership, live);

    private static BatchPhaseTransitionFacts Facts(BatchPhase? from, BatchPhase to, long version = 1,
        long? expectedVersion = null, long currentFence = 0, long requestedFence = 0)
        => new(from, version, currentFence, to, requestedFence, expectedVersion,
            [new BatchMemberClaim("a", "run-a", "review-a", BatchMemberDisposition.Claimed)], NoReplays);
}
