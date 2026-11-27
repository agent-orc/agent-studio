using AgentStudio.Git;

namespace AgentStudio.Pipeline;

/// <summary>One source for the context given to an agent after integration stalls.</summary>
public static class IntegrationContinuationPrompt
{
    /// <summary>
    /// The coding-mode verification scope in one sentence: focused checks in the
    /// run, whole test projects stay with the gates.
    /// </summary>
    internal const string VerificationScope =
        "Build, run the test classes that cover the change plus new or changed tests, "
        + "run the guard tests on every .NET test project (filter \"" + GuardFirstGatePlan.GuardFilter + "\") "
        + "and the project's lint commands, "
        + "and leave whole test projects to the gates. ";

    /// <summary>
    /// Keyed to the build-test gate's own output (<c>gateEvidence</c>, or the
    /// reason when that gate is the failed stage): review evidence can echo
    /// untrusted diff content and stays data (AGT-2989), so it never earns a
    /// command to run. Scope-aware, because a failed test gate names a whole
    /// <c>commands.test</c> entry, which stays with the gates.
    /// </summary>
    internal const string RerunFailingCommandFirst =
        "If the failed build-test gate output names a failing command, fix that failure first, before any broader test run: "
        + "rerun a failing lint, build, or guard-filter command exactly; "
        + "for a failing `commands.test` entry, do not rerun the whole entry, "
        + "rerun only the failed tests its output names with a class filter on that test project. ";

    public static string Build(
        string taskKey,
        string? deliveryRef,
        string? deliverySha,
        string integrationBranch,
        string stage,
        string reason,
        IntegrationConflictReport? conflict = null,
        string? evidence = null,
        IReadOnlyList<string>? conflictedFiles = null,
        string? integrationTip = null,
        string? evidenceRef = null,
        string? gateEvidence = null,
        bool runnerMergesIntegration = false,
        IReadOnlyList<string>? involvedCards = null)
    {
        var stages = conflict?.Stages.Count > 0
            ? string.Join("; ", conflict.Stages.Select(item =>
                $"{item.Stage}: {item.Outcome}, {item.ConflictedFileCount} conflicted files"
                + (item.StoppedCommitSha is null ? "" : $", stopped at {item.StoppedCommitSha}")))
            : stage;
        var files = conflictedFiles?.Count > 0
            ? string.Join(", ", conflictedFiles)
            : conflict?.ConflictedFiles.Count > 0
                ? string.Join(", ", conflict.ConflictedFiles)
            : "none recorded";
        var isReview = stage.Contains("review", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("quality", StringComparison.OrdinalIgnoreCase);
        var isConflict = conflict is not null
            || stage.Contains("merge", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("rebase", StringComparison.OrdinalIgnoreCase);
        var taskDirection = isReview
            ? "Resolve the recorded review findings on this same task. " + VerificationScope
              + "Then publish the corrected delivery. "
            : isConflict
                ? $"Continue on the pinned delivery. The runner merges current {integrationBranch} only during platform preparation. "
                  + "Read the platform preparation record at the path returned by "
                  + "`git rev-parse --git-path agent-studio-integration-preparation.json`. "
                  + "It records the actual delivery SHA, target SHA, checkout HEAD, unresolved files and exact instructions. "
                  + "For Conflicted, the platform left an active merge: resolve the listed files and keep both sides, including integration changes. "
                  + "For Merged or AlreadyContainsTarget, the checkout contains the recorded target. "
                  + "An Aborted or AbortFailed preparation is not a valid handoff; retain the evidence and report the preparation failure. "
                  + "Do not run Git mutations (merge, abort, reset, add, commit, rebase, push or branch changes). "
                  + "The platform stages and commits your file edits and publishes the delivery. "
                  + "Retain a one-to-one delivery commit mapping: do not squash, split, drop, or combine delivery commits. "
                  + VerificationScope + "Then finish the delivery report. "
                : "Resolve the recorded failure on this same task. " + VerificationScope
                  + "Then publish the corrected delivery. ";
        return "## CONTINUATION\n\n"
            + (isConflict ? "Automatic integration recovery context.\n" : "Orchestrator failure context.\n")
            + $"Continue task {taskKey} and resolve the integration dead end.\n"
            + $"Failed stage: {stage}. Reason: {reason}.\n"
            + $"Integration branch: {integrationBranch}; tip: {conflict?.IntegrationTipSha ?? integrationTip ?? "not recorded"}.\n"
            + $"Delivery side: {deliveryRef ?? "not recorded"} at {deliverySha ?? conflict?.DeliverySha ?? "not recorded"}.\n"
            + $"Attempted stages: {stages}. Conflicted files: {files}.\n"
            + (involvedCards is { Count: > 0 }
                ? $"Other cards whose integrated commits touched these files: {string.Join(", ", involvedCards)}.\n"
                : "")
            + (string.IsNullOrWhiteSpace(evidenceRef) ? "" : $"Failure evidence ref: {evidenceRef}.\n")
            + (string.IsNullOrWhiteSpace(evidence) ? ""
                : evidence.Contains('\n') ? $"Gate or review evidence:\n{evidence.TrimEnd()}\n"
                : $"Gate or review evidence: {evidence}.\n")
            + (string.IsNullOrWhiteSpace(gateEvidence) ? ""
                : gateEvidence.Contains('\n') ? $"Failed gate output:\n{gateEvidence.TrimEnd()}\n"
                : $"Failed gate output: {gateEvidence}.\n")
            + (runnerMergesIntegration
                ? "Use the delivery run's CLI and model with medium reasoning. "
                : "Preserve the card's model, CLI, and reasoning pins. ")
            + (string.IsNullOrWhiteSpace(gateEvidence) && stage != PipelineCatalogue.BuildTestGateStepId
                ? "" : RerunFailingCommandFirst)
            + taskDirection
            + "In the delivery report, identify this failed stage and link the evidence you resolved. "
            + "Do not move or push the integration branch. Finish with the normal task terminal sentinel.";
    }
}
