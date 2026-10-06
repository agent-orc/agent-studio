namespace AgentStudio.Runner;

/// <summary>
/// Drives <see cref="IOperatorSweepRunner"/> on one fixed cadence (AGT-3011),
/// replacing the <c>while true; sleep 600</c> loop an operator session had to
/// keep alive. A failed tick is logged and the next tick runs on schedule.
/// </summary>
public sealed class OperatorSweepHostedService : BackgroundService
{
    private readonly IOperatorSweepRunner _sweeps;
    private readonly ILogger<OperatorSweepHostedService> _logger;

    public OperatorSweepHostedService(IOperatorSweepRunner sweeps, ILogger<OperatorSweepHostedService> logger)
    {
        _sweeps = sweeps;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the first tick off the host startup path; a sweep may queue runs.
        await Task.Yield();
        var options = _sweeps.Options;
        if (!options.Enabled)
        {
            _logger.LogInformation("operator-sweeps are disabled by configuration");
            return;
        }

        try
        {
            if (options.InitialDelay > TimeSpan.Zero)
                await Task.Delay(options.InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await _sweeps.RunOnceAsync(stoppingToken);
                if (report.Error is not null)
                    _logger.LogWarning("operator-sweep tick ended with error={Error}", report.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "operator-sweep tick failed");
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
