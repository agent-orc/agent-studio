namespace AgentStudio.Pipeline;

/// <summary>
/// Durable verdict tokens of the accepted-integration step that other policies
/// key on. Only the tokens with cross-component meaning live here.
/// </summary>
public static class IntegrationStepVerdicts
{
    /// <summary>
    /// AGT-2720 - the gate could not judge the merge subject because its own
    /// environment failed before the first test. The attempt is undecided: the
    /// card stays <c>pending</c> with this reason and the backstop retries it.
    /// It is never a typed integration failure.
    /// </summary>
    public const string GateEnvironment = "gate-environment";
}

/// <summary>
/// Stable failure codes projected from the accepted-integration step. The raw
/// pipeline reason remains evidence; cards consume these codes and concise
/// operator copy instead of treating every failure as an undifferentiated
/// conflict.
/// </summary>
public static class AcceptedIntegrationFailureCodes
{
    public const string MergeConflict = "merge-conflict";
    public const string DeliveryGateFailed = "delivery-gate-failed";
    public const string BuildGateFailed = "build-gate-failed";
    public const string SourceNeedsRebase = "source-needs-rebase";
    public const string DeliveryAttributionAmbiguous = "delivery-attribution-ambiguous";
    public const string ReviewSubjectTaskKeyUnavailable = "review-subject-task-key-unavailable";
    public const string ReviewSubjectInvalid = "review-subject-invalid";
    public const string NoTaskBranch = "no-task-branch";
    public const string IntegrationError = "integration-error";
    /// <summary>
    /// The merge into develop succeeded locally but the push to origin did not
    /// (main/develop lineage blocked, or the remote genuinely diverged). Distinct
    /// from every other code above: those describe the merge step, this one
    /// describes the push step (AGT-2688).
    /// </summary>
    public const string IntegrationPushBlocked = "integration-push-blocked";
}

/// <summary>
/// Card-safe classification of one terminal accepted-integration failure.
/// </summary>
public sealed record AcceptedIntegrationFailure(
    string Code,
    string Label,
    string Reason,
    bool RebaseRecoveryAvailable);

/// <summary>
/// Pure policy that turns a durable merge-step verdict into an operator-facing
/// failure class. Both the pipeline writer and the card projection call this
/// policy so persisted codes, recovery eligibility, and visible copy cannot
/// drift.
/// </summary>
public static class AcceptedIntegrationFailurePolicy
{
    public static AcceptedIntegrationFailure? Classify(
        PipelineStepStatus status,
        string? verdict,
        string? reason,
        string? verdictSummary,
        string? persistedCode = null)
    {
        // AGT-2720: a gate that never reached the first test judged nothing. Even
        // if a caller records it as Failed, it must not become a card failure -
        // that is exactly how CAC-18 was marked partial for 412 green reviews.
        if (IsGateEnvironmentHold(verdict)) return null;

        var isNoTaskBranch = string.Equals(
            verdict,
            "no-branch",
            StringComparison.OrdinalIgnoreCase);
        if (status != PipelineStepStatus.Failed && !isNoTaskBranch) return null;

        var code = NormalizePersistedCode(persistedCode)
            ?? InferCode(verdict, reason);
        return code switch
        {
            AcceptedIntegrationFailureCodes.MergeConflict => new(
                code,
                "Merge conflict",
                FirstNonBlank(
                    verdictSummary,
                    reason,
                    "The delivery conflicts with the current integration branch."),
                RebaseRecoveryAvailable: true),
            AcceptedIntegrationFailureCodes.BuildGateFailed => new(
                code,
                "Build gate failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The build gate rejected the merged result."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.DeliveryGateFailed => new(
                code,
                "Delivery gate failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The Remote delivery gate rejected the reviewed result before integration."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.SourceNeedsRebase => new(
                code,
                "Rebase required",
                "The reviewed delivery is behind the current integration branch and must be rebased before acceptance.",
                RebaseRecoveryAvailable: true),
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous => new(
                code,
                "Delivery attribution needs a new round",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "Automatic integration could not retain a one-to-one delivery commit mapping."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable => new(
                code,
                "Task key unavailable",
                "The task key could not be resolved while validating the reviewed delivery. Retry acceptance after task storage is available.",
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.ReviewSubjectInvalid => new(
                code,
                "Review subject invalid",
                "The reviewed delivery no longer matches the task's current authoritative run.",
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.NoTaskBranch => new(
                code,
                "No task branch",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The accepted coding card had no delivery branch to integrate."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked => new(
                code,
                "Integration push blocked",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The delivery merged into the integration branch locally but the push to origin is blocked."),
                RebaseRecoveryAvailable: false),
            _ => new(
                AcceptedIntegrationFailureCodes.IntegrationError,
                "Integration failed",
                FirstNonBlank(reason, verdictSummary, "Integration failed without a diagnostic."),
                RebaseRecoveryAvailable: false),
        };
    }

    /// <summary>
    /// True when the recorded attempt is an undecided gate-environment hold. Both
    /// the card projection and the merge-queue projection use this so the pending
    /// state and its reason cannot drift apart.
    /// </summary>
    public static bool IsGateEnvironmentHold(string? verdict)
        => string.Equals(
            verdict,
            IntegrationStepVerdicts.GateEnvironment,
            StringComparison.OrdinalIgnoreCase);

    private static string InferCode(string? verdict, string? reason)
    {
        if (string.Equals(verdict, "conflict", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.MergeConflict;
        if (string.Equals(verdict, "gate-failed", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.BuildGateFailed;
        if (string.Equals(verdict, "delivery-gate-failed", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.DeliveryGateFailed;
        if (string.Equals(verdict, "no-branch", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.NoTaskBranch;
        if (string.Equals(verdict, "agent-round-required", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous;
        if (string.Equals(verdict, "lineage-blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verdict, "push-blocked", StringComparison.OrdinalIgnoreCase))
        {
            return AcceptedIntegrationFailureCodes.IntegrationPushBlocked;
        }

        var detail = reason ?? string.Empty;
        if (detail.Contains(
                "no stable key for review-subject validation",
                StringComparison.OrdinalIgnoreCase))
        {
            return AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable;
        }
        if (detail.Contains("must be rebased onto", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.SourceNeedsRebase;
        if (detail.Contains("review subject", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("review-subject", StringComparison.OrdinalIgnoreCase))
        {
            return AcceptedIntegrationFailureCodes.ReviewSubjectInvalid;
        }

        return AcceptedIntegrationFailureCodes.IntegrationError;
    }

    private static string? NormalizePersistedCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return code.Trim().ToLowerInvariant() switch
        {
            AcceptedIntegrationFailureCodes.MergeConflict => AcceptedIntegrationFailureCodes.MergeConflict,
            AcceptedIntegrationFailureCodes.DeliveryGateFailed => AcceptedIntegrationFailureCodes.DeliveryGateFailed,
            AcceptedIntegrationFailureCodes.BuildGateFailed => AcceptedIntegrationFailureCodes.BuildGateFailed,
            AcceptedIntegrationFailureCodes.SourceNeedsRebase => AcceptedIntegrationFailureCodes.SourceNeedsRebase,
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous => AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous,
            AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable => AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable,
            AcceptedIntegrationFailureCodes.ReviewSubjectInvalid => AcceptedIntegrationFailureCodes.ReviewSubjectInvalid,
            AcceptedIntegrationFailureCodes.NoTaskBranch => AcceptedIntegrationFailureCodes.NoTaskBranch,
            AcceptedIntegrationFailureCodes.IntegrationError => AcceptedIntegrationFailureCodes.IntegrationError,
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked => AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            _ => null,
        };
    }

    private static string FirstNonBlank(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}
