using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-2824: the operator's "Retry integration". Replays the integration of an
/// already reviewed delivery that failed on a gate environment failure, without
/// spending a review slot on a verdict that already exists.
/// </summary>
public static class IntegrationRetryEndpoints
{
    public static void MapIntegrationRetryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration/retry", async (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            GateEnvironmentIntegrationRetryService retries,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });

            var result = await retries.RetryOnOperatorRequestAsync(job, DateTimeOffset.UtcNow, ct);
            if (!result.Retried)
            {
                return Results.Conflict(new
                {
                    error = result.Decision.Reason,
                    action = result.Decision.Action.ToString(),
                });
            }

            return Results.Accepted(value: new
            {
                status = result.Integrated ? "integrated" : "failed",
                outcome = result.Outcome?.ToString(),
                attempt = result.Attempt,
                maxAutomaticAttempts = GateEnvironmentRetryPolicy.MaxAutomaticAttempts,
                reviewReused = true,
                detail = result.Error,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
