namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-2824 - periodic driver for <see cref="GateEnvironmentRetryService"/>.
///
/// <para>The tick is short relative to the first backoff step so a healed gate
/// host is picked up promptly; the bounded ladder itself lives in
/// <see cref="GateEnvironmentRetryPolicy"/>, not here.</para>
/// </summary>
public sealed class GateEnvironmentRetryHostedService : BackgroundService
{
    private readonly GateEnvironmentRetryService _retries;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GateEnvironmentRetryHostedService> _logger;

    public GateEnvironmentRetryHostedService(
        GateEnvironmentRetryService retries,
        IConfiguration configuration,
        ILogger<GateEnvironmentRetryHostedService> logger)
    {
        _retries = retries;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!(_configuration.GetValue<bool?>("Integration:GateEnvironmentRetryEnabled") ?? true))
        {
            _logger.LogInformation("gate-environment-retry disabled by configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue<int?>("Integration:GateEnvironmentRetryIntervalSeconds") ?? 120,
            30,
            60 * 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _retries.RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate environment retry sweep failed");
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
