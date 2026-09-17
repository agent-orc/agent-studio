using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2857: the torn-down-<c>/tmp</c> signature table was matched against the
/// whole command output, so a test whose display name or assertion message
/// quoted one of the signatures turned a red suite into
/// <c>ReviewInfra</c>/<c>TmpMountTornDown</c>. The matrix below pins both
/// directions: process-level evidence still classifies as infrastructure,
/// test-owned text never does.
/// </summary>
public sealed class ReviewInfraAttributionPolicyTests
{
    /// <summary>
    /// The captured verify-3 excerpt of review attempt
    /// <c>review_849e4484f1454ea2bfcc8b4d7be6dd31</c> (AGT-2819, candidate
    /// 14ff5b64): one genuinely failed test, and one passing theory case whose
    /// inline data quotes the NuGet mkdtemp ENOENT signature. The operator
    /// report truncated that display name at <c>AbC1"</c>; it is spelled out
    /// here with the full inline data of
    /// <c>backend.Tests/AgentOutcomeAnalyzerTests.cs</c>, which is what the
    /// runner printed and the old whole-output matcher read.
    /// </summary>
    internal const string CapturedVerifyOutput =
        """
          Determining projects to restore...
          All projects are up-to-date for restore.
        Test run for /home/agent/runner-work/review/AgentStudio.Tests.dll (.NETCoreApp,Version=v9.0)
          Passed AgentStudio.Tests.AgentOutcomeAnalyzerTests.EnvironmentalTransient_OnFailedRun_TypesAsEnvironmentalTransient(reply: "System.IO.IOException: mkdtemp("/tmp/.dotnet.AbC123") == nullptr; errno == ENOENT") [< 1 ms]
          Failed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_KeepsOldestOnContestedKey_ReKeysNamesake_PreservesContent_AndIsIdempotent [3 ms]
          Error Message:
           Assert.Equal() Failure: Strings differ
           Expected: "DEM-6"
           Actual:   "DEM-5"

        Failed!  - Failed:     1, Passed:  2117, Skipped:     0, Total:  2118, Duration: 4 m 11 s - AgentStudio.Tests.dll (net9.0)
        """;

    internal const string FailedTestName =
        "AgentStudio.Tests.DuplicateTaskKeyTests." +
        "Dedup_KeepsOldestOnContestedKey_ReKeysNamesake_PreservesContent_AndIsIdempotent";

    [Fact]
    public void Signature_in_a_passing_theory_display_name_is_a_product_failure()
    {
        var attribution = ReviewInfraAttributionPolicy.Attribute(
            commandSucceeded: false,
            CapturedVerifyOutput,
            string.Empty);

        Assert.Equal(ReviewInfraAttribution.ProductOwned, attribution);
    }

    /// <summary>
    /// 17.09.2026, agent-runner-01: three reviews in a row (AGT-2819 twice,
    /// AGT-2857) were graded <c>ReviewInfra / TmpMountTornDown</c> although
    /// <c>/tmp</c> was intact and exactly one test had failed. The mkdtemp
    /// signature came from the display name of a passing theory case
    /// (<c>AgentOutcomeAnalyzerTests.EnvironmentalTransient_...(reply:
    /// "System.IO.IOException: mkdtemp(\"/tmp/.dotnet.AbC1"...)</c>). A test
    /// run that printed a summary counting failed tests ran to the end; its red
    /// result is a product failure of the named tests, never a broken mount.
    /// The same signature on its own, with no test result around it, is still
    /// the mount.
    /// </summary>
    [Fact]
    public void A_test_run_summary_outranks_a_tmp_teardown_signature_in_test_content()
    {
        var output =
            "  Passed AgentStudio.Tests.AgentOutcomeAnalyzerTests.EnvironmentalTransient_OnFailedRun" +
            "(reply: \"System.IO.IOException: mkdtemp(\\\"/tmp/.dotnet.AbC1\"...) [< 1 ms]\n" +
            "  Failed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_KeepsOldest [12 ms]\n" +
            "  Error Message: errno == ENOENT was expected here\n" +
            "Failed!  - Failed:     1, Passed:  6811, Skipped:    23, Total:  6835, " +
            "Duration: 19 m - OrchestratorApi.Tests.dll (net10.0)\n";

        Assert.Equal(
            ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
        Assert.Equal(
            ReviewInfraAttribution.TmpMountTornDown,
            ReviewInfraAttributionPolicy.Attribute(
                commandSucceeded: false,
                "System.IO.IOException: mkdtemp(\"/tmp/.dotnet.AbC123\") == nullptr; errno == ENOENT",
                string.Empty));
    }

    [Theory]
    // The process-level signatures of the AGT-2750 incident: emitted by the
    // tool itself, with no test result in the output at all.
    [InlineData(
        "MSB1025: Startup of MSBuild's node communication pipe failed: " +
        "SocketException (99): Cannot assign requested address",
        true)]
    [InlineData(
        "System.IO.IOException: mkdtemp(\"/tmp/.dotnet.AbC123\") == nullptr; errno == ENOENT",
        true)]
    [InlineData(
        "Unhandled exception. System.Net.Sockets.SocketException (99): Cannot assign requested address",
        true)]
    // Ordinary tool failures carry no signature and stay with the test parser.
    [InlineData("error CS0103: The name 'Missing' does not exist in the current context",
        false)]
    // Prose that opens with "Failed" is tool output, not a test result line.
    [InlineData(
        "Failed to create the NuGet mutex: mkdtemp(\"/tmp/.dotnet.AbC123\") == nullptr; errno == ENOENT",
        true)]
    public void Process_level_output_keeps_the_torn_down_tmp_detection(string output, bool tornDown)
        => Assert.Equal(
            tornDown
                ? ReviewInfraAttribution.TmpMountTornDown
                : ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(
                commandSucceeded: false,
                string.Empty,
                output));

    [Fact]
    public void A_successful_command_is_never_an_infrastructure_incident()
        => Assert.Equal(
            ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(
                commandSucceeded: true,
                "MSB1025: Startup of MSBuild's node communication pipe failed",
                string.Empty));

    [Fact]
    public void Signature_in_the_assertion_message_of_a_failed_test_is_a_product_failure()
    {
        const string output =
            """
              Failed AgentStudio.Tests.AgentOutcomeAnalyzerTests.TmpTeardown_TypesAsEnvironmentalTransient [7 ms]
              Error Message:
               Assert.Equal() Failure: Strings differ
               Expected: EnvironmentalTransient for mkdtemp("/tmp/.dotnet.AbC1") == nullptr; errno == ENOENT
               Actual:   ProductFailure

            Failed!  - Failed:     1, Passed:     9, Skipped:     0, Total:    10, Duration: 2 s
            """;

        Assert.Equal(
            ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
    }

    [Fact]
    public void Signature_in_a_marker_style_test_result_line_is_a_product_failure()
    {
        const string output =
            """
            [xUnit.net 00:00:01.22]   Analyzer.Reports("MSB1025: node pipe failed") [PASS]
            [xUnit.net 00:00:01.31]   Analyzer.Dedup_KeepsOldestOnContestedKey [FAIL]
                Assert.Equal() Failure
              Failed:  1
            """;

        Assert.Equal(
            ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
    }

    /// <summary>
    /// A suite that reported no failed test is not evidence against an
    /// infrastructure fault: the mount can be torn down while the next project
    /// in the same command builds.
    /// </summary>
    [Fact]
    public void A_green_summary_followed_by_a_torn_down_mount_stays_infrastructure()
    {
        const string output =
            """
              Passed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_KeepsOldest [2 ms]
            Passed!  - Failed:     0, Passed:  2118, Skipped:     0, Total:  2118, Duration: 4 m 11 s
            MSB1025: Startup of MSBuild's node communication pipe failed: SocketException (99): Cannot assign requested address
            """;

        Assert.Equal(
            ReviewInfraAttribution.TmpMountTornDown,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
    }

    /// <summary>
    /// A runner that aborted before printing any summary keeps the
    /// infrastructure path even though it had already printed test results.
    /// </summary>
    [Fact]
    public void An_aborted_runner_without_a_summary_stays_infrastructure()
    {
        const string output =
            """
              Passed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_KeepsOldest [2 ms]
              Failed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_ReKeysNamesake [1 ms]
            The active test run was aborted.
            System.IO.IOException: mkdtemp("/tmp/.dotnet.AbC123") == nullptr; errno == ENOENT
            """;

        Assert.Equal(
            ReviewInfraAttribution.TmpMountTornDown,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
    }

    /// <summary>
    /// Runners that print the counters as an indented block under
    /// <c>Total tests:</c> are heard as well, so their failed tests own the
    /// verdict just like the single-line summary above.
    /// </summary>
    [Fact]
    public void An_indented_summary_block_with_failed_tests_is_a_product_failure()
    {
        const string output =
            """
              Failed AgentStudio.Tests.DuplicateTaskKeyTests.Dedup_ReKeysNamesake [1 ms]
              Error Message:
               Expected "DEM-6", got "DEM-5" while /tmp/.dotnet.AbC1 was in use
            Total tests: 2118
                 Passed: 2117
                 Failed: 1
            MSB1025: Startup of MSBuild's node communication pipe failed
            """;

        Assert.Equal(
            ReviewInfraAttribution.ProductOwned,
            ReviewInfraAttributionPolicy.Attribute(commandSucceeded: false, output, string.Empty));
    }
}
