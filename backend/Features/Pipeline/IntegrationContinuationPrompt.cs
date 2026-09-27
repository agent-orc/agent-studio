using AgentStudio.Git;

namespace AgentStudio.Pipeline;

/// <summary>One source for the context given to an agent after integration stalls.</summary>
public static class IntegrationContinuationPrompt
{
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
        string? evidenceRef = null)
    {
        var stages = conflict?.Stages.Count > 0
            ? string.Join("; ", conflict.Stages.Select(item =>
                $"{item.Stage}: {item.Outcome}, {item.ConflictedFileCount} conflicted files"
                + (item.StoppedCommitSha is null ? "" : $", stopped at {item.StoppedCommitSha}")))
            : stage;
        var files = conflict?.ConflictedFiles.Count > 0
            ? string.Join(", ", conflict.ConflictedFiles)
            : conflictedFiles?.Count > 0
                ? string.Join(", ", conflictedFiles)
            : "none recorded";
        var isReview = stage.Contains("review", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("quality", StringComparison.OrdinalIgnoreCase);
        var isConflict = conflict is not null
            || stage.Contains("merge", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("rebase", StringComparison.OrdinalIgnoreCase);
        var taskDirection = isReview
            ? "Resolve the recorded review findings on this same task, run the relevant tests, and publish the corrected delivery. "
            : isConflict
                ? $"Continue on the existing delivery branch, merging the latest integration branch by merging 'origin/{integrationBranch}' into it when needed. "
                  + "Retain a one-to-one delivery commit mapping: do not squash, split, drop, or combine delivery commits. "
                  + "Run the relevant tests and publish a new delivery. "
                : "Resolve the recorded failure on this same task, run the relevant tests, and publish the corrected delivery. ";
        return "## CONTINUATION\n\n"
            + (isConflict ? "Automatic integration recovery context.\n" : "Orchestrator failure context.\n")
            + $"Continue task {taskKey} and resolve the integration dead end.\n"
            + $"Failed stage: {stage}. Reason: {reason}.\n"
            + $"Integration branch: {integrationBranch}; tip: {conflict?.IntegrationTipSha ?? integrationTip ?? "not recorded"}.\n"
            + $"Delivery side: {deliveryRef ?? "not recorded"} at {deliverySha ?? conflict?.DeliverySha ?? "not recorded"}.\n"
            + $"Attempted stages: {stages}. Conflicted files: {files}.\n"
            + $"Failure evidence ref: {evidenceRef ?? "pipeline-execution.json"}.\n"
            + (string.IsNullOrWhiteSpace(evidence) ? "" : $"Gate or review evidence: {evidence}.\n")
            + "Preserve the card's model, CLI, and reasoning pins. " + taskDirection
            + "In the delivery report, identify this failed stage and link the evidence you resolved. "
            + "Do not move or push the integration branch. Finish with the normal task terminal sentinel.";
    }
}
