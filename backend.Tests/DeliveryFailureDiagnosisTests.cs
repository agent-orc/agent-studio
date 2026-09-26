using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryFailureDiagnosisTests
{
    public static IEnumerable<object[]> RecordedCases()
    {
        // CAC-18: repeated npm preparation exit 127 / missing local Angular CLI.
        yield return ["CAC-18 npm ci exit 127", new DeliveryFailureDiagnosisInput(
            "npm-ci:127", false, "npm-ci:127", false, "npm-ci:127", 2, 400),
            DeliveryFailureDiagnosis.Environment];
        // A green delivery with a reviewer block that cites no changed file.
        yield return ["green delivery blocked as delivery-gate failure", new DeliveryFailureDiagnosisInput(
            "review:uncited-block", true, null, true, null,
            ReviewerBlocked: true, EvidenceAgainstDiff: false),
            DeliveryFailureDiagnosis.Concern];
        // AGT-2858: the path-length guard failed reproducibly on the delivered diff.
        yield return ["path-length guard test", new DeliveryFailureDiagnosisInput(
            "test:long-ref-guard", true, null, false, "test:long-ref-guard"),
            DeliveryFailureDiagnosis.Product];
        // AGT-2858 preparation cache: incomplete immutable entry on the same host.
        yield return ["incomplete dependency cache", new DeliveryFailureDiagnosisInput(
            "cache:incomplete", false, "cache:incomplete", true, null, 1, 2),
            DeliveryFailureDiagnosis.Environment];
    }

    [Theory]
    [MemberData(nameof(RecordedCases))]
    public void Replays_recorded_failure_shapes(
        string caseName, DeliveryFailureDiagnosisInput input, string expected)
    {
        var result = DeliveryFailureDiagnosis.Classify(input);
        Assert.Equal(expected, result.Classification);
        Assert.Equal(expected == DeliveryFailureDiagnosis.Product, result.ChargesCard);
        Assert.NotEmpty(result.Evidence);
        Assert.True(result.Confidence > 0, caseName);
    }

    [Theory]
    [InlineData(true, false, 0, 0, false, "product")]
    [InlineData(false, false, 0, 0, false, "environment")]
    [InlineData(true, true, 0, 0, false, "unclassified-first-occurrence")]
    [InlineData(true, true, 0, 1, false, "intermittent")]
    [InlineData(true, true, 0, 0, true, "intermittent")]
    [InlineData(true, false, 1, 0, false, "environment")]
    public void Classifies_all_evidence_branches(
        bool baselineGreen, bool cleanGreen, int otherCards, int prior,
        bool knownPattern, string expected)
    {
        var result = DeliveryFailureDiagnosis.Classify(new(
            "test:guard", baselineGreen, null, cleanGreen,
            cleanGreen ? null : "test:guard", otherCards, prior, knownPattern));
        Assert.Equal(expected, result.Classification);
        Assert.Equal(expected == DeliveryFailureDiagnosis.Product, result.ChargesCard);
        Assert.InRange(result.Confidence, 0, 1);
        Assert.Equal(4, result.Evidence.Count);
    }

    [Fact]
    public void Same_baseline_fingerprint_is_environment()
        => Assert.Equal(DeliveryFailureDiagnosis.Environment,
            DeliveryFailureDiagnosis.Classify(new(
                "npm-ci:127", false, "npm-ci:127", false, "npm-ci:127")).Classification);

    [Fact]
    public void Missing_proof_never_charges_card()
        => Assert.Equal(DeliveryFailureDiagnosis.FirstOccurrence,
            DeliveryFailureDiagnosis.Classify(new("test:guard", null, null, null, null)).Classification);

    [Fact]
    public void Uncited_review_block_is_a_concern_even_with_red_tests()
    {
        var result = DeliveryFailureDiagnosis.Classify(new(
            "test:guard", true, null, false, "test:guard",
            ReviewerBlocked: true, EvidenceAgainstDiff: false));
        Assert.Equal(DeliveryFailureDiagnosis.Concern, result.Classification);
        Assert.False(result.ChargesCard);
    }

    [Fact]
    public void Different_clean_failure_does_not_prove_product()
        => Assert.Equal(DeliveryFailureDiagnosis.FirstOccurrence,
            DeliveryFailureDiagnosis.Classify(new(
                "test:guard", true, null, false, "test:other")).Classification);

    [Fact]
    public void Reviewer_block_without_changed_file_citation_becomes_concern()
    {
        var block = new ReviewVerdictDto(
            "requirements", "block", "RequirementGap", "Missing behavior.",
            "README.md", "src/guard.cs");
        var concern = ReviewDiffEvidencePolicy.Normalize(block, ["src/guard.cs"]);
        Assert.Equal("concerns", concern.Status);
        Assert.Equal(DeliveryFailureDiagnosis.Concern, concern.Diagnosis?.Classification);
        Assert.Equal("block", ReviewDiffEvidencePolicy.Normalize(
            block with { EvidenceChecked = "src/guard.cs:12" },
            ["src/guard.cs"]).Status);
        Assert.True(ReviewDiffEvidencePolicy.Normalize(
            block with { EvidenceChecked = "src/guard.cs:12" },
            ["src/guard.cs"]).Diagnosis!.ChargesCard);
        Assert.Equal("concerns", ReviewDiffEvidencePolicy.Normalize(
            block with { EvidenceChecked = "src/guard.cs.bak" },
            ["src/guard.cs"]).Status);
    }

    [Fact]
    public void Green_review_report_cannot_charge_as_product_failure()
    {
        var report = ReviewReportDiagnosisPolicy.Normalize(
            EmptyReport("ProductFailure"), new ReviewPlanDto([], []));
        Assert.Equal("Pass", report.Outcome);
        Assert.Null(report.FailureClassification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_deterministic_failures_charge_when_any_is_confirmed_product(bool productFirst)
    {
        var product = FailedCommand("product", DeliveryFailureDiagnosis.Product, true);
        var environment = FailedCommand("environment", DeliveryFailureDiagnosis.Environment, false);
        var ordered = productFirst ? new[] { product, environment } : [environment, product];
        var report = EmptyReport("ReviewInfra") with
        {
            FailureClassification = DeliveryFailureDiagnosis.Environment,
            Commands = ordered.SelectMany(command => new[]
            {
                command,
                command with { Phase = "clean-repeat", WorkspaceRole = "clean-repeat",
                    ExitCode = command.Diagnosis!.ChargesCard ? 1 : 0 },
            }).ToArray(),
        };

        var normalized = ReviewReportDiagnosisPolicy.Normalize(report, new ReviewPlanDto([], []));

        Assert.Equal("ProductFailure", normalized.Outcome);
        Assert.Equal(DeliveryFailureDiagnosis.Product, normalized.FailureClassification);
    }

    [Fact]
    public void Mixed_failure_with_missing_diagnosis_cannot_charge()
    {
        var product = FailedCommand("product", DeliveryFailureDiagnosis.Product, true);
        var missing = FailedCommand("missing", DeliveryFailureDiagnosis.Environment, false)
            with { Diagnosis = null };
        var report = EmptyReport("ProductFailure") with
        {
            Commands =
            [
                product,
                product with { Phase = "clean-repeat", WorkspaceRole = "clean-repeat" },
                missing,
                missing with { Phase = "clean-repeat", WorkspaceRole = "clean-repeat" },
            ],
        };

        var normalized = ReviewReportDiagnosisPolicy.Normalize(report, new ReviewPlanDto([], []));

        Assert.Equal("ReviewInfra", normalized.Outcome);
        Assert.Equal("DiagnosisMissing", normalized.FailureClassification);
    }

    [Fact]
    public void Semantic_block_without_diff_evidence_cannot_charge_as_product_failure()
    {
        var report = EmptyReport("ProductFailure") with
        {
            Verdicts = [new ReviewVerdictDto(
                "requirements", "block", "RequirementGap", "Missing guard.",
                "README.md", "src/guard.cs")],
            Workspace = EmptyReport("ProductFailure").Workspace with
            {
                ChangedPaths = ["src/guard.cs"],
            },
        };
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto("review-1", "requirements", "codex", [],
                ExecutionKind: ReviewCommandKinds.AgentAspect)],
            ["requirements"]);
        var normalized = ReviewReportDiagnosisPolicy.Normalize(report, plan);
        Assert.Equal("Pass", normalized.Outcome);
        Assert.Equal("concerns", Assert.Single(normalized.Verdicts).Status);
    }

    private static ReviewReportRequest EmptyReport(string outcome)
        => new("executor", "instance", "lease", 1, "report-1", outcome,
            "legacy-classification", null,
            new ReviewWorkspaceProofDto("repo", "sha", "sha", "tree", false, false,
                "identity", "namespace"),
            new ReviewEnvironmentDto("host", "executor", "instance", "Linux", "x64", "10",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], []);

    private static ReviewCommandEvidenceDto FailedCommand(
        string step, string classification, bool chargesCard)
        => new(step, "build-tests", "sh", [], "sha", "sha", "tree",
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "stdout", "stderr",
            BaselineExitCode: 0,
            Diagnosis: new DeliveryFailureDiagnosisResult(classification, 1,
                [chargesCard ? "confirmed regression" : "environment fault"]));
}
