using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Table tests for the gate and review failure taxonomy. Every fixture below is
/// a verbatim message from the 2026-09-06 overload night, when thirteen cards
/// were parked in Human Review as product failures. Replaying them must yield
/// zero <see cref="RunFailureClass.Product"/> verdicts.
/// </summary>
public sealed class RunFailureClassifierTests
{
    /// <summary>Recorded 2026-09-06 messages, keyed by the card they parked.</summary>
    public static class Recorded
    {
        // AGT-2707, AGT-2710: the suite needed a few seconds more than 30 minutes.
        public const string GateBudget =
            "dotnet test agent-taskboard.sln violated gate-run budget " +
            "(limit=1800000ms, consumed=1800488ms, phase=verification)";

        // AGT-2708: the 30-second GitNetworkProcessRunner cap on an integration fetch.
        public const string IntegrationFetch =
            "Integration branch 'develop' could not be fetched from origin: " +
            "git operation timed out after 30 seconds";

        // AGT-2713: the same cap on the delivery fetch.
        public const string DeliveryFetch =
            "Delivery branch 'task/agt-2713' could not be fetched from origin: " +
            "git operation timed out after 30 seconds";

        // AGT-2709, AGT-2711, QS-59, QS-82, QS-96: no test ran, yet the grader
        // counted the placeholder as a new failing test.
        public const string UnparsedFailure =
            "build-tests: 1 new failures: <unparsed failure in verify-2>";

        // 2026-09-07 addendum: the real stdout behind the "unparsed" outcomes.
        public const string MsBuildNodeCrash =
            "MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.\n" +
            "System.Net.Sockets.SocketException (99): Cannot assign requested address\n" +
            "   at System.IO.Pipes.NamedPipeServerStream..ctor(String pipeName, PipeDirection direction)\n" +
            "   at Microsoft.Build.Execution.OutOfProcNode.Run(Exception& shutdownException)";

        // AGT-2716, AGT-2719: NuGet's migration mutex against the unmounted private /tmp.
        public const string RestoreTempMissing =
            "PreparationFailed: dotnet restore failed: " +
            "mkdtemp(\"/tmp/.dotnet.XXXXXX\") failed: No such file or directory";

        // QS-89: a live-CLI test failed because the weekly Codex quota was gone.
        public const string CodexQuota =
            "LiveReviewIntegrationTests.CodexCanReviewSmallFile_WhenExplicitlyEnabled failed: " +
            "the Codex weekly quota is exhausted";

        // AGT-2721, AGT-2723: completion during the runner connectivity flap.
        public const string RunnerFlap =
            "review lease authority lost attempt=rev-8821 (409); stopping heartbeat";
    }

    public static TheoryData<string, string, RunFailureClass, string> RecordedFailures() => new()
    {
        { "AGT-2707", Recorded.GateBudget, RunFailureClass.Infrastructure, RunFailureSignatures.GateBudgetExceeded },
        { "AGT-2710", Recorded.GateBudget, RunFailureClass.Infrastructure, RunFailureSignatures.GateBudgetExceeded },
        { "AGT-2708", Recorded.IntegrationFetch, RunFailureClass.Infrastructure, RunFailureSignatures.GitNetworkTimeout },
        { "AGT-2713", Recorded.DeliveryFetch, RunFailureClass.Infrastructure, RunFailureSignatures.GitNetworkTimeout },
        { "AGT-2709", Recorded.MsBuildNodeCrash, RunFailureClass.Infrastructure, RunFailureSignatures.MsBuildNodeUnavailable },
        { "AGT-2711", Recorded.MsBuildNodeCrash, RunFailureClass.Infrastructure, RunFailureSignatures.MsBuildNodeUnavailable },
        { "QS-59", Recorded.MsBuildNodeCrash, RunFailureClass.Infrastructure, RunFailureSignatures.MsBuildNodeUnavailable },
        { "QS-82", Recorded.MsBuildNodeCrash, RunFailureClass.Infrastructure, RunFailureSignatures.MsBuildNodeUnavailable },
        { "QS-96", Recorded.MsBuildNodeCrash, RunFailureClass.Infrastructure, RunFailureSignatures.MsBuildNodeUnavailable },
        { "QS-89", Recorded.CodexQuota, RunFailureClass.Quota, RunFailureSignatures.CliQuotaExhausted },
        { "AGT-2721", Recorded.RunnerFlap, RunFailureClass.Infrastructure, RunFailureSignatures.RunnerDisconnected },
        { "AGT-2723", Recorded.RunnerFlap, RunFailureClass.Infrastructure, RunFailureSignatures.RunnerDisconnected },
    };

    [Theory]
    [MemberData(nameof(RecordedFailures))]
    public void RecordedOverloadNightFailure_IsNeverAProductFailure(
        string card,
        string message,
        RunFailureClass expectedClass,
        string expectedSignature)
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence { Text = message });

        Assert.Equal(expectedClass, verdict.Class);
        Assert.Equal(expectedSignature, verdict.Signature);
        Assert.True(verdict.Requeueable, $"{card} must be requeued, not parked.");
    }

    [Fact]
    public void ReplayingTheRecordedNight_YieldsTwelveRequeuesAndZeroProductFailures()
    {
        var verdicts = RecordedFailures()
            .Select(row => RunFailureClassifier.Classify(
                new RunFailureEvidence { Text = (string)row[1] }))
            .ToList();

        Assert.Equal(12, verdicts.Count(verdict => verdict.Requeueable));
        Assert.DoesNotContain(verdicts, verdict => verdict.Class == RunFailureClass.Product);
    }

    [Fact]
    public void UnparsedFailurePlaceholder_IsRecognizedAndNeverCountedAsATest()
    {
        Assert.True(RunFailureClassifier.IsUnparsedFailurePlaceholder("<unparsed failure in verify-2>"));
        Assert.False(RunFailureClassifier.IsUnparsedFailurePlaceholder("Product.NewFailure"));
    }

    /// <summary>
    /// The exact reason the five "ProductFailure" cards carried. It must classify
    /// as infrastructure even without the underlying MSBuild stdout.
    /// </summary>
    [Fact]
    public void RecordedUnparsedFailureReason_IsInfrastructure()
    {
        var verdict = RunFailureClassifier.Classify(
            new RunFailureEvidence { Text = Recorded.UnparsedFailure });

        Assert.Equal(RunFailureClass.Infrastructure, verdict.Class);
        Assert.Equal(RunFailureSignatures.UnparsedTestOutput, verdict.Signature);
    }

    [Fact]
    public void TestCommandThatExitedNonZeroWithoutParsableResults_IsInfrastructure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Determining projects to restore...",
            ExitCode = 1,
            ParsedTestFailures = 0,
            ExpectsTestResults = true,
        });

        Assert.Equal(RunFailureClass.Infrastructure, verdict.Class);
        Assert.Equal(RunFailureSignatures.UnparsedTestOutput, verdict.Signature);
    }

    [Fact]
    public void NonTestCommandThatExitedNonZeroWithoutSignature_StaysUnknown()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "eslint found 3 problems",
            ExitCode = 1,
            ExpectsTestResults = false,
        });

        Assert.Equal(RunFailureClass.Unknown, verdict.Class);
        Assert.False(verdict.Requeueable);
    }

    [Fact]
    public void ParsedFailingTest_StaysAProductFailure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Failed AgentStudio.Tests.BoardTests.LaneCountsMatch [12 ms]",
            ExitCode = 1,
            ParsedTestFailures = 1,
            ExpectsTestResults = true,
        });

        Assert.Equal(RunFailureClass.Product, verdict.Class);
        Assert.Equal(RunFailureSignatures.NewTestFailures, verdict.Signature);
        Assert.False(verdict.Requeueable);
    }

    /// <summary>
    /// A suite that reports a real failing test AND logs the word "timeout" from
    /// its own fixture must stay a product failure. Facts outrank text.
    /// </summary>
    [Fact]
    public void ParsedFailingTestWhoseOutputMentionsTimeout_StaysAProductFailure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Failed Product.HttpClientTests.RetriesOnce\n  Assert.Equal() Failure: expected 'timed out after 5s'",
            ExitCode = 1,
            ParsedTestFailures = 1,
            ExpectsTestResults = true,
        });

        Assert.Equal(RunFailureClass.Product, verdict.Class);
    }

    [Fact]
    public void CompilerError_StaysAProductFailure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Board.cs(42,17): error CS0103: The name 'lane' does not exist in the current context",
            ExitCode = 1,
        });

        Assert.Equal(RunFailureClass.Product, verdict.Class);
        Assert.Equal(RunFailureSignatures.CompilerError, verdict.Signature);
    }

    [Fact]
    public void MsBuildInternalFailure_IsNotTreatedAsACompilerError()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = Recorded.MsBuildNodeCrash,
            ExitCode = 1,
            ExpectsTestResults = true,
        });

        Assert.Equal(RunFailureClass.Infrastructure, verdict.Class);
        Assert.Equal(RunFailureSignatures.MsBuildNodeUnavailable, verdict.Signature);
    }

    [Fact]
    public void RestoreAgainstAnUnmountedPrivateTemp_IsInfrastructure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = Recorded.RestoreTempMissing,
        });

        Assert.Equal(RunFailureClass.Infrastructure, verdict.Class);
        Assert.Equal(RunFailureSignatures.PrivateTempUnmounted, verdict.Signature);
    }

    [Fact]
    public void ReviewerBlockOnTheDiff_IsAProductFailure()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "The change drops the null guard in ResolveLane.",
            ReviewerBlockedDiff = true,
        });

        Assert.Equal(RunFailureClass.Product, verdict.Class);
        Assert.Equal(RunFailureSignatures.ReviewFinding, verdict.Signature);
    }

    [Theory]
    [InlineData(true, false, RunFailureSignatures.GateBudgetExceeded)]
    [InlineData(false, true, RunFailureSignatures.CommandTimeout)]
    public void BudgetAndTimeoutFacts_ClassifyWithoutAnyText(
        bool budgetViolated,
        bool timedOut,
        string expectedSignature)
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            BudgetViolated = budgetViolated,
            TimedOut = timedOut,
        });

        Assert.Equal(RunFailureClass.Infrastructure, verdict.Class);
        Assert.Equal(expectedSignature, verdict.Signature);
    }

    [Fact]
    public void QuotaFact_OutranksEveryOtherSignal()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = "Failed Product.SomeTest",
            ParsedTestFailures = 1,
            QuotaExhausted = true,
        });

        Assert.Equal(RunFailureClass.Quota, verdict.Class);
    }

    [Fact]
    public void EmptyEvidence_IsUnknownAndNotRequeued()
    {
        var verdict = RunFailureClassifier.Classify(new RunFailureEvidence());

        Assert.Equal(RunFailureClass.Unknown, verdict.Class);
        Assert.Equal(RunFailureSignatures.Unclassified, verdict.Signature);
        Assert.False(verdict.Requeueable);
    }
}
