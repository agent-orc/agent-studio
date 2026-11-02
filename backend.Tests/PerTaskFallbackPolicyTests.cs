using AgentStudio.Git;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3434: matrix tests of the two pure decisions of the per-task batch
/// fallback. One chooses the tree its gate tests; the other lets the publish
/// step stand on that verdict only for the same tree on the same tip.
/// </summary>
public sealed class PerTaskFallbackPolicyTests
{
    private const string Result = "1111111111111111111111111111111111111111";
    private const string Tip = "2222222222222222222222222222222222222222";
    private const string MovedTip = "3333333333333333333333333333333333333333";
    private const string Candidate = "4444444444444444444444444444444444444444";
    private const string Tree = "5555555555555555555555555555555555555555";
    private const string OtherTree = "6666666666666666666666666666666666666666";

    [Fact]
    public void Clean_merge_onto_the_tip_is_the_tested_subject()
    {
        var subject = PerTaskFallbackPolicy.Choose(Result, false, Tip,
            new BatchReplayResult(true, Candidate, [], [], null, BatchReplayRoute.Merge), Tree);

        Assert.Equal(PerTaskFallbackSubjectKind.MergedTree, subject.Kind);
        Assert.Equal(Candidate, subject.TestedSha);
        Assert.Equal(Tip, subject.BaseSha);
        Assert.Equal(Tree, subject.TreeSha);
    }

    [Fact]
    public void Conflict_goes_to_a_merge_round_and_names_the_files()
    {
        var subject = PerTaskFallbackPolicy.Choose(Result, false, Tip,
            BatchReplayResult.Conflict(["README.md", "src/a.cs"], "conflict", BatchReplayRoute.Merge), null);

        Assert.Equal(PerTaskFallbackSubjectKind.MergeRound, subject.Kind);
        Assert.Null(subject.TestedSha);
        Assert.Equal(["README.md", "src/a.cs"], subject.ConflictedFiles);
        Assert.Contains("README.md, src/a.cs", subject.Reason);
        Assert.Contains(Tip[..8], subject.Reason);
    }

    public static TheoryData<bool, string?, BatchReplayResult?, string?, string> OwnBaseRows => new()
    {
        { true, Tip, new BatchReplayResult(true, Candidate, [], [], null, BatchReplayRoute.Merge), Tree,
            "red on its own" },
        { false, null, null, null, "could not be fetched" },
        { false, Tip, BatchReplayResult.Infrastructure("worktree failed"), null, "worktree failed" },
        { false, Tip, BatchReplayResult.Unattributable("already contains", BatchReplayRoute.AlreadyContained),
            null, "already contains" },
        { false, Tip, new BatchReplayResult(true, Candidate, [], [], null, BatchReplayRoute.Merge), null,
            "tree could not be read" },
    };

    [Theory]
    [MemberData(nameof(OwnBaseRows))]
    public void Without_a_mergeable_tip_the_gate_keeps_the_delivery_on_its_own_base(
        bool ownBaseRequested, string? tip, BatchReplayResult? merge, string? tree, string reason)
    {
        var subject = PerTaskFallbackPolicy.Choose(Result, ownBaseRequested, tip, merge, tree);

        Assert.Equal(PerTaskFallbackSubjectKind.OwnBase, subject.Kind);
        Assert.Equal(Result, subject.TestedSha);
        Assert.Null(subject.BaseSha);
        Assert.Null(subject.TreeSha);
        Assert.Contains(reason, subject.Reason);
    }

    [Theory]
    [InlineData(true, Candidate, BuildTestGateFailureKind.Code, "fail", true)]
    [InlineData(true, Candidate, BuildTestGateFailureKind.Code, "pass", false)]
    [InlineData(true, Candidate, BuildTestGateFailureKind.Code, null, false)]
    [InlineData(true, Candidate, BuildTestGateFailureKind.Environment, "fail", false)]
    [InlineData(true, Result, BuildTestGateFailureKind.Code, "fail", false)]
    [InlineData(false, Result, BuildTestGateFailureKind.Code, "fail", false)]
    public void Only_a_code_red_of_a_merge_on_a_red_tip_is_retested_on_the_own_base(
        bool merged, string tested, BuildTestGateFailureKind kind, string? tipOutcome, bool retest)
    {
        var subject = merged
            ? new PerTaskFallbackSubject(PerTaskFallbackSubjectKind.MergedTree, tested, Tip, Tree, "merged", [])
            : new PerTaskFallbackSubject(PerTaskFallbackSubjectKind.OwnBase, Result, null, null, "own", []);
        var gate = new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 1, "", "red", false, false)
        {
            FailureKind = kind,
        };

        Assert.Equal(retest, PerTaskFallbackPolicy.RetestOnOwnBase(subject, Result, gate, tipOutcome));
    }

    [Fact]
    public void Unmoved_tip_with_the_same_tree_reuses_the_per_task_verdict()
    {
        var decision = PerTaskGateVerdictReusePolicy.Decide(Input());

        Assert.True(decision.Reused);
        Assert.Equal("batch-fallback-rev-1.json", decision.EvidencePath);
        Assert.Contains("unchanged develop tip " + Tip[..8], decision.Reason);
    }

    [Fact]
    public void Moved_tip_keeps_the_merge_gate_and_names_both_tips()
    {
        var decision = PerTaskGateVerdictReusePolicy.Decide(Input() with { PreMergeTipSha = MovedTip });

        Assert.False(decision.Reused);
        Assert.Contains("develop moved since the per-task gate", decision.Reason);
        Assert.Contains(MovedTip[..8], decision.Reason);
        Assert.Contains(Tip[..8], decision.Reason);
    }

    public static TheoryData<string, Func<PerTaskGateVerdictReuseInput, PerTaskGateVerdictReuseInput>> GateRows => new()
    {
        { "no per-task gate verdict", input => input with { Ownership = null } },
        { "has not passed", input => input with { Ownership = input.Ownership! with { FallbackTestedSha = null } } },
        { "has not passed", input => input with { Ownership = input.Ownership! with { FallbackTestedSha = Candidate } } },
        { "on its own base", input => input with { Ownership = input.Ownership! with { FallbackBaseSha = null } } },
        { "on its own base", input => input with { Ownership = input.Ownership! with { FallbackCandidateTreeSha = null } } },
        { "this merge targets 'main'", input => input with { IntegrationBranch = "main" } },
        { "does not contain the delivery", input => input with { DeliveryContainedInMergeResult = false } },
        { "pre-merge integration tip", input => input with { PreMergeTipSha = null } },
        { "tree differs", input => input with { MergeResultTreeSha = OtherTree } },
        { "tree differs", input => input with { MergeResultTreeSha = null } },
    };

    [Theory]
    [MemberData(nameof(GateRows))]
    public void Missing_proof_keeps_the_merge_gate(string reason,
        Func<PerTaskGateVerdictReuseInput, PerTaskGateVerdictReuseInput> change)
    {
        var decision = PerTaskGateVerdictReusePolicy.Decide(change(Input()));

        Assert.False(decision.Reused);
        Assert.Contains(reason, decision.Reason);
    }

    private static PerTaskGateVerdictReuseInput Input()
        => new(new BatchGateOwnership("rev-1", Subject(),
                FallbackTestedSha: Result, FallbackEvidencePath: "batch-fallback-rev-1.json",
                FallbackBaseSha: Tip, FallbackCandidateSha: Candidate, FallbackCandidateTreeSha: Tree),
            "develop", Tip, true, Tree);

    private static BatchGateSubject Subject()
        => new("AGT-1", "project", "repository", "develop", "profile", "digest", "platform",
            "refs/heads/agent-studio/results/AGT-1", Result, "run-1", 1, 1, Result, true,
            true, true, true, false, null, false, DateTimeOffset.UnixEpoch, 1);
}
