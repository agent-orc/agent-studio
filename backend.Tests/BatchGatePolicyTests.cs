using AgentStudio.Git;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePolicyTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static BatchGateSubject Subject(string key, int minute = 0, long sequence = 1) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Sha, "run-1", 1, 1, Sha,
        true, true, true, true, false, null, true,
        Now.AddMinutes(minute), sequence);
    private static readonly BatchGateFormationOptions Enabled = new(Enabled: true);

    [Fact]
    public void Eligibility_RecordsEveryExclusionAndDoesNotSelectCode()
    {
        var result = BatchGatePolicy.Form([
            Subject("a", -20), Subject("f", -19), Subject("g", -18), Subject("h", -17),
            Subject("b") with { DocumentationOnly = false },
            Subject("c") with { CurrentGeneration = false },
            Subject("d") with { BuildTestDeferredToBatch = false },
            Subject("e") with { ActivePerTaskGate = true },
        ], Scope, Sha, Enabled, Now);
        Assert.Equal(["a", "f", "g", "h"], result.Manifest!.EligibleKeys);
        Assert.Equal(4, result.Exclusions.Count);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "b" && x.Reason == BatchExclusionReason.NotDocumentationOnly);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "c" && x.Reason == BatchExclusionReason.SupersededGeneration);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "d" && x.Reason == BatchExclusionReason.BuildTestNotDeferred);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "e" && x.Reason == BatchExclusionReason.ActivePerTaskGate);
    }

    [Fact]
    public void LeavesAsIntegrated_OnlyACurrentUnownedContainedMemberLeaves()
    {
        // AGT-3103: containment is asked last, and only for a member that could leave.
        var asked = 0;
        bool Contained(string sha) { asked++; return sha == Sha; }

        Assert.True(BatchGatePolicy.LeavesAsIntegrated(Subject("a"), Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(Subject("b"), _ => false));
        Assert.Equal(1, asked);
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("c") with { CurrentGeneration = false }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("d") with { OwningBatchId = "batch-1" }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("e") with { ActivePerTaskGate = true }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("f") with { ResultSha = "" }, Contained));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void OrderAndDigestAreStableAndSubjectSensitive()
    {
        var members = new[] { Subject("z", -2, 2), Subject("b", -2, 1), Subject("a", -2, 1), Subject("c", -1, 1) };
        var first = BatchGatePolicy.Form(members, Scope, Sha, Enabled, Now).Manifest!;
        var second = BatchGatePolicy.Form(members.Reverse(), Scope, Sha, Enabled, Now).Manifest!;
        Assert.Equal(["a", "b", "z", "c"], first.Members.Select(x => x.TaskKey));
        Assert.Equal(first.MembershipDigest, second.MembershipDigest);
        Assert.NotEqual(first.MembershipDigest, BatchGatePolicy.MembershipDigest(
            Sha, Scope, first.Members.Select(x => x.TaskKey == "a" ? x with { DeliveryEpoch = 2 } : x).ToArray()));
    }

    [Theory]
    [InlineData(8, 2, 0, false)]
    [InlineData(8, 3, 0, true)]
    [InlineData(8, 1, 3, true)]
    public void ConflictCascadeGuardIsStrictlyMoreThanQuarterOrThreeConsecutive(
        int total, int conflicts, int consecutive, bool expected)
        => Assert.Equal(expected, BatchGatePolicy.CascadeGuard(total, conflicts, consecutive));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    public void HalvingBudgetIsCeilingLog2(int members, int expected)
        => Assert.Equal(expected, BatchGatePolicy.DiagnosticRunsPerCycle(members));

    [Fact]
    public void FormationTriggersHonorDeadlinePressureAndLoneFallback()
    {
        Assert.Null(BatchGatePolicy.Form([Subject("a")], Scope, Sha, Enabled, Now).Manifest);
        Assert.True(BatchGatePolicy.Form([Subject("a", -16)], Scope, Sha, Enabled, Now).UsePerTaskGate);
        Assert.NotNull(BatchGatePolicy.Form([Subject("a"), Subject("b")], Scope, Sha, Enabled, Now,
            reviewQueueLength: 12).Manifest);
        Assert.Null(BatchGatePolicy.Form([Subject("a"), Subject("b")], Scope, Sha, Enabled, Now).Manifest);
    }

    [Theory]
    [InlineData(true, false, 0, BatchReplayRoute.AlreadyContained)]
    [InlineData(true, true, 2, BatchReplayRoute.AlreadyContained)]
    [InlineData(false, true, 0, BatchReplayRoute.Identity)]
    [InlineData(false, false, 0, BatchReplayRoute.Rebase)]
    [InlineData(false, true, 1, BatchReplayRoute.Merge)]
    [InlineData(false, false, 3, BatchReplayRoute.Merge)]
    public void Replay_route_merges_only_a_delivery_with_merge_commits(
        bool contained, bool descendant, int merges, BatchReplayRoute expected)
        => Assert.Equal(expected, BatchReplayPolicy.Choose(contained, descendant, merges));

    [Fact]
    public void Replay_route_rejects_a_negative_merge_count()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BatchReplayPolicy.Choose(false, false, -1));

    [Fact]
    public void Replay_disposition_ejects_only_an_unattributable_member_and_pauses_on_infrastructure()
    {
        Assert.Equal(BatchReplayDisposition.Admit, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, Sha, [new RebasedCommitReplacement(Sha, Sha)], [], null)));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            BatchReplayResult.Unattributable("no single candidate commit")));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, Sha, [], [], null)));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, null, [new RebasedCommitReplacement(Sha, Sha)], [], null)));
        Assert.Equal(BatchReplayDisposition.Pause, BatchGatePolicy.ReplayDisposition(
            BatchReplayResult.Infrastructure("worktree")));
        // An unclassified failure keeps the old fail-closed pause.
        Assert.Equal(BatchReplayDisposition.Pause, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(false, null, [], [], "unknown")));
    }
}
