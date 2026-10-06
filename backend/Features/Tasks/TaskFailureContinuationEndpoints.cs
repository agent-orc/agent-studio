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
            TaskFailureContinuationService continuations,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });

            var result = await continuations.ContinueAsync(job, "operator-failure-panel", automatic: false, ct);
            if (!result.Started)
                return result.Status == StatusCodes.Status409Conflict
                    ? Results.Conflict(new { error = result.Error })
                    : Results.Json(new { error = result.Error }, statusCode: result.Status);
            return Results.Accepted(value: new { status = result.RunStatus, stage = result.Stage, taskKey = result.TaskKey });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    internal static PipelineStepExecution? CurrentFailureStep(
        IEnumerable<PipelineStepExecution>? steps,
        TaskIntegrationStatus? status,
        ReviewSubjectRecord? subject)
        => TaskFailureContinuationService.CurrentFailureStep(steps, status, subject);
}
