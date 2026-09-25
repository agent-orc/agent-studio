using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewDiagnosisAdmissionPolicyTests
{
    [Fact]
    public void Unproven_block_cannot_report_product_failure()
    {
        var report = Report([new ReviewVerdictDto("build-tests", "block", "CommandFailed", "red")]);
        var normalized = ReviewDiagnosisAdmissionPolicy.NormalizeReport(report,
            [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-c", "test"], CompareToBaseline: true)]);
        Assert.Equal("Inconclusive", normalized.Outcome);
    }

    [Fact]
    public void Cited_diff_block_remains_product_failure()
    {
        var report = Report([new ReviewVerdictDto("requirement-fit", "block", "ReviewFinding",
            "Missing requirement", "unified diff: src/Handler.cs:42", "handler guard")]);
        var normalized = ReviewDiagnosisAdmissionPolicy.NormalizeReport(report,
            [new ReviewCommandDto("aspect-1", "requirement-fit", "agent", [],
                ExecutionKind: ReviewCommandKinds.AgentAspect)]);
        Assert.Equal("ProductFailure", normalized.Outcome);
    }

    [Fact]
    public void Baseline_red_gate_is_an_integration_defect()
    {
        var report = Report([new ReviewVerdictDto("build-tests", "block", "CommandFailed", "red")]) with
        {
            Commands = [Command() with { BaselineExitCode = 1, DiagnosisClass = "Environment" }],
        };
        var normalized = ReviewDiagnosisAdmissionPolicy.NormalizeReport(report, []);
        Assert.Equal("IntegrationBranchDefect", normalized.Outcome);
    }

    [Fact]
    public void Complete_gate_diagnosis_can_report_product_failure()
    {
        var report = Report([new ReviewVerdictDto("build-tests", "block", "NewTestFailures", "new")]) with
        {
            Commands = [Command() with
            {
                BaselineExitCode = 0,
                BaselineSha = new string('b', 40),
                CleanRepeatExitCode = 1,
                CleanRepeatSameFingerprint = true,
                DiagnosisClass = "Product",
                DiagnosisConfidence = 0.95,
            }],
        };
        var normalized = ReviewDiagnosisAdmissionPolicy.NormalizeReport(report,
            [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-c", "test"], CompareToBaseline: true)]);
        Assert.Equal("ProductFailure", normalized.Outcome);
    }

    [Fact]
    public void Unplanned_comparison_cannot_confirm_a_product_failure()
    {
        var report = Report([new ReviewVerdictDto("build-tests", "block", "CommandFailed", "red")]) with
        {
            Commands = [Command() with
            {
                BaselineExitCode = 0,
                BaselineSha = new string('b', 40),
                CleanRepeatExitCode = 1,
                CleanRepeatSameFingerprint = true,
                DiagnosisClass = "Product",
                DiagnosisConfidence = 0.95,
            }],
        };
        var normalized = ReviewDiagnosisAdmissionPolicy.NormalizeReport(report,
            [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-c", "test"])]);
        Assert.Equal("Inconclusive", normalized.Outcome);
    }

    private static ReviewReportRequest Report(IReadOnlyList<ReviewVerdictDto> verdicts)
        => new("executor", "instance", "lease", 1, "report", "ProductFailure", null, null,
            new ReviewWorkspaceProofDto("repo", new string('a', 40), new string('a', 40),
                new string('b', 40), false, false, "workspace", "namespace"),
            new ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", ".NET",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], verdicts);

    private static ReviewCommandEvidenceDto Command()
        => new("verify-1", "build-tests", "sh", ["-c", "test"],
            new string('a', 40), new string('a', 40), new string('b', 40),
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 1, null,
            new string('0', 64), new string('0', 64));
}
