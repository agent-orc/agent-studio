using AgentStudio.Operator;
using AgentStudio.Runner;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class OperatorReviewHostDriftTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private const string StableCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HostCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string StableImage = "registry/review@sha256:stable";
    private const string HostImage = "registry/review@sha256:host";

    [Fact]
    public void A_differing_review_role_with_claims_produces_one_fact_with_graded_cards()
    {
        var facts = OperatorReviewHostDriftFacts.Project(Snapshot(HostCommit, HostImage),
            [new("review-1", "host-1", "AGT-1", Now.AddMinutes(-8), true),
             new("review-1", "host-1", "AGT-2", Now.AddMinutes(-5), false)], Now);

        var fact = Assert.Single(facts);
        Assert.Equal(OperatorDecisionTypes.ReviewHostDrift, fact.Type);
        Assert.Equal(["AGT-1", "AGT-2"], fact.Cards);
        Assert.Contains("AGT-1", fact.Reason);
        Assert.Contains(HostCommit, fact.Reason);
        Assert.Contains(StableCommit, fact.Reason);
        Assert.Contains(HostImage, fact.Reason);
        Assert.Contains(StableImage, fact.Reason);
        Assert.Contains("host-1", fact.Reason);
        Assert.Equal("/workspace/settings/environment?host=host-1", Assert.Single(fact.Evidence).Href);
        Assert.Single(OperatorAttention.Project(facts, OperatorDecisionReader.Everything).Items);
    }

    [Fact]
    public void Idle_drift_waits_for_grace_and_a_matching_heartbeat_clears_the_fact()
    {
        Assert.Empty(OperatorReviewHostDriftFacts.Project(Snapshot(HostCommit, HostImage), [], Now));
        var work = new[] { new ReviewHostWork("review-1", "host-1", "AGT-1", Now, true) };
        Assert.Single(OperatorReviewHostDriftFacts.Project(Snapshot(HostCommit, HostImage), work, Now));
        Assert.Empty(OperatorReviewHostDriftFacts.Project(Snapshot(StableCommit, StableImage), work, Now));
    }

    [Fact]
    public void Image_only_drift_with_a_claim_also_needs_attention()
    {
        var work = new[] { new ReviewHostWork("review-1", "host-1", "AGT-3", Now, false) };
        var fact = Assert.Single(OperatorReviewHostDriftFacts.Project(Snapshot(StableCommit, HostImage), work, Now));
        Assert.Contains("AGT-3", fact.Reason);
        Assert.Contains("Graded cards: none yet", fact.Reason);
    }

    [Fact]
    public void A_newer_build_with_a_different_commit_still_reports_review_work()
    {
        var host = Snapshot(HostCommit, StableImage);
        host = host with { Hosts = [host.Hosts[0] with { State = HostReleaseDriftStates.Current }] };
        var work = new[] { new ReviewHostWork("review-1", "host-1", "AGT-4", Now, true) };
        Assert.Single(OperatorReviewHostDriftFacts.Project(host, work, Now));
    }

    private static HostReleaseDriftSnapshot Snapshot(string commit, string image)
        => new(Now, new("1.0.0", StableCommit, null), 0,
            [new("review-1", "Review one", "host-1", "review",
                new Contract.RunnerReleaseIdentityDto("1.0.0+host", "1.0.0", commit, ImageRef: image),
                commit == StableCommit ? HostReleaseDriftStates.Current : HostReleaseDriftStates.Differs,
                null, 0, false, "Different commit", Now, false, StableImage)]);
}
