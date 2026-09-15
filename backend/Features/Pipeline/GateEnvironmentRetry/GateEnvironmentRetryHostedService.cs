namespace AgentStudio.Pipeline;

/// <summary>
/// Drives the bounded gate-environment ladder (AGT-2824) on a fixed cadence.
/// The cadence is deliberately shorter than the shortest rung: the rungs are
/// due instants derived from durable receipts, so a short sweep only decides
/// them punctually, it never shortens or multiplies them.
/// </summary>
public sealed class GateEnvironmentRetryHostedService : BackgroundService
{
    private readonly GateEnvironmentRetryService _retries;
    private readonly ILogger<GateEnvironmentRetryHostedService> _logger;

    public GateEnvironmentRetryHostedService(
        GateEnvironmentRetryService retries,
        ILogger<GateEnvironmentRetryHostedService> logger)
    {
        _retries = retries;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the first sweep off the host startup path; a replay spawns Git
        // and a build gate.
        await Task.Yield();
        var options = _retries.Options;
        if (!options.Enabled)
        {
            _logger.LogInformation("gate-environment-retry is disabled by configuration");
            return;
        }

        using var timer = new PeriodicTimer(options.SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _retries.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "gate-environment-retry sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
