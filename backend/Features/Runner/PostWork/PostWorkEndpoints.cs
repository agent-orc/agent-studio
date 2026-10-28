using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public sealed record PostWorkCohortStartRequest(string CohortId, IReadOnlyList<string> TaskIds);
public sealed record PostWorkCohortRollbackRequest(string Reason);

/// <summary>
/// Operator controls for the DP-S8 cohort migration. The backend adds the
/// durable lane facts the authority reconciles against, and applies a
/// rollback by re-enqueueing exactly the cards the authority returns.
/// </summary>
public static class PostWorkEndpoints
{
    public static void MapPostWorkEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/post-work/shadow", async (
            string projectName, PostWorkSubjectCoordinator coordinator, CancellationToken ct)
            => coordinator.Enabled
                ? Results.Ok(await coordinator.Authority.ShadowAsync(ct))
                : Disabled());

        app.MapPost("/api/projects/{projectName}/post-work/cohorts", async (
            string projectName, PostWorkCohortStartRequest request, PostWorkSubjectCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (coordinator.Mode != PostWorkModes.Canary) return Disabled();
            return Results.Ok(await coordinator.Authority.StartCohortAsync(new StartPostWorkCohortRequest(
                request.CohortId, coordinator.ProjectIdFor(projectName), request.TaskIds), ct));
        });

        app.MapPost("/api/projects/{projectName}/post-work/cohorts/{cohortId}/advance", async (
            string projectName, string cohortId, PostWorkSubjectCoordinator coordinator,
            TaskScannerService scanner, ILogger<PostWorkSubjectCoordinator> logger, CancellationToken ct) =>
        {
            if (coordinator.Mode != PostWorkModes.Canary) return Disabled();
            var facts = coordinator.LaneFacts(ProjectCards(scanner, projectName), logger);
            return Results.Ok(await coordinator.Authority.AdvanceCohortAsync(cohortId,
                new AdvancePostWorkCohortRequest(facts), ct));
        });

        app.MapPost("/api/projects/{projectName}/post-work/cohorts/{cohortId}/rollback", async (
            string projectName, string cohortId, PostWorkCohortRollbackRequest request,
            PostWorkSubjectCoordinator coordinator, TaskScannerService scanner, TaskTransitionService transitions,
            AutoReviewPostProcessingQueue queue, ILogger<PostWorkSubjectCoordinator> logger, CancellationToken ct) =>
        {
            if (!coordinator.Enabled) return Disabled();
            var cards = ProjectCards(scanner, projectName);
            var status = await coordinator.Authority.RollbackCohortAsync(cohortId,
                new RollbackPostWorkCohortRequest(request.Reason, coordinator.LaneFacts(cards, logger)), ct);
            var (requeued, scheduled) = ApplyRollback(coordinator, status.Actions, cards, DateTime.UtcNow,
                card => transitions.RequeueAutoReviewPostProcessing(card, "post-work-rollback"),
                (card, action) =>
                {
                    queue.SetWaitState(card.ProjectName, card.Id, new AutoReviewQueueWaitState(
                        "post-work-deferred", action.Reason, 0, action.RetryAt!.Value));
                    var delay = action.RetryAt.Value - DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay);
                        if (scanner.FindJob(card.Id, card.WatchPath) is { } current)
                            transitions.RequeueAutoReviewPostProcessing(current, "post-work-rollback-deferral");
                    });
                });
            return Results.Ok(new { status, requeued, scheduled });
        });
    }

    /// <summary>
    /// Returns each unfinished card of a rolled-back cohort to the backend
    /// queue once, after the authority reconciled it with the lane. A deferred
    /// card keeps its recorded retry time instead of being re-driven at once.
    /// </summary>
    internal static (IReadOnlyList<string> Requeued, IReadOnlyList<string> Scheduled) ApplyRollback(
        PostWorkSubjectCoordinator coordinator, IReadOnlyList<PostWorkReconcileAction> actions,
        IReadOnlyList<TaskInfo> cards, DateTime now, Func<TaskInfo, bool> requeue,
        Action<TaskInfo, PostWorkReconcileAction> scheduleAtRetry)
    {
        var requeued = new List<string>();
        var scheduled = new List<string>();
        foreach (var action in actions)
        {
            var card = cards.FirstOrDefault(item => PostWorkSubjectCoordinator.CardId(item) == action.TaskId
                && coordinator.ProjectIdFor(item.ProjectName) == action.ProjectId);
            if (card is null) continue;
            if (action.Action == PostWorkReconcileActions.RetainDeferral && action.RetryAt > now)
            {
                scheduleAtRetry(card, action);
                scheduled.Add(action.TaskId);
            }
            else if (action.Action is PostWorkReconcileActions.RequeueBackend or PostWorkReconcileActions.RetainDeferral
                     && requeue(card))
                requeued.Add(action.TaskId);
        }
        return (requeued, scheduled);
    }

    private static List<TaskInfo> ProjectCards(TaskScannerService scanner, string projectName)
        => scanner.ScanAllAutomationJobs()
            .Where(card => string.Equals(card.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static IResult Disabled()
        => Results.Conflict(new { error = "Post-work cohorts require PostWork:Mode=canary and TaskServer:BaseUrl." });
}
