using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2871: nine reviewed, merged cards stayed <c>pending</c>/<c>partial</c>
/// for days because a first-round <c>wip(runner): salvage before teardown</c>
/// commit could never become an ancestor of <c>develop</c> - the generation
/// that was actually merged had re-authored the same files. The generation
/// policy is a pure decision, so the rule matrix is asserted directly here:
/// which rule answers for which commit, what the card's verdict is, and the
/// exact sentence the operator reads.
/// </summary>
public sealed class DeliveryGenerationPolicyTests
{
    private const string Salvage = "wip(runner): salvage before teardown - outcome Done";

    [Fact]
    public void SupersededSalvageCommitOfAnEarlierGeneration_IsSupersededNotMissing()
    {
        var commits = new[]
        {
            Commit("aaa1", attempt: "run-1", files: ["backend/Feature.cs"], message: Salvage),
            Commit("aaa2", attempt: "run-2", files: ["backend/Feature.cs"], message: Salvage),
            Commit("aaa3", attempt: "run-3", files: ["backend/Feature.cs", "docs/note.md"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("aaa3"));

        Assert.Equal(3, verdict.GenerationCount);
        Assert.Empty(verdict.Missing);
        Assert.True(verdict.IsFullyIntegrated);
        Assert.Equal("aaa3", verdict.Anchor!.Sha);
        Assert.Equal(
            [CommitIntegrationEvidence.GenerationSuperseded, CommitIntegrationEvidence.GenerationSuperseded],
            verdict.Superseded.Select(commit => commit.Evidence));
        Assert.Equal([1, 2], verdict.Superseded.Select(commit => commit.Generation));
    }

    [Fact]
    public void MissingCommitOfTheCurrentGeneration_StaysMissing()
    {
        // Same run attempt: nothing was re-delivered, so the hole is real and
        // must keep blocking acceptance exactly as before.
        var commits = new[]
        {
            Commit("bbb1", attempt: "run-2", files: ["backend/Other.cs"]),
            Commit("bbb2", attempt: "run-2", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("bbb2"));

        Assert.Equal(1, verdict.GenerationCount);
        Assert.False(verdict.IsFullyIntegrated);
        Assert.Equal(["bbb1"], verdict.Missing.Select(commit => commit.Sha));
        Assert.Empty(verdict.Superseded);
        Assert.Equal(2, verdict.ExpectedCount);
    }

    [Fact]
    public void LegacyCommitWithoutMarkers_IsPathSupersededWhenItsPathsWereRewritten()
    {
        // The nine-card shape: the old salvage commit carries no generation
        // marker at all, and the merged commit rewrote the same files.
        var commits = new[]
        {
            Commit("ccc1", files: ["backend/Feature.cs", "backend/Policy.cs"], message: Salvage),
            Commit("ccc2", files: ["backend/Feature.cs", "backend/Policy.cs", "backend/Sweep.cs", "docs/a.md", "docs/b.md", "docs/c.md", "docs/d.md"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("ccc2"));

        // No marker proves a second generation, so the path rule has to answer.
        Assert.Equal(1, verdict.GenerationCount);
        Assert.True(verdict.IsFullyIntegrated);
        var superseded = Assert.Single(verdict.Superseded);
        Assert.Equal(CommitIntegrationEvidence.PathSuperseded, superseded.Evidence);
        Assert.Equal("ccc2", superseded.ReplacementSha);
    }

    [Fact]
    public void LegacyCommitWhosePathWasNeverTouchedAgain_StaysMissing()
    {
        var commits = new[]
        {
            Commit("ddd1", files: ["backend/Untouched.cs"], message: Salvage),
            Commit("ddd2", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("ddd2"));

        Assert.Equal(["ddd1"], verdict.Missing.Select(commit => commit.Sha));
        Assert.Empty(verdict.Superseded);
    }

    [Fact]
    public void ContentProbe_AnswersForACommitNoOtherRuleResolves()
    {
        var commits = new[]
        {
            Commit("eee1", files: ["backend/Untouched.cs"], message: Salvage),
            Commit("eee2", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(
            commits,
            Integrated("eee2"),
            isContentIntegrated: commit => commit.Sha == "eee1");

        Assert.True(verdict.IsFullyIntegrated);
        Assert.Empty(verdict.Missing);
        var byContent = Assert.Single(
            verdict.Commits,
            commit => commit.Evidence == CommitIntegrationEvidence.ContentEqual);
        Assert.Equal("eee1", byContent.Sha);
    }

    [Fact]
    public void PersistedContentEvidence_IsHonouredWithoutAProbe()
    {
        // This is what keeps the board hot path free of git spawns: the
        // reconcile pass decided, the projection reads the decision.
        var commits = new[]
        {
            Commit("fff1", files: ["backend/Untouched.cs"], message: Salvage)
                with { IntegrationEvidence = CommitIntegrationEvidence.ContentEqual },
            Commit("fff2", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("fff2"));

        Assert.True(verdict.IsFullyIntegrated);
        Assert.Empty(verdict.Missing);
    }

    [Fact]
    public void AnExplicitReplacementRecord_KeepsTheCommitOutOfTheExpectation()
    {
        var commits = new[]
        {
            Commit("ggg1", files: ["backend/Feature.cs"]) with { SupersededByAttempt = "run-2" },
            Commit("ggg2", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("ggg2"));

        Assert.True(verdict.IsFullyIntegrated);
        Assert.Equal(1, verdict.ExpectedCount);
        Assert.Equal(
            CommitIntegrationEvidence.GenerationSuperseded,
            Assert.Single(verdict.Superseded).Evidence);
    }

    [Theory]
    // Comparable markers of different attempts prove two generations.
    [InlineData("run-1", "run-2", 2)]
    // The same attempt is one generation, however many commits it produced.
    [InlineData("run-1", "run-1", 1)]
    // A missing marker never starts a generation: absence proves nothing.
    [InlineData("run-1", null, 1)]
    [InlineData(null, "run-2", 1)]
    [InlineData(null, null, 1)]
    public void GenerationCount_OnlyGrowsOnProvenMarkers(string? first, string? second, int expected)
    {
        var commits = new[]
        {
            Commit("h1", attempt: first, files: ["a.cs"]),
            Commit("h2", attempt: second, files: ["a.cs"]),
        };

        Assert.Equal(expected, DeliveryGenerationPolicy.AssignGenerations(commits).Max());
    }

    [Fact]
    public void AssignGenerations_AdoptsAMarkerAfterAnUnmarkedFirstCommit()
    {
        // The salvage commit carries no marker, the first remote generation
        // does, and the next attempt must still be recognized as a new one.
        var commits = new[]
        {
            Commit("i1", files: ["a.cs"], message: Salvage),
            Commit("i2", attempt: "run-2", files: ["a.cs"]),
            Commit("i3", attempt: "run-3", files: ["a.cs"]),
        };

        Assert.Equal([1, 1, 2], DeliveryGenerationPolicy.AssignGenerations(commits));
    }

    [Fact]
    public void Detail_SaysWhichGenerationLandedAndWhatWasSuperseded()
    {
        var commits = new[]
        {
            Commit("36ee6108e9aa", attempt: "run-1", files: ["backend/Feature.cs"], message: Salvage),
            Commit("84e3249de2bb", attempt: "run-2", files: ["backend/Feature.cs"], message: Salvage),
            Commit("f4d26c0df7cc", attempt: "run-3", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("f4d26c0df7cc"));
        var detail = DeliveryGenerationDetail.Integrated(verdict);

        Assert.StartsWith(
            "integrated via f4d26c0 (generation 3); 2 earlier generation commits superseded",
            detail,
            StringComparison.Ordinal);
        Assert.Contains("36ee610 superseded by f4d26c0", detail, StringComparison.Ordinal);
        Assert.Contains("84e3249 superseded by f4d26c0", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_NamesTheCommitsIntegratedByContent()
    {
        var commits = new[]
        {
            Commit("1111111111", files: ["backend/Gone.cs"], message: Salvage),
            Commit("2222222222", files: ["backend/Feature.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(
            commits,
            Integrated("2222222222"),
            isContentIntegrated: commit => commit.Sha == "1111111111");

        Assert.Equal(
            "integrated via 2222222; 1 commit integrated by content: 1111111",
            DeliveryGenerationDetail.Integrated(verdict));
    }

    [Fact]
    public void Detail_KeepsThePlainAncestryTokenForAnOrdinarySingleGenerationDelivery()
    {
        var commits = new[] { Commit("aaaaaaa", files: ["a.cs"]) };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("aaaaaaa"));

        Assert.Equal("anchor-ancestor", DeliveryGenerationDetail.Integrated(verdict));
    }

    [Fact]
    public void Detail_ForPartialNamesTheMissingCommitAndTheSupersededOne()
    {
        var commits = new[]
        {
            Commit("aaa1111", attempt: "run-1", files: ["backend/Feature.cs"], message: Salvage),
            Commit("bbb2222", attempt: "run-2", files: ["backend/Feature.cs"]),
            Commit("ccc3333", attempt: "run-2", files: ["backend/Missing.cs"]),
        };

        var verdict = DeliveryGenerationPolicy.Evaluate(commits, Integrated("bbb2222"));

        Assert.Equal(
            "1/2 attributed commits integrated; missing: ccc3333; superseded: aaa1111 superseded by bbb2222",
            DeliveryGenerationDetail.Partial(verdict));
    }

    private static Func<string, bool> Integrated(params string[] shas)
        => sha => shas.Contains(sha, StringComparer.OrdinalIgnoreCase);

    private static TaskCommitInfo Commit(
        string sha,
        string? attempt = null,
        IReadOnlyList<string>? files = null,
        string message = "feat: delivery")
        => new()
        {
            Sha = sha,
            ShortSha = sha.Length > 7 ? sha[..7] : sha,
            Message = message,
            RunAttemptId = attempt,
            Files = (files ?? []).ToList(),
            FilesChanged = files?.Count ?? 0,
        };
}
