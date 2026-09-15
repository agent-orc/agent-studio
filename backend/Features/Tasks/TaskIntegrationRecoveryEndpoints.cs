using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator action for an accepted delivery in a rebase-recoverable failure
/// state. It queues a focused steer round on the original remote-runner
/// delivery branch instead of asking the operator to reconstruct the branch,
/// target, and recovery prompt by hand.
/// </summary>
public static class TaskIntegrationRecoveryEndpoints
{
    public static void MapTaskIntegrationRecoveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration/rebase", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            ProjectRegistry projects,
            ProjectSettingsService settings,
            TaskIntegrationStatusService integrationStatus,
            PipelineExecutionLog pipeline,
            TaskIntegrationRecoveryService recovery) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (job.State is not (
                    TaskStates.HumanReview
                    or TaskStates.Completed
                    or TaskStates.Archive))
            {
                return Results.Conflict(new
                {
                    error = $"Integration recovery requires a task in {TaskStates.HumanReview}, {TaskStates.Completed}, or {TaskStates.Archive}.",
                });
            }

            var status = integrationStatus.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
            if (status?.Status != IntegrationStatuses.ConflictSkipped
                || status.Failure?.RebaseRecoveryAvailable != true)
            {
                return Results.Conflict(new
                {
                    error = "Rebase recovery is not available for this integration failure.",
                    integrationStatus = status?.Status,
                    failureCode = status?.Failure?.Code,
                });
            }

            var mergeStep = pipeline.Read(job.FolderPath)?.Steps.LastOrDefault(
                step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
            var failure = mergeStep is null
                ? null
                : AcceptedIntegrationFailurePolicy.Classify(
                    mergeStep.Status,
                    mergeStep.Verdict,
                    mergeStep.Reason,
                    mergeStep.VerdictSummary,
                    mergeStep.FailureCode);
            if (failure?.RebaseRecoveryAvailable != true)
            {
                return Results.Conflict(new
                {
                    error = "Rebase recovery is not available for this integration failure.",
                    mergeVerdict = mergeStep?.Verdict,
                    failureCode = failure?.Code,
                });
            }

            var result = recovery.Queue(
                job,
                status,
                failure.Code,
                TaskIntegrationRecoveryService.OperatorSource);
            if (!result.Queued)
            {
                return result.InternalError
                    ? Results.Json(
                        new { error = result.Error },
                        statusCode: StatusCodes.Status500InternalServerError)
                    : Results.Conflict(new { error = result.Error });
            }

            return Results.Accepted(value: new
            {
                status = "queued",
                mode = ContinueModes.Steer,
                targetState = TaskStates.Ready,
                position = result.Position,
                deliveryRef = result.DeliveryRef,
                resultSha = result.ResultSha,
                integrationBranch = result.IntegrationBranch,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        // AGT-2824: the explicit half of the healed-gate retry. Same semantics
        // as the automatic rail - replay the integration for the delivery that
        // already passed review, never start a new review round - minus the
        // remaining backoff, which is the whole point of asking by hand.
        group.MapPost("/{jobId}/integration/retry", async (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            ProjectRegistry projects,
            GateEnvironmentRetryService retries,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (job.State != TaskStates.HumanReview)
            {
                return Results.Conflict(new
                {
                    error = $"Integration retry requires a task in {TaskStates.HumanReview}.",
                    state = job.State,
                });
            }

            var result = await retries.RetryNowAsync(job, ct);
            if (!result.Retried)
            {
                return Results.Conflict(new
                {
                    error = "Integration retry is not available for this task.",
                    reason = result.Reason,
                });
            }

            return Results.Accepted(value: new
            {
                status = result.Integrated ? "integrated" : "retried",
                integrated = result.Integrated,
                attempt = result.Attempt,
                maxAttempts = GateEnvironmentRetryPolicy.MaxRetries,
                outcome = result.Outcome,
                reviewReused = true,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
