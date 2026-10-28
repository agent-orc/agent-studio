using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Reconciles post-work subjects with the durable auto-review lane (DP-S8).
/// On every tick it reports lane facts so the authority closes subjects whose
/// card left the lane or already has a recorded decision, hands an accepted
/// host report to the existing worker for the server-owned decision, and
/// names a host-owned wait on the queue projection. It never re-enqueues
/// backend-owned cards: the startup recovery scan and the deferral schedule
/// keep that job, so the old queue path is unchanged for unconverted cards.
/// </summary>
public sealed class PostWorkSubjectReconcilerService(
    IServiceProvider services,
    ILogger<PostWorkSubjectReconcilerService> logger) : BackgroundService
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<PostWorkSubjectCoordinator>();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                if (coordinator.Enabled && MonolithTransitionSwitch.OwnsAutoReview(configuration))
                    await TickAsync(coordinator,
                        scope.ServiceProvider.GetRequiredService<TaskScannerService>(),
                        scope.ServiceProvider.GetRequiredService<AutoReviewPostProcessingQueue>(),
                        logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "post-work-reconcile-failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static async Task<IReadOnlyList<PostWorkReconcileAction>> TickAsync(
        PostWorkSubjectCoordinator coordinator,
        TaskScannerService scanner,
        AutoReviewPostProcessingQueue queue,
        ILogger logger,
        CancellationToken ct)
    {
        var cards = scanner.ScanAllAutomationJobs();
        var projects = scanner.GetWatchPaths().Select(entry => entry.Name)
            .Where(name => name.Length > 0).ToArray();
        var response = await coordinator.ReconcileAsync(projects, coordinator.LaneFacts(cards, logger), ct);
        foreach (var action in response.Actions)
        {
            var card = cards.FirstOrDefault(item =>
                coordinator.ProjectIdFor(item.ProjectName) == action.ProjectId
                && PostWorkSubjectCoordinator.CardId(item) == action.TaskId);
            if (card is null) continue;
            switch (action.Action)
            {
                case PostWorkReconcileActions.ApplyHostReport when queue.PositionOf(card.ProjectName, card.Id) is null:
                    queue.Enqueue(new AutoReviewPostProcessingRequest(card.ProjectName, card.Id, card.WatchPath,
                        DateTime.UtcNow, "post-work-host-report"));
                    break;
                case PostWorkReconcileActions.AwaitHost:
                    queue.SetWaitState(card.ProjectName, card.Id, new AutoReviewQueueWaitState(
                        "post-work-host-owned", "A host agent owns this card (" + action.Reason + ").", 0,
                        action.RetryAt ?? DateTime.UtcNow));
                    break;
            }
        }
        logger.LogInformation("post-work-reconciled actions={Actions} mode={Mode}",
            response.Actions.Count, coordinator.Mode);
        return response.Actions;
    }
}
