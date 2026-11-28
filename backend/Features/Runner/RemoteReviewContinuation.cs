using System.Collections.Concurrent;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

public enum RemoteReviewContinuationKind { FindingRound, BudgetEscalation, PlanMergeRound }

/// <summary>A settled report's durable lane and prompt decision.</summary>
/// <param name="Base">
/// AGT-3056: the reviewed delivery a finding round continues on, written as the
/// card's continuation base before the move to Ready. Null for an escalation and
/// for a subject that named no fenced result ref.
/// </param>
public sealed record RemoteReviewContinuation(
    RemoteReviewContinuationKind Kind,
    string FollowUp,
    IReadOnlyList<string> BlockingAspects,
    int MaximumConcernRounds,
    CardRoundBudgetState Budget,
    DeliveredReviewRound? DeliveredRound = null,
    ReviewRoundBudgetLedger? DeliveredRoundSeed = null,
    ContinuationBaseRecord? Base = null,
    ReviewPlanSkewPlan? SkewPlan = null);



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
        return MonolithTransitionAdapter.ContinuationReplay(
            ledger.Select(entry => new AgentStudio.OrchestrationDecisions.RoundTimelineFact(entry.Kind, entry.Details)),
            attemptId, cause, moveKey, currentState);

    }

    /// <summary>
    /// Whether a settled report opens a finding round. Only a delivered round
    /// (Pass, ProductFailure, IntegrationBranchDefect) can. An infrastructure
    /// report keeps its retry or escalation even when it carries a block
    /// verdict, as before the continuation was journaled (AGT-3100): its
    /// continuation would have no delivered round and the journal would refuse
    /// it as corrupt.
    /// </summary>
    public static bool OpensFindingRound(
        ReviewTerminalOutcome outcome, AgentStudio.TaskServer.Contracts.ReviewFollowUpAction followUp)
        => AgentStudio.Runner.MonolithTransitionAdapter.TransitionDecisionPolicyOpensFindingRound(outcome.ToString(), followUp);
}

public static class RemoteReviewContinuationProjection
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    internal static SemaphoreSlim GateForAttempt(string attemptId)
        => Gates.GetOrAdd(attemptId, _ => new SemaphoreSlim(1, 1));

    public static async Task<bool> ApplyAsync(
        TaskInfo task,
        RemoteReviewSettlementEntry entry,
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskMutationService mutations,
        TimelineLog timeline,
        ILogger logger,
        Func<bool> ownsReviewReport,
        CancellationToken ct)
    {
        if (entry.Continuation is not { } continuation) return true;
        if (!ownsReviewReport()) return false;
        var gate = GateForAttempt(continuation.Kind == RemoteReviewContinuationKind.PlanMergeRound
            ? "plan-skew:" + entry.TaskKey : entry.AttemptId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ownsReviewReport()) return false;
            task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
            var isReadyRound = continuation.Kind is RemoteReviewContinuationKind.FindingRound
                or RemoteReviewContinuationKind.PlanMergeRound;
            var isPlanMerge = continuation.Kind == RemoteReviewContinuationKind.PlanMergeRound;
            var target = isReadyRound
                ? TaskStates.Ready : TaskStates.Escalated;
            ReviewPlanSkewReservation? skewReservation = null;
            string? repairKey = null;
            var cause = isPlanMerge ? "review-plan-skew"
                : isReadyRound ? "multi-aspect-block" : "card-round-budget";
            var moveKey = $"continuation:{entry.IdempotencyKey}";
            // A settled review stays the card's current attempt until the next
            // one is minted, so a later return to 4-auto-review must not replay
            // this continuation: decide from the ledger first, never the lane.
            var step = RemoteReviewContinuationPolicy.Decide(
                timeline.ReadAll(task.FolderPath), entry.AttemptId, cause, moveKey, task.State);
            if (step == RemoteReviewContinuationStep.Completed) return true;
            if (step == RemoteReviewContinuationStep.Unapplicable) return false;
            if (isPlanMerge)
            {
                skewReservation = ReviewPlanSkewAllowance.Reserve(task.FolderPath, entry, timeline);
                if (skewReservation is null) return false;
                continuation = continuation with { Budget = skewReservation.Budget };
                isReadyRound = skewReservation.Action == CardRoundBudgetAction.OpenRound;
                target = isReadyRound ? TaskStates.Ready : TaskStates.Escalated;
                if (!isReadyRound)
                {
                    if (!ownsReviewReport()) return false;
                    repairKey = PlanRepairId(entry.TaskKey);
                }
            }
            var fixRoundBase = isReadyRound
                ? continuation.Base : null;
            if (step == RemoteReviewContinuationStep.Move)
            {
                if (!ownsReviewReport()) return false;
                // The base travels with the folder, so the card never sits in
                // Ready without it and no claim can start the round elsewhere.
                if (fixRoundBase is not null && !ContinuationBaseStore.Save(task.FolderPath, fixRoundBase))
                    return false;
                if (isReadyRound && !isPlanMerge)
                    PrepareFindingRound(task, entry.AttemptId, continuation.FollowUp, continuation.BlockingAspects,
                        entry.Report, continuation.MaximumConcernRounds, logger);
                var move = await transitions.MoveAsync(
                    task.Id, target, task.WatchPath, ct,
                    cause: $"remote-review-continuation:{entry.AttemptId}",
                    reason: isPlanMerge && !isReadyRound
                        ? HumanReviewEscalation.FormatReason("machine-blocker-quarantine",
                            $"Review plan skew allowance spent "
                            + $"({continuation.Budget.Used} of {continuation.Budget.Allowed}). Repair: {repairKey}. "
                            + "The immutable delivery is retained; acceptance evidence is unsatisfied.")
                        : isReadyRound
                        ? isPlanMerge ? entry.Report.Summary
                            ?? "The delivery predates the review plan; merge round sent."
                            : $"Blocking review findings from {entry.AttemptId}."
                        : $"Automatic round budget spent ({continuation.Budget.Used} of {continuation.Budget.Allowed}).",
                    authorityWrite: new AttemptWriteReference(
                        entry.AttemptId, entry.Report.Fence, entry.Report.AuthorityEpoch, moveKey),
                    suppressProductExecution: true,
                    expectedSourceState: TaskStates.AutoReview,
                    transitionCause: isReadyRound
                        ? LaneChangeCauses.QualityLoop : LaneChangeCauses.Escalated,
                    transitionDetail: isPlanMerge && !isReadyRound ? "machine-blocker-quarantine" : cause).ConfigureAwait(false);
                if (move.Status != MoveJobStatus.Success)
                {
                    if (fixRoundBase is not null) ContinuationBaseStore.Clear(task.FolderPath);
                    return false;
                }
                task = task with
                {
                    FolderPath = move.NewFolderPath ?? task.FolderPath,
                    State = target,
                };
            }
            if (isPlanMerge && !isReadyRound)
            {
                if (!ownsReviewReport()) return false;
                repairKey = EnsurePlanRepair(task, entry, skewReservation!, scanner, mutations);
                if (repairKey is null) return false;
            }
            var receipt = CardRoundBudgetReceipt.Details(
                continuation.Budget, continuation.BlockingAspects,
                cause,
                isReadyRound
                    ? CardRoundBudgetAction.OpenRound : skewReservation?.Action ?? CardRoundBudgetAction.Escalate);
            receipt["reviewAttemptId"] = entry.AttemptId;
            if (skewReservation is not null)
            {
                receipt["settlementKey"] = skewReservation.SettlementKey;
                receipt["deliverySha"] = skewReservation.DeliverySha;
                receipt["planCommitSha"] = skewReservation.PlanCommitSha ?? "unavailable";
                if (repairKey is not null) receipt["repairTaskKey"] = repairKey;
            }
            if (isReadyRound)
            {
                TaskJsonFile.UpdateOrder(task.FolderPath, 0, logger);
                if (isPlanMerge)
                    WriteRoundPrompt(task.FolderPath, entry.AttemptId, continuation.FollowUp, isPlanMerge: true);
                else if (step != RemoteReviewContinuationStep.Move)
                    PrepareFindingRound(task, entry.AttemptId, continuation.FollowUp, continuation.BlockingAspects,
                        entry.Report, continuation.MaximumConcernRounds, logger);
                receipt["aspects"] = string.Join(",", continuation.BlockingAspects);
                receipt["followUpPrompt"] = RunTriggerMetadata.PromptPreview(continuation.FollowUp);
                if (fixRoundBase is not null)
                {
                    ReviewFixRoundBase.RecordOnTimeline(timeline, task.FolderPath, fixRoundBase,
                        ReviewFixRoundBase.SourceReviewPlane, DateTime.UtcNow);
                    receipt["continuationBaseRef"] = fixRoundBase.Ref;
                    receipt["continuationBaseSha"] = fixRoundBase.CommitSha;
                }
            }
            return timeline.Append(task.FolderPath,
                isReadyRound
                    ? TimelineEventKinds.QualityLoopReopened : TimelineEventKinds.OrchestratorEscalated,
                TimelineActors.QualityLoop,
                isReadyRound
                    ? isPlanMerge ? entry.Report.Summary
                        ?? "The delivery predates the review plan; merge round sent."
                        : $"Blocking findings from review {entry.AttemptId} reopened the card."
                    : isPlanMerge
                        ? $"Review plan skew quarantined with runnable repair {repairKey}; immutable delivery retained."
                        : $"Automatic round budget spent ({continuation.Budget.Used} of {continuation.Budget.Allowed}); open blocking aspects: {string.Join(", ", continuation.BlockingAspects)}.",
                runId: entry.AttemptId, details: receipt);
        }
        finally { gate.Release(); }
    }

    internal static void PrepareFindingRound(
        TaskInfo task, string attemptId, string followUp, IReadOnlyList<string> aspects,
        AgentStudio.TaskServer.Contracts.ReviewReportRequest report, int maximumConcernRounds, ILogger logger)
    {
        WriteRoundPrompt(task.FolderPath, attemptId, followUp, isPlanMerge: false);
        ConcernTagWriter.MergeConcernTags(task.FolderPath, [ReviewDecisionOrchestrator.ReissueTagId], logger);
        ReviewConcernRoundStore.RecordFinding(task.FolderPath, maximumConcernRounds, attemptId, aspects,
            report.Workspace.ExpectedResultSha, report.Workspace.IntegrationTipSha, report.Verdicts,
            AgentStudio.TaskServer.Contracts.ReviewFindingRoundPrompt.RoutedFindingIds(followUp));
    }

    private static void WriteRoundPrompt(string folder, string attemptId, string followUp, bool isPlanMerge)
    {
        var history = Path.Combine(folder, "orchestrator-follow-up-history");
        Directory.CreateDirectory(history);
        var historyPath = Path.Combine(history, $"remote-review-finding-{attemptId}.md");
        if (File.Exists(historyPath))
            followUp = File.ReadAllText(historyPath).TrimEnd();
        else
            WritePromptAtomically(historyPath, followUp + "\n");

        var marker = $"<!-- review-finding-round:{attemptId} -->";
        var promptPath = Path.Combine(folder, "prompt.md");
        var previous = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        if (!previous.Contains(marker, StringComparison.Ordinal))
        {
            var kind = isPlanMerge ? "Review plan merge round" : "Review finding round";
            WritePromptAtomically(promptPath,
                previous + $"\n\n---\n\n## {kind} {attemptId}\n\n{marker}\n{followUp.TrimEnd()}\n");
        }
        var followUpPath = Path.Combine(folder, "orchestrator-follow-up.md");
        WritePromptAtomically(followUpPath, "# Orchestrator follow-up\n\n" + followUp + "\n");
    }

    private static void WritePromptAtomically(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string PlanRepairId(string taskKey)
        => "review-plan-repair-" + AttemptAuthorityService.Hash(taskKey)[..20];

    private static string? EnsurePlanRepair(TaskInfo source, RemoteReviewSettlementEntry entry,
        ReviewPlanSkewReservation reservation, TaskScannerService scanner, TaskMutationService mutations)
    {
        var id = PlanRepairId(entry.TaskKey);
        var repair = scanner.FindJob(id, source.WatchPath);
        if (repair is null)
        {
            var created = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            {
                Id = id,
                Title = $"Repair review plan configuration for {source.Key ?? source.Id}",
                WatchPath = source.WatchPath,
                TargetState = TaskStates.Ready,
                Agent = source.Agent, CliType = source.CliType, Model = source.Model,
                ThinkingLevel = source.ThinkingLevel, OwnerClientId = source.OwnerClientId,
                TaskType = TaskTypes.Bug, CreationSource = "review-plan-skew-repair", CreatedBy = "pipeline",
                PromptMarkdown = $"# Repair review plan configuration\n\nSource: {source.Key ?? source.Id}.\n"
                    + $"Review: {entry.AttemptId}. Plan commit: {reservation.PlanCommitSha ?? "unavailable"}.\n"
                    + $"Immutable delivery: `{entry.Continuation?.Base?.Ref}` at `{reservation.DeliverySha}`.\n\n"
                    + $"The shared automatic allowance is spent ({reservation.Budget.Used} of {reservation.Budget.Allowed}). "
                    + "Diagnose target plan churn and repair the verification configuration or preparation inputs. "
                    + "Use the platform-assigned base. Git and workspace mutations remain platform-owned. "
                    + "Preserve the source delivery and its attempt fences. Restore a stable, runnable review plan; "
                    + "do not accept the source without verification or open another merge round.\n\n"
                    + entry.Report.Summary,
            });
            repair = scanner.FindJob(created ?? id, source.WatchPath);
        }
        if (repair is null) return null;
        var sourceKey = source.Key ?? source.Id;
        var repairKey = repair.Key ?? repair.Id;
        if (!mutations.SetTaskReferences(repair.Id, (repair.References ?? new TaskReferences()) with
            { FollowUpOf = [sourceKey] }, repair.WatchPath)) return null;
        var latestSource = scanner.FindJob(source.Id, source.WatchPath) ?? source;
        if (!mutations.SetTaskReferences(source.Id, (latestSource.References ?? new TaskReferences()) with
            { RaisedFollowUps = (latestSource.References?.RaisedFollowUps ?? [])
                .Append(repairKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, source.WatchPath)) return null;
        return repairKey;
    }

}
