using System.Collections.Concurrent;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

public enum RemoteReviewContinuationKind { FindingRound, BudgetEscalation }

/// <summary>A settled report's durable lane and prompt decision.</summary>
public sealed record RemoteReviewContinuation(
    RemoteReviewContinuationKind Kind,
    string FollowUp,
    IReadOnlyList<string> BlockingAspects,
    int MaximumConcernRounds,
    CardRoundBudgetState Budget,
    DeliveredReviewRound? DeliveredRound = null,
    ReviewRoundBudgetLedger? DeliveredRoundSeed = null);

public static class RemoteReviewContinuationProjection
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static async Task<bool> ApplyAsync(
        TaskInfo task,
        RemoteReviewSettlementEntry entry,
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskMutationService mutations,
        TimelineLog timeline,
        ILogger logger,
        CancellationToken ct)
    {
        if (entry.Continuation is not { } continuation) return true;
        var gate = Gates.GetOrAdd(entry.AttemptId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
            var target = continuation.Kind == RemoteReviewContinuationKind.FindingRound
                ? TaskStates.Ready : TaskStates.Escalated;
            if (task.State == TaskStates.AutoReview)
            {
                var move = await transitions.MoveAsync(
                    task.Id, target, task.WatchPath, ct,
                    cause: $"remote-review-continuation:{entry.AttemptId}",
                    reason: continuation.Kind == RemoteReviewContinuationKind.FindingRound
                        ? $"Blocking review findings from {entry.AttemptId}."
                        : $"Automatic round budget spent ({continuation.Budget.Used} of {continuation.Budget.Allowed}).",
                    authorityWrite: new AttemptWriteReference(
                        entry.AttemptId, entry.Report.Fence, entry.Report.AuthorityEpoch,
                        $"continuation:{entry.IdempotencyKey}"),
                    suppressProductExecution: true,
                    expectedSourceState: TaskStates.AutoReview,
                    transitionCause: continuation.Kind == RemoteReviewContinuationKind.FindingRound
                        ? LaneChangeCauses.QualityLoop : LaneChangeCauses.Escalated,
                    transitionDetail: continuation.Kind == RemoteReviewContinuationKind.FindingRound
                        ? "multi-aspect-block" : "card-round-budget").ConfigureAwait(false);
                if (move.Status != MoveJobStatus.Success) return false;
                task = task with
                {
                    FolderPath = move.NewFolderPath ?? task.FolderPath,
                    State = target,
                };
            }
            if (task.State != target) return false;
            var cause = continuation.Kind == RemoteReviewContinuationKind.FindingRound
                ? "multi-aspect-block" : "card-round-budget";
            if (timeline.ReadAll(task.FolderPath).Any(item =>
                    item.Details?.GetValueOrDefault("reviewAttemptId") == entry.AttemptId
                    && item.Details?.GetValueOrDefault("cause") == cause))
                return true;
            var receipt = CardRoundBudgetReceipt.Details(
                continuation.Budget, continuation.BlockingAspects,
                cause,
                continuation.Kind == RemoteReviewContinuationKind.FindingRound
                    ? CardRoundBudgetAction.OpenRound : CardRoundBudgetAction.Escalate);
            receipt["reviewAttemptId"] = entry.AttemptId;
            if (continuation.Kind == RemoteReviewContinuationKind.FindingRound)
            {
                TaskJsonFile.UpdateOrder(task.FolderPath, 0, logger);
                ReviewConcernRoundStore.RecordFinding(task.FolderPath, continuation.MaximumConcernRounds,
                    entry.AttemptId, continuation.BlockingAspects,
                    entry.Report.Workspace.ExpectedResultSha, entry.Report.Workspace.IntegrationTipSha,
                    entry.Report.Verdicts);
                var marker = $"<!-- review-finding-round:{entry.AttemptId} -->";
                var promptPath = Path.Combine(task.FolderPath, "prompt.md");
                if (!File.Exists(promptPath) || !File.ReadAllText(promptPath).Contains(marker, StringComparison.Ordinal))
                {
                    var previous = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
                    var temporary = promptPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temporary, previous
                            + $"\n\n---\n\n## Review finding round {entry.AttemptId}\n\n"
                            + marker + "\n" + continuation.FollowUp.TrimEnd() + "\n");
                        File.Move(temporary, promptPath, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                var followUpPath = Path.Combine(task.FolderPath, "orchestrator-follow-up.md");
                File.WriteAllText(followUpPath, "# Orchestrator follow-up\n\n" + continuation.FollowUp + "\n");
                var history = Path.Combine(task.FolderPath, "orchestrator-follow-up-history");
                Directory.CreateDirectory(history);
                var historyPath = Path.Combine(history, $"remote-review-finding-{entry.AttemptId}.md");
                if (!File.Exists(historyPath))
                {
                    var temporary = historyPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temporary, continuation.FollowUp + "\n");
                        File.Move(temporary, historyPath, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                ConcernTagWriter.MergeConcernTags(task.FolderPath,
                    [ReviewDecisionOrchestrator.ReissueTagId], logger);
                receipt["aspects"] = string.Join(",", continuation.BlockingAspects);
                receipt["followUpPrompt"] = RunTriggerMetadata.PromptPreview(continuation.FollowUp);
            }
            timeline.Append(task.FolderPath,
                continuation.Kind == RemoteReviewContinuationKind.FindingRound
                    ? TimelineEventKinds.QualityLoopReopened : TimelineEventKinds.OrchestratorEscalated,
                TimelineActors.QualityLoop,
                continuation.Kind == RemoteReviewContinuationKind.FindingRound
                    ? $"Blocking findings from review {entry.AttemptId} reopened the card."
                    : $"Automatic round budget spent ({continuation.Budget.Used} of {continuation.Budget.Allowed}); open blocking aspects: {string.Join(", ", continuation.BlockingAspects)}.",
                runId: entry.AttemptId, details: receipt);
            return true;
        }
        finally { gate.Release(); }
    }
}
