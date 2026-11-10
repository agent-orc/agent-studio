using System.Text.RegularExpressions;
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
    /// <summary>
    /// The build/test gate failed before verification reached test discovery
    /// (a bundler/toolchain crash), not because of anything in the delivery.
    /// Never a product failure; the card projection keeps this out of
    /// <c>ConflictSkipped</c>/<c>Partial</c> (CAC-18).
    /// </summary>
    public const string GateEnvironmentFailure = "gate-environment-failure";

    /// <summary>
    /// The build/test gate never reached a verdict at all: the process that owned
    /// the merge disappeared (restart, host timeout, kill) while the gate was
    /// still running. Like <see cref="GateEnvironmentFailure"/> this says nothing
    /// about the reviewed change, so the card stays out of
    /// <c>ConflictSkipped</c>/<c>Partial</c> and the integration is replayed for
    /// the same delivery SHA without a new review (AGT-2849).
    /// </summary>
    public const string GateInterrupted = "gate-interrupted";

    // AGT-2995: one typed code per MergeIntoDevelopRunner Error site. Before
    // these existed every such result logged "outcome=Error" and the card read
    // "not yet integrated", so a delivery that only needed a rebase sat
    // unexplained for hours.

    /// <summary>The project's watch path resolves to no repository root.</summary>
    public const string RepositoryRootUnavailable = "repository-root-unavailable";

    /// <summary>The reviewed delivery is no longer the task's current settled run attempt.</summary>
    public const string StaleAttempt = "stale-attempt";

    /// <summary>The Studio-owned integration worktree could not be prepared (AGT-2832).</summary>
    public const string WorktreeUnavailable = "worktree-unavailable";

    /// <summary>The integration branch could not be synchronized with origin before the merge.</summary>
    public const string BranchSyncFailed = "branch-sync-failed";

    /// <summary>
    /// The integration fetch failed because Git's credential for origin was
    /// missing or refused. A transient host fault, not a fact about the delivery:
    /// the bounded gate-environment ladder replays the integration and parks the
    /// card only when the ladder is spent.
    /// </summary>
    public const string GitCredentialUnavailable = "git-credential-unavailable";

    /// <summary>The typed code of a failed integration-branch sync.</summary>
    public static string ForBranchSync(string? error)
        => RunFailureClassifier.IsGitCredentialRefusal(error) ? GitCredentialUnavailable : BranchSyncFailed;

    /// <summary>
    /// The main/develop lineage forbids advancing the release line (main is not
    /// an ancestor of develop). Distinct from <see cref="IntegrationPushBlocked"/>,
    /// which is the push step's lineage block after a successful merge.
    /// </summary>
    public const string LineageBlocked = "lineage-blocked";

    /// <summary>A mechanical rebase merged, but its commit attribution could not be persisted; the merge was rolled back.</summary>
    public const string RebaseAttributionFailed = "rebase-attribution-failed";

    /// <summary>An exact branch, tip, or merge-result SHA the merge needs could not be resolved.</summary>
    public const string IntegrationRefUnresolved = "integration-ref-unresolved";

    /// <summary>The mandatory pre-main test gate is not wired, so main is not advanced.</summary>
    public const string GateUnavailable = "gate-unavailable";

    /// <summary>
    /// The repository defines no verify commands and nothing is derivable, so
    /// no check ran (AGT-3428). A setup defect, not a red gate: the gate failure
    /// router never classifies it, and the reason names the missing file.
    /// </summary>
    public const string VerifyDefinitionMissing = "verify-definition-missing";

    /// <summary>
    /// The release line moved during every bounded full-suite run on the merge
    /// result of a delivery that was behind it (AGT-3435). Nothing was published;
    /// the delivery itself needs no change.
    /// </summary>
    public const string ReleaseLineMoving = "release-line-moving";

    /// <summary>
    /// Maps a non-successful merge result onto its typed code. An
    /// <see cref="MergeIntoIntegrationOutcome.Error"/> result carries its own
    /// <see cref="MergeIntoIntegrationResult.FailureCode"/>; a legacy Error
    /// without one is inferred from its reason. Successful and deliberately
    /// deferred outcomes have no failure code.
    /// </summary>
    public static string? For(MergeIntoIntegrationResult result)
        => result.Outcome switch
        {
            MergeIntoIntegrationOutcome.Error =>
                result.FailureCode ?? AcceptedIntegrationFailurePolicy.InferErrorCode(result.Error),
            MergeIntoIntegrationOutcome.Conflict => MergeConflict,
            MergeIntoIntegrationOutcome.AgentRoundRequired => DeliveryAttributionAmbiguous,
            MergeIntoIntegrationOutcome.GateFailed => BuildGateFailed,
            MergeIntoIntegrationOutcome.GateEnvironmentFailure => GateEnvironmentFailure,
            MergeIntoIntegrationOutcome.NoTaskBranch => NoTaskBranch,
            _ => null,
        };
}

/// <summary>
/// Card-safe classification of one terminal accepted-integration failure.
/// </summary>
/// <param name="FailureClass">
/// AGT-2749: whether the raw evidence attributes this failure to the reviewed
/// change, the host, or the provider account (see <see cref="RunFailureClass"/>).
/// The acceptance rail requeues <see cref="RunFailureClass.Infrastructure"/> and
/// <see cref="RunFailureClass.Quota"/> instead of parking them.
/// </param>
/// <param name="FailureSignature">Stable signature slug behind <see cref="FailureClass"/>; see <see cref="RunFailureSignatures"/>.</param>
public sealed record AcceptedIntegrationFailure(
    string Code,
    string Label,
    string Reason,
    bool RebaseRecoveryAvailable,
    RunFailureClass FailureClass = RunFailureClass.Unknown,
    string FailureSignature = RunFailureSignatures.Unclassified)
{
    public IntegrationConflictReport? ConflictReport { get; init; }

    /// <summary>
    /// AGT-3695: the failure left the reviewed delivery untouched and only its
    /// publication is missing, so replaying the integration of the same
    /// delivery SHA (Retry integration) can finish it without a new review.
    /// </summary>
    public bool ReplayAvailable { get; init; }
}

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
        // AGT-3428: a not-applicable gate used to be recorded as a red build or
        // delivery gate. Its text still identifies the missing definition.
        if (code is AcceptedIntegrationFailureCodes.BuildGateFailed
                or AcceptedIntegrationFailureCodes.DeliveryGateFailed
            && (VerifyDefinitionPolicy.DescribesMissingDefinition(reason)
                || VerifyDefinitionPolicy.DescribesMissingDefinition(verdictSummary)))
            code = AcceptedIntegrationFailureCodes.VerifyDefinitionMissing;

        // AGT-2749: classify off the raw evidence (not the code-specific
        // fallback copy below) so a git network timeout or a gate-run budget
        // overrun is attributed to the host even when the persisted code is
        // a generic "integration-error". A merge conflict / rebase-required
        // code carries its own recovery path (RebaseRecoveryAvailable) that
        // the acceptance rail checks first, so its class is informational.
        var classified = RunFailureClassifier.Classify(new RunFailureEvidence
        {
            Text = reason ?? verdictSummary ?? string.Empty,
            ParsedTestFailures = code == AcceptedIntegrationFailureCodes.BuildGateFailed
                && reason?.Contains("failed tests were observed before cutoff", StringComparison.Ordinal) == true ? 1 : 0,
        });

        AcceptedIntegrationFailure failure = code switch
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
                "Merge conflict",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "Automatic integration could not retain a one-to-one delivery commit mapping."),
                RebaseRecoveryAvailable: true),
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
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked
                when (DescribeApprovedMergeOffLane(reason) ?? DescribeApprovedMergeOffLane(verdictSummary))
                    is { } offLane => new(code, "Integration push blocked", offLane, RebaseRecoveryAvailable: false)
                {
                    ReplayAvailable = true,
                },
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked => new(
                code,
                "Integration push blocked",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The delivery merged into the integration branch locally but the push to origin is blocked."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure => new(
                code,
                "Gate environment failure",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The gate host or run budget prevented verification from completing."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.GateInterrupted => new(
                code,
                "Integration gate interrupted",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The integration build gate was interrupted before it reached a verdict."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.RepositoryRootUnavailable => new(
                code,
                "Repository unavailable",
                FirstNonBlank(reason, verdictSummary, "The project's repository root could not be resolved."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.StaleAttempt => new(
                code,
                "Stale attempt",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The reviewed delivery no longer matches the task's current authoritative run."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.WorktreeUnavailable => new(
                code,
                "Integration worktree unavailable",
                FirstNonBlank(reason, verdictSummary, "The integration worktree is unavailable."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.BranchSyncFailed => new(
                code,
                "Branch sync failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The integration branch could not be synchronized with origin."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.GitCredentialUnavailable => new(
                code,
                "Git credential unavailable",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The integration branch could not be fetched because the Git credential for origin was missing or refused."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.LineageBlocked => new(
                code,
                "Lineage blocked",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The release line cannot advance because main is not an ancestor of develop."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.RebaseAttributionFailed => new(
                code,
                "Rebase attribution failed",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "Mechanical rebase attribution could not be persisted."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.IntegrationRefUnresolved => new(
                code,
                "Integration ref unresolved",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "An exact branch or commit the integration needs could not be resolved."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.GateUnavailable => new(
                code,
                "Gate unavailable",
                FirstNonBlank(reason, verdictSummary, "The mandatory integration gate is not available."),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.VerifyDefinitionMissing => new(
                code,
                "Verify commands missing",
                // A legacy record carries only the bare gate reason; the generic
                // message still names the file and shows an example.
                FirstNonBlank(
                    HasPrefix(reason) ? reason : null,
                    HasPrefix(verdictSummary) ? verdictSummary : null,
                    VerifyDefinitionPolicy.Message(VerifyDefinitionPolicy.ExampleFor(null))),
                RebaseRecoveryAvailable: false),
            AcceptedIntegrationFailureCodes.ReleaseLineMoving => new(
                code,
                "Release line kept moving",
                FirstNonBlank(
                    reason,
                    verdictSummary,
                    "The release line moved during every full-suite run on the merge result; nothing was published."),
                RebaseRecoveryAvailable: false),
            _ => new(
                AcceptedIntegrationFailureCodes.IntegrationError,
                "Integration failed",
                FirstNonBlank(reason, verdictSummary, "Integration failed without a diagnostic."),
                RebaseRecoveryAvailable: false),
        };
        if (code == AcceptedIntegrationFailureCodes.GateEnvironmentFailure
            && BuildTestGateRunner.IsPreparationCacheNuGetFailure(reason ?? verdictSummary))
        {
            classified = new RunFailureVerdict(
                RunFailureClass.Infrastructure,
                RunFailureSignatures.GatePreparationCacheTorn,
                "The gate's private NuGet preparation cache was torn before verification completed.");
        }
        return failure with
        {
            FailureClass = classified.Class,
            FailureSignature = classified.Signature,
        };
    }

    private static string InferCode(string? verdict, string? reason)
    {
        if (string.Equals(verdict, "conflict", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.MergeConflict;
        if (string.Equals(verdict, "gate-failed", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.BuildGateFailed;
        if (string.Equals(verdict, "gate-environment-failure", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.GateEnvironmentFailure;
        if (string.Equals(verdict, "gate-interrupted", StringComparison.OrdinalIgnoreCase))
            return AcceptedIntegrationFailureCodes.GateInterrupted;
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

        return InferErrorCode(reason);
    }

    /// <summary>
    /// Infers the code of an Error whose writer predates typed codes (or of a
    /// Git primitive that only reports prose). Anything unrecognized stays the
    /// generic <see cref="AcceptedIntegrationFailureCodes.IntegrationError"/>.
    /// </summary>
    public static string InferErrorCode(string? reason)
    {
        var detail = reason ?? string.Empty;
        if (detail.Contains(
                "no stable key for review-subject validation",
                StringComparison.OrdinalIgnoreCase))
        {
            return AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable;
        }
        if (detail.Contains("must be rebased onto", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("is not a fast-forward of", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("moved after the pre-main test run", StringComparison.OrdinalIgnoreCase))
        {
            return AcceptedIntegrationFailureCodes.SourceNeedsRebase;
        }
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
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure => AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            AcceptedIntegrationFailureCodes.GateInterrupted => AcceptedIntegrationFailureCodes.GateInterrupted,
            AcceptedIntegrationFailureCodes.RepositoryRootUnavailable => AcceptedIntegrationFailureCodes.RepositoryRootUnavailable,
            AcceptedIntegrationFailureCodes.StaleAttempt => AcceptedIntegrationFailureCodes.StaleAttempt,
            AcceptedIntegrationFailureCodes.WorktreeUnavailable => AcceptedIntegrationFailureCodes.WorktreeUnavailable,
            AcceptedIntegrationFailureCodes.BranchSyncFailed => AcceptedIntegrationFailureCodes.BranchSyncFailed,
            AcceptedIntegrationFailureCodes.GitCredentialUnavailable => AcceptedIntegrationFailureCodes.GitCredentialUnavailable,
            AcceptedIntegrationFailureCodes.LineageBlocked => AcceptedIntegrationFailureCodes.LineageBlocked,
            AcceptedIntegrationFailureCodes.RebaseAttributionFailed => AcceptedIntegrationFailureCodes.RebaseAttributionFailed,
            AcceptedIntegrationFailureCodes.IntegrationRefUnresolved => AcceptedIntegrationFailureCodes.IntegrationRefUnresolved,
            AcceptedIntegrationFailureCodes.GateUnavailable => AcceptedIntegrationFailureCodes.GateUnavailable,
            AcceptedIntegrationFailureCodes.VerifyDefinitionMissing => AcceptedIntegrationFailureCodes.VerifyDefinitionMissing,
            AcceptedIntegrationFailureCodes.ReleaseLineMoving => AcceptedIntegrationFailureCodes.ReleaseLineMoving,
            _ => null,
        };
    }

    /// <summary>Start of the plain reason for an approved merge the integration line does not carry.</summary>
    public const string ApprovedMergeOffLanePrefix = "Publication stopped: the approved merge result";

    private static readonly Regex ApprovedMergeOffLanePattern = new(
        @"The approved merge result (?<sha>[0-9a-fA-F]{7,64}) is not contained in local (?<branch>[^\s;]+); refusing to push it",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// AGT-3695: the plain-words reason for a <c>sha-not-on-branch</c> push
    /// refusal, read from the git refusal itself or from a reason already in
    /// this form. Null when <paramref name="text"/> describes another failure.
    /// </summary>
    public static string? DescribeApprovedMergeOffLane(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith(ApprovedMergeOffLanePrefix, StringComparison.Ordinal)) return trimmed;
        var match = ApprovedMergeOffLanePattern.Match(trimmed);
        if (!match.Success) return null;
        var sha = match.Groups["sha"].Value;
        var branch = match.Groups["branch"].Value;
        return $"{ApprovedMergeOffLanePrefix} {sha[..Math.Min(9, sha.Length)]} is not on the local {branch} "
               + $"integration line, so nothing was pushed to origin/{branch}. The reviewed delivery is unchanged; "
               + "Retry integration replays it without a new review round.";
    }

    private static bool HasPrefix(string? text)
        => text?.Contains(VerifyDefinitionPolicy.MessagePrefix, StringComparison.Ordinal) == true;

    private static string FirstNonBlank(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}
