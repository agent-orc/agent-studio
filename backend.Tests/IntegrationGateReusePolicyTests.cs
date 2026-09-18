using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2839: the local integration gate may stand on the Remote Review verdict
/// only when the merge lands on the exact base the review verified. These are
/// direct matrix tests of the pure decision - one row per way the reuse can be
/// refused, plus the single row that grants it.
/// </summary>
public sealed class IntegrationGateReusePolicyTests
{
    private const string Base = "1111111111111111111111111111111111111111";
    private const string MovedBase = "2222222222222222222222222222222222222222";
    private const string Result = "3333333333333333333333333333333333333333";

    [Fact]
    public void Unchanged_tip_reuses_the_review_verdict_and_names_the_attempt()
    {
        var decision = IntegrationGateReusePolicy.Decide(Input());

        Assert.True(decision.Reused);
        Assert.Equal("reused", decision.Token);
        Assert.Equal("rev_1", decision.ReviewAttemptId);
        Assert.Contains("unchanged develop tip", decision.Reason);
        Assert.Contains(Base[..8], decision.Reason);
    }

    [Fact]
    public void Moved_tip_with_unchanged_merge_base_runs_the_full_gate_and_names_both_tips()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { PreMergeTipSha = MovedBase });

        Assert.False(decision.Reused);
        Assert.Equal("full", decision.Token);
        Assert.Contains("develop moved since the review", decision.Reason);
        Assert.Contains(MovedBase[..8], decision.Reason);
        Assert.Contains(Base[..8], decision.Reason);
    }

    [Fact]
    public void Review_report_without_a_merge_base_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { MergeBaseSha = null } });

        Assert.False(decision.Reused);
        Assert.Equal("the Remote Review report records no merge base", decision.Reason);
    }

    [Fact]
    public void Review_report_without_an_integration_ref_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { IntegrationRef = null } });

        Assert.False(decision.Reused);
        Assert.Equal("the Remote Review report records no integration ref", decision.Reason);
    }

    [Fact]
    public void Legacy_proof_without_an_integration_tip_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { IntegrationTipSha = null } });
        Assert.False(decision.Reused);
        Assert.Contains("records no integration tip", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Base)]
    public void A_missing_or_different_tested_tree_runs_the_full_gate(string? testedTree)
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { TestedTreeSha = testedTree } });
        Assert.False(decision.Reused);
        Assert.Contains("tree", decision.Reason);
    }

    [Fact]
    public void Conflict_resolution_runs_the_full_gate_even_with_identical_tip_and_tree()
    {
        var decision = IntegrationGateReusePolicy.Decide(Input() with { ConflictsResolved = true });
        Assert.False(decision.Reused);
        Assert.Contains("conflict resolution", decision.Reason);
    }

    [Fact]
    public void Missing_review_record_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(Input() with { Review = null });

        Assert.False(decision.Reused);
        Assert.Null(decision.ReviewAttemptId);
        Assert.Contains("no settled Remote Review verification", decision.Reason);
    }

    [Fact]
    public void Disabled_setting_runs_the_full_gate_even_on_an_unchanged_base()
    {
        var decision = IntegrationGateReusePolicy.Decide(Input() with { Enabled = false });

        Assert.False(decision.Reused);
        Assert.Contains("project setting", decision.Reason);
    }

    [Theory]
    [InlineData("ProductFailure")]
    [InlineData("Inconclusive")]
    public void A_review_that_did_not_pass_runs_the_full_gate(string outcome)
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { Outcome = outcome } });

        Assert.False(decision.Reused);
        Assert.Contains(outcome, decision.Reason);
    }

    [Theory]
    [InlineData(ReviewBuildTestGateClasses.NotApplicable)]
    [InlineData(ReviewBuildTestGateClasses.Failed)]
    public void A_review_without_a_green_build_test_gate_runs_the_full_gate(string gateClass)
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { BuildTestGate = gateClass } });

        Assert.False(decision.Reused);
        Assert.Contains("no verdict to reuse", decision.Reason);
    }

    [Fact]
    public void A_review_of_another_integration_line_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { IntegrationRef = "refs/heads/main" } });

        Assert.False(decision.Reused);
        Assert.Contains("compared against 'main', this merge targets 'develop'", decision.Reason);
    }

    [Fact]
    public void A_merge_result_without_the_reviewed_delivery_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { DeliveryContainedInMergeResult = false });

        Assert.False(decision.Reused);
        Assert.Contains("does not contain the reviewed delivery", decision.Reason);
    }

    [Fact]
    public void A_mechanically_replayed_delivery_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { DeliveryWasReplayed = true });

        Assert.False(decision.Reused);
        Assert.Contains("mechanically replayed", decision.Reason);
    }

    [Fact]
    public void An_underivable_merge_base_runs_the_full_gate()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { PreMergeTipSha = null });

        Assert.False(decision.Reused);
        Assert.Contains("could not be determined", decision.Reason);
    }

    [Fact]
    public void An_abbreviated_ref_spelling_of_the_same_branch_still_reuses()
    {
        var decision = IntegrationGateReusePolicy.Decide(
            Input() with { Review = Review() with { IntegrationRef = "origin/develop" } });

        Assert.True(decision.Reused);
    }

    [Theory]
    // Explicit override wins over the execution-placement default in both directions.
    [InlineData(true, ExecutionLocations.Local, true)]
    [InlineData(false, "agent-runner-01", false)]
    // No override: a remotely executing project gets a Remote Review to reuse,
    // a locally executing one never does.
    [InlineData(null, "agent-runner-01", true)]
    [InlineData(null, ExecutionLocations.Local, false)]
    public void IsEnabled_resolves_the_override_before_the_execution_placement_default(
        bool? configured,
        string executionLocation,
        bool expected)
    {
        var settings = new ProjectSettings
        {
            IntegrationGateReviewReuse = configured,
            ExecutionLocation = executionLocation,
        };

        Assert.Equal(expected, IntegrationGateReusePolicy.IsEnabled(settings));
    }

    [Fact]
    public void IsEnabled_without_settings_keeps_the_full_gate()
        => Assert.False(IntegrationGateReusePolicy.IsEnabled(null));

    private static IntegrationGateReuseInput Input() => new(
        Enabled: true,
        Review: Review(),
        IntegrationBranch: "develop",
        PreMergeTipSha: Base,
        DeliveryContainedInMergeResult: true,
        DeliveryWasReplayed: false,
        MergeResultTreeSha: Result);

    private static ReviewVerificationRecord Review() => new()
    {
        TaskKey = "AGT-1",
        AttemptId = "rev_1",
        SubjectId = "subj_1",
        Outcome = "Pass",
        ResultSha = Result,
        IntegrationRef = "refs/heads/develop",
        MergeBaseSha = Base,
        IntegrationTipSha = Base,
        TestedTreeSha = Result,
        BuildTestGate = ReviewBuildTestGateClasses.Passed,
        VerifiedAtUtc = DateTimeOffset.UtcNow,
    };
}
