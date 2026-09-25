namespace AgentStudio.TaskServer.Contracts;

/// <summary>The attribution required before a failed delivery spends a card budget.</summary>
public enum DeliveryFailureClass
{
    Product,
    Environment,
    Flaky,
    Concern,
    Inconclusive,
}

public sealed record DeliveryFailureEvidence(
    bool BaselineMeasured,
    bool BaselineGreen,
    bool BaselineHasSameFingerprint,
    bool CleanRepeatMeasured,
    bool CleanRepeatGreen,
    bool CleanRepeatSameFingerprint,
    bool SameFingerprintOnOtherCardWithin24Hours,
    bool SporadicOnBothSides,
    bool ReviewerBlock = false,
    bool ReviewerCitesDiffEvidence = false);

public sealed record DeliveryFailureDiagnosis(
    DeliveryFailureClass Class,
    double Confidence,
    string Reason)
{
    public bool ChargesCard => Class == DeliveryFailureClass.Product;
}

/// <summary>
/// Pure decision table for the baseline, uncached repeat, and 24-hour fingerprint
/// comparison. Missing proof never charges a card.
/// </summary>
public static class DeliveryFailureDiagnosisPolicy
{
    public static DeliveryFailureDiagnosis Classify(DeliveryFailureEvidence evidence)
    {
        if (evidence.ReviewerBlock && !evidence.ReviewerCitesDiffEvidence)
            return new(DeliveryFailureClass.Concern, 0.95,
                "Reviewer block has no cited evidence against the delivery diff.");
        if (!evidence.BaselineMeasured || !evidence.CleanRepeatMeasured)
            return new(DeliveryFailureClass.Inconclusive, 0.2,
                "Baseline comparison and an uncached clean repeat are required.");
        if (!evidence.BaselineGreen || evidence.BaselineHasSameFingerprint)
            return new(DeliveryFailureClass.Environment, 0.99,
                "The integration baseline is red or has the same failure; report baseline health.");
        if (evidence.SporadicOnBothSides)
            return new(DeliveryFailureClass.Flaky, 0.9,
                "The same failure is intermittent on both the integration state and delivery.");
        if (evidence.CleanRepeatGreen)
            return new(DeliveryFailureClass.Environment, 0.95,
                "The delivery passes on a clean repeat without restored dependencies.");
        if (!evidence.CleanRepeatSameFingerprint)
            return new(DeliveryFailureClass.Environment, 0.8,
                "The original failure did not reproduce on the clean repeat; it produced a different fingerprint.");
        if (evidence.SameFingerprintOnOtherCardWithin24Hours)
            return new(DeliveryFailureClass.Environment, 0.9,
                "The normalized failure also occurred on another card within 24 hours.");
        return new(DeliveryFailureClass.Product, 0.95,
            "The baseline is green, the uncached repeat is red, and the failure is card specific.");
    }
}
