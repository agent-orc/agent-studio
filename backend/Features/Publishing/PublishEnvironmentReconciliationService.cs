namespace AgentStudio.Publishing;

/// <summary>Reconciles hosting reservations even when nobody opens the publish or environment panel.</summary>
public sealed class PublishEnvironmentReconciliationService(
    IPublishEnvironmentReconciler actions, TimeProvider clock,
    ILogger<PublishEnvironmentReconciliationService> logger,
    AgentStudio.Projects.ProjectEnvironmentService? environments = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        Reconcile();
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Reconcile();
    }

    private void Reconcile()
    {
        try { actions.ReconcileRunningWebsites(); }
        catch (Exception ex) { logger.LogWarning(ex, "website-environment-reconciliation-failed"); }
        try { environments?.ReconcileHostingSlots(); }
        catch (Exception ex) { logger.LogWarning(ex, "declared-environment-slot-reconciliation-failed"); }
    }
}
