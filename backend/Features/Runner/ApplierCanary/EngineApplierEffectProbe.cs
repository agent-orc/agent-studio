using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>The applied effects of one engine decision as the probe found them.</summary>
public sealed record EngineEffectCheck(string Occurrence, string Action, bool Verified,
    IReadOnlyList<string> Missing, string Evidence, DateTime CheckedAt);

/// <summary>
/// AGT-3218: reads what an applied engine decision left behind and compares it
/// with <see cref="EngineApplierContract"/>. It writes nothing, runs no Git,
/// and reads the card, the attempt authority, the batch queue and a few small
/// card files. The applier canary calls it for the first engine decisions
/// after a switch; the contract tests call it after the real applier ran.
/// </summary>
public sealed class EngineApplierEffectProbe(
    TaskScannerService scanner,
    AttemptAuthorityService authority,
    BatchGateStore batchQueue,
    BatchGatePilotService batches,
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
            return Result(runId, action, [nameof(EngineApplierEffectKind.DecisionReceipt)],
                $"The occurrence is {occurrence.Run.Status}; the applier has not completed it.");
        if (occurrence.Run.CurrentStage == OrchestrationStage.BatchPlan)
            return Result(runId, EngineApplierContract.BatchPlanAction,
                batches.ReceivedEngineBatchPlan(runId) ? [] : [nameof(EngineApplierEffectKind.BatchPlanIntent)],
                "The batch tick " + (batches.ReceivedEngineBatchPlan(runId) ? "received" : "did not receive")
                    + " the engine plan in this process.");
        // An active choice that holds the card hands it to the AI Operator,
        // which keeps the wait marker on purpose.
        if (AiOperatorDecisionQueue.HasCustody(configuration, runId))
            return Result(runId, "ask", [], "The AI Operator holds the decision as an ask.");

        var task = Card(occurrence);
        if (task is null)
            return Result(runId, action, ["card"], "The card of the occurrence was not found.");
        using var output = JsonDocument.Parse(completion.OutputJson);
        var root = output.RootElement;
        if (root.TryGetProperty("activeChoice", out var activeChoice)
            && activeChoice.ValueKind == JsonValueKind.Object
            && activeChoice.Deserialize<DecisionChoiceV1>(Json)?.Action == DecisionActionKinds.Checkpoint)
            return Result(runId, "checkpoint",
                File.Exists(Path.Combine(task.FolderPath, "results", "checkpoint.md")) ? [] : ["checkpoint"],
                "The verified-tree checkpoint is written to results/checkpoint.md.");

        var transition = root.TryGetProperty("transition", out var element)
            && element.ValueKind == JsonValueKind.Object
            ? element.Deserialize<TransitionDecision>(Json) : null;
        var row = EngineApplierContract.For(transition?.Action ?? TransitionAction.Continue);
        using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
        var payloadRoot = payload.RootElement;
        var lane = MonolithTransitionAdapter.TransitionDecisionPolicyTargetLane(transition?.Action,
            payloadRoot.TryGetProperty("targetState", out var target) && target.ValueKind == JsonValueKind.String
                ? target.GetString() : null,
            completion.Action);
        var owed = EngineApplierContract.Owed(row, lane,
            occurrence.Run.CurrentStage == OrchestrationStage.GateDispatch,
            payloadRoot.TryGetProperty("reviewOutcome", out _),
            payloadRoot.TryGetProperty("reviewAttemptRequest", out var request)
                && request.ValueKind == JsonValueKind.Object);
        var writeKey = $"engine-decision:{runId}:{completion.IdempotencyKey}";
        var missing = owed.Where(effect => !Applied(effect, occurrence, task, lane, writeKey))
            .Select(effect => effect.ToString()).ToArray();
        return Result(runId, row.Action, missing,
            $"{task.Key ?? task.Id} is in {task.State}; owed {string.Join(", ", owed)}"
            + (missing.Length == 0 ? "." : $"; missing {string.Join(", ", missing)}."));
    }

    private bool Applied(EngineApplierEffectKind effect, MonolithOrchestrationOccurrence occurrence,
        TaskInfo task, string? lane, string writeKey) => effect switch
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
            => occurrence.Run.StageResults?.LastOrDefault()?.Action == OrchestrationAction.Continue,
        EngineApplierEffectKind.BatchPlanIntent => batches.ReceivedEngineBatchPlan(occurrence.Run.RunId),
        EngineApplierEffectKind.WaitMarkerCleared
            => EngineDecisionWaitMarker.Read(task.FolderPath) != occurrence.Run.RunId,
        EngineApplierEffectKind.DecisionReceipt => occurrence.EngineReceipt is not null
            && occurrence.Run.StageResults is { Count: > 0 },
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

    private TaskInfo? Card(MonolithOrchestrationOccurrence occurrence)
    {
        scanner.InvalidateCache();
        return scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));
    }

    private EngineEffectCheck Result(string runId, string action, IReadOnlyList<string> missing, string evidence)
        => new(runId, action, missing.Count == 0, missing, evidence, time.GetUtcNow().UtcDateTime);
}
