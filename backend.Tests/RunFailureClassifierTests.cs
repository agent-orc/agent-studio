using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2749: the 2026-09-06 overload night parked thirteen cards in Human
/// Review as product failures. Twelve of them are host or account faults
/// replayed here verbatim from the incident log; the thirteenth (AGT-2706) was
/// never a failure at all - a `concerns` aspect graded as `ProductFailure` - and
/// is covered separately by <c>ReviewGradingPolicyTests</c>.
/// </summary>
public sealed class RunFailureClassifierTests
{
    public static TheoryData<string, string, RunFailureClass, string> SeptemberSixIncidents => new()
    {
        {
            "AGT-2707",
            "dotnet test ... violated gate-run budget (limit=1800000ms, consumed=1800488ms, phase=verification)",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.GateBudgetExceeded
        },
        {
            "AGT-2710",
            "dotnet test ... violated gate-run budget (limit=1800000ms, consumed=1800512ms, phase=verification)",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.GateBudgetExceeded
        },
        {
            "AGT-2708",
            "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.GitNetworkTimeout
        },
        {
            "AGT-2713",
            "Delivery branch 'task/agt-2713' could not be fetched: git operation timed out after 30 seconds",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.GitNetworkTimeout
        },
        {
            "AGT-2709",
            "build-tests: 1 new failures: <unparsed failure in verify-2>",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.UnparsedTestOutput
        },
        {
            "AGT-2711",
            "build-tests: 1 new failures: <unparsed failure in verify-2>",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.UnparsedTestOutput
        },
        {
            "QS-59",
            "build-tests: 1 new failures: <unparsed failure in verify-2>",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.UnparsedTestOutput
        },
        {
            "QS-82",
            "build-tests: 1 new failures: <unparsed failure in verify-3>",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.UnparsedTestOutput
        },
        {
            "QS-96",
            "build-tests: 1 new failures: <unparsed failure in verify-2>",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.UnparsedTestOutput
        },
        {
            "QS-89",
            "LiveReviewIntegrationTests.CodexCanReviewSmallFile_WhenExplicitlyEnabled failed because the Codex weekly quota was exhausted",
            RunFailureClass.Quota,
            RunFailureSignatures.CliQuotaExhausted
        },
        {
            "AGT-2721",
            "AGT-2721 completed out-of-band during a runner connectivity flap and reached Human Review without any review.",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.RunnerDisconnected
        },
        {
            "AGT-2723",
            "AGT-2723 completed during the runner connectivity flap; review output is missing.",
            RunFailureClass.Infrastructure,
            RunFailureSignatures.RunnerDisconnected
        },
    };

    [Theory]
    [MemberData(nameof(SeptemberSixIncidents))]
    public void Recorded_overload_failures_are_never_product(
        string cardId,
        string evidence,
        RunFailureClass expectedClass,
        string expectedSignature)
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence { Text = evidence });

        Assert.True(
            expectedClass == classified.Class,
            $"{cardId}: expected {expectedClass} but got {classified.Class} ({classified.Signature}: {classified.Detail})");
        Assert.Equal(expectedSignature, classified.Signature);
        Assert.NotEqual(RunFailureClass.Product, classified.Class);
    }

    [Fact]
    public void Replaying_the_2026_09_06_incidents_yields_twelve_infrastructure_or_quota_and_zero_product()
    {
        var verdicts = SeptemberSixIncidents
            .Select(row => RunFailureClassifier.Classify(new RunFailureEvidence { Text = (string)row[1]! }))
            .ToList();

        Assert.Equal(12, verdicts.Count);
        Assert.All(verdicts, verdict => Assert.NotEqual(RunFailureClass.Product, verdict.Class));
        Assert.All(
            verdicts,
            verdict => Assert.True(verdict.Class is RunFailureClass.Infrastructure or RunFailureClass.Quota));
    }

    [Fact]
    public void Parsed_failing_test_is_product_and_survives_a_timeout_mention_in_the_same_log()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Failed! - Failed: 1, Passed: 40. The suite timed out after retrying once.",
            ParsedTestFailures = 1,
        });

        Assert.Equal(RunFailureClass.Product, classified.Class);
        Assert.Equal(RunFailureSignatures.NewTestFailures, classified.Signature);
    }

    [Fact]
    public void Reviewer_blocking_verdict_on_the_diff_is_product()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "aspect 'requirement-fit' returned block",
            ReviewerBlockedDiff = true,
        });

        Assert.Equal(RunFailureClass.Product, classified.Class);
        Assert.Equal(RunFailureSignatures.ReviewFinding, classified.Signature);
    }

    [Fact]
    public void Compiler_error_is_product()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Program.cs(12,5): error CS0103: The name 'foo' does not exist in the current context",
        });

        Assert.Equal(RunFailureClass.Product, classified.Class);
        Assert.Equal(RunFailureSignatures.CompilerError, classified.Signature);
    }

    [Fact]
    public void Quota_probe_fact_outranks_ambiguous_text()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "generic failure with no recognizable signature",
            QuotaExhausted = true,
        });

        Assert.Equal(RunFailureClass.Quota, classified.Class);
    }

    [Fact]
    public void Exit_nonzero_with_no_parsed_failures_on_a_test_command_is_infrastructure()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "dotnet test exited abnormally",
            ExitCode = 1,
            ExpectsTestResults = true,
        });

        Assert.Equal(RunFailureClass.Infrastructure, classified.Class);
        Assert.Equal(RunFailureSignatures.UnparsedTestOutput, classified.Signature);
    }

    [Fact]
    public void Unrecognized_failure_stays_unknown_and_is_not_requeueable()
    {
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Something unexpected happened.",
        });

        Assert.Equal(RunFailureClass.Unknown, classified.Class);
        Assert.False(classified.Requeueable);
    }

    [Fact]
    public void Infrastructure_and_quota_verdicts_are_requeueable()
    {
        Assert.True(new RunFailureVerdict(RunFailureClass.Infrastructure, "x", "x").Requeueable);
        Assert.True(new RunFailureVerdict(RunFailureClass.Quota, "x", "x").Requeueable);
        Assert.False(new RunFailureVerdict(RunFailureClass.Product, "x", "x").Requeueable);
        Assert.False(new RunFailureVerdict(RunFailureClass.Unknown, "x", "x").Requeueable);
    }

    [Fact]
    public void Unparsed_failure_placeholder_is_recognized()
    {
        Assert.True(RunFailureClassifier.IsUnparsedFailurePlaceholder("<unparsed failure in verify-2>"));
        Assert.False(RunFailureClassifier.IsUnparsedFailurePlaceholder("AgentStudio.Tests.RealRegression"));
        Assert.False(RunFailureClassifier.IsUnparsedFailurePlaceholder(null));
    }
}
