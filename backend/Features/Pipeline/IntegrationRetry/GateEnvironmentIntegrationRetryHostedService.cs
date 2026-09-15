namespace AgentStudio.Pipeline;

/// <summary>
/// Periodic sweep that drives <see cref="GateEnvironmentIntegrationRetryService"/>
/// over every card parked in Human Review after a gate environment failure
/// (AGT-2824).
///
/// The accepted-integration backstop only covers cards that an operator already
/// accepted. A card whose review passed and whose integrate-on-delivery merge
/// died on the gate host sits before that lane, so nothing retried it and the
/// only operator path was a complete new remote review. This sweep is that
/// missing backstop.
/// </summary>
public sealed class GateEnvironmentIntegrationRetryHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly GateEnvironmentIntegrationRetryService _retries;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GateEnvironmentIntegrationRetryHostedService> _logger;

    public GateEnvironmentIntegrationRetryHostedService(
        TaskScannerService scanner,
        GateEnvironmentIntegrationRetryService retries,
        IConfiguration configuration,
        ILogger<GateEnvironmentIntegrationRetryHostedService> logger)
    {
        _scanner = scanner;
        _retries = retries;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// One sweep. Returns how many integrations this pass actually replayed.
    /// </summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var candidates = _scanner.ScanAllAutomationJobs()
            .Where(job => string.Equals(job.State, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase))
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var retried = 0;
        var parked = 0;
        foreach (var job in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _retries.AdvanceAsync(job, nowUtc, ct).ConfigureAwait(false);
                if (result.Retried) retried++;
                if (result.Decision.Action == GateEnvironmentRetryAction.Park) parked++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "gate-environment-retry sweep item failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        if (retried > 0 || parked > 0)
        {
            _logger.LogInformation(
                "gate-environment-retry sweep retried={Retried} parked={Parked} candidates={Candidates}",
                retried,
                parked,
                candidates.Count);
        }
        return retried;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep disk and Git work off the host startup path.
        await Task.Yield();
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("Integration:GateEnvironmentRetryIntervalMinutes") ?? 5,
            1,
            60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate environment integration retry sweep failed");
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
