using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3056 (AGT-W65 D17 A): a fix round continues on the delivery the review
/// graded. Pure decisions for the base, its prompt sentence, and the claim-side
/// lifetime of the record.
/// </summary>
public sealed class ReviewFixRoundBaseTests : IDisposable
{
    // clock-independent: timestamps are inputs to pure base selection and timeline metadata.
    private static readonly DateTime Now = new(2026, 10, 13, 8, 30, 0, DateTimeKind.Utc);
    private static readonly string ReviewedSha = new('a', 40);
    private static readonly string ResultRef = Contract.FencedGitRefs.ImmutableResult("run_reviewed", 3, ReviewedSha);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fix-round-base-" + Guid.NewGuid().ToString("N"));

    public ReviewFixRoundBaseTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("refs/heads/agent-studio/results/run_1/fence-2/x", "refs/heads/agent-studio/results/run_1/fence-2/x")]
    [InlineData("agent-studio/results/run_1/fence-2/x", "refs/heads/agent-studio/results/run_1/fence-2/x")]
    [InlineData("origin/task/AGT-1", "refs/heads/task/AGT-1")]
    [InlineData("refs/tags/v1", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Decide_normalizes_the_reviewed_ref_to_a_branch_ref(string? resultRef, string? expected)
    {
        var record = ReviewFixRoundBase.Decide("rev_1", resultRef, ReviewedSha, Now);

        Assert.Equal(expected, record?.Ref);
        if (record is null) return;
        Assert.Equal(ReviewedSha, record.CommitSha);
        Assert.Equal("review-fix-round", record.Reason);
        Assert.Equal("rev_1", record.AttemptId);
        Assert.Equal(Now, record.SavedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc123")]
    [InlineData("not-a-sha-not-a-sha-not-a-sha-not-a-sha!")]
    public void Decide_without_a_full_reviewed_sha_records_no_base(string? sha)
        => Assert.Null(ReviewFixRoundBase.Decide("rev_1", ResultRef, sha, Now));

    [Fact]
    public void ForReview_prefers_the_subject_ref_and_falls_back_to_the_folder_subject_for_the_same_sha()
    {
        Assert.Equal(ResultRef, ReviewFixRoundBase.ForReview(Review(ResultRef), null, Now)!.Ref);

        var folder = Subject(ReviewedSha, immutableRef: ResultRef, resultRef: "refs/heads/runner/r/AGT-1");
        Assert.Equal(ResultRef, ReviewFixRoundBase.ForReview(Review(null), folder, Now)!.Ref);

        var otherDelivery = Subject(new string('b', 40), immutableRef: ResultRef);
        Assert.Null(ReviewFixRoundBase.ForReview(Review(null), otherDelivery, Now));
    }

    [Fact]
    public void ForOperatorContinue_is_a_fix_round_only_after_a_fresh_ProductFailure_without_another_base()
    {
        var subject = Subject(ReviewedSha, immutableRef: ResultRef);
        var failed = Review(ResultRef, ReviewTerminalOutcome.ProductFailure);

        Assert.Equal(ResultRef, ReviewFixRoundBase.ForOperatorContinue(failed, subject, null, Now)!.Ref);
        Assert.Null(ReviewFixRoundBase.ForOperatorContinue(
            Review(ResultRef, ReviewTerminalOutcome.Pass), subject, null, Now));
        Assert.Null(ReviewFixRoundBase.ForOperatorContinue(null, subject, null, Now));
        Assert.Null(ReviewFixRoundBase.ForOperatorContinue(
            failed, Subject(new string('c', 40), immutableRef: null), null, Now));
        Assert.Null(ReviewFixRoundBase.ForOperatorContinue(failed, null, null, Now));
        var salvage = new ContinuationBaseRecord("refs/heads/salvage", new string('d', 40), "operator-sweep-salvage", "run_x", Now);
        Assert.Null(ReviewFixRoundBase.ForOperatorContinue(failed, subject, salvage, Now));
    }

    [Fact]
    public void Operator_continue_replaces_a_stale_review_base_with_the_current_reviewed_delivery()
    {
        var stale = ReviewFixRoundBase.Decide("rev_old", "refs/heads/pruned", new string('b', 40), Now)!;
        var replacement = ReviewFixRoundBase.ForOperatorContinue(
            Review(ResultRef, ReviewTerminalOutcome.ProductFailure),
            Subject(ReviewedSha, ResultRef), stale, Now);

        Assert.NotNull(replacement);
        Assert.Equal(ResultRef, replacement.Ref);
        Assert.Equal(ReviewedSha, replacement.CommitSha);
        Assert.Equal("rev_1", replacement.AttemptId);
    }

    [Fact]
    public void The_prompt_sentence_names_the_base_in_one_sentence_and_no_sha()
    {
        foreach (var continues in new[] { true, false })
        {
            var sentence = ReviewFixRoundBase.PromptSentence(continues);
            Assert.Single(sentence.Split(". ", StringSplitOptions.RemoveEmptyEntries));
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
            Assert.DoesNotMatch("[0-9a-f]{7,}", sentence);
            Assert.DoesNotContain("checkout ", sentence, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("delivery the review graded", ReviewFixRoundBase.PromptSentence(true), StringComparison.Ordinal);
        Assert.Contains("integration branch", ReviewFixRoundBase.PromptSentence(false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_finding_round_prompt_states_its_base_and_keeps_the_findings_contract()
    {
        var text = V1ReviewPlaneEndpoints.BuildRemoteFindingFollowUp(
            [new Contract.ReviewFollowUpFinding("code-quality", "block", "A defect remains.")], "rev_1", true);

        Assert.Contains(ReviewFixRoundBase.PromptSentence(true), text, StringComparison.Ordinal);
        Assert.Contains("Fix exactly the blocking findings below and nothing else.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReviewedSha, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fix_round_base_survives_its_claim_until_a_run_completes()
    {
        var record = ReviewFixRoundBase.Decide("rev_1", ResultRef, ReviewedSha, Now)!;
        Assert.True(ContinuationBaseStore.Save(_folder, record));

        Assert.Equal(ResultRef, ContinuationBaseStore.ConsumeForClaim(_folder)?.Ref);
        // A claim that fails before the agent starts must find it again.
        Assert.Equal(ResultRef, ContinuationBaseStore.ConsumeForClaim(_folder)?.Ref);

        ContinuationBaseStore.ClearAfterCompletion(_folder);
        Assert.Null(ContinuationBaseStore.Read(_folder));
    }

    [Fact]
    public void Replacing_a_base_for_the_same_review_records_the_new_choice_once()
    {
        var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
        var oldBase = ReviewFixRoundBase.Decide("rev_1", "refs/heads/retired", ReviewedSha, Now)!;
        var newBase = ReviewFixRoundBase.Decide("rev_1", ResultRef, ReviewedSha, Now)!;
        ReviewFixRoundBase.RecordOnTimeline(timeline, _folder, oldBase, ReviewFixRoundBase.SourceOperatorContinue, Now);
        ReviewFixRoundBase.RecordOnTimeline(timeline, _folder, newBase, ReviewFixRoundBase.SourceOperatorContinue, Now);
        ReviewFixRoundBase.RecordOnTimeline(timeline, _folder, newBase, ReviewFixRoundBase.SourceOperatorContinue, Now);
        var rows = timeline.ReadAll(_folder).Where(row => row.Kind == TimelineEventKinds.ContinuationBaseRecorded).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(ResultRef, rows[1].Details!["ref"]);
    }

    [Fact]
    public void Other_bases_keep_todays_single_use_claim()
    {
        var salvage = new ContinuationBaseRecord(
            "refs/heads/agent-studio/salvage/x", ReviewedSha, LostWorkerContinuationPolicy.ContinuationReason, "run_1", Now);
        Assert.True(ContinuationBaseStore.Save(_folder, salvage));
        ContinuationBaseStore.ClearAfterCompletion(_folder);
        Assert.NotNull(ContinuationBaseStore.Read(_folder));

        Assert.Equal(salvage.Ref, ContinuationBaseStore.ConsumeForClaim(_folder)?.Ref);
        Assert.Null(ContinuationBaseStore.Read(_folder));
    }

    private static ReviewAttemptDto Review(string? resultRef, ReviewTerminalOutcome? outcome = null)
        => new(
            "rev_1", "AGT-1", "repo", "run_reviewed", null,
            new ReviewSubjectDto("subject", "repo", ReviewedSha, "run_reviewed", "req", "policy", [], Now,
                ResultRef: resultRef),
            AttemptLifecycleState.Completed, null, 1, 1, Now, Now, outcome, null, ReviewedSha, null, []);

    private static ReviewSubjectRecord Subject(string sha, string? immutableRef, string? resultRef = null)
        => new()
        {
            TaskKey = "AGT-1",
            RunAttemptId = "run_reviewed",
            AttemptChainId = "chain",
            ResultSha = sha,
            ImmutableResultRef = immutableRef,
            ResultRef = resultRef,
        };
}
