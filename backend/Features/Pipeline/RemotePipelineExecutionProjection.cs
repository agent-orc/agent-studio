using System.Globalization;
using AgentStudio.Runner;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Read-time bridge between the remote execution lifecycle and the existing
/// task pipeline table. Remote runners deliberately do not write the local
/// <c>pipeline-execution.json</c> lifecycle. Their canonical facts already live
/// in task/session metadata, the task timeline, Remote Review Grade evidence,
/// and the token ledger. This projection joins those sources without creating
/// another persisted state.
/// </summary>
internal static class RemotePipelineExecutionProjection
{
    internal const string NotApplicableReason =
        "Executed remotely; this local pipeline step is not applicable.";

    internal sealed record Result(
        PipelineExecutionRecord? Execution,
        IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> LedgerCalls);

    internal sealed record ReviewGrade(
        string AttemptId,
        DateTime ReceivedAt,
        string Outcome,
        string? Summary,
        IReadOnlyDictionary<string, ReviewGradeAspect> Aspects);

    internal sealed record ReviewGradeAspect(string Status, string Summary);

    public static Result Project(
        PipelineExecutionRecord? local,
        TaskPipeline pipeline,
        TaskInfo task,
        IReadOnlyList<SessionEvent> sessions,
        IReadOnlyList<TimelineEvent> timeline,
        TaskTokenSummary? tokenSummary,
        BatchGateStore? batchGates = null)
    {
        var remoteSessions = sessions
            .Where(IsRemoteSession)
            .OrderBy(item => item.Ts)
            .ToList();
        var remoteCompletions = timeline
            .Where(IsRemoteCompletion)
            .OrderBy(item => item.Ts)
            .ToList();

        if (remoteSessions.Count == 0 && remoteCompletions.Count == 0)
            return new Result(local, EmptyLedger());
        var latestSession = sessions.OrderBy(item => item.Ts).LastOrDefault();
        var latestCompletion = timeline
            .Where(item => string.Equals(
                item.Kind,
                TimelineEventKinds.AgentRunFinished,
                StringComparison.Ordinal))
            .OrderBy(item => item.Ts)
            .LastOrDefault();
        if ((latestSession is not null
                && !IsRemoteSession(latestSession)
                && latestSession.Ts > (remoteSessions.LastOrDefault()?.Ts ?? DateTime.MinValue))
            || (latestCompletion is not null
                && !IsRemoteCompletion(latestCompletion)
                && latestCompletion.Ts > (remoteCompletions.LastOrDefault()?.Ts ?? DateTime.MinValue)))
        {
            return new Result(local, EmptyLedger());
        }

        var completion = remoteCompletions.LastOrDefault();
        var startedAt = remoteSessions.LastOrDefault(item =>
                completion is null || item.Ts <= completion.Ts)?.Ts
            ?? remoteSessions.LastOrDefault()?.Ts
            ?? local?.StartedAt
            ?? task.CreatedAt;
        var grade = ReadLatestGrade(task.FolderPath);
        var completedAt = completion?.Ts;
        var decisionId = ResolveDecisionStepId(pipeline);

        var execution = local ?? NewRecord(pipeline, task, startedAt);
        var attempt = execution.Attempt <= 0 ? 1 : execution.Attempt;
        var steps = execution.Steps
            .Select(step => step with { Attempt = step.Attempt ?? attempt })
            .ToList();

        if (grade is not null)
            ReconcileLegacyRemoteAspectSteps(steps, grade);

        SkipRemoteOnlySteps(steps, pipeline.Pre.Select(step => step.Id));
        SkipRemoteOnlySteps(
            steps,
            pipeline.Post
                .Where(step =>
                    step.Kind is StepKind.Aspect or StepKind.Drift
                    || step.Id is PipelineCatalogue.OrchestratorReviewStepId
                        or PipelineCatalogue.CodeReviewGradeStepId)
                .Select(step => step.Id));

        var allocations = AllocateLedgerCalls(
            tokenSummary,
            task,
            startedAt,
            completion?.Ts,
            grade,
            decisionId);
        var coreCalls = allocations.GetValueOrDefault(PipelineCatalogue.CoreAgentRunStepId) ?? [];
        if (completion is not null)
        {
            Upsert(
                steps,
                pipeline,
                PipelineCatalogue.CoreAgentRunStepId,
                BuildCoreStep(task, completion, startedAt, attempt, coreCalls));
        }

        if (grade is not null)
        {
            var decisionCalls = allocations.GetValueOrDefault(decisionId) ?? [];
            Upsert(
                steps,
                pipeline,
                decisionId,
                BuildDecisionStep(decisionId, grade, attempt, decisionCalls));
        }

        var settlement = ReadLatestSettlement(task.FolderPath, startedAt);
        if (settlement is not null)
            ProjectSettlement(steps, pipeline, settlement.Value.Entry,
                settlement.Value.Number, decisionId);
        if (batchGates is not null)
            ProjectBatchGate(steps, pipeline, task, batchGates, settlement?.Entry);

        var latestProjectedAt = new[]
            {
                execution.CompletedAt,
                completedAt,
                grade?.ReceivedAt,
                steps.Where(step => step.CompletedAt.HasValue)
                    .Select(step => step.CompletedAt)
                    .Max(),
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var taskSettled = task.State is TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive;
        var recordCompletedAt = execution.CompletedAt
            ?? (taskSettled && latestProjectedAt != default ? latestProjectedAt : null);

        execution = execution with
        {
            StartedAt = Min(execution.StartedAt, startedAt),
            CompletedAt = recordCompletedAt,
            Steps = steps,
        };
        return new Result(execution, allocations);
    }

    private static PipelineExecutionRecord NewRecord(
        TaskPipeline pipeline,
        TaskInfo task,
        DateTime startedAt) =>
        new()
        {
            PipelineId = pipeline.Id,
            PipelineVersion = pipeline.Version,
            Project = task.ProjectName,
            JobId = task.Id,
            StartedAt = startedAt,
            Attempt = 1,
            Steps = pipeline.AllSteps.Select(step => new PipelineStepExecution
            {
                StepId = step.Id,
                Kind = step.Kind,
                Attempt = 1,
                Model = step.Model,
                Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
            }).ToList(),
        };

    private static PipelineStepExecution BuildCoreStep(
        TaskInfo task,
        TimelineEvent completion,
        DateTime startedAt,
        int attempt,
        IReadOnlyList<TaskTokenCall> calls)
    {
        var statusToken = Detail(completion, "status")?.Trim().ToLowerInvariant();
        var passed = statusToken is "done" or "noop" or "completed" or "pass";
        // AGT-3045: a run that parked on operator decisions is not a failure.
        var needsDecision = statusToken is "needsinput" or "needs-input" or "needs-decision";
        var completedAt = completion.Ts;
        return WithLedgerUsage(
            new PipelineStepExecution
            {
                StepId = PipelineCatalogue.CoreAgentRunStepId,
                Kind = StepKind.Core,
                Attempt = attempt,
                Model = task.Model,
                ThinkingLevel = task.ThinkingLevel,
                Status = passed ? PipelineStepStatus.Passed
                    : needsDecision ? PipelineStepStatus.NeedsDecision
                    : PipelineStepStatus.Failed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = DurationMs(startedAt, completedAt),
                Reason = passed
                    ? "Remote runner completed the fenced coding run."
                    : needsDecision
                        ? "Remote run parked on open operator decisions (NeedsInput)."
                        : completion.Summary,
                Verdict = statusToken,
            },
            calls,
            task.Model);
    }

    private static PipelineStepExecution BuildDecisionStep(
        string stepId,
        ReviewGrade grade,
        int attempt,
        IReadOnlyList<TaskTokenCall> calls)
    {
        var passed = string.Equals(grade.Outcome, "Pass", StringComparison.OrdinalIgnoreCase);
        var verdict = grade.Outcome.Trim().ToLowerInvariant() switch
        {
            "productfailure" => "product-failure",
            "reviewinfra" => "review-infra",
            var value => value,
        };
        var reason = $"Remote Review Plane verdict {grade.Outcome} (attempt {grade.AttemptId}).";
        if (!string.IsNullOrWhiteSpace(grade.Summary))
            reason += " " + grade.Summary!.Trim();

        return WithLedgerUsage(
            new PipelineStepExecution
            {
                StepId = stepId,
                Kind = StepKind.Orchestrator,
                Attempt = attempt,
                Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                StartedAt = grade.ReceivedAt,
                CompletedAt = grade.ReceivedAt,
                DurationMs = 0,
                Reason = reason,
                Verdict = verdict,
                VerdictSummary = grade.Summary,
            },
            calls,
            calls.LastOrDefault()?.Model);
    }

    private static PipelineStepExecution WithLedgerUsage(
        PipelineStepExecution step,
        IReadOnlyList<TaskTokenCall> calls,
        string? fallbackModel)
    {
        if (calls.Count == 0) return step;
        return step with
        {
            Model = calls.LastOrDefault(call => !string.IsNullOrWhiteSpace(call.Model))?.Model
                ?? fallbackModel,
            InputTokens = calls.Sum(call => call.InputTokens),
            OutputTokens = calls.Sum(call => call.OutputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
            InputIncludesCached = calls.Select(call => call.InputIncludesCached).Distinct().Count() == 1
                ? calls[0].InputIncludesCached
                : null,
            TokenUsageSource = $"Remote token ledger · {calls.Count} call{(calls.Count == 1 ? "" : "s")}",
        };
    }

    private static Dictionary<string, IReadOnlyList<TaskTokenCall>> AllocateLedgerCalls(
        TaskTokenSummary? summary,
        TaskInfo task,
        DateTime startedAt,
        DateTime? coreCompletedAt,
        ReviewGrade? grade,
        string decisionStepId)
    {
        var result = new Dictionary<string, IReadOnlyList<TaskTokenCall>>(StringComparer.OrdinalIgnoreCase);
        if (summary is null || summary.TotalTokens <= 0) return result;

        var calls = summary.Entries
            .Where(call => call.Ts == default || call.Ts >= startedAt)
            .OrderBy(call => call.Ts)
            .ToList();
        if (calls.Count == 0)
        {
            calls.Add(new TaskTokenCall
            {
                Ts = summary.LastUpdate ?? coreCompletedAt ?? startedAt,
                Model = summary.LastModel ?? task.Model,
                ParticipantId = "agent:remote",
                InputTokens = summary.InputTokens,
                OutputTokens = summary.OutputTokens,
                CacheReadTokens = summary.CacheReadTokens,
                CacheCreationTokens = summary.CacheCreationTokens,
                EstimatedApiCostUsd = summary.EstimatedApiCostUsd,
                ModelPriced = summary.AllModelsPriced,
            });
        }

        var reviewCalls = calls.Where(call =>
            call.ParticipantId?.StartsWith(RemotePipelineReviewEvidenceProjector.ReviewParticipantPrefix,
                StringComparison.Ordinal) == true
            && ReviewStepId(call.ParticipantId) is not null).ToList();
        var remaining = calls.Where(call => !reviewCalls.Contains(call)).ToList();
        var agentCalls = remaining
            .Where(call => TokenParticipants.IsAgent(call.ParticipantId))
            .ToList();
        if (agentCalls.Count == 0)
            agentCalls = remaining.Where(call => grade is null || call.Ts <= (coreCompletedAt ?? grade.ReceivedAt)).ToList();
        if (agentCalls.Count == 0 && remaining.Count > 0)
            agentCalls.Add(remaining[0]);

        var agentSet = agentCalls.ToHashSet();
        foreach (var group in reviewCalls.GroupBy(call =>
                     ReviewStepId(call.ParticipantId), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key is not null) result[group.Key] = group.ToList();
        }
        var decisionCalls = remaining.Where(call => !agentSet.Contains(call)).ToList();
        if (grade is null && decisionCalls.Count > 0)
        {
            agentCalls.AddRange(decisionCalls);
            decisionCalls.Clear();
        }

        if (agentCalls.Count > 0) result[PipelineCatalogue.CoreAgentRunStepId] = agentCalls;
        if (grade is not null && decisionCalls.Count > 0)
            result[decisionStepId] = decisionCalls;
        return result;
    }

    private static string? ReviewStepId(string? participant)
    {
        if (participant is null) return null;
        var marker = participant.IndexOf(":step:", StringComparison.Ordinal);
        if (marker < 0) return null;
        var start = marker + ":step:".Length;
        var end = participant.IndexOf(":usage:", start, StringComparison.Ordinal);
        return end > start ? participant[start..end] : null;
    }

    private static string ResolveDecisionStepId(TaskPipeline pipeline) =>
        pipeline.AllSteps.Any(step =>
            string.Equals(
                step.Id,
                PipelineCatalogue.OrchestratorDecisionStepId,
                StringComparison.OrdinalIgnoreCase))
            ? PipelineCatalogue.OrchestratorDecisionStepId
            : PipelineCatalogue.CodeReviewGradeStepId;

    private static void SkipRemoteOnlySteps(
        List<PipelineStepExecution> steps,
        IEnumerable<string> stepIds)
    {
        var ids = stepIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!ids.Contains(step.StepId)
                || step.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Planned))
                continue;
            steps[i] = step with
            {
                Status = PipelineStepStatus.Skipped,
                Reason = NotApplicableReason,
            };
        }
    }

    private static (RemoteReviewSettlementEntry Entry, int Number)? ReadLatestSettlement(
        string folder, DateTime startedAt)
    {
        var logs = TaskPaths.LogsDir(folder);
        if (!Directory.Exists(logs)) return null;
        var entries = Directory.EnumerateFiles(logs, "remote-review-settlement-*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)["remote-review-settlement-".Length..])
            .Select(id => RemoteReviewSettlementJournal.Read(folder, id).Entry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.ReceivedAtUtc)
            .ToArray();
        var latest = entries.LastOrDefault(entry => entry.ReceivedAtUtc >= startedAt);
        return latest is null ? null : (latest, Array.IndexOf(entries, latest) + 1);
    }

    private static void ProjectSettlement(List<PipelineStepExecution> steps, TaskPipeline pipeline,
        RemoteReviewSettlementEntry entry, int number, string decisionId)
    {
        var report = entry.Report;
        var gradeVerdict = report.Outcome.Trim().ToLowerInvariant() switch
        {
            "productfailure" => "product-failure",
            "reviewinfra" => "review-infra",
            "integrationbranchdefect" => "integration-branch-defect",
            var value => value,
        };
        var gradeRef = $"remote-review-grade-{entry.AttemptId}.md";
        var agentCommands = report.Commands
            .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .ToArray();
        var firstCommand = agentCommands.MinBy(command => command.StartedAt);
        var lastCommand = agentCommands.MaxBy(command => command.FinishedAt);
        var gradeStartedAt = firstCommand?.StartedAt ?? entry.ReceivedAtUtc;
        var gradeDurationMs = firstCommand is null || lastCommand is null ? 0
            : Math.Max(0, (long)(lastCommand.FinishedAt - gradeStartedAt).TotalMilliseconds);
        var grade = steps.FirstOrDefault(step => step.StepId == decisionId);
        if (grade is not null)
        {
            var passed = string.Equals(report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase);
            Upsert(steps, pipeline, decisionId, grade with
            {
                Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                Verdict = gradeVerdict,
                VerdictSummary = report.Summary,
                Reason = $"Remote Review attempt {number}: {report.Summary ?? report.Outcome}",
                EvidenceRef = gradeRef,
                Model = lastCommand?.Model ?? grade.Model,
                ReviewCli = lastCommand is null ? grade.ReviewCli
                    : Path.GetFileNameWithoutExtension(lastCommand.FileName),
                StartedAt = gradeStartedAt,
                CompletedAt = entry.ReceivedAtUtc,
                DurationMs = gradeDurationMs,
                ExecutionLocation = "remote",
                ExecutionAttemptId = entry.AttemptId,
                ReviewAttemptNumber = number,
            });
        }
        if (decisionId != PipelineCatalogue.CodeReviewGradeStepId)
        {
            var row = steps.FirstOrDefault(step => step.StepId == PipelineCatalogue.CodeReviewGradeStepId);
            if (row is not null)
                Upsert(steps, pipeline, row.StepId, row with
                {
                    Status = string.Equals(report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
                        ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                    Verdict = gradeVerdict,
                    VerdictSummary = report.Summary,
                    Reason = $"Remote Review attempt {number}: {report.Summary ?? report.Outcome}",
                    EvidenceRef = gradeRef,
                    Model = lastCommand?.Model ?? row.Model,
                    ReviewCli = lastCommand is null ? row.ReviewCli
                        : Path.GetFileNameWithoutExtension(lastCommand.FileName),
                    StartedAt = gradeStartedAt,
                    CompletedAt = entry.ReceivedAtUtc,
                    DurationMs = gradeDurationMs,
                    ExecutionLocation = "remote",
                    ExecutionAttemptId = entry.AttemptId,
                    ReviewAttemptNumber = number,
                });
        }

        foreach (var verdict in report.Verdicts)
        {
            var stepId = verdict.Aspect.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? verdict.Aspect : "aspect-" + verdict.Aspect;
            var row = steps.FirstOrDefault(step => string.Equals(step.StepId, stepId,
                StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            var command = report.Commands.LastOrDefault(item =>
                string.Equals(item.StepId, stepId, StringComparison.OrdinalIgnoreCase)
                && Contract.ReviewCommandKinds.IsAgent(item.ExecutionKind));
            var failed = command is not null && Contract.ReviewFailureAttributionPolicy.Failed(command);
            var status = verdict.Status.ToLowerInvariant();
            Upsert(steps, pipeline, stepId, row with
            {
                Status = failed || status is "block" or "blocked" or "fail" or "failed" or "infrastructure"
                    ? PipelineStepStatus.Failed : PipelineStepStatus.Passed,
                Verdict = status,
                VerdictSummary = verdict.Summary,
                Reason = verdict.Summary,
                Model = command?.Model ?? row.Model,
                ThinkingLevel = command?.ThinkingLevel ?? row.ThinkingLevel,
                ReviewCli = command is null ? row.ReviewCli : Path.GetFileNameWithoutExtension(command.FileName),
                StartedAt = command?.StartedAt ?? row.StartedAt,
                CompletedAt = command?.FinishedAt ?? row.CompletedAt,
                DurationMs = command is null ? row.DurationMs
                    : Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds),
                InputTokens = command?.InputTokens ?? row.InputTokens,
                OutputTokens = command?.OutputTokens ?? row.OutputTokens,
                CacheReadTokens = command?.CacheReadTokens ?? row.CacheReadTokens,
                CacheCreationTokens = command?.CacheCreationTokens ?? row.CacheCreationTokens,
                EvidenceRef = gradeRef,
                ExecutionLocation = "remote",
                ExecutionAttemptId = entry.AttemptId,
                ReviewAttemptNumber = number,
            });
        }
        foreach (var skipped in report.SkippedAspects ?? [])
        {
            var stepId = skipped.Aspect.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? skipped.Aspect : "aspect-" + skipped.Aspect;
            var row = steps.FirstOrDefault(step => string.Equals(step.StepId, stepId,
                StringComparison.OrdinalIgnoreCase));
            if (row is null || row.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Skipped)) continue;
            Upsert(steps, pipeline, stepId, row with
            {
                Status = PipelineStepStatus.Skipped, Reason = skipped.Reason,
                ExecutionAttemptId = entry.AttemptId, ReviewAttemptNumber = number,
            });
        }
        if (report.Verdicts.Any(item => item.Aspect == "build-tests"
            && item.Classification == "DeferredToBatch"))
        {
            var row = steps.FirstOrDefault(step => step.StepId == PipelineCatalogue.BuildTestGateStepId);
            if (row is not null && row.Status is PipelineStepStatus.Pending or PipelineStepStatus.Skipped)
                Upsert(steps, pipeline, row.StepId, row with
                {
                    Status = PipelineStepStatus.Skipped,
                    Reason = "Build and tests deferred to the batch gate.",
                    Verdict = "deferred-to-batch",
                    ExecutionAttemptId = entry.AttemptId,
                    ReviewAttemptNumber = number,
                });
        }
        else if (!report.Commands.Any(command => command.Phase == "verification"
            && !Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)))
        {
            var row = steps.FirstOrDefault(step => step.StepId == PipelineCatalogue.BuildTestGateStepId);
            if (row is not null && row.Status is PipelineStepStatus.Pending or PipelineStepStatus.Skipped)
                Upsert(steps, pipeline, row.StepId, row with
                {
                    Status = PipelineStepStatus.Skipped,
                    Reason = "This Remote Review did not run build or test commands.",
                    ExecutionAttemptId = entry.AttemptId,
                    ReviewAttemptNumber = number,
                });
        }
    }

    private static void ProjectBatchGate(List<PipelineStepExecution> steps, TaskPipeline pipeline,
        TaskInfo task, BatchGateStore store, RemoteReviewSettlementEntry? settlement)
    {
        if (settlement is null || !settlement.Report.Verdicts.Any(item =>
                item.Aspect == "build-tests" && item.Classification == "DeferredToBatch")) return;
        var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
        var batchId = ownership?.ReviewAttemptId == settlement?.AttemptId
            ? ownership.BatchId : null;
        if (batchId is null && settlement is not null)
            batchId = store.ListManifests()
                .Where(manifest => manifest.ClosedAtUtc.UtcDateTime >= settlement.ReceivedAtUtc
                    && manifest.Members.Any(member => member.TaskKey == task.TaskKey)
                    && store.ReadPublication(manifest.BatchId) is { } publication
                    && store.TryReadMember(manifest.BatchId, task.TaskKey,
                        publication.BatchRunId) is not null)
                .OrderByDescending(manifest => manifest.ClosedAtUtc)
                .FirstOrDefault()?.BatchId;
        if (batchId is null) return;
        var row = steps.FirstOrDefault(step => step.StepId == PipelineCatalogue.BuildTestGateStepId);
        if (row is null) return;
        var state = store.LatestState(batchId);
        var publication = store.ReadPublication(batchId);
        var runId = publication?.BatchRunId ?? state.BatchRunId ?? ownership?.BatchRunId;
        var verdict = runId is null ? null : store.ReadVerdict(batchId, runId);
        var phase = state.Phase.ToString().ToLowerInvariant();
        var passed = publication is not null && string.Equals(verdict?.Outcome, "pass", StringComparison.OrdinalIgnoreCase);
        Upsert(steps, pipeline, row.StepId, row with
        {
            Status = passed ? PipelineStepStatus.Passed
                : state.Phase is BatchPhase.Red or BatchPhase.Abandoned
                    ? PipelineStepStatus.Failed : PipelineStepStatus.Skipped,
            Verdict = verdict?.Outcome ?? phase,
            Reason = $"Batch {batchId}: {phase}; verdict {verdict?.Outcome ?? "pending"}"
                + (publication is null ? "." : $"; published {publication.VerifiedRemoteSha}."),
            CompletedAt = publication?.VerifiedAtUtc.UtcDateTime ?? verdict?.CompletedAtUtc.UtcDateTime,
            ReviewedResultSha = publication?.VerifiedRemoteSha ?? verdict?.TestedCandidateSha,
            BatchGate = new BatchGatePipelineEvidence(batchId, phase, verdict?.Outcome,
                publication?.VerifiedRemoteSha),
            ExecutionLocation = "batch",
        });
    }

    private static void ReconcileLegacyRemoteAspectSteps(
        List<PipelineStepExecution> steps,
        ReviewGrade grade)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Kind != StepKind.Aspect
                || step.Status != PipelineStepStatus.Failed
                || !string.Equals(step.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase))
                continue;
            var aspectId = step.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? step.StepId["aspect-".Length..]
                : step.StepId;
            if (!grade.Aspects.TryGetValue(aspectId, out var aspect)
                || !string.Equals(aspect.Status, "concerns", StringComparison.OrdinalIgnoreCase))
                continue;
            steps[index] = step with
            {
                Status = PipelineStepStatus.Passed,
                Verdict = "concerns",
                VerdictSummary = aspect.Summary,
            };
        }
    }

    private static void Upsert(
        List<PipelineStepExecution> steps,
        TaskPipeline pipeline,
        string stepId,
        PipelineStepExecution value)
    {
        var index = steps.FindIndex(step =>
            string.Equals(step.StepId, stepId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            steps[index] = value with { Kind = steps[index].Kind };
            return;
        }

        var catalogue = pipeline.AllSteps.FirstOrDefault(step =>
            string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (catalogue is not null)
            steps.Add(value with { Kind = catalogue.Kind });
    }

    private static bool IsRemoteSession(SessionEvent item) =>
        string.Equals(item.Cli, "remote-runner", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.ExecutionLocation?.ExecutionKind, "remote", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemoteCompletion(TimelineEvent item) =>
        string.Equals(item.Kind, TimelineEventKinds.AgentRunFinished, StringComparison.Ordinal)
        && (string.Equals(Detail(item, "cli"), "remote-runner", StringComparison.OrdinalIgnoreCase)
            || item.Summary.Contains("remote run", StringComparison.OrdinalIgnoreCase));

    private static string? Detail(TimelineEvent item, string key) =>
        item.Details is not null && item.Details.TryGetValue(key, out var value)
            ? value
            : null;

    private static long DurationMs(DateTime start, DateTime finish) =>
        Math.Max(0, (long)(finish - start).TotalMilliseconds);

    private static DateTime Min(DateTime left, DateTime right) =>
        left == default || right < left ? right : left;

    private static IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> EmptyLedger() =>
        new Dictionary<string, IReadOnlyList<TaskTokenCall>>(StringComparer.OrdinalIgnoreCase);

    internal static ReviewGrade? ReadLatestGrade(string jobFolder)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return null;
        ReviewGrade? latest = null;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "remote-review-grade-*.md"))
        {
            var parsed = ParseGrade(path);
            if (parsed is not null && (latest is null || parsed.ReceivedAt > latest.ReceivedAt))
                latest = parsed;
        }
        return latest;
    }

    private static ReviewGrade? ParseGrade(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return null; }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inFrontmatter = false;
        var frontmatterClosed = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line == "---")
            {
                if (!inFrontmatter && !frontmatterClosed) inFrontmatter = true;
                else if (inFrontmatter)
                {
                    inFrontmatter = false;
                    frontmatterClosed = true;
                }
                continue;
            }
            if (!inFrontmatter) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            fields[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }

        if (!fields.TryGetValue("attemptId", out var attemptId)
            || !fields.TryGetValue("receivedAt", out var receivedRaw)
            || !DateTime.TryParse(
                receivedRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var receivedAt)
            || !fields.TryGetValue("outcome", out var outcome))
        {
            return null;
        }

        string? summary = null;
        var outcomeLine = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("**Outcome:**", StringComparison.OrdinalIgnoreCase));
        if (outcomeLine >= 0)
        {
            for (var i = outcomeLine + 1; i < lines.Length; i++)
            {
                var candidate = lines[i].Trim();
                if (candidate.StartsWith("## ", StringComparison.Ordinal)) break;
                if (candidate.Length > 0)
                {
                    summary = candidate;
                    break;
                }
            }
        }

        return new ReviewGrade(attemptId, receivedAt, outcome, summary, ParseAspectRows(lines));
    }

    private static IReadOnlyDictionary<string, ReviewGradeAspect> ParseAspectRows(string[] lines)
    {
        var aspects = new Dictionary<string, ReviewGradeAspect>(StringComparer.OrdinalIgnoreCase);
        var heading = Array.FindIndex(lines, line =>
            line.Trim().Equals("## Aspect verdicts", StringComparison.OrdinalIgnoreCase));
        if (heading < 0) return aspects;
        for (var index = heading + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!line.StartsWith('|') || line.Contains("---", StringComparison.Ordinal)) continue;
            var cells = SplitMarkdownRow(line);
            if (cells.Count < 2 || cells[0].Equals("Aspect", StringComparison.OrdinalIgnoreCase)) continue;
            aspects[MarkdownLinkLabel(cells[0])] = new ReviewGradeAspect(
                cells[1],
                cells.Count >= 6 ? cells[5] : string.Empty);
        }
        return aspects;
    }

    private static string MarkdownLinkLabel(string value)
    {
        if (!value.StartsWith("[", StringComparison.Ordinal)
            || !value.EndsWith(")", StringComparison.Ordinal))
            return value;
        var separator = value.IndexOf("](", StringComparison.Ordinal);
        return separator > 1 ? value[1..separator] : value;
    }

    private static IReadOnlyList<string> SplitMarkdownRow(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var ch in line.Trim().Trim('|'))
        {
            if (escaped)
            {
                cell.Append(ch);
                escaped = false;
            }
            else if (ch == '\\') escaped = true;
            else if (ch == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        return value;
    }
}
