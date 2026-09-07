using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Table coverage for the gate and review failure classifier.
/// <para>
/// The fixtures are the recorded failure text of the overload night of
/// 06.09.2026, when thirteen cards were parked in <c>5-human-review</c> as
/// broken product. Twelve of them had failed for a host reason. Replaying them
/// through the classifier is the acceptance criterion for AGT-2749, so the
/// strings below are copied verbatim from the logs rather than paraphrased: a
/// paraphrase would prove that the classifier matches a paraphrase.
/// </para>
/// </summary>
public sealed class GateFailureClassifierTests
{
    /// <summary>
    /// The thirteen recorded failures. Each entry is (card, class, signature,
    /// reason text). Twelve are infrastructure or quota; only the last one is a
    /// genuine product failure, and it is not from that night.
    /// </summary>
    public static TheoryData<string, GateFailureClass, string, string> RecordedFailures() => new()
    {
        // Budget overrun: the suite needed 488 ms more than the 30-minute cap.
        {
            "AGT-2707", GateFailureClass.Infrastructure, GateFailureSignatures.GateRunBudgetExceeded,
            "dotnet test agent-taskboard.sln --configuration Release violated gate-run budget "
            + "(limit=1800000ms, consumed=1800488ms, phase=verification)"
        },
        {
            "AGT-2710", GateFailureClass.Infrastructure, GateFailureSignatures.GateRunBudgetExceeded,
            "dotnet test agent-taskboard.sln --configuration Release violated gate-run budget "
            + "(limit=1800000ms, consumed=1800271ms, phase=verification)"
        },

        // The 30-second network cap of GitNetworkProcessRunner.DefaultTimeout.
        {
            "AGT-2708", GateFailureClass.Infrastructure, GateFailureSignatures.GitNetworkTimeout,
            "Integration branch 'develop' could not be fetched from origin: "
            + "git operation timed out after 30 seconds"
        },
        {
            "AGT-2713", GateFailureClass.Infrastructure, GateFailureSignatures.GitNetworkTimeout,
            "Delivery branch 'agent/AGT-2713' could not be fetched from origin: "
            + "git operation timed out after 30 seconds"
        },

        // The verification step produced no parsable test result, and the grader
        // counted "unparsed" as a new failing test.
        {
            "AGT-2709", GateFailureClass.Infrastructure, GateFailureSignatures.UnparsedTestOutcome,
            "build-tests: 1 new failures: <unparsed failure in verify-2>"
        },
        {
            "AGT-2711", GateFailureClass.Infrastructure, GateFailureSignatures.UnparsedTestOutcome,
            "build-tests: 1 new failures: <unparsed failure in verify-2>"
        },
        {
            "QS-82", GateFailureClass.Infrastructure, GateFailureSignatures.UnparsedTestOutcome,
            "build-tests: 1 new failures: <unparsed failure in verify-3>"
        },
        {
            "QS-96", GateFailureClass.Infrastructure, GateFailureSignatures.UnparsedTestOutcome,
            "build-tests: 1 new failures: <unparsed failure in verify-3>"
        },

        // Same night, same root cause, seen one layer down: the private /tmp was
        // unmounted under the still-running worker, so the MSBuild node could
        // not open its pipe and no test ever ran.
        {
            "QS-59", GateFailureClass.Infrastructure, GateFailureSignatures.MsBuildNodeUnavailable,
            "MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.\n"
            + "System.Net.Sockets.SocketException (99): Cannot assign requested address\n"
            + "   at System.Net.Sockets.Socket.DoBind(EndPoint endPointSnapshot, SocketAddress socketAddress)\n"
            + "   at Microsoft.Build.Execution.OutOfProcNode.Run(Exception& shutdownException)"
        },

        // NuGet's migration mutex uses mkdtemp("/tmp/.dotnet.XXXXXX"), which
        // fails the same way once /tmp is gone.
        {
            "AGT-2716", GateFailureClass.Infrastructure, GateFailureSignatures.TempDirectoryUnmounted,
            "PreparationFailed: dotnet restore failed: "
            + "mkdtemp(\"/tmp/.dotnet.XXXXXX\") failed with ENOENT (No such file or directory)"
        },

        // A live-CLI test is not a product regression when the provider quota is
        // gone.
        {
            "QS-89", GateFailureClass.Quota, GateFailureSignatures.CliQuotaExhausted,
            "LiveReviewIntegrationTests.CodexCanReviewSmallFile_WhenExplicitlyEnabled failed: "
            + "Codex weekly quota exhausted, resets 2026-09-08T00:00:00Z"
        },

        // Completed out-of-band during the runner connectivity flap and reached
        // Human Review without any review at all.
        {
            "AGT-2721", GateFailureClass.Infrastructure, GateFailureSignatures.RunnerDisconnected,
            "Run completed out-of-band: runner disconnected before the review lane could claim the attempt."
        },

        // The control case: a real failing test still parks the card.
        {
            "control", GateFailureClass.Product, GateFailureSignatures.NewTestFailures,
            "build-tests: 1 new failures: "
            + "AgentStudio.Tests.AcceptanceRailPolicyTests.Requeues_recoverable_conflict"
        },
    };

    [Theory]
    [MemberData(nameof(RecordedFailures))]
    public void Recorded_failures_classify_to_their_cause(
        string card,
        GateFailureClass expectedClass,
        string expectedSignature,
        string reason)
    {
        var classification = GateFailureClassifier.Classify(reason);

        Assert.Equal(expectedClass, classification.Class);
        Assert.Equal(expectedSignature, classification.Signature);
        Assert.False(string.IsNullOrWhiteSpace(classification.Detail), card);
    }

    /// <summary>
    /// The acceptance criterion of AGT-2749, asserted as one statement rather
    /// than only per row: replaying the recorded night yields twelve retryable
    /// failures and no product failure.
    /// </summary>
    [Fact]
    public void Recorded_night_yields_twelve_retryable_failures_and_no_product_failure()
    {
        var night = RecordedFailures()
            .Select(row => (Card: (string)row[0]!, Reason: (string)row[3]!))
            .Where(row => row.Card != "control")
            .Select(row => GateFailureClassifier.Classify(row.Reason))
            .ToArray();

        Assert.Equal(12, night.Length);
        Assert.Equal(12, night.Count(item => item.IsRetryable));
        Assert.DoesNotContain(night, item => item.Class == GateFailureClass.Product);
        Assert.DoesNotContain(night, item => item.Class == GateFailureClass.Unknown);
    }

    [Fact]
    public void Quota_failure_waits_for_the_reset_and_infrastructure_does_not()
    {
        var quota = GateFailureClassifier.Classify("Codex weekly quota exhausted");
        var infrastructure = GateFailureClassifier.Classify(
            "dotnet test violated gate-run budget (limit=1800000ms, consumed=1800488ms, phase=verification)");

        Assert.True(quota.WaitsForQuotaReset);
        Assert.True(quota.IsRetryable);
        Assert.False(infrastructure.WaitsForQuotaReset);
        Assert.True(infrastructure.IsRetryable);
    }

    [Theory]
    [InlineData("Failed AgentStudio.Tests.FooTests.Bar_does_baz", GateFailureSignatures.NewTestFailures)]
    [InlineData("Failed!  - Failed:     3, Passed:  4340", GateFailureSignatures.NewTestFailures)]
    [InlineData("src/App.cs(12,5): error CS0103: The name 'x' does not exist", GateFailureSignatures.CompilerError)]
    [InlineData("src/app.ts(4,1): error TS2322: Type 'string' is not assignable", GateFailureSignatures.CompilerError)]
    public void Product_failures_stay_product(string reason, string expectedSignature)
    {
        var classification = GateFailureClassifier.Classify(reason);

        Assert.Equal(GateFailureClass.Product, classification.Class);
        Assert.Equal(expectedSignature, classification.Signature);
        Assert.False(classification.IsRetryable);
    }

    /// <summary>
    /// A clean run reports "0 new failures". The count regex must not read that
    /// as a failure, or every passing baseline comparison would park its card.
    /// </summary>
    [Fact]
    public void Zero_new_failures_is_not_a_product_failure()
    {
        var classification = GateFailureClassifier.Classify(
            "build-tests: 0 new failures; 0 pre-existing failures; 0 flaky quarantined failures.");

        Assert.NotEqual(GateFailureClass.Product, classification.Class);
    }

    /// <summary>
    /// An unrecognised cause is not retried. Looping on an unknown failure burns
    /// the budget and still ends in Human Review, only later and with less
    /// evidence.
    /// </summary>
    [Fact]
    public void Unknown_cause_is_not_retryable()
    {
        var classification = GateFailureClassifier.Classify("the gate said no");

        Assert.Equal(GateFailureClass.Unknown, classification.Class);
        Assert.Equal(GateFailureSignatures.Unclassified, classification.Signature);
        Assert.False(classification.IsRetryable);
    }

    [Fact]
    public void Empty_reason_is_unknown_rather_than_a_pass()
    {
        var classification = GateFailureClassifier.Classify(null, null);

        Assert.Equal(GateFailureClass.Unknown, classification.Class);
        Assert.False(classification.IsRetryable);
    }

    /// <summary>
    /// The rule from the 07.09. addendum. Zero parsed test results with a
    /// non-zero exit means the command failed to run a test, not that a test
    /// failed. Five cards were graded <c>ProductFailure</c> on exactly this.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(134)]
    [InlineData(null)]
    public void Exit_without_a_parsable_test_result_is_infrastructure(int? exitCode)
    {
        var classification = GateFailureClassifier.ClassifyVerificationCommand(
            exitCode,
            parsedTestFailureCount: 0,
            reason: "dotnet test exited without a report");

        Assert.Equal(GateFailureClass.Infrastructure, classification.Class);
        Assert.Equal(GateFailureSignatures.UnparsedTestOutcome, classification.Signature);
        Assert.True(classification.IsRetryable);
    }

    [Fact]
    public void Parsed_test_failures_with_a_non_zero_exit_stay_product()
    {
        var classification = GateFailureClassifier.ClassifyVerificationCommand(
            exitCode: 1,
            parsedTestFailureCount: 2,
            reason: "Failed AgentStudio.Tests.FooTests.Bar_does_baz");

        Assert.Equal(GateFailureClass.Product, classification.Class);
        Assert.Equal(GateFailureSignatures.NewTestFailures, classification.Signature);
        Assert.False(classification.IsRetryable);
    }

    /// <summary>
    /// A host signature outranks a parsed failure count. If /tmp vanished
    /// mid-run, whatever the parser scraped out of the truncated output is not
    /// evidence about the diff.
    /// </summary>
    [Fact]
    public void Host_signature_outranks_a_parsed_failure_count()
    {
        var classification = GateFailureClassifier.ClassifyVerificationCommand(
            exitCode: 1,
            parsedTestFailureCount: 1,
            reason: "verification failed",
            output: "MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.");

        Assert.Equal(GateFailureClass.Infrastructure, classification.Class);
        Assert.Equal(GateFailureSignatures.MsBuildNodeUnavailable, classification.Signature);
    }

    [Fact]
    public void A_clean_command_is_not_classified_as_a_failure()
    {
        var classification = GateFailureClassifier.ClassifyVerificationCommand(
            exitCode: 0,
            parsedTestFailureCount: 0);

        Assert.Equal(GateFailureClass.Unknown, classification.Class);
        Assert.False(classification.IsRetryable);
    }
}
