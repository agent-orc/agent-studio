using AgentStudio.Pipeline;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator action for a delivery whose integration failed on the gate
/// environment (AGT-2824) or on a server checkout that now exists (AGT-3810).
/// It replays the integration of the unchanged delivery SHA and reuses the
/// review that already passed for it, so recovering from a broken gate host
/// or a missing checkout no longer costs a coding run or a review round.
/// </summary>
public static class TaskIntegrationRetryEndpoints
{
    public static void MapTaskIntegrationRetryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration/retry", async (
            string jobId,
            string? project,
            string? watchPath,
            HttpContext context,
            TaskScannerService scanner,
            ProjectRegistry projects,
            GateEnvironmentRetryService retries,
            TaskTransitionService transitions,
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
            var integrated = result.Status == GateEnvironmentRetryStatus.Replayed
                && result.Outcome?.IsSuccessfulIntegration() == true;
            // AGT-3810: an Escalated card was parked only because its passed
            // delivery could not integrate. Once the replay landed it, the
            // card returns to Human Review for the owner's acceptance instead
            // of waiting on a decision the integration already answered.
            string? movedTo = null;
            if (integrated && job.State == TaskStates.Escalated)
            {
                var moved = await transitions.MoveAsync(job.Id, TaskStates.HumanReview, job.WatchPath, ct,
                    cause: TaskCrudEndpoints.OperatorActor(context),
                    reason: "Retry integration merged the reviewed delivery; the card returns to Human Review.",
                    expectedSourceState: TaskStates.Escalated,
                    transitionCause: LaneChangeCauses.OperatorMove,
                    transitionDetail: "integration-retry");
                if (moved.Status == MoveJobStatus.Success) movedTo = TaskStates.HumanReview;
            }
            return result.Status switch
            {
                GateEnvironmentRetryStatus.Replayed => Results.Ok(new
                {
                    status = integrated ? "integrated" : "failed",
                    movedTo,
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
                    comparedDeliverySha = result.ComparedDeliverySha,
                    latestAttempt = result.LatestAttempt,
                }),
                GateEnvironmentRetryStatus.NoPassedReview => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "no-passed-review",
                    code = result.Code,
                    deliverySha = result.DeliverySha,
                    comparedDeliverySha = result.ComparedDeliverySha,
                    latestAttempt = result.LatestAttempt,
                }),
                _ => Results.Conflict(new
                {
                    error = result.Reason,
                    status = "not-applicable",
                    code = result.Code,
                    state = job.State,
                    comparedDeliverySha = result.ComparedDeliverySha,
                    latestAttempt = result.LatestAttempt,
                }),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
