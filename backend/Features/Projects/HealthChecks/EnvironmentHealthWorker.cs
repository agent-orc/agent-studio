namespace AgentStudio.Projects;

/// <summary>Ticks <see cref="EnvironmentHealthService.RunDueAsync"/>; each check keeps its own interval.</summary>
public sealed class EnvironmentHealthWorker(EnvironmentHealthService health, IConfiguration configuration,
    TimeProvider clock, ILogger<EnvironmentHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("HealthChecks:Enabled", true))
        {
            logger.LogInformation("environment-health-worker disabled by HealthChecks:Enabled");
            return;
        }
        var tick = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("HealthChecks:TickSeconds", 15), 5, 60));
        using var timer = new PeriodicTimer(tick, clock);
        try
        {
            do
            {
                await RunSafelyAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("environment-health-worker stopped");
        }
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await health.RunDueAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "environment-health tick failed; the next tick retries");
        }
    }
}
