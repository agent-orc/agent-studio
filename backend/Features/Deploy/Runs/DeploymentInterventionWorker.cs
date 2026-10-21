namespace AgentStudio.Deploy;

/// <summary>Expires unattended guided failures even when nobody opens the dashboard.</summary>
public sealed class DeploymentInterventionWorker(DeployService deploy, TimeProvider clock,
    ILogger<DeploymentInterventionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    deploy.Matrix();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Guided deployment intervention sweep failed; the next tick retries");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Guided deployment intervention sweep stopped");
        }
    }
}
