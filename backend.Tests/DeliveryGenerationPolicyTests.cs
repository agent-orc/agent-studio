using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2871: nine integrated cards sat in Human Review reading pending/partial
/// because the salvage commits of delivery rounds the card had already replaced
/// were counted as missing work. The policy matrix below pins the split the
/// board depends on: a replaced generation is history, a commit whose content is
/// already in the branch is landed, and a genuinely missing commit of the
/// CURRENT generation still blocks.
/// </summary>
public sealed class DeliveryGenerationPolicyTests
{
    [Fact]
    public void AssignGenerations_OneRunAttemptAcrossSeveralCommits_IsOneGeneration()
    {
        var generations = DeliveryGenerationPolicy.AssignGenerations(
        [
            Commit("aaaaaaa", attempt: "run_1"),
            Commit("bbbbbbb", attempt: "run_1"),
            Commit("ccccccc", attempt: "run_2"),
        ]);

        Assert.Equal([1, 1, 2], generations.Select(row => row.Generation));
        Assert.Equal("run_1", generations[0].Identity);
        Assert.Equal("run_2", generations[2].Identity);
    }

    /// <summary>
    /// An identified generation stamps every commit it produced, so an unstamped
    /// commit cannot belong to it. "Unknown" is therefore never a shared
    /// identity - each legacy record is its own generation.
    /// </summary>
    [Fact]
    public void AssignGenerations_UnmarkedLegacyRecords_EachStartItsOwnGeneration()
    {
        var generations = DeliveryGenerationPolicy.AssignGenerations(
        [
            Commit("aaaaaaa"),
            Commit("bbbbbbb"),
            Commit("ccccccc", attempt: "run_3"),
        ]);

        Assert.Equal([1, 2, 3], generations.Select(row => row.Generation));
        Assert.Null(generations[0].Identity);
        Assert.Equal("run_3", generations[2].Identity);
    }

    /// <summary>
    /// The shape of AGT-2810: two salvage commits of earlier rounds plus the
    /// reviewed, merged third generation. The card is integrated.
    /// </summary>
    [Fact]
    public void Evaluate_SupersededSalvageOfAnEarlierGeneration_IsNotMissing()
    {
        var first = Salvage("1111111", ["backend/a.cs"]);
        var second = Salvage("2222222", ["backend/a.cs"]);
        var current = Commit("3333333", attempt: "run_3", files: ["backend/a.cs"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [first, second, current],
            sha => sha == current.Sha);

        Assert.Empty(verdict.Missing);
        Assert.Equal(3, verdict.CurrentGeneration);
        Assert.True(verdict.CurrentGenerationIsIdentified);
        Assert.Equal(current.Sha, verdict.Anchor?.Sha);
        Assert.Equal(
            [CommitIntegrationRules.SupersededGeneration, CommitIntegrationRules.SupersededGeneration],
            verdict.Superseded.Select(commit => commit.Rule));
        Assert.All(verdict.Superseded, commit => Assert.Equal(current.Sha, commit.ReplacementSha));
    }

    /// <summary>
    /// The shape of AGT-2810 as the operator checked it: the old salvage is not
    /// an ancestor, but merging it into develop yields develop's own tree, so it
    /// adds nothing. Content equality outranks every supersession rule because
    /// it is the stronger statement.
    /// </summary>
    [Fact]
    public void Evaluate_ContentEqualCommit_IsIntegratedByContent()
    {
        var legacy = Salvage("1111111", ["backend/a.cs"]);
        var current = Commit("2222222", attempt: "run_2", files: ["backend/b.cs"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [legacy, current],
            sha => sha == current.Sha,
            commit => commit.Sha == legacy.Sha);

        Assert.Empty(verdict.Missing);
        Assert.Equal(CommitIntegrationRules.ContentEqual, verdict.Commits[0].Rule);
        Assert.True(verdict.Commits[0].IsIntegrated);
    }

    /// <summary>The recorded verdict is trusted on the hot path, with no git check available.</summary>
    [Fact]
    public void Evaluate_PersistedContentEqualRule_IsHonouredWithoutAGitCheck()
    {
        var legacy = Salvage("1111111", ["backend/a.cs"]) with
        {
            IntegrationRule = CommitIntegrationRules.ContentEqual,
        };
        var current = Commit("2222222", files: ["backend/b.cs"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [legacy, current],
            sha => sha == current.Sha);

        Assert.Empty(verdict.Missing);
        Assert.Equal(CommitIntegrationRules.ContentEqual, verdict.Commits[0].Rule);
    }

    /// <summary>
    /// The shape of AGT-2828: no generation markers anywhere, and the old
    /// salvage conflicts with today's develop because the newer round rewrote
    /// the same files. Path coverage by a later, integrated commit of a
    /// different generation is what decides it.
    /// </summary>
    [Fact]
    public void Evaluate_LegacyCommitWhosePathsALaterIntegratedCommitRewrote_IsSupersededByContent()
    {
        var legacy = Salvage("1111111", ["backend/a.cs", "backend/b.cs"]);
        var current = Commit("2222222", files: ["backend/a.cs", "backend/b.cs", "docs/note.md"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [legacy, current],
            sha => sha == current.Sha);

        Assert.Empty(verdict.Missing);
        var superseded = Assert.Single(verdict.Superseded);
        Assert.Equal(CommitIntegrationRules.SupersededContent, superseded.Rule);
        Assert.Equal(current.Sha, superseded.ReplacementSha);
    }

    /// <summary>A path the later delivery never touched is not covered, so the commit stays a hole.</summary>
    [Fact]
    public void Evaluate_LegacyCommitWithAnUncoveredPath_StaysMissing()
    {
        var legacy = Salvage("1111111", ["backend/a.cs", "retention/only-here.cs"]);
        var current = Commit("2222222", files: ["backend/a.cs"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [legacy, current],
            sha => sha == current.Sha);

        var missing = Assert.Single(verdict.Missing);
        Assert.Equal(legacy.Sha, missing.Sha);
    }

    /// <summary>
    /// The constraint the fix must not break: a commit of the CURRENT
    /// generation that never landed keeps blocking, even when a sibling of the
    /// same generation covers its paths.
    /// </summary>
    [Fact]
    public void Evaluate_MissingCommitOfTheCurrentGeneration_StillBlocks()
    {
        var missingSibling = Commit("1111111", attempt: "run_9", files: ["backend/a.cs"]);
        var landedSibling = Commit("2222222", attempt: "run_9", files: ["backend/a.cs", "backend/b.cs"]);

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [missingSibling, landedSibling],
            sha => sha == landedSibling.Sha);

        var missing = Assert.Single(verdict.Missing);
        Assert.Equal(missingSibling.Sha, missing.Sha);
        Assert.True(missing.IsCurrentGeneration);
        Assert.Equal(1, verdict.CurrentGeneration);
    }

    /// <summary>
    /// A wholly legacy card carries no generation evidence, so the mere
    /// existence of a newer commit must not retire an older one. Only the
    /// content rules may, and here none applies: the older commit has no
    /// changed-file metadata to compare.
    /// </summary>
    [Fact]
    public void Evaluate_UnmarkedCardWithoutPathMetadata_DoesNotInventSupersession()
    {
        var older = Commit("1111111");
        var newer = Commit("2222222");

        var verdict = DeliveryGenerationPolicy.Evaluate(
            [older, newer],
            sha => sha == newer.Sha);

        var missing = Assert.Single(verdict.Missing);
        Assert.Equal(older.Sha, missing.Sha);
        Assert.False(verdict.CurrentGenerationIsIdentified);
    }

    /// <summary>
    /// Nothing landed: there is no anchor to claim the delivery with, and the
    /// current generation is the one that has to answer for it. The replaced
    /// round stays history either way - re-delivering a card does not make its
    /// first round a second expectation.
    /// </summary>
    [Fact]
    public void Evaluate_NoCommitIntegrated_HasNoAnchorAndBlocksOnTheCurrentGeneration()
    {
        var replaced = Commit("1111111", attempt: "run_1");
        var current = Commit("2222222", attempt: "run_2");

        var verdict = DeliveryGenerationPolicy.Evaluate([replaced, current], static _ => false);

        Assert.Null(verdict.Anchor);
        var missing = Assert.Single(verdict.Missing);
        Assert.Equal(current.Sha, missing.Sha);
        var superseded = Assert.Single(verdict.Superseded);
        Assert.Equal(replaced.Sha, superseded.Sha);
    }

    private static TaskCommitInfo Salvage(string sha, string[] files)
        => Commit(sha, message: "wip(runner): salvage before teardown - outcome Done", files: files);

    private static TaskCommitInfo Commit(
        string sha,
        string? attempt = null,
        string[]? files = null,
        string? message = null)
        => new()
        {
            Sha = sha,
            ShortSha = sha[..7],
            Message = message ?? $"feat(AGT-2871): delivery {sha}",
            RunAttemptId = attempt,
            Files = (files ?? []).ToList(),
            FilesChanged = (files ?? []).Length,
        };
}
