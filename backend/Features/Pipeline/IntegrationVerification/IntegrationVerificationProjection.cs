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
        string? currentIntegrationSha,
        string currentIntegrationBranch)
    {
        if (string.IsNullOrWhiteSpace(currentIntegrationSha))
            return Unverified(null, "The current integration tree SHA is unavailable; gate evidence cannot be matched.");

        // A later verified integration record can settle a tree previously
        // recorded as unverified. Check it before the task-folder snapshot.
        if (NamesVerifiedTree(integrationRecords, currentIntegrationSha, currentIntegrationBranch))
            return new TaskIntegrationVerification
            {
                State = IntegrationVerificationStates.Verified,
                Sha = currentIntegrationSha,
                Evidence = IntegrationVerificationEvidence.IntegrationRecord,
                Reason = "An integrated-verified integration record names this exact tree.",
            };

        if (record is not null
            && string.Equals(record.Sha, currentIntegrationSha, StringComparison.OrdinalIgnoreCase)
            && SameBranch(record.IntegrationBranch, currentIntegrationBranch))
        {
            return record.ToProjection();
        }

        if (record is not null)
            return Unverified(currentIntegrationSha,
                $"The current integration tree {currentIntegrationSha} on {currentIntegrationBranch} differs from "
                + $"the recorded verification tree {record.Sha ?? "unknown"} on "
                + $"{record.IntegrationBranch}; the current branch has no matching gate evidence.");

        var outcome = lastMerge?.Verdict;
        return Unverified(currentIntegrationSha,
            string.IsNullOrWhiteSpace(outcome)
                ? $"The delivery is contained in {currentIntegrationSha}, but no gate evidence names that tree."
                : $"The delivery is contained in {currentIntegrationSha}, but the last merge outcome "
                  + $"'{outcome}' has no gate evidence for that exact tree.");
    }

    /// <summary>Only a verified record naming the exact integration SHA and branch proves this tree.</summary>
    public static bool NamesVerifiedTree(IEnumerable<TaskIntegrationRecord> records, string sha, string branch)
        => records.Any(record => string.Equals(
                                    record.Classification,
                                    IntegrationRecordClasses.IntegratedVerified,
                                    StringComparison.Ordinal)
                                 && string.Equals(record.IntegrationSha, sha, StringComparison.OrdinalIgnoreCase)
                                 && SameBranch(record.IntegrationBranch, branch));

    /// <summary>Local and remote ref spellings of one branch carry the same gate scope.</summary>
    public static bool SameBranch(string? recordedBranch, string? currentBranch)
        => !string.IsNullOrWhiteSpace(recordedBranch)
           && !string.IsNullOrWhiteSpace(currentBranch)
           && string.Equals(
               AgentStudio.Tasks.TaskIntegrationBranch.Name(recordedBranch, fallback: string.Empty),
               AgentStudio.Tasks.TaskIntegrationBranch.Name(currentBranch, fallback: string.Empty),
               StringComparison.OrdinalIgnoreCase);

    private static TaskIntegrationVerification Unverified(string? sha, string reason) => new()
    {
        State = IntegrationVerificationStates.Unverified,
        Sha = sha,
        Evidence = IntegrationVerificationEvidence.None,
        Reason = reason,
    };
}
