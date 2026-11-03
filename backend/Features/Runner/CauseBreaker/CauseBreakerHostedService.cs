using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>
/// Closes open cause breakers once their cause card is integrated
/// (AGT-W57 §5 E1 point 6). A green probe closes a breaker directly on the
/// review report path; this loop covers the cause-card side. AGT-3060: the
/// close decision is the AI Operator decider <c>cause-breaker</c>, selected by
/// the <c>OperatorDecision</c> switch; this service applies its engine receipt.
/// </summary>
public sealed class CauseBreakerHostedService : BackgroundService, IAiOperatorEffect
{
    public const int DefaultIntervalSeconds = 60;

    private readonly TaskScannerService _scanner;
    private readonly CauseBreakerService _breakers;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CauseBreakerHostedService> _logger;
    private readonly AiOperatorDecisionGateway _decisions;

    public CauseBreakerHostedService(
        TaskScannerService scanner,
        CauseBreakerService breakers,
        IConfiguration configuration,
        ILogger<CauseBreakerHostedService> logger,
        AiOperatorDecisionGateway? decisions = null)
    {
        _scanner = scanner;
        _breakers = breakers;
        _configuration = configuration;
        _logger = logger;
        _decisions = decisions ?? AiOperatorDecisionGateway.ForTestHost(configuration);
    }

    public string Decider => AiOperatorDeciders.CauseBreaker;

    public Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _breakers.ApplyCloseReceipt(application);
        return Task.CompletedTask;
    }

    /// <summary>One sweep pass; the decision goes through the AI Operator switch.</summary>
    public int SweepOnce() => _breakers.Sweep(_decisions);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A sweep reads the task index synchronously under the breaker lock;
        // before the first publication that read holds a pool worker, and the
        // lock, until the scan ends.
        await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
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
                SweepOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cause-breaker-sweep-failed");
            }
        }
    }
}
