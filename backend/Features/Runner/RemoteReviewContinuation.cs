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

public enum RemoteReviewContinuationStep { Move, Finish, Completed, Unapplicable }

/// <summary>
/// Which part of a journaled continuation still has to run. The receipt
/// completes it; a lane row keyed by the continuation idempotency key means the
/// move landed and only the prompt and ledger writes remain. Only a card that
/// never took the move and still sits in 4-auto-review is moved.
/// </summary>
public static class RemoteReviewContinuationPolicy
{
    public static RemoteReviewContinuationStep Decide(
        IEnumerable<TimelineEvent> ledger, string attemptId, string cause, string moveKey, string currentState)
    {
        var rows = ledger.ToList();
        if (rows.Any(item => (item.Kind is TimelineEventKinds.QualityLoopReopened or TimelineEventKinds.OrchestratorEscalated)
                             && item.Details?.GetValueOrDefault("reviewAttemptId") == attemptId
                             && item.Details?.GetValueOrDefault("cause") == cause))
            return RemoteReviewContinuationStep.Completed;
        if (rows.Any(item => item.Kind == TimelineEventKinds.LaneChanged
                             && item.Details?.GetValueOrDefault("idempotencyKey") == moveKey))
            return RemoteReviewContinuationStep.Finish;
        return currentState == TaskStates.AutoReview
            ? RemoteReviewContinuationStep.Move
            : RemoteReviewContinuationStep.Unapplicable;
    }
}

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
            var cause = continuation.Kind == RemoteReviewContinuationKind.FindingRound
                ? "multi-aspect-block" : "card-round-budget";
            var moveKey = $"continuation:{entry.IdempotencyKey}";
            // A settled review stays the card's current attempt until the next
            // one is minted, so a later return to 4-auto-review must not replay
            // this continuation: decide from the ledger first, never the lane.
            var step = RemoteReviewContinuationPolicy.Decide(
                timeline.ReadAll(task.FolderPath), entry.AttemptId, cause, moveKey, task.State);
            if (step == RemoteReviewContinuationStep.Completed) return true;
            if (step == RemoteReviewContinuationStep.Unapplicable) return false;
            if (step == RemoteReviewContinuationStep.Move)
            {
                var move = await transitions.MoveAsync(
                    task.Id, target, task.WatchPath, ct,
                    cause: $"remote-review-continuation:{entry.AttemptId}",
                    reason: continuation.Kind == RemoteReviewContinuationKind.FindingRound
                        ? $"Blocking review findings from {entry.AttemptId}."
                        : $"Automatic round budget spent ({continuation.Budget.Used} of {continuation.Budget.Allowed}).",
                    authorityWrite: new AttemptWriteReference(
                        entry.AttemptId, entry.Report.Fence, entry.Report.AuthorityEpoch, moveKey),
                    suppressProductExecution: true,
                    expectedSourceState: TaskStates.AutoReview,
                    transitionCause: continuation.Kind == RemoteReviewContinuationKind.FindingRound
                        ? LaneChangeCauses.QualityLoop : LaneChangeCauses.Escalated,
                    transitionDetail: cause).ConfigureAwait(false);
                if (move.Status != MoveJobStatus.Success) return false;
                task = task with
                {
                    FolderPath = move.NewFolderPath ?? task.FolderPath,
                    State = target,
                };
            }
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
