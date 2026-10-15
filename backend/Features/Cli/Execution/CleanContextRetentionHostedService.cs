using AgentStudio.CliHosting;
using AgentStudio.Runner;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded retention sweep for task-stable clean-context homes. The same store
/// also sweeps opportunistically on Agent Host acquisition; this hosted tick
/// covers a quiet local backend that receives no new runs after homes expire.
/// </summary>
public sealed class CleanContextRetentionHostedService : BackgroundService
{
    public const int DefaultSweepIntervalHours = 6;

    private readonly IConfiguration _configuration;
    private readonly ILogger<CleanContextRetentionHostedService> _logger;
    private bool _decisionPending;
    private string _resumeToken = string.Empty;
    private int _failedSweeps;

    internal bool DecisionPending => _decisionPending;

    public CleanContextRetentionHostedService(
        IConfiguration configuration,
        ILogger<CleanContextRetentionHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public CleanContextCleanupResult RunOnce(DateTimeOffset? nowUtc = null)
    {
        var root = TaskCleanContextStore.ResolveRoot(
            GenericCliExecutionService.ResolveUserHome(),
            ResolveRootOverride(_configuration));
        var retentionDays = Math.Clamp(
            _configuration.GetValue<int?>("CleanContext:RetentionDays")
                ?? TaskCleanContextStore.DefaultRetentionDays,
            1,
            90);
        var result = TaskCleanContextStore.Cleanup(
            root,
            nowUtc,
            TimeSpan.FromDays(retentionDays));
        if (result.Deleted > 0 || result.FailedPaths.Count > 0)
        {
            _logger.LogInformation(
                "Clean-context retention sweep scanned {Scanned}, deleted {Deleted}, failed {Failed}",
                result.Scanned,
                result.Deleted,
                result.FailedPaths.Count);
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SweepSafely();
        using var timer = new PeriodicTimer(ResolveInterval());
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

            SweepSafely();
        }
    }

    internal static string? ResolveRootOverride(IConfiguration configuration)
        => configuration["CleanContext:Root"]
           ?? configuration[TaskCleanContextStore.RootOverrideEnvironmentVariable];

    private TimeSpan ResolveInterval()
    {
        var hours = Math.Clamp(
            _configuration.GetValue<int?>("CleanContext:SweepIntervalHours")
                ?? DefaultSweepIntervalHours,
            1,
            24 * 7);
        return TimeSpan.FromHours(hours);
    }

    internal void SweepSafely()
    {
        var engineMode = MonolithTransitionSwitch.For(_configuration,
            ReviewInputDecisionGateway.Transition) == OrchestrationExecutionMode.Engine;
        var resumeToken = _configuration["CleanContext:ResumeSignal"] ?? string.Empty;
        if (!engineMode || !string.Equals(resumeToken, _resumeToken, StringComparison.Ordinal))
        {
            _decisionPending = false;
            _failedSweeps = 0;
            _resumeToken = resumeToken;
        }
        if (_decisionPending) return;
        try
        {
            RunOnce();
            _failedSweeps = 0;
        }
        catch (Exception ex)
        {
            if (engineMode && MonolithTransitionAdapter.LoopSignal(
                "clean-context.retention-sweep", ++_failedSweeps, 1,
                "per-host-sweep", resumeToken.Length == 0 ? "initial" : resumeToken,
                ["defer next sweep", "ask"]) is { } signal)
            {
                _decisionPending = true;
                _logger.LogWarning(ex,
                    "Clean-context retention raised {Signal} and is held until operator resume token changes",
                    signal.Name);
                return;
            }
            _logger.LogWarning(ex, "Clean-context retention sweep failed; the next bounded tick will retry");
        }
    }
}
