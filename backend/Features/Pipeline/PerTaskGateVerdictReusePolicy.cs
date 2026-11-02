namespace AgentStudio.Pipeline;

/// <param name="Ownership">The card's batch gate marker, or null.</param>
/// <param name="IntegrationBranch">Branch this merge targets.</param>
/// <param name="PreMergeTipSha">Exact integration tip immediately before this merge.</param>
/// <param name="DeliveryContainedInMergeResult">The marker's result SHA is an ancestor of the merge result.</param>
/// <param name="MergeResultTreeSha">Tree of the merge result.</param>
public sealed record PerTaskGateVerdictReuseInput(
    BatchGateOwnership? Ownership,
    string IntegrationBranch,
    string? PreMergeTipSha,
    bool DeliveryContainedInMergeResult,
    string? MergeResultTreeSha);

/// <param name="EvidencePath">The per-task gate evidence file beside the card, when reused.</param>
public sealed record PerTaskGateVerdictReuseDecision(bool Reused, string Reason, string? EvidencePath = null);

/// <summary>
/// Whether the merge into the integration branch may stand on the per-task
/// batch fallback verdict instead of running its own gate (AGT-3434). That
/// gate ran the full suite on the delivery merged onto one integration tip.
/// When the merge lands on the same tip with the same tree, the publish step
/// would test exactly that tree again. A moved tip, a different tree, a
/// verdict for another delivery or branch, or a candidate tested on the
/// delivery's own base keeps the merge gate.
/// </summary>
public static class PerTaskGateVerdictReusePolicy
{
    public static PerTaskGateVerdictReuseDecision Decide(PerTaskGateVerdictReuseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ownership = input.Ownership;
        if (ownership is null)
            return Gate("no per-task gate verdict is recorded beside the task");
        if (string.IsNullOrWhiteSpace(ownership.FallbackTestedSha)
            || !string.Equals(ownership.FallbackTestedSha, ownership.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase))
            return Gate("the per-task gate has not passed for this delivery");
        if (string.IsNullOrWhiteSpace(ownership.FallbackBaseSha)
            || string.IsNullOrWhiteSpace(ownership.FallbackCandidateTreeSha))
            return Gate("the per-task gate tested the delivery on its own base, not on the integration tip");
        var verified = TaskIntegrationBranch.Name(ownership.Subject.IntegrationBranch, fallback: string.Empty);
        var target = TaskIntegrationBranch.Name(input.IntegrationBranch, fallback: string.Empty);
        if (verified.Length == 0 || !string.Equals(verified, target, StringComparison.Ordinal))
            return Gate($"the per-task gate verified '{verified}', this merge targets '{target}'");
        if (!input.DeliveryContainedInMergeResult)
            return Gate($"the merge result does not contain the delivery {Short(ownership.Subject.ResultSha)}");
        if (string.IsNullOrWhiteSpace(input.PreMergeTipSha))
            return Gate("the pre-merge integration tip could not be determined");
        if (!string.Equals(input.PreMergeTipSha, ownership.FallbackBaseSha, StringComparison.OrdinalIgnoreCase))
            return Gate($"{target} moved since the per-task gate: tip is {Short(input.PreMergeTipSha)}, "
                + $"the gate tested on {Short(ownership.FallbackBaseSha)}");
        if (!string.Equals(input.MergeResultTreeSha, ownership.FallbackCandidateTreeSha,
                StringComparison.OrdinalIgnoreCase))
            return Gate("the merge result tree differs from the tree the per-task gate tested");
        return new(true,
            $"the per-task gate passed tree {Short(ownership.FallbackCandidateTreeSha)} on the unchanged "
            + $"{target} tip {Short(ownership.FallbackBaseSha)}; the merge result has the same tree",
            ownership.FallbackEvidencePath);
    }

    private static PerTaskGateVerdictReuseDecision Gate(string reason) => new(false, reason);

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length <= 8 ? sha : sha[..8];
}
