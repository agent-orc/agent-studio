using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryFailureDiagnosisPolicyTests
{
    private static readonly DeliveryFailureEvidence Confirmed = new(
        BaselineMeasured: true,
        BaselineGreen: true,
        BaselineHasSameFingerprint: false,
        CleanRepeatMeasured: true,
        CleanRepeatGreen: false,
        CleanRepeatSameFingerprint: true,
        SameFingerprintOnOtherCardWithin24Hours: false,
        SporadicOnBothSides: false);

    [Fact]
    public void Guard_test_regression_is_product_only_with_all_three_proofs()
    {
        var result = DeliveryFailureDiagnosisPolicy.Classify(Confirmed);
        Assert.Equal(DeliveryFailureClass.Product, result.Class);
        Assert.True(result.ChargesCard);
        Assert.InRange(result.Confidence, 0.9, 1);
    }

    [Theory]
    [InlineData(false, false, false, DeliveryFailureClass.Environment)]
    [InlineData(true, true, false, DeliveryFailureClass.Environment)]
    [InlineData(true, false, true, DeliveryFailureClass.Environment)]
    public void Npm_exit_127_and_incomplete_dependency_cache_do_not_charge(
        bool baselineGreen,
        bool cleanGreen,
        bool seenOnAnotherCard,
        DeliveryFailureClass expected)
    {
        var result = DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
        {
            BaselineGreen = baselineGreen,
            CleanRepeatGreen = cleanGreen,
            SameFingerprintOnOtherCardWithin24Hours = seenOnAnotherCard,
        });
        Assert.Equal(expected, result.Class);
        Assert.False(result.ChargesCard);
    }

    [Fact]
    public void Same_fingerprint_on_baseline_is_environment()
        => Assert.Equal(DeliveryFailureClass.Environment,
            DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
            {
                BaselineHasSameFingerprint = true,
            }).Class);

    [Fact]
    public void Different_fingerprint_on_clean_repeat_is_environment()
        => Assert.Equal(DeliveryFailureClass.Environment,
            DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
            {
                CleanRepeatSameFingerprint = false,
            }).Class);

    [Fact]
    public void Sporadic_on_both_sides_is_flaky()
        => Assert.Equal(DeliveryFailureClass.Flaky,
            DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
            {
                SporadicOnBothSides = true,
            }).Class);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Missing_mandatory_measurement_is_inconclusive(bool baseline, bool clean)
        => Assert.Equal(DeliveryFailureClass.Inconclusive,
            DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
            {
                BaselineMeasured = baseline,
                CleanRepeatMeasured = clean,
            }).Class);

    [Fact]
    public void Green_delivery_blocked_by_uncited_review_is_a_concern()
    {
        var result = DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
        {
            ReviewerBlock = true,
            ReviewerCitesDiffEvidence = false,
        });
        Assert.Equal(DeliveryFailureClass.Concern, result.Class);
        Assert.False(result.ChargesCard);
    }

    [Fact]
    public void Cited_diff_regression_can_charge_after_gate_proof()
        => Assert.True(DeliveryFailureDiagnosisPolicy.Classify(Confirmed with
        {
            ReviewerBlock = true,
            ReviewerCitesDiffEvidence = true,
        }).ChargesCard);
}
