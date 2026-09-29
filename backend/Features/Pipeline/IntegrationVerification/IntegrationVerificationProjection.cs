namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3002 - pure projection of a merged card's gate evidence onto its
/// integration status. The lane's own record wins. A card integrated before
/// the lane wrote that record is read from its last merge step: a fresh merge
/// went through its gate, a legacy "already merged" step is verified only when
/// it carried a gate verdict, and a failed step on a contained delivery means
/// the delivery reached the branch some other way. Without either, the answer
/// is unknown (null), never an invented verdict.
/// </summary>
public static class IntegrationVerificationProjection
{
    public static TaskIntegrationVerification? Resolve(
        IntegrationVerificationRecord? record,
        PipelineStepExecution? lastMerge,
        IReadOnlyCollection<TaskIntegrationRecord> integrationRecords)
    {
        if (record is not null) return record.ToProjection();

        if (lastMerge is not null && lastMerge.Status != PipelineStepStatus.Pending)
        {
            var verdict = lastMerge.Verdict?.Trim().ToLowerInvariant();
            if (lastMerge.Status == PipelineStepStatus.Passed)
            {
                return verdict switch
                {
                    "merged" or "merged-after-rebase" => Verified(
                        IntegrationVerificationEvidence.MergeGate,
                        "The integration lane created this merge and its gate admitted it."),
                    "already-merged" when lastMerge.GateVerdictSource is not null => Verified(
                        IntegrationVerificationEvidence.GateReceipt,
                        "The recorded already-merged outcome carried an exact gate verdict."),
                    "already-merged" or "already-integrated" or "already-on-integration-branch" => Unverified(
                        "The card was completed as already merged without gate evidence for the merged tree."),
                    _ => null,
                };
            }

            if (lastMerge.Status == PipelineStepStatus.Failed
                && !string.Equals(verdict, "operator-override", StringComparison.Ordinal))
            {
                return Unverified(
                    $"The latest integration attempt ended '{verdict ?? "failed"}', "
                    + "but the branch contains the delivery: it arrived without a gate this card can name.");
            }

            return null;
        }

        return integrationRecords.Any(IsVerifiedRecord)
            ? Verified(
                IntegrationVerificationEvidence.IntegrationRecord,
                "An integrated-verified integration record exists for this card.")
            : null;
    }

    /// <summary>
    /// True when an <c>integrated-verified</c> record names
    /// <paramref name="sha"/> as the exact integration SHA. Records that prove
    /// containment only (no integration SHA) do not name a tree.
    /// </summary>
    public static bool NamesVerifiedTree(IEnumerable<TaskIntegrationRecord> records, string sha)
        => records.Any(record => IsVerifiedRecord(record)
                                 && string.Equals(record.IntegrationSha, sha, StringComparison.OrdinalIgnoreCase));

    private static bool IsVerifiedRecord(TaskIntegrationRecord record)
        => string.Equals(record.Classification, IntegrationRecordClasses.IntegratedVerified, StringComparison.Ordinal);

    private static TaskIntegrationVerification Verified(string evidence, string reason) => new()
    {
        State = IntegrationVerificationStates.Verified,
        Evidence = evidence,
        Reason = reason,
    };

    private static TaskIntegrationVerification Unverified(string reason) => new()
    {
        State = IntegrationVerificationStates.Unverified,
        Evidence = IntegrationVerificationEvidence.None,
        Reason = reason,
    };
}
