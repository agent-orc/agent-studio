using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>Creates an orchestrator-authored follow-up from the current durable failure.</summary>
public static class TaskFailureContinuationEndpoints
{
    public static void MapTaskFailureContinuationEndpoints(this RouteGroupBuilder group)
    {
        // AGT-3582: the same guard as the POST, read only, so the task detail
        // offers the action only for a delivery with a failure to continue.
        group.MapGet("/{jobId}/failure/continue", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            TaskListGitProjectionCache gitProjection,
            AgentStudio.Pipeline.BatchIsolationGuard isolation,
            TaskFailureContinuationService continuations) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (isolation.Find(job) is { } isolating)
                return Results.Ok(new { available = false, reason = AgentStudio.Pipeline.BatchIsolationGuardPolicy.Message(isolating), isolationActive = true });
            // The published status the task detail renders, not a live git read.
            var refusal = continuations.Check(job, gitProjection.ReadTask(job).Data?.Integration);
            return Results.Ok(new { available = refusal is null, reason = refusal });
        });

        group.MapPost("/{jobId}/failure/continue", async (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Pipeline.BatchIsolationGuard isolation,
            TaskFailureContinuationService continuations,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (isolation.Find(job) is { } isolating)
                return Results.Json(AgentStudio.Pipeline.BatchIsolationGuardPolicy.Body(isolating),
                    statusCode: StatusCodes.Status409Conflict);

            var result = await continuations.ContinueAsync(job, "operator-failure-panel", automatic: false, ct);
            if (!result.Accepted)
                return result.Status == StatusCodes.Status409Conflict
                    ? Results.Conflict(new { error = result.Error })
                    : Results.Json(new { error = result.Error }, statusCode: result.Status);
            return Results.Accepted(value: new
            {
                status = result.RunStatus,
                stage = result.Stage,
                taskKey = result.TaskKey,
                savedReason = result.SavedReason,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    internal static PipelineStepExecution? CurrentFailureStep(
        IEnumerable<PipelineStepExecution>? steps,
        TaskIntegrationStatus? status,
        ReviewSubjectRecord? subject)
        => TaskFailureContinuationService.CurrentFailureStep(steps, status, subject);
}
