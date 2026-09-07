using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

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
/// <para>
/// <see cref="Code"/> answers "which integration step said no";
/// <see cref="FailureClass"/> answers "was the reviewed change at fault at all".
/// The two are independent: the same <c>build-gate-failed</c> code carries
/// <see cref="RunFailureClass.Product"/> for a failing test and
/// <see cref="RunFailureClass.Infrastructure"/> for a git network timeout
/// (AGT-2749).
/// </para>
/// </summary>
public sealed record AcceptedIntegrationFailure(
    string Code,
    string Label,
    string Reason,
    bool RebaseRecoveryAvailable,
    RunFailureClass FailureClass = RunFailureClass.Unknown,
    string FailureSignature = RunFailureSignatures.Unclassified);

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
        var isNoTaskBranch = string.Equals(
            verdict,
            "no-branch",
            StringComparison.OrdinalIgnoreCase);
        if (status != PipelineStepStatus.Failed && !isNoTaskBranch) return null;

        var code = NormalizePersistedCode(persistedCode)
            ?? InferCode(verdict, reason);

        // AGT-2749: classify the raw step evidence once against the shared
        // taxonomy so a failure that describes the host or the provider account
        // can be requeued instead of parked. The code keeps its own recovery
        // semantics: merge-conflict and source-needs-rebase stay
        // rebase-recoverable whatever the class says.
        var verdictClass = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = Evidence(reason, verdictSummary),
        });
        var failureClass = verdictClass.Class;
        var signature = verdictClass.Signature;

        return code switch
        {
            AcceptedIntegrationFailureCodes.MergeConflict => new(
                code,
                "Merge conflict",
                FirstNonBlank(
                    verdictSummary,
                    reason,
                    "The delivery conflicts with the current integration branch."),
                RebaseRecoveryAvailable: true,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.BuildGateFailed => new(
                code,
                "Build gate failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The build gate rejected the merged result."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.DeliveryGateFailed => new(
                code,
                "Delivery gate failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The Remote delivery gate rejected the reviewed result before integration."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.SourceNeedsRebase => new(
                code,
                "Rebase required",
                "The reviewed delivery is behind the current integration branch and must be rebased before acceptance.",
                RebaseRecoveryAvailable: true,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous => new(
                code,
                "Delivery attribution needs a new round",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "Automatic integration could not retain a one-to-one delivery commit mapping."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable => new(
                code,
                "Task key unavailable",
                "The task key could not be resolved while validating the reviewed delivery. Retry acceptance after task storage is available.",
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.ReviewSubjectInvalid => new(
                code,
                "Review subject invalid",
                "The reviewed delivery no longer matches the task's current authoritative run.",
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.NoTaskBranch => new(
                code,
                "No task branch",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The accepted coding card had no delivery branch to integrate."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked => new(
                code,
                "Integration push blocked",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The delivery merged into the integration branch locally but the push to origin is blocked."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
            _ => new(
                AcceptedIntegrationFailureCodes.IntegrationError,
                "Integration failed",
                FirstNonBlank(reason, verdictSummary, "Integration failed without a diagnostic."),
                RebaseRecoveryAvailable: false,
                failureClass,
                signature),
        };
    }

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

    /// <summary>
    /// Both evidence fields, one per line. The classifier reads line by line, so
    /// a host signature in the reason is not hidden by a generic verdict summary
    /// (or the other way round).
    /// </summary>
    private static string Evidence(string? reason, string? verdictSummary)
        => string.Join(
            '\n',
            new[] { reason, verdictSummary }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FirstNonBlank(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}
