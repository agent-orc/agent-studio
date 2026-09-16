using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2819. <c>npm --prefix frontend run lint</c> had been red on <c>develop</c>
/// since 2026-08-19, so review step <c>verify-5</c> exited 1 for every delivery
/// and every remote review graded <c>ProductFailure</c>. These tests pin the
/// table that separates "the card broke it" from "it was already broken over
/// there".
/// </summary>
public sealed class ReviewFailureAttributionPolicyTests
{
    private const string BaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    // Exit-status comparison (lint, build): the exit codes are the whole story.
    [InlineData(ReviewBaselineModes.ExitStatus, 1, ReviewFailureOwner.IntegrationBranch)]
    [InlineData(ReviewBaselineModes.ExitStatus, 0, ReviewFailureOwner.Delivery)]
    // Failure-name comparison (tests): names decide, exit code breaks the tie.
    [InlineData(ReviewBaselineModes.TestFailures, 1, ReviewFailureOwner.IntegrationBranch)]
    [InlineData(ReviewBaselineModes.TestFailures, 0, ReviewFailureOwner.Tolerated)]
    public void A_failure_without_new_names_is_attributed_from_the_merge_base(
        string mode,
        int baselineExitCode,
        ReviewFailureOwner expected)
        => Assert.Equal(
            expected,
            ReviewFailureAttributionPolicy.Attribute(
                commandFailed: true,
                mode,
                BaseSha,
                baselineExitCode,
                newFailures: []));

    [Theory]
    [InlineData(ReviewBaselineModes.ExitStatus)]
    [InlineData(ReviewBaselineModes.TestFailures)]
    public void A_new_failure_name_belongs_to_the_delivery_even_on_a_red_branch(string mode)
        => Assert.Equal(
            ReviewFailureOwner.Delivery,
            ReviewFailureAttributionPolicy.Attribute(
                commandFailed: true,
                mode,
                BaseSha,
                baselineExitCode: 1,
                newFailures: ["Product.NewFailure"]));

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData(BaseSha, null)]
    public void Incomplete_baseline_evidence_fails_closed_to_the_delivery(
        string? baselineSha,
        int? baselineExitCode)
        => Assert.Equal(
            ReviewFailureOwner.Delivery,
            ReviewFailureAttributionPolicy.Attribute(
                commandFailed: true,
                ReviewBaselineModes.ExitStatus,
                baselineSha,
                baselineExitCode,
                newFailures: []));

    [Fact]
    public void A_passing_command_is_attributed_to_nobody()
        => Assert.Equal(
            ReviewFailureOwner.None,
            ReviewFailureAttributionPolicy.Attribute(
                commandFailed: false,
                ReviewBaselineModes.ExitStatus,
                BaseSha,
                baselineExitCode: 1,
                newFailures: []));

    [Fact]
    public void A_gate_that_was_not_planned_against_the_baseline_charges_the_delivery()
        => Assert.Equal(
            ReviewFailureOwner.Delivery,
            ReviewFailureAttributionPolicy.Attribute(
                Command(compareToBaseline: false),
                Evidence(exitCode: 1, baselineSha: BaseSha, baselineExitCode: 1)));

    /// <summary>
    /// The observed incident, end to end through the evidence overload: the lint
    /// gate fails on the card and on the merge base, with no test-failure names
    /// on either side. It is the branch's defect, not a product failure.
    /// </summary>
    [Fact]
    public void Agt_2819_regression_a_lint_gate_red_on_the_merge_base_is_an_integration_branch_defect()
    {
        var owner = ReviewFailureAttributionPolicy.Attribute(
            Command(compareToBaseline: true, mode: ReviewBaselineModes.ExitStatus),
            Evidence(exitCode: 1, baselineSha: BaseSha, baselineExitCode: 1));

        Assert.Equal(ReviewFailureOwner.IntegrationBranch, owner);
        Assert.NotEqual(ReviewFailureOwner.Delivery, owner);
    }

    [Fact]
    public void A_lint_gate_green_on_the_merge_base_is_the_delivery_s_own_failure()
        => Assert.Equal(
            ReviewFailureOwner.Delivery,
            ReviewFailureAttributionPolicy.Attribute(
                Command(compareToBaseline: true, mode: ReviewBaselineModes.ExitStatus),
                Evidence(exitCode: 1, baselineSha: BaseSha, baselineExitCode: 0)));

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(1, null, true)]
    [InlineData(0, "timeout", true)]
    [InlineData(-1, null, true)]
    public void Failure_detection_covers_signals_and_abnormal_exits(
        int exitCode,
        string? signal,
        bool expected)
        => Assert.Equal(
            expected,
            ReviewFailureAttributionPolicy.Failed(
                Evidence(exitCode, baselineSha: null, baselineExitCode: null) with { Signal = signal }));

    private static ReviewCommandDto Command(
        bool compareToBaseline,
        string mode = ReviewBaselineModes.TestFailures)
        => new(
            "verify-5",
            "lint",
            "sh",
            ["-lc", "npm --prefix frontend run lint"],
            CompareToBaseline: compareToBaseline,
            BaselineMode: mode);

    private static ReviewCommandEvidenceDto Evidence(
        int? exitCode,
        string? baselineSha,
        int? baselineExitCode)
        => new(
            "verify-5",
            "lint",
            "sh",
            ["-lc", "npm --prefix frontend run lint"],
            new string('b', 40),
            new string('b', 40),
            new string('t', 40),
            DateTime.UtcNow.AddSeconds(-30),
            DateTime.UtcNow,
            exitCode,
            null,
            new string('0', 64),
            new string('0', 64),
            BaselineSha: baselineSha,
            NewFailures: [],
            PreExistingFailures: [],
            BaselineExitCode: baselineExitCode);
}
