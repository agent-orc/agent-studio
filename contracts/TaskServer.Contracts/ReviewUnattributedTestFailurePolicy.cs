namespace AgentStudio.TaskServer.Contracts;

/// <summary>A measured but unattributable test failure is a review concern, not a card blocker.</summary>
public static class ReviewUnattributedTestFailurePolicy
{
    public const string EvidencePrefix = "review-test-attribution: ";

    public static bool IsNonBlocking(DeliveryFailureDiagnosisResult? diagnosis)
        => diagnosis?.Classification == DeliveryFailureDiagnosis.Environment
           && diagnosis.Evidence.Any(item => item.StartsWith(EvidencePrefix, StringComparison.Ordinal));
}
