namespace AgentStudio.Pipeline;

/// <summary>
/// Inputs the local integration gate has about one merge, gathered by
/// <see cref="MergeIntoDevelopRunner"/> and handed to the pure decision below.
/// </summary>
/// <param name="Enabled">Resolved project setting.</param>
/// <param name="Review">Record of what the Remote Review verified, or null.</param>
/// <param name="IntegrationBranch">Branch this merge targets.</param>
/// <param name="PreMergeTipSha">
/// Exact integration tip immediately before this merge. Null when it could not be derived.
/// </param>
/// <param name="DeliveryContainedInMergeResult">
/// True when the reviewed result SHA is an ancestor of (or equal to) the merge
/// result, i.e. this merge really integrated the reviewed delivery.
/// </param>
/// <param name="DeliveryWasReplayed">
/// True when the delivery had to be mechanically rebased onto a moved target
/// before it could merge. The merged content is then no longer the content the
/// review built.
/// </param>
public sealed record IntegrationGateReuseInput(
    bool Enabled,
    ReviewVerificationRecord? Review,
    string IntegrationBranch,
    string? PreMergeTipSha,
    bool DeliveryContainedInMergeResult,
    bool DeliveryWasReplayed,
    string? MergeResultTreeSha = null,
    bool ConflictsResolved = false);

/// <summary>What the gate does with this merge.</summary>
public enum IntegrationGateReuseState
{
    /// <summary>Run the full gate: build, tests, lint.</summary>
    FullGate,

    /// <summary>
    /// Reuse the Remote Review verdict for the tests and lint, and keep only
    /// the compile step, because the merge result is the exact state the review
    /// tested, with the same integration tip.
    /// </summary>
    Reused,
}

/// <summary>
/// Decision plus the one-line justification written into the gate evidence.
/// </summary>
public sealed record IntegrationGateReuseDecision(
    IntegrationGateReuseState State,
    string Reason,
    string? ReviewAttemptId = null)
{
    public bool Reused => State == IntegrationGateReuseState.Reused;

    /// <summary>Stable token for the gate-evidence header and logs.</summary>
    public string Token => Reused ? "reused" : "full";
}

/// <summary>
/// Decides whether the local integration gate may stand on the Remote Review
/// verdict instead of running the suite the review just ran (AGT-2839).
///
/// <para>
/// Reuse requires the exact integration tip captured before review commands
/// and a merge tree identical to the tested tree. Merge-base equality cannot
/// prove this: unrelated integration commits leave the merge base unchanged.
/// Missing proof, conflict resolution, replay, or a changed tip/tree keeps the
/// full gate. The compile step always runs on the local merge result.
/// </para>
/// </summary>
public static class IntegrationGateReusePolicy
{
    /// <summary>
    /// Resolved reuse setting: the explicit project value, otherwise on for a
    /// project whose execution is placed on a remote runner (and which therefore
    /// gets a Remote Review to reuse), off for a locally executing one. Null
    /// settings (legacy fixtures) stay off. The placement is resolved through
    /// <see cref="ProjectExecutionPolicy"/> so the answer does not depend on
    /// whether the caller handed in a migrated record.
    /// </summary>
    public static bool IsEnabled(ProjectSettings? settings)
        => settings is not null
           && (settings.IntegrationGateReviewReuse
               ?? !ProjectExecutionPolicy.IsLocalExecution(settings));

    public static IntegrationGateReuseDecision Decide(IntegrationGateReuseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.Enabled)
            return Full("the project setting keeps the full integration gate");

        var review = input.Review;
        if (review is null)
            return Full("no settled Remote Review verification is recorded beside the task");

        if (!string.Equals(review.Outcome, "Pass", StringComparison.OrdinalIgnoreCase))
            return Full($"the Remote Review ended with '{review.Outcome}', not Pass", review.AttemptId);

        if (!string.Equals(
                review.BuildTestGate,
                ReviewBuildTestGateClasses.Passed,
                StringComparison.OrdinalIgnoreCase))
        {
            return Full(
                $"the Remote Review build/test gate was '{review.BuildTestGate}', so there is no verdict to reuse",
                review.AttemptId);
        }

        if (string.IsNullOrWhiteSpace(review.MergeBaseSha))
            return Full("the Remote Review report records no merge base", review.AttemptId);

        if (string.IsNullOrWhiteSpace(review.IntegrationTipSha))
            return Full("the Remote Review report records no integration tip", review.AttemptId);

        if (input.ConflictsResolved)
            return Full("the merge required conflict resolution", review.AttemptId);

        var reviewed = TaskIntegrationBranch.Name(review.IntegrationRef, fallback: string.Empty);
        var target = TaskIntegrationBranch.Name(input.IntegrationBranch, fallback: string.Empty);
        if (reviewed.Length == 0)
            return Full("the Remote Review report records no integration ref", review.AttemptId);
        if (!string.Equals(reviewed, target, StringComparison.Ordinal))
        {
            return Full(
                $"the Remote Review compared against '{reviewed}', this merge targets '{target}'",
                review.AttemptId);
        }

        if (!input.DeliveryContainedInMergeResult)
        {
            return Full(
                $"the merge result does not contain the reviewed delivery {Short(review.ResultSha)}",
                review.AttemptId);
        }

        if (input.DeliveryWasReplayed)
        {
            return Full(
                "the delivery was mechanically replayed onto a moved integration base, "
                + "so the merged content is not what the review built",
                review.AttemptId);
        }

        if (string.IsNullOrWhiteSpace(input.PreMergeTipSha))
            return Full("the pre-merge integration tip could not be determined", review.AttemptId);

        if (!string.Equals(
                input.PreMergeTipSha,
                review.IntegrationTipSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return Full(
                $"{target} moved since the review: tip is {Short(input.PreMergeTipSha)}, "
                + $"the review verified {Short(review.IntegrationTipSha)}",
                review.AttemptId);
        }

        if (string.IsNullOrWhiteSpace(review.TestedTreeSha)
            || string.IsNullOrWhiteSpace(input.MergeResultTreeSha))
            return Full("the reviewed or merged tree proof is missing", review.AttemptId);

        if (!string.Equals(review.TestedTreeSha, input.MergeResultTreeSha, StringComparison.OrdinalIgnoreCase))
            return Full("the merge result tree differs from the tree tested by the Remote Review", review.AttemptId);

        return new IntegrationGateReuseDecision(
            IntegrationGateReuseState.Reused,
            $"the Remote Review verified {Short(review.ResultSha)} on the unchanged {target} tip "
            + $"{Short(review.IntegrationTipSha)} with tested tree {Short(review.TestedTreeSha)}; only the compile step runs on the merge result",
            review.AttemptId);
    }

    private static IntegrationGateReuseDecision Full(string reason, string? attemptId = null)
        => new(IntegrationGateReuseState.FullGate, reason, attemptId);

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length <= 8 ? sha : sha[..8];
}
