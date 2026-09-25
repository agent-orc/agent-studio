using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Settles gate queue and lease deadlines even when no host is polling.</summary>
public sealed class GateDeadlineHostedService(
    TaskServerStore store,
    ILogger<GateDeadlineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!store.AuthorityReady || store.Mode != TaskServerMode.Normal)
                continue;
            try
            {
                await store.ReconcileGateDeadlinesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Gate deadline reconciliation failed");
            }
        }
    }
}
