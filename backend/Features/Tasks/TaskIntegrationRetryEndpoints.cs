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

            // Eligibility - lane, delivery, failure code, passed review, and an
            // acceptance integration already in flight - belongs to
            // GateEnvironmentRetryPolicy alone, so the button and the automatic
            // ladder can never disagree about the same card. The refusal carries
            // the policy's reason slug next to its operator-facing sentence.
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
                    code = result.Code,
                }),
                GateEnvironmentRetryStatus.NoPassedReview => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "no-passed-review",
                    code = result.Code,
                    deliverySha = result.DeliverySha,
                }),
                _ => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "not-applicable",
                    code = result.Code,
                    state = job.State,
                }),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
