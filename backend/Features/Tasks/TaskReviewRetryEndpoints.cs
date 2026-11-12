using AgentStudio.Runner;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>Operator recovery of a watchdog park without another coding run.</summary>
public static class TaskReviewRetryEndpoints
{
    public static void MapTaskReviewRetryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/review/retry", async (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, ProjectRegistry projects,
            AutoReviewStrandingWatchdog watchdog, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var card = scanner.FindJob(jobId, watchPath);
            if (card is null) return Results.NotFound(new { error = "Task not found." });
            var result = await watchdog.RetryParkedReviewAsync(card, ct);
            return result.Started ? Results.Ok(new { status = result.Reason })
                : Results.Conflict(new { error = result.Reason });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
