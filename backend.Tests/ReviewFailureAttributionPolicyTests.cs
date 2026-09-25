using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewFailureAttributionPolicyTests
{
    private const string BaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void A_passing_command_has_no_failure_owner()
        => Assert.Equal(ReviewFailureOwner.None,
            ReviewFailureAttributionPolicy.Attribute(Plan(), Evidence(0, "Product")));

    [Fact]
    public void A_command_without_baseline_planning_cannot_charge_the_delivery()
        => Assert.Equal(ReviewFailureOwner.Undiagnosed,
            ReviewFailureAttributionPolicy.Attribute(Plan(compare: false), Evidence(1, "Product")));

    [Theory]
    [InlineData("Product", 0, ReviewFailureOwner.Delivery)]
    [InlineData("Environment", 0, ReviewFailureOwner.Environment)]
    [InlineData("Environment", 1, ReviewFailureOwner.IntegrationBranch)]
    [InlineData("Flaky", 0, ReviewFailureOwner.Tolerated)]
    [InlineData("Concern", 0, ReviewFailureOwner.Tolerated)]
    [InlineData("Inconclusive", 0, ReviewFailureOwner.Undiagnosed)]
    [InlineData(null, 0, ReviewFailureOwner.Undiagnosed)]
    public void Completed_diagnosis_controls_review_charge(
        string? diagnosis, int baselineExitCode, ReviewFailureOwner expected)
        => Assert.Equal(expected, ReviewFailureAttributionPolicy.Attribute(
            Plan(), Evidence(1, diagnosis) with { BaselineExitCode = baselineExitCode }));

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(1, null, true)]
    [InlineData(0, "timeout", true)]
    [InlineData(-1, null, true)]
    public void Failure_detection_covers_signals_and_abnormal_exits(
        int exitCode, string? signal, bool expected)
        => Assert.Equal(expected, ReviewFailureAttributionPolicy.Failed(
            Evidence(exitCode, null) with { Signal = signal }));

    private static ReviewCommandDto Plan(bool compare = true)
        => new("verify-5", "lint", "sh", ["-lc", "npm run lint"], CompareToBaseline: compare);

    private static ReviewCommandEvidenceDto Evidence(int exitCode, string? diagnosis)
        => new("verify-5", "lint", "sh", ["-lc", "npm run lint"],
            new string('b', 40), new string('b', 40), new string('c', 40),
            DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow,
            exitCode, null, new string('0', 64), new string('0', 64),
            BaselineSha: BaseSha, BaselineExitCode: 0,
            CleanRepeatExitCode: 1, CleanRepeatSameFingerprint: true,
            DiagnosisClass: diagnosis);
}
