using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class ReviewFailureTaxonomyTests
{
    public static TheoryData<string, ReviewFailureClass> SeptemberSixInfrastructureFailures => new()
    {
        { "dotnet test ... violated gate-run budget (limit=1800000ms, consumed=1800488ms, phase=verification)", ReviewFailureClass.Infrastructure },
        { "dotnet test ... violated gate-run budget (limit=1800000ms, consumed=1800512ms, phase=verification)", ReviewFailureClass.Infrastructure },
        { "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds", ReviewFailureClass.Infrastructure },
        { "Delivery branch 'task/agt-2713' could not be fetched: git operation timed out after 30 seconds", ReviewFailureClass.Infrastructure },
        { "build-tests: 1 new failures: <unparsed failure in verify-2>", ReviewFailureClass.Infrastructure },
        { "AGT-2711 build-tests: 1 new failures: <unparsed failure in verify-2>", ReviewFailureClass.Infrastructure },
        { "QS-59 build-tests: 1 new failures: <unparsed failure in verify-2>", ReviewFailureClass.Infrastructure },
        { "QS-82 build-tests: 1 new failures: <unparsed failure in verify-2>", ReviewFailureClass.Infrastructure },
        { "QS-96 build-tests: 1 new failures: <unparsed failure in verify-2>", ReviewFailureClass.Infrastructure },
        { "LiveReviewIntegrationTests.CodexCanReviewSmallFile_WhenExplicitlyEnabled failed because the Codex weekly quota was exhausted", ReviewFailureClass.Quota },
        { "AGT-2721 completed out-of-band during a runner disconnect and reached Human Review without review", ReviewFailureClass.Infrastructure },
        { "AGT-2723 completed during the runner connectivity flap; review output is missing", ReviewFailureClass.Infrastructure },
    };

    [Theory]
    [MemberData(nameof(SeptemberSixInfrastructureFailures))]
    public void Recorded_overload_failures_are_never_product(
        string evidence,
        ReviewFailureClass expected)
    {
        var classified = ReviewFailureClassifier.Classify(evidence);

        Assert.Equal(expected, classified.FailureClass);
        Assert.NotEqual(ReviewFailureClass.Product, classified.FailureClass);
    }

    [Fact]
    public void Parsed_failing_test_is_product_and_keeps_its_name()
    {
        var classified = ReviewFailureClassifier.Classify(
            "Failed! - Failed: 1",
            ["AgentStudio.Tests.RealRegression_WhenValueChanges"]);

        Assert.Equal(ReviewFailureClass.Product, classified.FailureClass);
        Assert.Contains("RealRegression_WhenValueChanges", classified.Reason);
    }

    [Theory]
    [InlineData("  Failed AgentStudio.Tests.RealRegression [12 ms]", "AgentStudio.Tests.RealRegression")]
    [InlineData(" FAIL  src/math.spec.ts > arithmetic > changes value", "src/math.spec.ts > arithmetic > changes value")]
    public void Failing_test_parser_requires_a_named_test(string evidence, string expected)
        => Assert.Equal([expected], ReviewFailureClassifier.ParseFailingTests(evidence));

    [Fact]
    public void Unparsed_placeholder_is_not_a_parsed_test_failure()
    {
        var classified = ReviewFailureClassifier.Classify(
            "build-tests stopped",
            ["<unparsed failure in verify-2>"]);

        Assert.Equal(ReviewFailureClass.Unknown, classified.FailureClass);
        Assert.False(ReviewFailureClassifier.IsParsedFailureName("<unparsed failure in verify-2>"));
    }

    [Theory]
    [InlineData("pass", ReviewGradingOutcome.Pass)]
    [InlineData("concerns", ReviewGradingOutcome.PassWithConcerns)]
    [InlineData("block", ReviewGradingOutcome.ProductFailure)]
    [InlineData("fail", ReviewGradingOutcome.ProductFailure)]
    public void Aspect_mapping_is_table_driven(string status, ReviewGradingOutcome expected)
        => Assert.Equal(expected, ReviewGradingPolicy.Map([status]));

    [Fact]
    public void Infrastructure_command_overrides_aspect_concerns()
        => Assert.Equal(
            ReviewGradingOutcome.InfrastructureFailure,
            ReviewGradingPolicy.Map(["pass", "concerns"], ReviewFailureClass.Infrastructure));

    [Theory]
    [InlineData(0, 1, 30)]
    [InlineData(1, 2, 120)]
    [InlineData(2, 3, 300)]
    public void Infrastructure_retries_use_bounded_backoff(
        int retriesUsed,
        int expectedRetry,
        int expectedSeconds)
    {
        var now = new DateTime(2026, 9, 7, 4, 45, 0, DateTimeKind.Utc);

        var decision = ReviewFailureRetryPolicy.Decide(
            ReviewFailureClass.Infrastructure,
            retriesUsed,
            now);

        Assert.True(decision.Requeue);
        Assert.Equal(expectedRetry, decision.RetryNumber);
        Assert.Equal(now.AddSeconds(expectedSeconds), decision.RetryAtUtc);
    }

    [Fact]
    public void Quota_retry_waits_for_known_reset_and_stops_after_budget()
    {
        var now = new DateTime(2026, 9, 7, 4, 45, 0, DateTimeKind.Utc);
        var reset = now.AddHours(6);

        var retry = ReviewFailureRetryPolicy.Decide(
            ReviewFailureClass.Quota,
            retriesUsed: 0,
            now,
            quotaResetAtUtc: reset,
            loadGateReadyAtUtc: now.AddMinutes(5));
        var exhausted = ReviewFailureRetryPolicy.Decide(
            ReviewFailureClass.Quota,
            retriesUsed: 3,
            now);

        Assert.True(retry.Requeue);
        Assert.Equal(reset, retry.RetryAtUtc);
        Assert.False(exhausted.Requeue);
    }
}
