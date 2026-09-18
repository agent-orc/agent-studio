using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryGenerationPolicyTests
{
    [Fact]
    public void EarlierNumberedGeneration_IsSupersededWithoutContentHeuristics()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("old", 1), Commit("current", 3)],
            sha => sha == "current", _ => false,
            _ => throw new InvalidOperationException("Numbered generations must use ancestry."));

        Assert.Equal(CommitIntegrationRules.Superseded, result[0].IntegrationRule);
        Assert.Equal("current", result[0].SupersededBySha);
        Assert.Equal(CommitIntegrationRules.Ancestor, result[1].IntegrationRule);
    }

    [Fact]
    public void MissingCurrentGeneration_StillBlocksEvenWhenLaterCommitTouchesSamePaths()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("missing", 3), Commit("landed", 3)],
            sha => sha == "landed", _ => false,
            _ => throw new InvalidOperationException("Current generation must not use content fallback."));

        Assert.Equal(CommitIntegrationRules.Missing, result[0].IntegrationRule);
        Assert.False(result[0].OnIntegrationBranch);
        Assert.Equal(CommitIntegrationRules.Ancestor, result[1].IntegrationRule);
    }

    [Theory]
    [InlineData(true, CommitIntegrationRules.IntegratedByContent)]
    [InlineData(false, CommitIntegrationRules.Superseded)]
    public void LegacyRules_PreferTreeEqualityThenLaterAncestorWithSamePaths(bool contentEqual, string rule)
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("old"), Commit("new")],
            sha => sha == "new", _ => false, _ => contentEqual);

        Assert.Equal(rule, result[0].IntegrationRule);
        Assert.Equal(CommitIntegrationRules.Ancestor, result[1].IntegrationRule);
    }

    [Fact]
    public void UnnumberedEarlierAttempt_CanUseContentProofWithoutPathMetadata()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("old") with { RunAttemptId = "earlier", Files = [] }, Commit("new") with { RunAttemptId = "current", Files = [] }],
            sha => sha == "new", _ => false, _ => true);
        Assert.Equal(CommitIntegrationRules.IntegratedByContent, result[0].IntegrationRule);
    }

    [Fact]
    public void SameKnownAttempt_DoesNotSupersedeMissingCommit()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("old") with { RunAttemptId = "current" }, Commit("new") with { RunAttemptId = "current" }],
            sha => sha == "new", _ => false, _ => true);

        Assert.Equal(CommitIntegrationRules.Missing, result[0].IntegrationRule);
    }

    [Fact]
    public void KnownCurrentAttempt_IsNotSupersededByAnUnmarkedCommit()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("missing") with { RunAttemptId = "current" }, Commit("landed")],
            sha => sha == "landed", _ => false, _ => true);
        Assert.Equal(CommitIntegrationRules.Missing, result[0].IntegrationRule);
    }

    [Fact]
    public void LegacyPartialPathOverlap_AndUnavailableGraph_DoNotProveSupersession()
    {
        var commits = new[] { Commit("old") with { Files = ["a", "b"] }, Commit("new") with { Files = ["a"] } };
        var result = DeliveryGenerationPolicy.Evaluate(commits, sha => sha == "new", _ => false, _ => false);
        Assert.Equal(CommitIntegrationRules.Missing, result[0].IntegrationRule);
        Assert.All(DeliveryGenerationPolicy.Evaluate(commits, _ => false, _ => false, _ => false),
            commit => Assert.Equal(CommitIntegrationRules.Missing, commit.IntegrationRule));
    }

    [Fact]
    public void PersistedEvidence_IsRecomputedAfterAnIntegrationReset()
    {
        var result = DeliveryGenerationPolicy.Evaluate(
            [Commit("old") with { IntegrationRule = CommitIntegrationRules.IntegratedByContent }],
            _ => false, _ => false, _ => false);
        Assert.Equal(CommitIntegrationRules.Missing, Assert.Single(result).IntegrationRule);
    }

    [Fact]
    public void ReconciliationDoesNotHideAChangedIntegrationFailureBehindTheSameSummary()
    {
        var task = new TaskInfo { State = TaskStates.HumanReview };
        var status = new TaskIntegrationStatus { Status = IntegrationStatuses.ConflictSkipped, Detail = "Merge conflict" };
        var decision = new AcceptanceRailDecision(AcceptanceRailAction.Requeue, "integration-conflict");
        var first = AcceptanceRailAttemptPolicy.Fingerprint(task, status, decision, "Conflict in first.txt");
        var next = AcceptanceRailAttemptPolicy.Fingerprint(task, status, decision, "Conflict in second.txt");
        Assert.NotEqual(first, next);
    }

    [Fact]
    public void HistoricalSweep_DoesNotSupersedeInheritedWorkInTheCurrentGeneration()
    {
        var old = Commit("old", 3) with
        {
            RunAttemptId = "original-producer",
            Message = "wip(runner): salvage before teardown - outcome Done",
        };
        var current = Commit("current", 3) with { RunAttemptId = "current-producer" };
        var decision = SupersededCommitSweepPolicy.Evaluate([old, current], sha => sha == "current");
        Assert.Empty(decision.Replacements);
    }

    private static TaskCommitInfo Commit(string sha, int? generation = null)
        => new() { Sha = sha, DeliveryGeneration = generation, Files = ["feature.txt"], FilesChanged = 1 };
}
