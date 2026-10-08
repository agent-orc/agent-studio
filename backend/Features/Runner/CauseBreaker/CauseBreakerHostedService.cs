namespace AgentStudio.Runner;

/// <summary>
/// Closes open cause breakers once their cause card is integrated
/// (AGT-W57 §5 E1 point 6). A green probe closes a breaker directly on the
/// review report path; this loop covers the cause-card side.
/// </summary>
public sealed class CauseBreakerHostedService : BackgroundService
{
    public const int DefaultIntervalSeconds = 60;

    private readonly CauseBreakerService _breakers;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CauseBreakerHostedService> _logger;

    public CauseBreakerHostedService(
        CauseBreakerService breakers,
        IConfiguration configuration,
        ILogger<CauseBreakerHostedService> logger)
    {
        _breakers = breakers;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("Runner:CauseBreaker:IntervalSeconds") ?? DefaultIntervalSeconds,
            10, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                _breakers.Sweep();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cause-breaker-sweep-failed");
            }
        }
    }
}
