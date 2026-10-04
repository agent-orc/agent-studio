using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateFailureAssessmentTests
{
    private static string Captured(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "gate-failure-assessment", name + ".log"));

    private static BuildTestGateResult Failed(string name, BuildTestGateFailureKind kind,
        DeliveryFailureDiagnosisResult? diagnosis = null)
        => new(BuildTestGateVerdict.Fail, 1, 1, Captured(name), "Gate command failed.", false, false)
        {
            FailureKind = kind,
            Diagnosis = diagnosis,
        };

    [Fact]
    public void Captured_toolchain_gate_log_is_environment_and_stays_out_of_human_review()
    {
        // AGT-2782's captured WEB-19 gate replay: Angular's worker module was absent from its cache.
        var assessment = GateFailureAssessmentPolicy.Classify(Failed(
            "environment", BuildTestGateFailureKind.Environment));

        Assert.Equal(GateFailureAssessmentPolicy.Environment, assessment.Classification);
        Assert.True(GateOutcomeRoutingPolicy.WaitsInAutoReview(
            nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure), null));
    }

    [Fact]
    public void Captured_red_test_gate_log_is_product_with_exact_item()
    {
        // AGT-2995's captured backend gate failure.
        var diagnosis = new DeliveryFailureDiagnosisResult(DeliveryFailureDiagnosis.Product, 1,
            ["baseline=green", "clean-repeat=red"]);
        var assessment = GateFailureAssessmentPolicy.Classify(Failed(
            "product", BuildTestGateFailureKind.Code, diagnosis));

        Assert.Equal(GateFailureAssessmentPolicy.Product, assessment.Classification);
        Assert.Contains(assessment.FailingItems, item => item.Contains("TestClockGuardTests", StringComparison.Ordinal));
        Assert.Contains("TestClockGuardTests", assessment.Reason);
    }

    [Fact]
    public void Captured_vitest_failure_uses_the_spec_path_without_duration_in_its_fingerprint()
    {
        var diagnosis = new DeliveryFailureDiagnosisResult(DeliveryFailureDiagnosis.Product, 1,
            ["baseline=green", "clean-repeat=red"]);
        var gate = Failed("frontend-product", BuildTestGateFailureKind.Code, diagnosis);

        var assessment = GateFailureAssessmentPolicy.Classify(gate);

        Assert.Equal(GateFailureAssessmentPolicy.Product, assessment.Classification);
        Assert.Contains("src/app/features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.digest.spec.ts",
            assessment.FailingItems);
        Assert.Equal(assessment.Fingerprint, GateFailureAssessmentPolicy.Classify(gate with
        {
            Output = gate.Output.Replace("606ms", "1380ms", StringComparison.Ordinal),
        }).Fingerprint);
    }

    [Fact]
    public void Captured_red_test_on_integration_tip_is_a_branch_cause()
    {
        var diagnosis = new DeliveryFailureDiagnosisResult(DeliveryFailureDiagnosis.Environment, 1,
            ["baseline=red; fingerprint=code:captured", "clean-repeat=red"]);
        var assessment = GateFailureAssessmentPolicy.Classify(Failed(
            "integration-branch", BuildTestGateFailureKind.Environment, diagnosis) with
        {
            FailureFingerprint = "code:captured",
        });

        Assert.Equal(GateFailureAssessmentPolicy.IntegrationBranch, assessment.Classification);
        Assert.True(GateOutcomeRoutingPolicy.OpensCause(assessment));
        Assert.True(GateOutcomeRoutingPolicy.WaitsInAutoReview(
            nameof(MergeIntoIntegrationOutcome.GateIntegrationBranchFailure), null));
    }

    [Fact]
    public void Different_red_baseline_item_does_not_charge_the_integration_branch()
    {
        var diagnosis = new DeliveryFailureDiagnosisResult(DeliveryFailureDiagnosis.Environment, 1,
            ["baseline=red; fingerprint=code:other-item", "clean-repeat=red"]);
        var assessment = GateFailureAssessmentPolicy.Classify(Failed(
            "integration-branch", BuildTestGateFailureKind.Environment, diagnosis) with
        {
            FailureFingerprint = "code:captured",
        });

        Assert.Equal(GateFailureAssessmentPolicy.Undecidable, assessment.Classification);
        Assert.Contains("same failing item", assessment.MissingEvidence);
    }

    [Fact]
    public void Captured_red_test_without_baseline_is_undecidable_and_names_missing_evidence()
    {
        // AGT-2859 captured this failure, but the excerpt contains no baseline diagnosis.
        var assessment = GateFailureAssessmentPolicy.Classify(Failed(
            "undecidable", BuildTestGateFailureKind.Code));

        Assert.Equal(GateFailureAssessmentPolicy.Undecidable, assessment.Classification);
        Assert.Contains("baseline", assessment.MissingEvidence);
        Assert.False(GateOutcomeRoutingPolicy.WaitsInAutoReview(
            nameof(MergeIntoIntegrationOutcome.GateUndecidable), assessment.MissingEvidence));
    }

    [Fact]
    public void Missing_gate_report_states_what_human_review_needs()
    {
        var gate = new BuildTestGateResult(BuildTestGateVerdict.Fail, null, 0,
            string.Empty, "Gate failed without a report.", false, false);
        var assessment = GateFailureAssessmentPolicy.Classify(gate);

        Assert.Equal(GateFailureAssessmentPolicy.Undecidable, assessment.Classification);
        Assert.Contains("gate report is missing", assessment.MissingEvidence);
    }

    [Fact]
    public void Second_card_with_same_fingerprint_opens_a_shared_cause()
    {
        var first = new GateFailureAssessment(GateFailureAssessmentPolicy.Product,
            "gate:item:one", ["AgentStudio.Tests.Architecture.TestClockGuardTests"], "[FAIL]",
            OtherCards: []);
        var second = first with { OtherCards = ["AGT-2988"] };

        Assert.False(GateOutcomeRoutingPolicy.OpensCause(first));
        Assert.True(GateOutcomeRoutingPolicy.OpensCause(second));
    }

    [Fact]
    public void Flake_label_requires_a_recorded_passing_rerun_on_the_same_tree()
    {
        var result = Failed("product", BuildTestGateFailureKind.Code) with
        {
            FlakyQuarantinedFailures = ["AgentStudio.Tests.Architecture.TestClockGuardTests"],
            ExpectedSha = "0123456789abcdef0123456789abcdef01234567",
            TestedSha = "0123456789abcdef0123456789abcdef01234567",
        };

        Assert.Null(result.FlakyClassification);
        Assert.Null((result with { RetryPerformed = true, TestedSha = "other-tree" }).FlakyClassification);
        Assert.Equal(ReviewFlakyQuarantine.Classification,
            (result with { RetryPerformed = true }).FlakyClassification);
    }
}
