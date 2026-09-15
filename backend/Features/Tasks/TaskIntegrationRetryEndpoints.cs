using AgentStudio.Pipeline;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator action for a delivery whose integration failed on the gate
/// environment (AGT-2824). It replays the integration of the unchanged delivery
/// SHA and reuses the review that already passed for it, so recovering from a
/// broken gate host no longer costs a full remote review round.
/// </summary>
public static class TaskIntegrationRetryEndpoints
{
    public static void MapTaskIntegrationRetryEndpoints(this RouteGroupBuilder group)
    {
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
            if (!GateEnvironmentRetryPolicy.Lanes.Contains(job.State))
            {
                return Results.Conflict(new
                {
                    error = $"Retrying an integration requires a task in {TaskStates.HumanReview} or {TaskStates.Escalated}.",
                    state = job.State,
                });
            }

            var result = await retries.RetryNowAsync(job, ct);
            return result.Status switch
            {
                GateEnvironmentRetryStatus.Replayed => Results.Ok(new
                {
                    status = result.Outcome?.IsSuccessfulIntegration() == true ? "integrated" : "failed",
                    reason = result.Reason,
                    outcome = result.Outcome?.ToString(),
                    rung = result.Rung,
                    deliverySha = result.DeliverySha,
                    integrationBranch = result.IntegrationBranch,
                    reviewReused = true,
                }),
                GateEnvironmentRetryStatus.AlreadyRunning => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "already-running",
                }),
                GateEnvironmentRetryStatus.NoPassedReview => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "no-passed-review",
                    deliverySha = result.DeliverySha,
                }),
                _ => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "not-applicable",
                }),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
