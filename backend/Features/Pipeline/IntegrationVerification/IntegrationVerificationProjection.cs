namespace AgentStudio.Pipeline;

/// <summary>
/// Projects verification evidence onto the current integration branch tip.
/// Historical records and pipeline outcomes cannot verify a different tree.
/// A verified record stays the card's evidence after the tip advances only
/// while the branch still carries the exact tree it names and that tree
/// contained the card's current delivery; a rewritten branch or a newer
/// delivery makes it stale.
/// </summary>
public static class IntegrationVerificationProjection
{
    /// <param name="currentDeliverySha">
    /// The card's current delivery identity (full or abbreviated SHA), used to
    /// keep a verified record on an ancestor tree. Null disables that carry.
    /// </param>
    /// <param name="branchCarries">
    /// Whether the current integration branch history contains a SHA. Null
    /// disables the carry, so only the exact tip can match.
    /// </param>
    public static TaskIntegrationVerification Resolve(
        IntegrationVerificationRecord? record,
        PipelineStepExecution? lastMerge,
        IReadOnlyCollection<TaskIntegrationRecord> integrationRecords,
        string? currentIntegrationSha,
        string currentIntegrationBranch,
        string? currentDeliverySha = null,
        Func<string, bool>? branchCarries = null)
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

        // The tip advanced past a verified tree. That tree's verdict is still
        // exact for it, and later commits belong to other cards; re-gating
        // here would run a gate per completed card on every tip change and
        // let an unrelated red tip reopen them.
        if (record is not null
            && CarriesVerifiedDelivery(record, currentIntegrationBranch, currentDeliverySha, branchCarries))
        {
            return record.ToProjection() with
            {
                Reason = $"{record.Reason} {currentIntegrationBranch} still carries that tree at "
                         + $"{currentIntegrationSha}, and the verdict covers the current delivery {currentDeliverySha!.Trim()}.",
            };
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

    /// <summary>
    /// A verified record on a tree the branch still carries, written while
    /// that tree contained the card's current delivery.
    /// </summary>
    public static bool CarriesVerifiedDelivery(
        IntegrationVerificationRecord record,
        string currentIntegrationBranch,
        string? currentDeliverySha,
        Func<string, bool>? branchCarries)
    {
        if (branchCarries is null
            || !string.Equals(record.State, IntegrationVerificationStates.Verified, StringComparison.Ordinal)
            || !SameBranch(record.IntegrationBranch, currentIntegrationBranch)
            || !ReviewSubjectStore.IsValidResultSha(record.Sha))
        {
            return false;
        }

        var delivery = currentDeliverySha?.Trim();
        if (string.IsNullOrEmpty(delivery) || delivery.Length < 7 || !delivery.All(Uri.IsHexDigit)) return false;
        return record.DeliveryShas.Any(sha => ReviewSubjectStore.IsValidResultSha(sha)
                                              && sha.StartsWith(delivery, StringComparison.OrdinalIgnoreCase))
               && branchCarries(record.Sha!);
    }

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
