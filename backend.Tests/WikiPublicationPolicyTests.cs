using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the hosted-wiki publication decision. Everything here is
/// pure: no repository, no clock, no host. The service tests then prove that
/// the coordination layer applies exactly these verdicts.
/// </summary>
public class WikiPublicationPolicyTests
{
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PriorSha = "cccccccccccccccccccccccccccccccccccccccc";

    private static WikiPublicationFacts Facts(
        string? sourceRef = "origin/develop",
        bool repositoryAvailable = true,
        bool refAccepted = true,
        string? fetchError = null,
        string? candidateSha = CandidateSha,
        string? publishedSha = null,
        bool publishedSnapshotUsable = true) =>
        new(sourceRef, repositoryAvailable, refAccepted, fetchError, candidateSha,
            publishedSha, publishedSnapshotUsable);

    // ---- Accepted publication sources ----

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("develop")]
    [InlineData("origin/main")]
    [InlineData("origin/develop")]
    [InlineData("release/2026.09")]
    [InlineData("origin/release/2026.09")]
    [InlineData("v1")]
    [InlineData("v2026.9.1")]
    public void IsPublishableRef_AcceptsIntegrationBranchesAndReleaseRevisions(string gitRef) =>
        Assert.True(WikiPublicationPolicy.IsPublishableRef(gitRef, "origin"));

    [Theory]
    [InlineData("task/hosted-wiki-live-revision-sync")]
    [InlineData("origin/task/hosted-wiki-live-revision-sync")]
    [InlineData("runner/agent-runner-01/AGT-2278")]
    [InlineData("docs/wiki-edit/2026-09-15-note")]
    [InlineData("feature/spike")]
    [InlineData("developer-notes")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPublishableRef_RejectsTaskAndArbitraryBranches(string? gitRef) =>
        Assert.False(WikiPublicationPolicy.IsPublishableRef(gitRef, "origin"));

    [Fact]
    public void IsPublishableRef_AcceptsAPinnedReleaseCommit() =>
        Assert.True(WikiPublicationPolicy.IsPublishableRef(new string('a', 40), "origin"));

    [Fact]
    public void IsPublishableRef_HonoursAConfiguredPatternSet()
    {
        string[] accepted = ["integration", "stable/*"];
        Assert.True(WikiPublicationPolicy.IsPublishableRef("origin/integration", "origin", accepted));
        Assert.True(WikiPublicationPolicy.IsPublishableRef("stable/2026", "origin", accepted));
        Assert.False(WikiPublicationPolicy.IsPublishableRef("develop", "origin", accepted));
    }

    [Theory]
    [InlineData("origin/develop", "origin", true)]
    [InlineData("develop", "origin", false)]
    [InlineData("upstream/main", "upstream", true)]
    [InlineData("origin/main", "upstream", false)]
    public void RequiresFetch_OnlyForRemoteQualifiedRefs(string gitRef, string remote, bool expected) =>
        Assert.Equal(expected, WikiPublicationPolicy.RequiresFetch(gitRef, remote));

    [Fact]
    public void RequiresFetch_IsFalseForAPinnedCommit() =>
        Assert.False(WikiPublicationPolicy.RequiresFetch(new string('a', 40), "origin"));

    // ---- Decision matrix ----

    [Fact]
    public void Decide_WithoutASourceRef_LeavesTheProjectCheckoutBacked()
    {
        var decision = WikiPublicationPolicy.Decide(Facts(sourceRef: null));
        Assert.Equal(WikiPublicationAction.Disabled, decision.Action);
        Assert.Equal(WikiPublicationFailure.None, decision.Failure);
    }

    [Fact]
    public void Decide_WithoutARepository_FailsTypedAndPromotesNothing()
    {
        var decision = WikiPublicationPolicy.Decide(
            Facts(repositoryAvailable: false, publishedSha: PriorSha));
        Assert.Equal(WikiPublicationAction.Fail, decision.Action);
        Assert.Equal(WikiPublicationFailure.RepositoryUnavailable, decision.Failure);
        Assert.Equal(PriorSha, decision.FromSha);
        Assert.Null(decision.ToSha);
    }

    [Fact]
    public void Decide_WithAnUnacceptedRef_RefusesToPublishATaskBranch()
    {
        var decision = WikiPublicationPolicy.Decide(
            Facts(sourceRef: "origin/task/spike", refAccepted: false));
        Assert.Equal(WikiPublicationAction.Fail, decision.Action);
        Assert.Equal(WikiPublicationFailure.UnacceptedRef, decision.Failure);
        Assert.Contains("origin/task/spike", decision.Reason);
    }

    [Fact]
    public void Decide_WithAFetchFailure_KeepsThePublishedRevision()
    {
        var published = PriorSha;
        var decision = WikiPublicationPolicy.Decide(
            Facts(fetchError: "could not resolve host", publishedSha: published));
        Assert.Equal(WikiPublicationAction.Fail, decision.Action);
        Assert.Equal(WikiPublicationFailure.FetchFailed, decision.Failure);
        Assert.Equal(published, decision.FromSha);
        Assert.Null(decision.ToSha);
    }

    [Fact]
    public void Decide_WithAnUnresolvableRevision_FailsTyped()
    {
        var decision = WikiPublicationPolicy.Decide(Facts(candidateSha: null));
        Assert.Equal(WikiPublicationAction.Fail, decision.Action);
        Assert.Equal(WikiPublicationFailure.RevisionNotFound, decision.Failure);
    }

    [Fact]
    public void Decide_WhenTheAcceptedRevisionIsAlreadyOnline_IsANoOp()
    {
        var sha = CandidateSha;
        var decision = WikiPublicationPolicy.Decide(Facts(candidateSha: sha, publishedSha: sha));
        Assert.Equal(WikiPublicationAction.NoOp, decision.Action);
        Assert.Equal(sha, decision.FromSha);
        Assert.Equal(sha, decision.ToSha);
    }

    [Fact]
    public void Decide_WhenThePublishedSnapshotIsGone_RepublishesTheSameCommit()
    {
        var sha = CandidateSha;
        var decision = WikiPublicationPolicy.Decide(
            Facts(candidateSha: sha, publishedSha: sha, publishedSnapshotUsable: false));
        Assert.Equal(WikiPublicationAction.Promote, decision.Action);
        Assert.Equal(sha, decision.ToSha);
    }

    [Fact]
    public void Decide_FirstPublication_PromotesAndReportsNoPredecessor()
    {
        var decision = WikiPublicationPolicy.Decide(Facts());
        Assert.Equal(WikiPublicationAction.Promote, decision.Action);
        Assert.Null(decision.FromSha);
        Assert.Equal(CandidateSha, decision.ToSha);
    }

    [Fact]
    public void Decide_NewerRevision_PromotesAndRecordsOldAndNewSha()
    {
        var old = PriorSha;
        var decision = WikiPublicationPolicy.Decide(Facts(publishedSha: old));
        Assert.Equal(WikiPublicationAction.Promote, decision.Action);
        Assert.Equal(old, decision.FromSha);
        Assert.Equal(CandidateSha, decision.ToSha);
    }

    /// <summary>
    /// Ordering matters: a repository or ref problem must be reported as such
    /// even when a stale fetch error or candidate is also present, so the
    /// recovery runbook names the first real cause.
    /// </summary>
    [Fact]
    public void Decide_ReportsTheFirstBlockingCause()
    {
        var decision = WikiPublicationPolicy.Decide(Facts(
            repositoryAvailable: false, refAccepted: false, fetchError: "offline", candidateSha: null));
        Assert.Equal(WikiPublicationFailure.RepositoryUnavailable, decision.Failure);
    }

    // ---- Materialization verdict ----

    [Theory]
    [InlineData(null, true, WikiPublicationFailure.None)]
    [InlineData("archive failed", true, WikiPublicationFailure.SnapshotFailed)]
    [InlineData(null, false, WikiPublicationFailure.SnapshotFailed)]
    [InlineData("archive failed", false, WikiPublicationFailure.SnapshotFailed)]
    public void ValidateMaterialization_RefusesAnythingButACompleteDocsTree(
        string? snapshotError, bool docsTreePresent, WikiPublicationFailure expected) =>
        Assert.Equal(expected, WikiPublicationPolicy.ValidateMaterialization(snapshotError, docsTreePresent));

    // ---- Options ----

    [Fact]
    public void Options_DefaultToDisabledWithAnSloCompatibleInterval()
    {
        var options = WikiPublicationOptions.FromConfiguration(null);
        Assert.False(options.Enabled);
        Assert.Equal("origin", options.Remote);
        Assert.Equal(120, options.IntervalSeconds);
        Assert.True(options.IntervalSeconds <= WikiPublicationOptions.MaxIntervalSeconds);
    }

    [Fact]
    public void Options_ClampTheIntervalSoTheFreshnessSloStaysReachable()
    {
        Assert.Equal(
            WikiPublicationOptions.MaxIntervalSeconds,
            Build(("WikiPublication:IntervalSeconds", "86400")).IntervalSeconds);
        Assert.Equal(
            WikiPublicationOptions.MinIntervalSeconds,
            Build(("WikiPublication:IntervalSeconds", "1")).IntervalSeconds);
    }

    [Fact]
    public void Options_ReadRemoteAcceptedRefsAndRetention()
    {
        var options = Build(
            ("WikiPublication:Enabled", "true"),
            ("WikiPublication:Remote", " upstream "),
            ("WikiPublication:SnapshotRetention", "5"),
            ("WikiPublication:AcceptedRefs:0", "integration"),
            ("WikiPublication:AcceptedRefs:1", "stable/*"));

        Assert.True(options.Enabled);
        Assert.Equal("upstream", options.Remote);
        Assert.Equal(5, options.SnapshotRetention);
        Assert.Equal(["integration", "stable/*"], options.AcceptedRefs);
    }

    [Fact]
    public void Options_RetentionNeverDropsBelowCurrentAndPrevious() =>
        Assert.Equal(2, Build(("WikiPublication:SnapshotRetention", "0")).SnapshotRetention);

    private static WikiPublicationOptions Build(params (string Key, string Value)[] settings) =>
        WikiPublicationOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());
}
