namespace AgentStudio.TaskServer.Contracts;

/// <summary>The evidence required before a gate or review failure can charge a card.</summary>
public sealed record DeliveryFailureDiagnosisInput(
    string? Fingerprint,
    bool? BaselinePassed,
    string? BaselineFingerprint,
    bool? CleanRepeatPassed,
    string? CleanRepeatFingerprint,
    int OtherCardMatchesLast24Hours = 0,
    int PriorMatchesLast24Hours = 0,
    bool KnownIntermittentPattern = false,
    bool ReviewerBlocked = false,
    bool EvidenceAgainstDiff = true);

public sealed record DeliveryFailureDiagnosisResult(
    string Classification,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public bool ChargesCard => Classification == DeliveryFailureDiagnosis.Product;
}

/// <summary>Pure decision table for the mandatory baseline, clean repeat and history comparison.</summary>
public static class DeliveryFailureDiagnosis
{
    public const string Product = "product";
    public const string Environment = "environment";
    public const string Intermittent = "intermittent";
    public const string FirstOccurrence = "unclassified-first-occurrence";
    public const string Concern = "concern";

    public static DeliveryFailureDiagnosisResult Classify(DeliveryFailureDiagnosisInput input)
    {
        var evidence = new List<string>
        {
            $"fingerprint={input.Fingerprint ?? "unavailable"}",
            $"baseline={State(input.BaselinePassed)}; fingerprint={input.BaselineFingerprint ?? "none"}",
            $"clean-repeat={State(input.CleanRepeatPassed)}; fingerprint={input.CleanRepeatFingerprint ?? "none"}",
            $"history-24h: other-cards={input.OtherCardMatchesLast24Hours}; prior={input.PriorMatchesLast24Hours}",
        };

        if (input.ReviewerBlocked && !input.EvidenceAgainstDiff)
        {
            evidence.Add("review block has no evidence against the delivery diff");
            return new(Concern, 1, evidence);
        }

        // A red or unavailable integration baseline cannot prove a card regression.
        if (input.BaselinePassed == false
            || (input.Fingerprint is not null
                && string.Equals(input.BaselineFingerprint, input.Fingerprint, StringComparison.Ordinal)))
            return new(Environment, 1, evidence);

        if (input.BaselinePassed is null || input.CleanRepeatPassed is null
            || string.IsNullOrWhiteSpace(input.Fingerprint))
            return new(FirstOccurrence, 0.2, evidence);

        if (input.CleanRepeatPassed == true)
        {
            if (input.KnownIntermittentPattern || input.PriorMatchesLast24Hours > 0)
                return new(Intermittent, 0.9, evidence);
            return new(FirstOccurrence, 0.5, evidence);
        }

        if (input.OtherCardMatchesLast24Hours > 0)
            return new(Environment, 0.9, evidence);

        if (input.BaselinePassed == true
            && string.Equals(input.CleanRepeatFingerprint, input.Fingerprint, StringComparison.Ordinal))
            return new(Product, 1, evidence);

        return new(FirstOccurrence, 0.4, evidence);
    }

    private static string State(bool? passed) => passed switch
    {
        true => "green",
        false => "red",
        _ => "unavailable",
    };
}
