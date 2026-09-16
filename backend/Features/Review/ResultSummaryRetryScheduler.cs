namespace AgentStudio.Review;

/// <summary>
/// AGT-2850. Fires the Result-summary retries recorded by
/// <see cref="RemoteResultFinalizationService.FinalizeForAcknowledgementAsync"/>.
/// The upload endpoint only decides that a summary is owed and acknowledges the
/// delivered result; this driver is what makes the summary real once the host
/// load throttle clears or the provider recovers, so a card never sits with a
/// scaffold Result just because the machine was saturated at delivery time.
/// </summary>
public sealed class ResultSummaryRetryScheduler : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;

    private readonly RemoteResultFinalizationService _finalization;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResultSummaryRetryScheduler> _logger;

    public ResultSummaryRetryScheduler(
        RemoteResultFinalizationService finalization,
        IConfiguration configuration,
        ILogger<ResultSummaryRetryScheduler> logger)
    {
        _finalization = finalization;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Runner:ResultSummaryRetry:Enabled", true))
        {
            _logger.LogInformation(
                "ResultSummaryRetryScheduler: disabled via Runner:ResultSummaryRetry:Enabled=false");
            return;
        }

        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("Runner:ResultSummaryRetry:IntervalSeconds")
            ?? DefaultIntervalSeconds,
            5,
            300);
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
                await _finalization.RunDueRetriesAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "result-summary-retry-scheduler-tick-failed");
            }
        }
    }
}
