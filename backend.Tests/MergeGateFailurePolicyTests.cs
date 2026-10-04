using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MergeGateFailurePolicyTests
{
    private static MergeGateFailure Read(string name)
        => MergeGateFailurePolicy.ClassifyLog(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "merge-gate", name + ".log")));

    [Fact]
    public void Worker_crash_is_environment_and_never_human_review()
    {
        var failure = Read("worker-crash");
        Assert.Equal(MergeGateFailurePolicy.Environment, failure.Classification);
        Assert.Equal(MergeGateFailureRoute.Redeliver, MergeGateFailurePolicy.Route(failure));
        Assert.True(MergeGateFailurePolicy.WaitsInAutoReview(
            nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure), null));
    }

    [Fact]
    public void Budget_overrun_is_environment_redelivery()
        => Assert.Equal(MergeGateFailureRoute.Redeliver,
            MergeGateFailurePolicy.Route(Read("budget")));

    [Fact]
    public void Transport_failure_retries_the_gate()
        => Assert.Equal(MergeGateFailureRoute.RetryGate,
            MergeGateFailurePolicy.Route(Read("transport")));

    [Fact]
    public void Reproduced_test_failure_opens_a_fix_round_with_the_name()
    {
        var failure = Read("product");
        Assert.Equal(MergeGateFailurePolicy.Product, failure.Classification);
        Assert.Equal(MergeGateFailureRoute.FixRound, MergeGateFailurePolicy.Route(failure));
        Assert.Contains(failure.FailingItems, item => item.Contains("TestClockGuardTests", StringComparison.Ordinal));
    }

    [Fact]
    public void Red_integration_baseline_waits_for_a_branch_cause()
    {
        var failure = Read("branch");
        Assert.Equal(MergeGateFailurePolicy.IntegrationBranch, failure.Classification);
        Assert.Equal(MergeGateFailureRoute.WaitForCause, MergeGateFailurePolicy.Route(failure));
    }

    [Fact]
    public void Second_distinct_card_with_same_item_opens_cause_path()
    {
        var first = Read("product");
        var second = Read("second-card");
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(1, second.OtherCardMatches);
        Assert.Contains("AGT-2988", second.OtherCardKeys!);
        Assert.Equal(MergeGateFailureRoute.WaitForCause, MergeGateFailurePolicy.Route(second));
    }

    [Fact]
    public void Third_attempt_of_the_same_fingerprint_opens_cause_path()
    {
        var failure = Read("product") with { PriorMatches = 2 };
        Assert.Equal(MergeGateFailureRoute.WaitForCause, MergeGateFailurePolicy.Route(failure));
    }

    [Fact]
    public void Missing_or_conflicting_evidence_is_explicit_human_fallback()
    {
        foreach (var failure in new[] { Read("missing"), Read("conflict"), MergeGateFailurePolicy.ClassifyLog(null) })
        {
            Assert.Equal(MergeGateFailurePolicy.Undecidable, failure.Classification);
            Assert.Equal(MergeGateFailureRoute.HumanReview, MergeGateFailurePolicy.Route(failure));
            Assert.NotEmpty(failure.MissingEvidence);
        }
    }
}
