using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;
using ReviewAttemptDto = AgentStudio.Shared.ReviewAttemptDto;
using ReviewSubjectDto = AgentStudio.Shared.ReviewSubjectDto;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3483: the decision matrix for the base of an operator continue, steer
/// or extend round, without file or HTTP plumbing.
/// </summary>
public sealed class ContinueRoundBasePolicyTests
{
    private static readonly DateTime Saved = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string OlderSha = new('1', 40);
    private static readonly string LatestSha = new('2', 40);
    private static readonly string OlderRef = FencedGitRefs.ImmutableResult("run-older", 1, OlderSha);
    private static readonly string LatestRef = FencedGitRefs.ImmutableResult("run-latest", 2, LatestSha);

    private static readonly CardResultRef[] Refs =
    [
        new(LatestRef, LatestSha, "run-latest", Saved.AddMinutes(-5)),
        new(OlderRef, OlderSha, "run-older", Saved.AddHours(-2)),
    ];

    [Fact]
    public void Without_a_named_ref_the_latest_recorded_result_is_the_base()
    {
        var decision = ContinueRoundBasePolicy.Decide(null, Refs, null, startsFromClaim: true, Saved);

        Assert.Equal(ContinueRoundBaseAction.Use, decision.Action);
        Assert.Equal(ContinueRoundBaseSources.LatestResult, decision.Source);
        Assert.Equal(new ContinuationBaseRecord(LatestRef, LatestSha,
            ContinuationBaseReasons.ContinueRoundResult, "run-latest", Saved), decision.Base);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_named_ref_of_the_card_wins_in_full_or_short_form(bool shortForm)
    {
        var named = shortForm ? OlderRef["refs/heads/".Length..] : OlderRef;

        var decision = ContinueRoundBasePolicy.Decide(named, Refs, null, startsFromClaim: true, Saved);

        Assert.Equal(ContinueRoundBaseAction.Use, decision.Action);
        Assert.Equal(ContinueRoundBaseSources.Explicit, decision.Source);
        Assert.Equal(OlderRef, decision.Base!.Ref);
        Assert.Equal(OlderSha, decision.Base.CommitSha);
    }

    [Theory]
    [InlineData("refs/heads/agent-studio/results/someone-else")]
    [InlineData("refs/tags/v1")]
    [InlineData("develop")]
    public void A_ref_the_card_does_not_own_is_rejected(string named)
    {
        var decision = ContinueRoundBasePolicy.Decide(named, Refs, null, startsFromClaim: true, Saved);

        Assert.Equal(ContinueRoundBaseAction.RejectUnknownRef, decision.Action);
        Assert.Equal(ContinueRoundBasePolicy.UnknownRefCode, decision.Code);
        Assert.Null(decision.Base);
    }

    [Fact]
    public void A_named_ref_is_rejected_before_its_applicability()
    {
        var unknown = ContinueRoundBasePolicy.Decide("refs/heads/elsewhere", Refs, null, startsFromClaim: false, Saved);
        var known = ContinueRoundBasePolicy.Decide(OlderRef, Refs, null, startsFromClaim: false, Saved);

        Assert.Equal(ContinueRoundBaseAction.RejectUnknownRef, unknown.Action);
        Assert.Equal(ContinueRoundBaseAction.RejectNotClaimed, known.Action);
        Assert.Equal(ContinueRoundBasePolicy.NotClaimedCode, known.Code);
    }

    [Fact]
    public void A_round_no_claim_starts_keeps_todays_checkout()
        => Assert.Equal(ContinueRoundBaseAction.None,
            ContinueRoundBasePolicy.Decide(null, Refs, null, startsFromClaim: false, Saved).Action);

    [Fact]
    public void A_card_without_result_refs_has_no_base()
        => Assert.Equal(ContinueRoundBaseAction.None,
            ContinueRoundBasePolicy.Decide(null, [], null, startsFromClaim: true, Saved).Action);

    [Theory]
    [InlineData("lost-worker-with-salvage", ContinueRoundBaseSources.Existing)]
    [InlineData(ContinuationBaseReasons.ReviewFixRound, ContinueRoundBaseSources.ReviewFixRound)]
    public void A_base_another_round_saved_is_kept(string reason, string source)
    {
        var existing = new ContinuationBaseRecord("refs/heads/salvage", OlderSha, reason, "run-salvage", Saved);

        var decision = ContinueRoundBasePolicy.Decide(null, Refs, existing, startsFromClaim: true, Saved);

        Assert.Equal(ContinueRoundBaseAction.Keep, decision.Action);
        Assert.Equal(source, decision.Source);
        Assert.Same(existing, decision.Base);
    }

    [Fact]
    public void A_named_ref_replaces_a_base_another_round_saved()
    {
        var existing = new ContinuationBaseRecord("refs/heads/salvage", OlderSha, "lost-worker-with-salvage", "run-salvage", Saved);

        var decision = ContinueRoundBasePolicy.Decide(LatestRef, Refs, existing, startsFromClaim: true, Saved);

        Assert.Equal(ContinueRoundBaseAction.Use, decision.Action);
        Assert.Equal(LatestRef, decision.Base!.Ref);
    }

    [Fact]
    public void Refs_without_a_full_sha_or_outside_branches_are_not_candidates()
    {
        CardResultRef[] refs =
        [
            new(OlderRef, OlderSha, "run-older", Saved.AddHours(-1)),
            new("refs/heads/agent-studio/results/short", "abc123", "run-short", Saved),
            new("refs/pull/7/head", LatestSha, "run-pull", Saved),
        ];

        var decision = ContinueRoundBasePolicy.Decide(null, refs, null, startsFromClaim: true, Saved);

        Assert.Equal(OlderRef, decision.Base!.Ref);
    }

    [Fact]
    public void Collect_reads_run_envelopes_review_subjects_and_the_folder_subject()
    {
        var created = Saved.AddHours(-3);
        var run = new RunAttemptDto("run-latest", "AGT-1", "repo", null, AttemptLifecycleState.Completed, null, 2, 1,
            created, Saved.AddMinutes(-5), LatestSha, "done", null, [],
            new ImmutableResultEnvelope("repo", "run-latest", new string('0', 40), LatestSha, LatestRef, null, new string('c', 64)));
        var unsettled = run with { AttemptId = "run-open", ResultEnvelope = null };
        var subject = new ReviewSubjectDto("subject-1", "repo", OlderSha, "run-older", "req", "policy", [], created,
            ResultRef: OlderRef);
        var review = new ReviewAttemptDto("rev-1", "AGT-1", "repo", "run-older", null, subject,
            AttemptLifecycleState.Completed, null, 1, 1, created, Saved, ReviewTerminalOutcome.Pass,
            null, OlderSha, null, []);
        var projection = new AttemptAuthorityProjection("AGT-1", 1, run, null, review, [unsettled, run], [review], false);
        var folder = new ReviewSubjectRecord
        {
            TaskKey = "AGT-1",
            RunAttemptId = "run-folder",
            ResultSha = OlderSha,
            ResultRef = "agent-studio/salvage/AGT-1",
            ImmutableResultRef = OlderRef,
            CompletedAtUtc = new DateTimeOffset(created),
        };

        var refs = ContinueRoundBasePolicy.Collect(projection, folder);

        Assert.Equal(
            [LatestRef, OlderRef, "agent-studio/salvage/AGT-1", OlderRef],
            refs.Select(item => item.Ref));
        Assert.Equal(Saved.AddMinutes(-5), refs[0].RecordedAtUtc);
    }
}
