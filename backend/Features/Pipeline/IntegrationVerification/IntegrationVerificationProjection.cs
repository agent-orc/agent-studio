namespace AgentStudio.Pipeline;

/// <summary>
/// Projects verification evidence onto the current integration branch tip.
/// Historical records and pipeline outcomes cannot verify a different tree.
/// </summary>
public static class IntegrationVerificationProjection
{
    public static TaskIntegrationVerification Resolve(
        IntegrationVerificationRecord? record,
        PipelineStepExecution? lastMerge,
        IReadOnlyCollection<TaskIntegrationRecord> integrationRecords,
        string? currentIntegrationSha)
    {
        if (string.IsNullOrWhiteSpace(currentIntegrationSha))
            return Unverified(null, "The current integration tree SHA is unavailable; gate evidence cannot be matched.");

        if (record is not null)
        {
            if (string.Equals(record.Sha, currentIntegrationSha, StringComparison.OrdinalIgnoreCase))
                return record.ToProjection();

            return Unverified(currentIntegrationSha,
                $"The current integration tree {currentIntegrationSha} differs from the recorded verification tree "
                + $"{record.Sha ?? "unknown"}; the current tree has no matching gate evidence.");
        }

        if (NamesVerifiedTree(integrationRecords, currentIntegrationSha))
            return new TaskIntegrationVerification
            {
                State = IntegrationVerificationStates.Verified,
                Sha = currentIntegrationSha,
                Evidence = IntegrationVerificationEvidence.IntegrationRecord,
                Reason = "An integrated-verified integration record names this exact tree.",
            };

        var outcome = lastMerge?.Verdict;
        return Unverified(currentIntegrationSha,
            string.IsNullOrWhiteSpace(outcome)
                ? $"The delivery is contained in {currentIntegrationSha}, but no gate evidence names that tree."
                : $"The delivery is contained in {currentIntegrationSha}, but the last merge outcome "
                  + $"'{outcome}' has no gate evidence for that exact tree.");
    }

    /// <summary>Only a verified record naming the exact integration SHA proves this tree.</summary>
    public static bool NamesVerifiedTree(IEnumerable<TaskIntegrationRecord> records, string sha)
        => records.Any(record => string.Equals(
                                    record.Classification,
                                    IntegrationRecordClasses.IntegratedVerified,
                                    StringComparison.Ordinal)
                                 && string.Equals(record.IntegrationSha, sha, StringComparison.OrdinalIgnoreCase));

    private static TaskIntegrationVerification Unverified(string? sha, string reason) => new()
    {
        State = IntegrationVerificationStates.Unverified,
        Sha = sha,
        Evidence = IntegrationVerificationEvidence.None,
        Reason = reason,
    };
}
