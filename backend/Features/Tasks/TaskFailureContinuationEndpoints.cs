using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>Creates an orchestrator-authored follow-up from the current durable failure.</summary>
public static class TaskFailureContinuationEndpoints
{
    public static void MapTaskFailureContinuationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/failure/continue", async (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            GitService git,
            TaskIntegrationStatusService integration,
            PipelineExecutionLog pipeline,
            TaskRunnerService runner,
            TimelineLog timeline,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (job.State is not (TaskStates.HumanReview or TaskStates.Escalated or TaskStates.AutoReview
                                  or TaskStates.Completed or TaskStates.Archive))
                return Results.Conflict(new { error = "A failure continuation requires a delivered task." });

            var status = integration.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
            if (status?.ReachUnavailable == true)
                return Results.Conflict(new { error = "Git reach is unavailable. Re-check integration before continuing." });
            if (status?.Status == IntegrationStatuses.Integrated)
                return Results.Conflict(new { error = "The current delivery is already integrated." });
            var steps = pipeline.Read(job.FolderPath)?.Steps;
            var subject = ReviewSubjectStore.Read(job.FolderPath);
            var failed = CurrentFailureStep(steps, status, subject);
            if (status?.Status == IntegrationStatuses.NoBranch && failed is null)
                return Results.Conflict(new { error = "This task has no delivery failure to continue." });
            if (status?.Failure is not null && failed is null)
                return Results.Conflict(new { error = "The recorded failure belongs to an earlier delivery. Re-check the task." });
            if (status is null && failed is null)
                return Results.Conflict(new { error = "Failure evidence is unavailable. Re-check the task." });
            if (status?.Failure is null && failed is null && status?.Status != IntegrationStatuses.Pending
                && status?.Status != IntegrationStatuses.Partial && status?.Status != IntegrationStatuses.MergedLocally)
                return Results.Conflict(new { error = "The current delivery has no failure to continue." });

            var stage = failed?.StepId ?? "delivery-pending";
            var currentFailure = status?.Failure is { } projectedFailure
                && failed?.StepId == projectedFailure.Stage ? projectedFailure : null;
            var reason = currentFailure?.Reason ?? failed?.Reason ?? status?.Detail
                ?? "The delivery is not reachable from the integration branch.";
            var findings = steps is null ? string.Empty : string.Join("; ", steps
                .Where(step => IsCurrentStep(step, subject) && step.Verdict is "concerns" or "block")
                .Select(step => $"{step.StepId}: {step.VerdictSummary ?? step.Reason}")
                .Take(8));
            var evidence = string.Join("\n", new[]
            {
                findings,
                failed?.VerdictSummary ?? string.Empty,
                ReadEvidenceExcerpt(job.FolderPath, failed?.EvidenceRef),
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var prompt = IntegrationContinuationPrompt.Build(
                job.Key ?? job.Id,
                subject?.ResultRef ?? status?.DeliveryRef,
                subject?.ResultSha,
                status?.IntegrationBranch ?? "develop",
                stage, reason,
                failed?.ConflictReport ?? currentFailure?.ConflictReport,
                evidence,
                integrationTip: ReadIntegrationTip(git, job.WatchPath, status?.IntegrationBranch),
                evidenceRef: failed?.EvidenceRef);
            try
            {
                // No model, CLI, or thinking override: task pins remain authoritative.
                var response = await runner.ContinueJobAsync(
                    job.Id, prompt, job.WatchPath, mode: ContinueModes.Extend, ct: ct);
                var current = scanner.FindJob(job.Id, job.WatchPath) ?? job;
                timeline.Append(current.FolderPath, TimelineEventKinds.IntegrationRecoveryQueued,
                    TimelineActors.System,
                    $"Continuation queued from {stage}: {reason}",
                    payloadRef: LatestExtension(current.FolderPath),
                    details: new Dictionary<string, string>
                    {
                        ["automatic"] = "false",
                        ["source"] = "operator-failure-panel",
                        ["failureStage"] = stage,
                        ["failureCode"] = failed?.FailureCode ?? currentFailure?.Code ?? "delivery-pending",
                        ["integrationBranch"] = status?.IntegrationBranch ?? "develop",
                        ["deliveryRef"] = subject?.ResultRef ?? status?.DeliveryRef ?? string.Empty,
                        ["failureEvidenceRef"] = failed?.EvidenceRef ?? string.Empty,
                    });
                return Results.Accepted(value: new { status = response.Status, stage, taskKey = job.Key });
            }
            catch (TaskOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    internal static PipelineStepExecution? CurrentFailureStep(
        IEnumerable<PipelineStepExecution>? steps,
        TaskIntegrationStatus? status,
        ReviewSubjectRecord? subject)
    {
        if (status?.Status == IntegrationStatuses.Integrated
            || status?.ReachUnavailable == true)
            return null;
        var current = steps?.Where(step => IsCurrentStep(step, subject)).ToList();
        return current?.LastOrDefault(step => step.StepId == status?.Failure?.Stage
            && (step.Status == PipelineStepStatus.Failed || step.Verdict is "concerns" or "block"))
            ?? current?.LastOrDefault(step => step.Status == PipelineStepStatus.Failed
                || step.Verdict is "concerns" or "block");
    }

    private static bool IsCurrentStep(PipelineStepExecution step, ReviewSubjectRecord? subject)
        => subject is null || subject.CompletedAtUtc == default
            || step.CompletedAt is { } completed
                && completed.ToUniversalTime() >= subject.CompletedAtUtc.UtcDateTime;

    private static string ReadEvidenceExcerpt(string folder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return string.Empty;
        try
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(folder, relativePath));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) return string.Empty;
            using var stream = File.OpenRead(path);
            var bytes = new byte[Math.Min(4096, (int)Math.Min(stream.Length, int.MaxValue))];
            var count = stream.Read(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static string LatestExtension(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "prompt-*.md", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(Path.GetFileName)
                .FirstOrDefault() ?? "prompt.md";
        }
        catch (IOException) { return "prompt.md"; }
        catch (UnauthorizedAccessException) { return "prompt.md"; }
    }

    private static string? ReadIntegrationTip(GitService git, string watchPath, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        try
        {
            var root = git.ResolveRepoRootForWatchPath(watchPath);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var readRef = git.RemoteBranchExists(root, branch) ? "origin/" + branch : branch;
            return git.GetRefShaFresh(root, readRef);
        }
        catch (Exception) { return null; }
    }
}
