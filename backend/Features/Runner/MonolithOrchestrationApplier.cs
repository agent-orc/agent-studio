using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Tasks;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Applies an engine decision through the existing keyed lane writer.</summary>
public sealed class MonolithOrchestrationApplier(
    TaskScannerService scanner,
    TaskTransitionService transitions,
    TaskMutationService mutations,
    TimelineLog timeline,
    AttemptAuthorityService authority,
    ReviewAttemptTaskLifecycleService reviewAttemptLifecycle,
    HumanReviewEscalation escalation,
    RemoteDeliveryIntegrationCoordinator integration,
    BatchGatePilotService batches,
    AgentStudio.Projects.ProjectSettingsService settings,
    IOperatorSweepActions sweepActions,
    ActiveChoiceAdmissionAdapter activeDecisionAuthority,
    IAiOperatorDecisionQueue aiOperator,
    TimeProvider time,
    IConfiguration configuration,
    IServiceProvider services,
    OrchestratorLog operatorLog) : IMonolithOrchestrationApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion, CancellationToken ct)
    {
        if (occurrence.Run.CurrentStage == OrchestrationStage.BatchPlan)
        {
            ApplyBatchPlan(occurrence, completion);
            return Task.CompletedTask;
        }
        return ApplyTransitionAsync(occurrence, completion, ct);
    }

    /// <summary>
    /// A batch plan is project-scoped intent. The batch tick re-checks the
    /// authority floors against its current facts before it acts.
    /// </summary>
    private void ApplyBatchPlan(MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion)
    {
        using var output = JsonDocument.Parse(completion.OutputJson);
        if (!output.RootElement.TryGetProperty("batchPlan", out var plan)
            || plan.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The engine batch plan is missing its decision.");
        var decision = plan.Deserialize<AgentStudio.TaskServer.Contracts.BatchGate.BatchPlanDecision>(Json)
            ?? throw new InvalidDataException("The engine batch plan is empty.");
        batches.AcceptEngineBatchPlan(occurrence.Run.RunId, occurrence.Run.TaskId, decision);
    }

    private async Task ApplyTransitionAsync(MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion, CancellationToken ct)
    {
        if (occurrence.Run.CurrentStage == OrchestrationStage.OperatorDecision)
        {
            await ApplyOperatorDecisionAsync(occurrence, completion, ct);
            return;
        }
        var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));
        if (task is null) throw new KeyNotFoundException("Orchestration task was not found.");
        if (occurrence.Run.CurrentStage == OrchestrationStage.OperatorSweep)
        {
            await ApplySweepAsync(task, occurrence, completion, ct);
            return;
        }
        using var output = JsonDocument.Parse(completion.OutputJson);
        var root = output.RootElement;
        DecisionChoiceV1? activeChoice = null;
        if (root.TryGetProperty("activeChoice", out _))
            activeChoice = activeDecisionAuthority.Validate(occurrence, task, completion.Action, root);
        else if (occurrence.Profile == "CodingCompletion"
            && completion.EngineId != "engine_rollback"
            && activeDecisionAuthority.HasActiveGrounds(occurrence.Run.RunId))
            throw new InvalidOperationException("active-decision-receipt-required");
        TransitionDecision? decision = null;
        if (root.TryGetProperty("transition", out var transition)
            && transition.ValueKind == JsonValueKind.Object)
            decision = transition.Deserialize<TransitionDecision>(Json);

        var target = TransitionDecisionPolicy.TargetLane(decision?.Action,
            TargetFromPayload(occurrence.Run.PayloadJson), completion.Action);
        if (task.State != occurrence.ExpectedSourceState && task.State != target)
            throw new InvalidOperationException("orchestration-source-state-mismatch");
        if (decision is null && occurrence.Run.CurrentStage == OrchestrationStage.GateDispatch)
            throw new InvalidDataException("The engine gate decision is missing its transition.");
        if (decision is null && HasPayloadProperty(occurrence.Run.PayloadJson, "reviewOutcome"))
            throw new InvalidDataException("The engine review decision is missing its transition.");
        if (activeChoice is { Action: DecisionActionKinds.Ask or DecisionActionKinds.Defer })
        {
            aiOperator.Enqueue(task, occurrence.Run.RunId, activeChoice);
            scanner.InvalidateCache();
            return;
        }
        if (activeChoice is { Action: DecisionActionKinds.Checkpoint })
        {
            var verified = activeDecisionAuthority.VerifiedTree(occurrence.Run.RunId);
            var directory = Path.Combine(task.FolderPath, "results");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "checkpoint.md");
            if (!File.Exists(path))
                File.WriteAllText(path, $"# Verified-tree checkpoint\n\nLast verified tree: `{verified}`\n"
                    + $"Decision occurrence: `{occurrence.Run.RunId}`\n");
            scanner.InvalidateCache();
            return;
        }
        if (decision?.Action == TransitionAction.ReviewInfrastructureRetry)
        {
            if (!authority.HasScheduledReviewInfrastructureRetry(occurrence.AuthorityAttemptId))
            {
                var scheduled = authority.ScheduleReviewInfrastructureRetry(
                    occurrence.AuthorityAttemptId, decision.Reason);
                if (!scheduled.Accepted)
                    throw new InvalidOperationException($"Review retry could not be scheduled: {scheduled.Status}");
            }
        }
        if (target is null)
        {
            EngineDecisionWaitMarker.Clear(task.FolderPath, occurrence.Run.RunId);
            scanner.InvalidateCache();
            return;
        }
        var key = $"engine-decision:{occurrence.Run.RunId}:{completion.IdempotencyKey}";
        if (decision?.Action == TransitionAction.DegradeWithFollowUp)
            EnsureLinkedFollowUp(task, occurrence, decision);
        if (decision?.Action is TransitionAction.AcceptAndIntegrate
            or TransitionAction.DegradeWithFollowUp)
        {
            var review = authority.GetReview(occurrence.AuthorityAttemptId)
                ?? throw new InvalidOperationException("The reviewed attempt was not found.");
            var project = settings.Get(task.ProjectName);
            var subject = ReviewSubjectStore.Read(task.FolderPath);
            var request = new RemoteDeliveryIntegrationRequest(task.ProjectName, task.Id,
                task.FolderPath, task.WatchPath,
                TaskIntegrationBranch.Resolve(task, project.IntegrationBranch),
                project.IntegrationStrategy, PipelineTypes.Resolve(task),
                subject?.CompletedAtUtc ?? DateTimeOffset.UtcNow,
                review.AttemptId, review.Subject.ExpectedResultSha);
            var journal = RemoteDeliverySettlementStore.Read(task.FolderPath);
            if (journal?.ReviewAttemptId != review.AttemptId)
            {
                RemoteDeliverySettlementStore.Write(task.FolderPath,
                    new RemoteDeliverySettlementRecord
                    {
                        TaskKey = review.TaskKey,
                        ReviewAttemptId = review.AttemptId,
                        JournalRequired = true,
                        Outcome = review.Outcome?.ToString() ?? "Pass",
                        ShouldIntegrate = true,
                        BuildTestGate = "Passed",
                        GateReason = decision.Reason,
                        IntegrationBranch = request.IntegrationBranch,
                        IntegrationStrategy = request.IntegrationStrategy,
                        PipelineType = request.PipelineType,
                        DeliveredAtUtc = request.DeliveredAtUtc,
                        Stage = RemoteDeliverySettlementStage.IntegrationPending,
                        RecordedAtUtc = DateTimeOffset.UtcNow,
                    });
            }
            if (RemoteDeliverySettlementStore.Read(task.FolderPath)?.Stage
                < RemoteDeliverySettlementStage.IntegrationSettled)
            {
                var result = await integration.EnqueueAsync(request);
                RemoteDeliverySettlementStore.Advance(task.FolderPath,
                    RemoteDeliverySettlementStage.IntegrationSettled,
                    result.Outcome.ToString(), RemoteDeliveryParkReason.For(result));
            }
        }
        if (decision?.Action == TransitionAction.DeferToBatch)
        {
            var review = authority.GetReview(occurrence.AuthorityAttemptId)
                ?? throw new InvalidOperationException("The reviewed attempt was not found.");
            var source = authority.GetRun(review.SourceRunAttemptId)
                ?? throw new InvalidOperationException("The batch source run was not found.");
            batches.Enqueue(task, review, source, DateTimeOffset.UtcNow);
        }
        if (decision?.ContinuationBase is { } continuation
            && decision.Action is TransitionAction.FindingRound or TransitionAction.ConcernRound)
        {
            var baseRecord = ReviewFixRoundBase.Decide(
                continuation.AttemptId, continuation.ResultRef, continuation.ResultSha,
                DateTime.UtcNow);
            if (baseRecord is not null && !ContinuationBaseStore.Save(task.FolderPath, baseRecord))
                throw new IOException("The reviewed continuation base could not be saved.");
        }
        if (task.State == occurrence.ExpectedSourceState)
        {
            var isSalvage = activeChoice is { Action: DecisionActionKinds.Adapt,
                Target: "salvage-handoff" };
            AgentStudio.Runner.ContinuationBaseRecord? salvageBase = null;
            if (isSalvage)
            {
                using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
                salvageBase = new ContinuationBaseRecord(
                    payload.RootElement.GetProperty("resultRef").GetString()!,
                    payload.RootElement.GetProperty("resultSha").GetString()!,
                    "model-selected-salvage-handoff", occurrence.AuthorityAttemptId,
                    time.GetUtcNow().UtcDateTime);
            }
            var move = await transitions.MoveAsync(task.Id, target, task.WatchPath, ct,
                cause: "engine-decision", reason: decision?.Reason ?? "Engine stage completed.",
                authorityWrite: new AttemptWriteReference(occurrence.AuthorityAttemptId,
                    occurrence.AuthorityFence, occurrence.AuthorityEpoch, key),
                suppressProductExecution: true,
                expectedSourceState: occurrence.ExpectedSourceState,
                transitionCause: decision?.Action is TransitionAction.FindingRound or TransitionAction.ConcernRound
                    ? LaneChangeCauses.QualityLoop : LaneChangeCauses.Delivered,
                transitionDetail: decision?.Action.ToString() ?? completion.Action.ToString(),
                engineDecisionRequeue: activeChoice is { Action: DecisionActionKinds.Retry,
                    Target: "card:ready" } or { Action: DecisionActionKinds.Adapt,
                    Target: "salvage-handoff" },
                engineContinuationBase: salvageBase);
            if (move.Status != MoveJobStatus.Success)
                throw new InvalidOperationException($"Engine lane move failed: {move.Status} {move.Message}");
            scanner.InvalidateCache();
            task = scanner.FindJob(task.Id, task.WatchPath) ?? task with
            { FolderPath = move.NewFolderPath ?? task.FolderPath, State = target };
        }
        else if (task.State != target)
            throw new InvalidOperationException("orchestration-source-state-mismatch");

        if (target == TaskStates.HumanReview
            && HasPayloadProperty(occurrence.Run.PayloadJson, "reviewOutcome"))
        {
            using var reviewPayload = JsonDocument.Parse(occurrence.Run.PayloadJson);
            var outcome = reviewPayload.RootElement.GetProperty("reviewOutcome").GetString()
                ?? throw new InvalidDataException("The review outcome is missing.");
            var reviewAttemptId = reviewPayload.RootElement.TryGetProperty("reviewAttemptId", out var reviewId)
                && reviewId.ValueKind == JsonValueKind.String
                ? reviewId.GetString() : occurrence.AuthorityAttemptId;
            escalation.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
                task.FolderPath, outcome, decision?.Reason ?? string.Empty,
                reviewAttemptId: reviewAttemptId,
                requireJournal: !HasPayloadProperty(occurrence.Run.PayloadJson, "monolithGateResult"));
        }

        if (target == TaskStates.AutoReview)
        {
            using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
            if (payload.RootElement.TryGetProperty("reviewAttemptRequest", out var requestElement)
                && requestElement.ValueKind == JsonValueKind.Object)
            {
                var request = requestElement.Deserialize<CreateReviewAttemptRequest>(Json)
                    ?? throw new InvalidDataException("The coding review attempt is missing.");
                var review = reviewAttemptLifecycle.CreateReviewAttemptInAutoReview(task, request);
                if (!review.Accepted || review.ReviewAttempt is null)
                    throw new InvalidOperationException($"Engine review attempt could not be created: {review.Status} {review.Message}");
                if (!payload.RootElement.TryGetProperty("reviewSubject", out var subjectElement)
                    || subjectElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The coding review subject is missing.");
                var subject = subjectElement.Deserialize<ReviewSubjectRecord>(Json)
                    ?? throw new InvalidDataException("The coding review subject is invalid.");
                ReviewSubjectStore.Write(task.FolderPath, subject);
            }
        }

        if (decision?.Action is TransitionAction.FindingRound or TransitionAction.ConcernRound
            or TransitionAction.ReturnToReady)
        {
            var marker = $"<!-- engine-decision:{occurrence.Run.RunId} -->";
            var promptPath = Path.Combine(task.FolderPath, "prompt.md");
            var current = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
            if (!current.Contains(marker, StringComparison.Ordinal))
            {
                var findings = decision.Action == TransitionAction.ReturnToReady
                    ? GateReason(occurrence.Run.PayloadJson)
                    : string.Join("\n", decision.Findings.Select(finding =>
                        $"- {finding.Aspect}: {finding.Summary}"));
                if (!mutations.AppendContinuationNote(task.Id,
                    $"{marker}\n{(decision.Action == TransitionAction.ReturnToReady ? "Fix the build and test gate failure:" : "Fix the reviewed findings on the reviewed delivery:")}\n{findings}", task.WatchPath))
                    throw new IOException("The engine follow-up prompt could not be appended.");
            }
        }
        EngineDecisionWaitMarker.Clear(task.FolderPath, occurrence.Run.RunId);
        if (decision?.Action is TransitionAction.FindingRound or TransitionAction.ConcernRound
            && !timeline.ReadAll(task.FolderPath).Any(entry => entry.Details is not null
            && entry.Details.TryGetValue("authorityWriteKey", out var recorded)
            && recorded == key))
            timeline.Append(task.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.System, $"Engine decision {decision?.Action.ToString() ?? completion.Action.ToString()} applied.",
                occurrence.AuthorityAttemptId,
                details: new Dictionary<string, string>
                {
                    ["orchestrationRunId"] = occurrence.Run.RunId,
                    ["authorityWriteKey"] = key,
                    ["reviewAttemptId"] = occurrence.AuthorityAttemptId,
                    ["cause"] = decision.Action == TransitionAction.ConcernRound
                        ? "multi-aspect-concern" : "multi-aspect-block",
                });
        scanner.InvalidateCache();
    }

    private void EnsureLinkedFollowUp(AgentStudio.Shared.TaskInfo task,
        MonolithOrchestrationOccurrence occurrence, TransitionDecision decision)
    {
        var marker = $"engine-follow-up:{occurrence.Run.RunId}";
        var existing = scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.CreationSource == marker);
        if (existing is null)
        {
            var prompt = $"Fix the remaining review findings from {task.Key ?? task.Id}.\n\n"
                + string.Join("\n", decision.Findings.Select(item => $"- {item.Aspect}: {item.Summary}"))
                + $"\n\nSource card: {task.Key ?? task.Id}.";
            var createdId = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            {
                Title = $"Follow-up: review findings in {task.Key ?? task.Id}",
                Agent = task.Agent,
                CliType = task.CliType,
                Model = task.Model,
                ThinkingLevel = task.ThinkingLevel,
                WatchPath = task.WatchPath,
                PromptMarkdown = prompt,
                TargetState = TaskStates.Ready,
                TaskType = TaskTypes.Bug,
                OwnerClientId = task.OwnerClientId,
                CreationSource = marker,
                CreatedBy = "pipeline",
            }) ?? throw new IOException("The engine follow-up could not be created.");
            existing = scanner.FindJob(createdId, task.WatchPath)
                ?? throw new IOException("The engine follow-up could not be read.");
        }
        var sourceKey = task.Key ?? task.Id;
        var followUpKey = existing.Key ?? existing.Id;
        mutations.SetTaskReferences(existing.Id, (existing.References ?? new AgentStudio.Shared.TaskReferences()) with
        { FollowUpOf = [sourceKey] }, existing.WatchPath);
        mutations.SetTaskReferences(task.Id, (task.References ?? new AgentStudio.Shared.TaskReferences()) with
        {
            RaisedFollowUps = (task.References?.RaisedFollowUps ?? [])
                .Append(followUpKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }, task.WatchPath);
    }

    private async Task ApplySweepAsync(AgentStudio.Shared.TaskInfo task,
        MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<OperatorSweepStagePayload>(occurrence.Run.PayloadJson, Json)
            ?? throw new InvalidDataException("Sweep occurrence payload is missing.");
        using var output = JsonDocument.Parse(completion.OutputJson);
        if (!output.RootElement.TryGetProperty("sweepDecision", out var element))
            throw new InvalidDataException("Engine sweep decision is missing.");
        var decision = element.Deserialize<OperatorSweepDecision>(Json)
            ?? throw new InvalidDataException("Engine sweep decision is invalid.");
        if (decision.Action == OperatorSweepAction.Act)
        {
            var trigger = payload.Trigger
                ?? throw new InvalidDataException("Acting sweep has no trigger.");
            var authorityWrite = new AttemptWriteReference(occurrence.AuthorityAttemptId,
                occurrence.AuthorityFence, occurrence.AuthorityEpoch,
                $"engine-sweep:{occurrence.Run.RunId}:{completion.IdempotencyKey}");
            OperatorSweepActionResult result = task.State != occurrence.ExpectedSourceState
                && task.State is (TaskStates.Ready or TaskStates.Progress)
                ? new OperatorSweepActionResult(true, "The sweep round already moved the card.")
                : payload.Sweep switch
            {
                OperatorSweepKinds.FixRounds => await sweepActions.OpenFixRoundAsync(task,
                    new OperatorSweepFixRound(payload.ReviewAttemptId
                        ?? throw new InvalidDataException("Review attempt is missing."),
                        payload.FollowUp ?? throw new InvalidDataException("Fix-round prompt is missing."),
                        ReviewFixRoundBase.Decide(payload.ReviewAttemptId!, payload.ResultRef,
                            payload.ResultSha, DateTime.UtcNow), authorityWrite), ct),
                OperatorSweepKinds.GateTriage => await sweepActions.ContinueGateFailureAsync(
                    task, trigger.SubjectKey, ct),
                OperatorSweepKinds.Salvage => await sweepActions.ContinueSalvageAsync(task,
                    new OperatorSweepSalvageFacts(new RunSalvageReference(
                        payload.SalvageBranch ?? throw new InvalidDataException("Salvage branch is missing."),
                        payload.SalvageSha ?? throw new InvalidDataException("Salvage SHA is missing.")),
                        occurrence.AuthorityAttemptId, payload.SalvageReason), ct),
                _ => throw new InvalidDataException("Unknown operator sweep."),
            };
            if (!result.Started) throw new InvalidOperationException(result.Detail);
            var moved = scanner.FindJob(task.Id, task.WatchPath) ?? task;
            var workspace = configuration["TaskRepository"]
                ?? throw new InvalidOperationException("TaskRepository is unavailable.");
            if (!ReviewDecisionLog.ReadAll(workspace, task.ProjectName).Any(record =>
                record.JobId == task.Id && record.FailureKind == $"operator-sweep:{payload.Sweep}"
                && record.Response == trigger.SubjectKey))
                ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
                    CreatedAt: DateTime.UtcNow, JobId: task.Id, Project: task.ProjectName,
                    Kind: ReviewDecisionKind.Reissue,
                    Reason: $"Operator sweep {payload.Sweep}: {decision.Reason}",
                    Prompt: $"(operator sweep {payload.Sweep})",
                    Response: trigger.SubjectKey, FollowUp: result.Detail)
                {
                    AttemptEpoch = OperatorReviewRequeueService.ReadEpoch(moved.FolderPath),
                    FailureKind = $"operator-sweep:{payload.Sweep}",
                });
            if (!timeline.ReadAll(moved.FolderPath).Any(entry => entry.Kind == TimelineEventKinds.OperatorSweepRoundStarted
                && entry.Details?.GetValueOrDefault(OperatorSweepTriggers.ReceiptSubjectKey) == trigger.SubjectKey
                && entry.Details?.GetValueOrDefault(OperatorSweepTriggers.ReceiptSweepKey) == payload.Sweep))
            {
                var receipt = CardRoundBudgetReceipt.Details(
                    payload.Guard.Budget with { Used = payload.Guard.Budget.Used + 1 },
                    [], $"operator-sweep:{payload.Sweep}");
                receipt[OperatorSweepTriggers.ReceiptSweepKey] = payload.Sweep;
                receipt[OperatorSweepTriggers.ReceiptSubjectKey] = trigger.SubjectKey;
                receipt["orchestrationRunId"] = occurrence.Run.RunId;
                timeline.Append(moved.FolderPath, TimelineEventKinds.OperatorSweepRoundStarted,
                    TimelineActors.System, $"Engine sweep {payload.Sweep} opened a round.",
                    details: receipt);
            }
            task = moved;
        }
        EngineDecisionWaitMarker.Clear(task.FolderPath, occurrence.Run.RunId);
        scanner.InvalidateCache();
    }

    /// <summary>
    /// AGT-3060: an AI Operator receipt. Hold has no effect; Propose records an
    /// A2 proposal for a person; Act and Ask run the decider's keyed effect.
    /// </summary>
    private async Task ApplyOperatorDecisionAsync(MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion, CancellationToken ct)
    {
        var payload = AiOperatorDecisionGateway.ReadPayload(occurrence.Run.PayloadJson);
        var decision = AiOperatorDecisionGateway.ReadDecision(completion.OutputJson);
        if (decision.Decider != payload.Decider || decision.SubjectKey != payload.SubjectKey)
            throw new InvalidDataException("The AI Operator receipt does not match its occurrence.");
        if (decision.Disposition == AiOperatorDisposition.Hold) return;
        if (decision.Disposition == AiOperatorDisposition.Propose)
        {
            RecordOperatorDecision(occurrence, decision, "AI Operator proposal waits for a person");
            return;
        }
        var effect = services.GetServices<IAiOperatorEffect>()
            .SingleOrDefault(candidate => candidate.Decider == decision.Decider)
            ?? throw new InvalidOperationException($"No authority effect is registered for {decision.Decider}.");
        await effect.ApplyAsync(new AiOperatorApplication(occurrence.Run.RunId, occurrence.Run.ProjectId,
            occurrence.Run.TaskId, occurrence.ExpectedSourceState,
            $"engine-operator:{occurrence.Run.RunId}:{completion.IdempotencyKey}", payload, decision,
            occurrence.AuthorityAttemptId, occurrence.AuthorityFence, occurrence.AuthorityEpoch), ct);
        RecordOperatorDecision(occurrence, decision, decision.Disposition == AiOperatorDisposition.Ask
            ? "AI Operator asked a person" : "AI Operator applied");
    }

    private void RecordOperatorDecision(MonolithOrchestrationOccurrence occurrence,
        AiOperatorDecision decision, string summary)
    {
        var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));
        if (task is null) return;
        var topic = "ai-operator:" + occurrence.Run.RunId;
        if (operatorLog.Read(task.WatchPath).Any(entry => entry.Topic == topic)) return;
        operatorLog.Append(task.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = topic,
            JobId = task.Id,
            Summary = $"{summary}: {decision.Decider} {decision.Effect} on {task.Key ?? task.Id} ({decision.Autonomy}).",
            Reasoning = decision.Detail,
        });
    }

    private static string? TargetFromPayload(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        if (payload.RootElement.TryGetProperty("targetState", out var state)
            && state.ValueKind == JsonValueKind.String)
            return state.GetString();
        return null;
    }

    private static bool HasPayloadProperty(string payloadJson, string property)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty(property, out _);
    }

    private static string GateReason(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("gateReason", out var reason)
            && reason.ValueKind == JsonValueKind.String
            ? reason.GetString() ?? "The build and test gate failed."
            : "The build and test gate failed.";
    }
}
