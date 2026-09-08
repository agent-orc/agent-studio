

namespace AgentStudio.Runner;

/// <summary>
/// Periodic safety net for completed-job auto-push. The synchronous trigger
/// lives on the move to <c>6-completed</c>; this sweep covers missed process
/// windows and pre-existing completed jobs after a backend restart.
/// </summary>
public sealed class CompletedPushBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskTransitionService _transitions;
    private readonly IConfiguration _config;
    private readonly ILogger<CompletedPushBackstopHostedService> _logger;

    public CompletedPushBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskTransitionService transitions,
        IConfiguration config,
        ILogger<CompletedPushBackstopHostedService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _transitions = transitions;
        _config = config;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var completed = _scanner.ScanAllAutomationJobs()
            .Where(j => j.State == TaskStates.Completed)
            .OrderBy(j => j.LastActivity)
            .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pushed = 0;
        var skippedSuperseded = 0;
        var rejected = 0;
        var skippedIntegrated = 0;

        foreach (var job in completed)
        {
            ct.ThrowIfCancellationRequested();
            var strategy = AutoPushStrategies.Normalize(_settings.Get(job.ProjectName).AutoPushStrategy);
            if (strategy == AutoPushStrategies.Never) continue;
            var result = await _transitions.PushCompletedJobCommitsDetailedAsync(job, strategy, ct);
            pushed += result.Pushed;
            skippedSuperseded += result.SkippedSuperseded;
            rejected += result.Rejected;
            if (result.CardSkippedIntegrated) skippedIntegrated++;
        }

        // One summary line per cycle instead of a warning per rejected commit
        // (AGT-2761): a card whose delivery generation left dozens of
        // superseded intermediate commits behind used to log one non-fast-forward
        // warning and emit one bus event per commit, every 15 minutes, forever.
        _logger.LogInformation(
            "Completed auto-push backstop cycle: {Scanned} scanned, {Pushed} pushed, {SkippedIntegrated} card(s) already integrated, {SkippedSuperseded} commit(s) superseded, {Rejected} commit(s) rejected",
            completed.Count, pushed, skippedIntegrated, skippedSuperseded, rejected);
        return pushed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Completed auto-push backstop sweep failed");
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

    private TimeSpan ResolveInterval()
    {
        var minutes = _config.GetValue<int?>("AutoPush:BackstopIntervalMinutes") ?? 15;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }
}
