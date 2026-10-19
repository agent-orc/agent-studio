using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>The applied effects of one engine decision as the probe found them.</summary>
/// <param name="Basis">
/// What the check compared, one of <see cref="EngineEffectBasis"/>. Only a
/// verified <see cref="EngineEffectBasis.Row"/> check counts toward a canary.
/// </param>
/// <param name="CompletedAt">When the applier completed the occurrence; null while it is not applied.</param>
/// <param name="AwaitingDownstream">
/// Every missing effect is one that a later pass of another loop writes
/// (<see cref="EngineApplierContract.Downstream"/>).
/// </param>
public sealed record EngineEffectCheck(string Occurrence, string Action, bool Verified,
    IReadOnlyList<string> Missing, string Evidence, DateTime CheckedAt,
    string Basis = EngineEffectBasis.Row, DateTime? CompletedAt = null, bool AwaitingDownstream = false);

/// <summary>
/// AGT-3218: reads what an applied engine decision left behind and compares it
/// with <see cref="EngineApplierContract"/>. It writes nothing, runs no Git,
/// rescans nothing, and reads the cached card, the attempt authority, the
/// occurrence journal, the batch queue and a few small card files. The applier
/// canary calls it for the first engine decisions after a switch; the
/// contract tests call it after the real applier ran.
/// </summary>
public sealed class EngineApplierEffectProbe(
    TaskScannerService scanner,
    AttemptAuthorityService authority,
    BatchGateStore batchQueue,
    MonolithOrchestrationStore occurrences,
    TimelineLog timeline,
    OrchestratorLog operatorLog,
    IConfiguration configuration,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EngineEffectCheck Check(MonolithOrchestrationOccurrence occurrence)
    {
        var runId = occurrence.Run.RunId;
        var completion = occurrence.Completion;
        var action = occurrence.EngineReceipt?.Action ?? completion?.Action.ToString() ?? "unknown";
        if (occurrence.Run.Status != "completed" || completion is null || occurrence.EngineReceipt is null)
            return NotApplied(occurrence, action);
        if (occurrence.Run.CurrentStage == OrchestrationStage.BatchPlan)
            return Result(occurrence, EngineApplierContract.BatchPlanAction,
                BatchPlanStored(occurrence, out var planEvidence) ? [] : [nameof(EngineApplierEffectKind.BatchPlanIntent)],
                planEvidence);
        // An active choice that holds the card hands it to the AI Operator,
        // which keeps the wait marker on purpose.
        if (AiOperatorDecisionQueue.HasCustody(configuration, runId))
            return Result(occurrence, "ask", [], "The AI Operator holds the decision as an ask; no row effect is owed.",
                EngineEffectBasis.Ask);

        var task = Card(occurrence);
        if (task is null)
            return Result(occurrence, action, ["card"], "The card of the occurrence was not found.");
        using var output = JsonDocument.Parse(completion.OutputJson);
        var root = output.RootElement;
        if (root.TryGetProperty("activeChoice", out var activeChoice)
            && activeChoice.ValueKind == JsonValueKind.Object
            && activeChoice.Deserialize<DecisionChoiceV1>(Json)?.Action == DecisionActionKinds.Checkpoint)
            return Result(occurrence, "checkpoint",
                File.Exists(Path.Combine(task.FolderPath, "results", "checkpoint.md")) ? [] : ["checkpoint"],
                "The verified-tree checkpoint is written to results/checkpoint.md.", EngineEffectBasis.Checkpoint);

        var transition = root.TryGetProperty("transition", out var element)
            && element.ValueKind == JsonValueKind.Object
            ? element.Deserialize<TransitionDecision>(Json) : null;
        // A stage result without a transition continues, as the applier reads it.
        var row = EngineApplierContract.Find(transition?.Action.ToString() ?? nameof(TransitionAction.Continue))!;
        using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
        var payloadRoot = payload.RootElement;
        var lane = MonolithTransitionAdapter.TransitionDecisionPolicyTargetLane(transition?.Action,
            payloadRoot.TryGetProperty("targetState", out var target) && target.ValueKind == JsonValueKind.String
                ? target.GetString() : null,
            completion.Action);
        // The applier stops at a review-input decision only after it chose a lane.
        var handoff = lane is null ? null : ReviewInputHandoff(occurrence, task);
        var owed = EngineApplierContract.Owed(row, lane,
            occurrence.Run.CurrentStage == OrchestrationStage.GateDispatch,
            payloadRoot.TryGetProperty("reviewOutcome", out _),
            payloadRoot.TryGetProperty("reviewAttemptRequest", out var request)
                && request.ValueKind == JsonValueKind.Object,
            handoff is not null);
        var writeKey = $"engine-decision:{runId}:{completion.IdempotencyKey}";
        var missing = owed.Where(effect => !Applied(effect, occurrence, task, lane, writeKey, handoff))
            .ToArray();
        return Result(occurrence, row.Action, missing.Select(effect => effect.ToString()).ToArray(),
            $"{task.Key ?? task.Id} is in {task.State}; owed {string.Join(", ", owed)}"
            + (handoff is null ? "" : $"; review-input decision {handoff} holds the card")
            + (missing.Length == 0 ? "." : $"; missing {string.Join(", ", missing)}."),
            handoff is null ? EngineEffectBasis.Row : EngineEffectBasis.ReviewInputHandoff,
            missing.Length > 0 && missing.All(EngineApplierContract.Downstream));
    }

    /// <summary>
    /// An occurrence the applier has not completed. A card that a person moved
    /// out of the source lane is no applier defect: the applier refuses the
    /// decision with orchestration-source-state-mismatch on purpose.
    /// </summary>
    private EngineEffectCheck NotApplied(MonolithOrchestrationOccurrence occurrence, string action)
    {
        var task = occurrence.Run.CurrentStage == OrchestrationStage.BatchPlan ? null : Card(occurrence);
        if (task is not null && task.State != occurrence.ExpectedSourceState
            && ManualExit(task, occurrence.ExpectedSourceState) is { } move)
            return Result(occurrence, action, [],
                $"{task.Key ?? task.Id} left {occurrence.ExpectedSourceState} for {task.State} by {move.Actor} at "
                + $"{move.Ts:u} before the applier ran; the applier refuses the source-state mismatch on purpose, "
                + "so the decision does not count.", EngineEffectBasis.ManualMove);
        return Result(occurrence, action, [nameof(EngineApplierEffectKind.DecisionReceipt)],
            $"The occurrence is {occurrence.Run.Status} since {occurrence.Run.UpdatedAt:u}; "
            + "the applier has not completed it.");
    }

    // The last move out of the source lane, when a person made it.
    private TimelineEvent? ManualExit(TaskInfo task, string sourceLane)
        => timeline.ReadAll(task.FolderPath).LastOrDefault(entry => entry.Kind == TimelineEventKinds.LaneChanged
            && entry.Details?.GetValueOrDefault("from") == sourceLane) is { } exit
            && TimelineActors.IsHuman(exit.Actor) ? exit : null;

    private bool Applied(EngineApplierEffectKind effect, MonolithOrchestrationOccurrence occurrence,
        TaskInfo task, string? lane, string writeKey, string? handoff) => effect switch
    {
        EngineApplierEffectKind.LaneMove => task.State == lane || LaneWrites(task, writeKey)
            .Any(entry => entry.Details?.GetValueOrDefault("to") == lane),
        EngineApplierEffectKind.LaneKept => !LaneWrites(task, writeKey).Any(),
        EngineApplierEffectKind.BatchAdmission => BatchAdmitted(occurrence, task),
        EngineApplierEffectKind.ReviewRetryScheduled
            => authority.HasScheduledReviewInfrastructureRetry(occurrence.AuthorityAttemptId),
        EngineApplierEffectKind.LinkedFollowUp => LinkedFollowUp(occurrence, task),
        EngineApplierEffectKind.IntegrationJournal => RemoteDeliverySettlementStore.Read(task.FolderPath) is
            { } journal && journal.ReviewAttemptId == occurrence.AuthorityAttemptId
            && journal.Stage >= RemoteDeliverySettlementStage.IntegrationSettled,
        EngineApplierEffectKind.ContinuationPrompt => File.Exists(Path.Combine(task.FolderPath, "prompt.md"))
            && File.ReadAllText(Path.Combine(task.FolderPath, "prompt.md"))
                .Contains($"<!-- engine-decision:{occurrence.Run.RunId} -->", StringComparison.Ordinal),
        EngineApplierEffectKind.QualityLoopReceipt => timeline.ReadAll(task.FolderPath).Any(entry =>
            entry.Kind == TimelineEventKinds.QualityLoopReopened
            && entry.Details?.GetValueOrDefault("authorityWriteKey") == writeKey),
        EngineApplierEffectKind.ReviewAttempt => ReviewSubjectStore.Read(task.FolderPath) is not null
            && authority.GetTaskProjection(task.Key ?? task.Id).CurrentReviewAttempt?.SourceRunAttemptId
                == occurrence.AuthorityAttemptId,
        EngineApplierEffectKind.ParkVerdict => ParkVerdict(occurrence, task),
        EngineApplierEffectKind.GateDispatch
            => EngineGateDispatchReceipt.Recorded(timeline, task.FolderPath, occurrence.Run.RunId),
        EngineApplierEffectKind.BatchPlanIntent => BatchPlanStored(occurrence, out _),
        EngineApplierEffectKind.WaitMarkerCleared
            => EngineDecisionWaitMarker.Read(task.FolderPath) != occurrence.Run.RunId,
        EngineApplierEffectKind.DecisionReceipt => occurrence.EngineReceipt is not null
            && occurrence.Run.StageResults is { Count: > 0 },
        EngineApplierEffectKind.ReviewInputHandoff => handoff is not null,
        _ => false,
    };

    // The keyed lane write of this decision; it stays in the card timeline
    // after a runner picked the card up again.
    private IEnumerable<TimelineEvent> LaneWrites(TaskInfo task, string writeKey)
        => timeline.ReadAll(task.FolderPath).Where(entry => entry.Kind == TimelineEventKinds.LaneChanged
            && entry.Details?.GetValueOrDefault("idempotencyKey") == writeKey);

    /// <summary>
    /// The three outcomes of <see cref="EngineBatchAdmission"/>: the queue owns
    /// the review, the per-task gate settled the card, or the feed records why
    /// no admission was owed.
    /// </summary>
    private bool BatchAdmitted(MonolithOrchestrationOccurrence occurrence, TaskInfo task)
        => batchQueue.ReadPending(occurrence.AuthorityAttemptId) is not null
            && BatchGateOwnershipStore.Read(task.FolderPath)?.ReviewAttemptId == occurrence.AuthorityAttemptId
            || task.State is TaskStates.HumanReview or TaskStates.Escalated
                && occurrence.ExpectedSourceState == TaskStates.AutoReview
            || operatorLog.Read(task.WatchPath).Any(entry =>
                entry.Topic == "engine-batch-admission:" + occurrence.Run.RunId);

    /// <summary>
    /// The durable intent of the plan's scope names the plan, or the batch tick
    /// recorded what it did with it. Both are files, so a restart inside the
    /// canary window loses neither.
    /// </summary>
    private bool BatchPlanStored(MonolithOrchestrationOccurrence occurrence, out string evidence)
    {
        if (batchQueue.ReadEngineIntentReceipt(occurrence.Run.RunId) is { } receipt)
        {
            evidence = $"The batch tick took the plan at {receipt.AtUtc:u}: {receipt.Outcome}"
                + (string.IsNullOrWhiteSpace(receipt.Reason) ? "." : $" ({receipt.Reason}).");
            return true;
        }
        if (batchQueue.ReadEngineIntent(occurrence.Run.TaskId) is { } intent && intent.RunId == occurrence.Run.RunId)
        {
            evidence = $"The plan waits as the durable intent of {occurrence.Run.TaskId} since {intent.AtUtc:u}.";
            return true;
        }
        evidence = $"Neither the durable intent of {occurrence.Run.TaskId} nor a batch tick receipt names the plan.";
        return false;
    }

    /// <summary>
    /// The review-input decision that holds the card, as the applier found it:
    /// the wait marker names it, or, once that decision applied and cleared the
    /// marker, it was raised for the card while this occurrence applied.
    /// </summary>
    private string? ReviewInputHandoff(MonolithOrchestrationOccurrence occurrence, TaskInfo task)
    {
        if (EngineDecisionWaitMarker.Read(task.FolderPath) is { } waiting && waiting != occurrence.Run.RunId
            && occurrences.Get(waiting)?.CurrentStage == OrchestrationStage.ReviewInputDecision)
            return waiting;
        return occurrences.List(occurrence.Run.ProjectId, null).FirstOrDefault(run =>
            run.TaskId == occurrence.Run.TaskId && run.CurrentStage == OrchestrationStage.ReviewInputDecision
            && run.CreatedAt >= occurrence.Run.CreatedAt && run.CreatedAt <= occurrence.Run.CompletedAt)?.RunId;
    }

    private bool LinkedFollowUp(MonolithOrchestrationOccurrence occurrence, TaskInfo task)
    {
        var followUp = scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.CreationSource == "engine-follow-up:" + occurrence.Run.RunId);
        return followUp is not null
            && followUp.References.FollowUpOf.Contains(task.Key ?? task.Id, StringComparer.OrdinalIgnoreCase)
            && task.References.RaisedFollowUps.Contains(followUp.Key ?? followUp.Id,
                StringComparer.OrdinalIgnoreCase);
    }

    private bool ParkVerdict(MonolithOrchestrationOccurrence occurrence, TaskInfo task)
    {
        var workspace = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return false;
        using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
        var reviewAttemptId = payload.RootElement.TryGetProperty("reviewAttemptId", out var reviewId)
            && reviewId.ValueKind == JsonValueKind.String
            ? reviewId.GetString() : occurrence.AuthorityAttemptId;
        return ReviewDecisionLog.ReadAll(workspace, task.ProjectName).Any(record =>
            record.JobId == task.Id && record.FailureKind == $"remote-review-park:{reviewAttemptId}");
    }

    // The scanner's cached snapshot: the applier and every lane move
    // invalidate it, so the probe never forces a rescan.
    private TaskInfo? Card(MonolithOrchestrationOccurrence occurrence)
        => scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));

    private EngineEffectCheck Result(MonolithOrchestrationOccurrence occurrence, string action,
        IReadOnlyList<string> missing, string evidence, string basis = EngineEffectBasis.Row,
        bool awaitingDownstream = false)
        => new(occurrence.Run.RunId, action, missing.Count == 0 && basis != EngineEffectBasis.ManualMove,
            missing, evidence, time.GetUtcNow().UtcDateTime, basis,
            occurrence.Run.Status == "completed" ? occurrence.Run.CompletedAt : null, awaitingDownstream);
}
