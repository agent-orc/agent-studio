using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for <see cref="ReviewAttemptChainSummary"/>, the policy behind
/// AGT-2220: a park/escalation summary that counted attempts by frequency
/// reported "4 attempts, all ReviewInfra/BaselineUnavailable" and concluded
/// "infrastructure problem, no finding against the card content", while a
/// fifth, younger attempt classified <c>ShaMismatch</c> was the actual hard
/// blocker. The rules pinned here: the newest attempt is always named, the
/// classification enumeration is complete and dated, divergent attempts are
/// called out, and the operator options follow the NEWEST cause.
/// </summary>
public sealed class ReviewAttemptChainSummaryTests
{
    private static readonly DateTime Day = new(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);

    private static ReviewAttemptChainEntry Infra(
        string id, int startHour, int startMinute, int endHour, int endMinute,
        string classification, string? reason = null) =>
        new(
            id,
            Day.AddHours(startHour).AddMinutes(startMinute),
            Day.AddHours(endHour).AddMinutes(endMinute),
            ReviewTerminalOutcome.InfrastructureFailure,
            classification,
            reason);

    /// <summary>The AGT-2220 chain: four baseline faults and one younger,
    /// harder ShaMismatch.</summary>
    private static List<ReviewAttemptChainEntry> Agt2220Chain() =>
    [
        Infra("review_aaaa", 18, 10, 18, 30, "BaselineUnavailable"),
        Infra("review_bbbb", 19, 0, 19, 20, "BaselineUnavailable"),
        Infra("review_cccc", 20, 0, 20, 25, "BaselineUnavailable"),
        Infra("review_dddd", 21, 0, 21, 12, "BaselineUnavailable"),
        Infra(
            "review_97ba5034e235409d8887a78668fbb48b", 21, 45, 22, 8, "ShaMismatch",
            "Materialized HEAD '744deb892' does not match expected Result-SHA 'f538f896'."),
    ];

    [Fact]
    public void Four_baseline_attempts_and_one_younger_sha_mismatch_are_summarized_by_the_youngest()
    {
        var summary = ReviewAttemptChainSummary.Build(Agt2220Chain());

        Assert.Equal("review_97ba5034e235409d8887a78668fbb48b", summary.NewestAttempt!.AttemptId);
        Assert.Equal("ReviewInfra/ShaMismatch", summary.NewestAttempt.Classification);

        // The one-line reason that reaches the decision journal and the board.
        Assert.Contains("review_97ba5034e235409d8887a78668fbb48b", summary.Headline);
        Assert.Contains("ReviewInfra/ShaMismatch", summary.Headline);
        Assert.Contains("2026-07-28 22:08 UTC", summary.Headline);
        Assert.Contains("Materialized HEAD", summary.Headline);
        // The exact false conclusion AGT-2220 drew must be unreachable.
        Assert.DoesNotContain("All 5 attempts carry", summary.Headline);
        Assert.Contains("Divergent chain", summary.Headline);
    }

    [Fact]
    public void Classification_enumeration_is_complete_and_dated()
    {
        var summary = ReviewAttemptChainSummary.Build(Agt2220Chain());

        Assert.True(summary.HasDivergentClassifications);
        Assert.Equal(
            ["ReviewInfra/ShaMismatch", "ReviewInfra/BaselineUnavailable"],
            summary.Classifications.Select(group => group.Classification).ToArray());

        var mismatch = summary.Classifications[0];
        Assert.Equal(1, mismatch.Count);
        Assert.Equal(Day.AddHours(22).AddMinutes(8), mismatch.FirstObservedAt);
        Assert.Equal(Day.AddHours(22).AddMinutes(8), mismatch.LastObservedAt);

        var baseline = summary.Classifications[1];
        Assert.Equal(4, baseline.Count);
        Assert.Equal(Day.AddHours(18).AddMinutes(30), baseline.FirstObservedAt);
        Assert.Equal(Day.AddHours(21).AddMinutes(12), baseline.LastObservedAt);
        Assert.Equal(
            ["review_dddd", "review_cccc", "review_bbbb", "review_aaaa"],
            baseline.AttemptIds.ToArray());

        // Rendered: every distinct class appears with its own dated row, and no
        // attempt is folded away into a majority class.
        var detail = summary.Detail;
        Assert.Contains("- Failure classifications (2 distinct, complete, newest first):", detail);
        Assert.Contains("  - ReviewInfra/ShaMismatch: 1 attempt, 2026-07-28 22:08 UTC", detail);
        Assert.Contains(
            "  - ReviewInfra/BaselineUnavailable: 4 attempts, 2026-07-28 18:30 UTC to 2026-07-28 21:12 UTC",
            detail);
        foreach (var attempt in Agt2220Chain())
            Assert.Contains(attempt.AttemptId, detail);
    }

    [Fact]
    public void Attempts_that_diverge_from_the_newest_classification_are_highlighted()
    {
        var summary = ReviewAttemptChainSummary.Build(Agt2220Chain());

        Assert.Equal(4, summary.DivergentAttempts.Count);
        Assert.All(
            summary.DivergentAttempts,
            attempt => Assert.Equal("ReviewInfra/BaselineUnavailable", attempt.Classification));

        var detail = summary.Detail;
        Assert.Contains(
            "- Divergent attempts: 4 of 5 attempts are classified differently from the newest cause "
            + "ReviewInfra/ShaMismatch; decide on the newest cause, not on the most frequent one.",
            detail);
        Assert.Contains("  - 2026-07-28 21:12 UTC review_dddd ReviewInfra/BaselineUnavailable", detail);
    }

    [Fact]
    public void Operator_options_follow_the_newest_cause_not_the_most_frequent_one()
    {
        var summary = ReviewAttemptChainSummary.Build(Agt2220Chain());

        Assert.Contains(
            summary.Options,
            option => option.Contains("Result-SHA was not the materialized HEAD", StringComparison.Ordinal));
        Assert.Contains(
            summary.Options,
            option => option.Contains("re-merge this subject unchanged", StringComparison.Ordinal));
        // The remedy for the four older baseline faults must not be offered as
        // the answer to a ShaMismatch: it cannot fix it (the AGT-2220 counter-check).
        Assert.DoesNotContain(
            summary.Options,
            option => option.Contains("Restore the baseline ref", StringComparison.Ordinal));
        Assert.Contains(
            "- Operator options for the newest cause ReviewInfra/ShaMismatch:",
            summary.Detail);
    }

    [Fact]
    public void A_baseline_only_chain_still_gets_the_baseline_options()
    {
        var summary = ReviewAttemptChainSummary.Build(
            Agt2220Chain().Where(attempt => attempt.FailureClassification == "BaselineUnavailable"));

        Assert.False(summary.HasDivergentClassifications);
        Assert.Empty(summary.DivergentAttempts);
        Assert.Contains("All 4 attempts carry this classification.", summary.Headline);
        Assert.Contains(
            summary.Options,
            option => option.Contains("Restore the baseline ref", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordering_is_by_observed_evidence_so_a_late_settling_attempt_wins()
    {
        // Created first, settled last: the newest CAUSE is the newest evidence,
        // not the newest attempt record.
        var summary = ReviewAttemptChainSummary.Build(
        [
            Infra("review_slow", 18, 0, 23, 0, "ShaMismatch"),
            Infra("review_fast", 20, 0, 20, 5, "BaselineUnavailable"),
        ]);

        Assert.Equal("review_slow", summary.NewestAttempt!.AttemptId);
    }

    [Fact]
    public void Superseded_and_open_attempts_never_become_the_newest_cause()
    {
        var summary = ReviewAttemptChainSummary.Build(
        [
            Infra("review_real", 20, 0, 20, 30, "ShaMismatch"),
            new(
                "review_revoked", Day.AddHours(21), Day.AddHours(21),
                ReviewTerminalOutcome.Superseded, null, "Card left auto review."),
            new("review_open", Day.AddHours(22), null, null, null, null),
        ]);

        Assert.Equal("review_real", summary.NewestAttempt!.AttemptId);
        Assert.Single(summary.GradedAttempts);
        Assert.Equal(2, summary.UngradedCount);
        Assert.Contains(
            "- Not graded: 2 attempt(s) still open or superseded; they carry no review verdict.",
            summary.Detail);
    }

    [Fact]
    public void A_product_failure_newest_cause_is_named_as_a_finding_not_as_infrastructure()
    {
        var summary = ReviewAttemptChainSummary.Build(
        [
            Infra("review_infra", 18, 0, 18, 20, "BaselineUnavailable"),
            new(
                "review_grade", Day.AddHours(19), Day.AddHours(19).AddMinutes(40),
                ReviewTerminalOutcome.ProductFailure, null, "Acceptance test 3 fails."),
        ]);

        Assert.Equal("ProductFailure", summary.NewestAttempt!.Classification);
        Assert.Contains(
            summary.Options,
            option => option.Contains("finding against the card content", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chain_longer_than_the_listing_cap_states_the_omission_and_keeps_the_enumeration_complete()
    {
        var attempts = Enumerable
            .Range(0, ReviewAttemptChainSummary.MaxListedAttempts + 3)
            .Select(index => Infra($"review_{index:D2}", 0, index, 0, index + 1, "BaselineUnavailable"))
            .Append(Infra("review_newest", 23, 0, 23, 30, "ShaMismatch"))
            .ToList();

        var summary = ReviewAttemptChainSummary.Build(attempts);
        var detail = summary.Detail;

        Assert.Contains("older attempts omitted from this listing", detail);
        Assert.Contains(
            $"  - ReviewInfra/BaselineUnavailable: {ReviewAttemptChainSummary.MaxListedAttempts + 3} attempts,",
            detail);
    }

    [Fact]
    public void An_empty_chain_is_honest_instead_of_claiming_a_cause()
    {
        var summary = ReviewAttemptChainSummary.Build(Array.Empty<ReviewAttemptChainEntry>());

        Assert.Null(summary.NewestAttempt);
        Assert.Empty(summary.Classifications);
        Assert.Equal("No graded review attempt exists for this subject.", summary.Headline);
        Assert.Contains("- Review attempt chain: No graded review attempt exists", summary.Detail);
    }

    // AGT-3690 (S5-F14): a chain the review host lost by restarting names the
    // restarts, host and time, instead of claiming the subject cannot be
    // materialized.
    [Fact]
    public void A_chain_lost_to_review_host_restarts_names_every_restart()
    {
        var attempts = new[]
        {
            Infra("review_r1", 1, 26, 1, 27, "ExecutorRestarted") with { HostId = "lab-review-host" },
            Infra("review_r2", 1, 28, 1, 31, "ExecutorRestarted") with { HostId = "lab-review-host" },
            Infra("review_r3", 1, 34, 1, 40, "ExecutorRestarted"),
        };

        var summary = ReviewAttemptChainSummary.Build(attempts);

        Assert.Equal(
            "The review was lost to 3 review host restarts: "
            + "lab-review-host 2026-07-28 01:27 UTC (review_r1); "
            + "lab-review-host 2026-07-28 01:31 UTC (review_r2); "
            + "2026-07-28 01:40 UTC (review_r3). "
            + "The automatic retry budget of 3 is spent; no review check failed against the card.",
            summary.HostRestartSentence(3));
        Assert.Contains("Find out why the review host restarted", summary.Detail);
        Assert.DoesNotContain("cannot be materialized", summary.Detail);
    }

    [Fact]
    public void A_chain_whose_newest_cause_is_not_a_restart_has_no_restart_sentence()
    {
        var attempts = new[]
        {
            Infra("review_r1", 1, 26, 1, 27, "ExecutorRestarted"),
            Infra("review_sha", 2, 0, 2, 5, "ShaMismatch"),
        };

        Assert.Null(ReviewAttemptChainSummary.Build(attempts).HostRestartSentence(3));
    }
}
