namespace AgentStudio.Runner;

/// <summary>
/// AGT-3060: applies the AI Operator's receipt for a remote completion that
/// ended without a recognized terminal outcome. The remote completion endpoint
/// settles the attempt and submits the frozen facts; the continuation round or
/// the visible park follows under the completing attempt's authority.
/// </summary>
public sealed class RunTimeoutSalvageEffect(
    TaskScannerService scanner,
    RunTimeoutContinuationService continuations,
    HumanReviewEscalation escalation,
    AgentStudio.Tasks.TimelineLog timeline,
    OrchestratorLog operatorLog) : IAiOperatorEffect
{
    public string Decider => AiOperatorDeciders.TimeoutSalvage;

    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == application.ProjectId
            && (candidate.Id == application.TaskId || candidate.TaskKey == application.TaskId));
        if (task is null) throw new KeyNotFoundException("The timeout completion card was not found.");
        if (task.State != application.ExpectedSourceState) return;
        var facts = application.Payload.TimeoutSalvage
            ?? throw new InvalidDataException("The timeout-salvage occurrence has no facts.");
        var attemptId = ReadAttemptId(application.Payload.SubjectKey);
        var write = new AttemptWriteReference(application.AuthorityAttemptId,
            application.AuthorityFence, application.AuthorityEpoch, application.WriteKey);
        var reason = application.Decision.Detail;
        if (application.Decision.Effect == AiOperatorEffects.StartContinuation)
        {
            var continuation = await continuations.StartAsync(task,
                facts.Salvage ?? throw new InvalidDataException("The continuation has no salvage."),
                facts.ReportedReason ?? string.Empty, attemptId, write, ct);
            if (continuation.Started)
            {
                operatorLog.Append(task.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Decision,
                    Topic = RunTimeoutSalvageContinuationPolicy.ContinuationReason,
                    JobId = task.Id,
                    Summary = $"Started continuation round {continuation.Round} for \"{task.Title}\": "
                        + $"the run ended without a terminal outcome, salvaged as {facts.Salvage.Describe()}.",
                    Reasoning = continuation.Reason,
                });
                return;
            }
            // The bounded round could not start; the person sees why.
            task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
            if (task.State != application.ExpectedSourceState) return;
            reason = RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason(
                $"{facts.ReportedReason} (continuation not started: {continuation.Reason})".Trim(),
                facts.Salvage, facts.AutomaticContinuationRoundsUsed);
        }
        else if (application.Decision.Effect != AiOperatorEffects.Escalate) return;

        var escalated = await escalation.EscalateAsync(task.Id, task.WatchPath, task.ProjectName,
            HumanReviewEscalationCategories.RemoteOutcomeUnknown, reason, ct, write);
        if (escalated.Status != MoveJobStatus.Success)
            throw new InvalidOperationException($"Escalation lane move refused: {escalated.Status} {escalated.Message}");
        timeline.Append(escalated.NewFolderPath ?? task.FolderPath, TimelineEventKinds.OrchestratorEscalated,
            TimelineActors.System, reason, runId: attemptId,
            details: new Dictionary<string, string>
            {
                ["category"] = HumanReviewEscalationCategories.RemoteOutcomeUnknown,
                ["reason"] = reason,
                ["orchestrationRunId"] = application.RunId,
            });
        operatorLog.Append(task.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Intervention,
            Topic = HumanReviewEscalationCategories.RemoteOutcomeUnknown,
            JobId = task.Id,
            Summary = $"Escalated \"{task.Title}\": {reason}",
            Reasoning = reason,
        });
    }

    /// <summary>Subject key shape: <c>timeout:&lt;attemptId&gt;:&lt;completionKey&gt;</c>.</summary>
    public static string SubjectKey(string attemptId, string completionKey) => $"timeout:{attemptId}:{completionKey}";

    private static string ReadAttemptId(string subjectKey)
    {
        var parts = subjectKey.Split(':', 3);
        return parts.Length == 3 && parts[0] == "timeout" && parts[1].Length > 0
            ? parts[1] : throw new InvalidDataException("The timeout-salvage subject is malformed.");
    }
}
