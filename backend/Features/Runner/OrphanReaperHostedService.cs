
namespace AgentStudio.Runner;

/// <summary>
/// Periodic safety net for orphaned CLI process trees. The startup reaper
/// (<see cref="GenericCliExecutionService.ReattachOnStartup"/>) only fires once at
/// boot, so a backend left up for days accumulates orphan codex / node
/// processes from finished or crashed runs whose tree-kill fell back to a
/// single-process kill or whose monitor died before it could clean up. Those
/// survivors keep job-folder handles open and wedge the next lane move with
/// "the process cannot access the file because it is being used by another
/// process". This sweep calls each CLI backend's
/// <see cref="ICliExecutionService.ReapStaleOrphans"/> on a timer; that method
/// only reaps process trees the backend no longer tracks as a live run, so an
/// in-flight run is never killed.
/// </summary>
public sealed class OrphanReaperHostedService : BackgroundService
{
    private readonly CliRouter _router;
    private readonly IConfiguration _config;
    private readonly ILogger<OrphanReaperHostedService> _logger;
    private readonly WorktreeOrphanDirectorySweeper _worktreeDirectories;

    public OrphanReaperHostedService(
        CliRouter router,
        IConfiguration config,
        ILogger<OrphanReaperHostedService> logger)
    {
        _router = router;
        _config = config;
        _logger = logger;
        _worktreeDirectories = new WorktreeOrphanDirectorySweeper(logger);
    }

    public void RunOnce()
    {
        try
        {
            _router.ReapStaleOrphansAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLI orphan-reaper sweep failed");
        }

        try
        {
            _worktreeDirectories.Sweep(
                Path.Combine(Path.GetTempPath(), "ass-worktrees"),
                DateTime.UtcNow,
                TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worktree orphan-directory sweep failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled())
        {
            _logger.LogInformation("Orphan-reaper sweep disabled via Runner:OrphanReaperEnabled=false");
            return;
        }

        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

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

            // Synchronous, fast (file read + a handful of GetProcessById /
            // taskkill calls). Run off the timer thread so a slow taskkill
            // does not skew the cadence.
            await Task.Run(RunOnce, CancellationToken.None);
        }
    }

    private bool Enabled() => _config.GetValue<bool?>("Runner:OrphanReaperEnabled") ?? true;

    private TimeSpan ResolveInterval()
    {
        var minutes = _config.GetValue<int?>("Runner:OrphanReaperIntervalMinutes") ?? 5;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }
}
