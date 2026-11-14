using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3759: an isolation probe is a code red only when a completed test run on
/// the tested candidate proves failed tests; everything else stays infrastructure.
/// </summary>
public sealed class BatchGateProbeClassifierTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static BuildTestGateProcessEvidence Run(params string[] failed) => new()
    {
        Command = "dotnet test backend.Tests/OrchestratorApi.Tests.csproj",
        ExitCode = 1,
        TestReport = failed.Length == 0 ? null : new GateTestReport(GateTestCounts.None, failed),
    };

    private static BuildTestGateResult Red(int? exitCode, BuildTestGateFailureKind kind,
        params BuildTestGateProcessEvidence[] processes)
        => new(BuildTestGateVerdict.Fail, exitCode, 1, "", "dotnet test exit 1", false, false)
        {
            TestedSha = Sha,
            FailureKind = kind,
            Processes = processes,
        };

    [Fact]
    public void A_parsed_report_with_failed_tests_and_exit_1_is_a_code_red()
        => Assert.Equal(BatchGateFailureClass.DeterministicSuiteRed, BatchGateProbeClassifier.Classify(
            Red(1, BuildTestGateFailureKind.Code, Run("OperatorPolicyStepTests.A")), Sha));

    [Fact]
    public void A_parsed_report_with_failed_tests_is_a_code_red_after_the_exit_code_was_lost()
        => Assert.Equal(BatchGateFailureClass.DeterministicSuiteRed, BatchGateProbeClassifier.Classify(
            Red(null, BuildTestGateFailureKind.Code, Run("OperatorPolicyStepTests.A")), Sha));

    [Fact]
    public void A_run_without_a_parsed_report_and_without_an_exit_code_stays_infrastructure()
        => Assert.Equal(BatchGateFailureClass.InfrastructureRed, BatchGateProbeClassifier.Classify(
            Red(null, BuildTestGateFailureKind.Code, Run()), Sha));

    [Theory]
    [InlineData(BuildTestGateFailureKind.Cancellation)]
    [InlineData(BuildTestGateFailureKind.Timeout)]
    [InlineData(BuildTestGateFailureKind.MissingSource)]
    [InlineData(BuildTestGateFailureKind.Environment)]
    public void An_infrastructure_kind_stays_infrastructure_even_with_a_report(BuildTestGateFailureKind kind)
        => Assert.Equal(BatchGateFailureClass.InfrastructureRed, BatchGateProbeClassifier.Classify(
            Red(null, kind, Run("OperatorPolicyStepTests.A")), Sha));

    [Fact]
    public void A_hung_run_stays_infrastructure()
        => Assert.Equal(BatchGateFailureClass.InfrastructureRed, BatchGateProbeClassifier.Classify(
            Red(null, BuildTestGateFailureKind.Code, Run("OperatorPolicyStepTests.A") with
            {
                ShardHang = new GateShardHangEvidence("backend", 0, 2, "no output", 600_000, ["X"]),
            }), Sha));

    [Fact]
    public void A_tested_sha_mismatch_stays_infrastructure()
        => Assert.Equal(BatchGateFailureClass.InfrastructureRed, BatchGateProbeClassifier.Classify(
            Red(1, BuildTestGateFailureKind.Code, Run("OperatorPolicyStepTests.A")) with { TestedSha = null }, Sha));

    [Fact]
    public void Failed_test_names_come_from_completed_reports_only()
        => Assert.Equal(["OperatorPolicyStepTests.A"], BatchGateProbeClassifier.FailedTests(
            Red(1, BuildTestGateFailureKind.Code, Run("OperatorPolicyStepTests.A"),
                Run("Other.B") with { TimedOut = true })));
}
