namespace AgentStudio.Deploy;

/// <summary>
/// Records the running version on first start, then keeps the git-derived
/// instance deploy facts fresh: every two minutes, and soon after a report
/// took a release live (AGT-3879).
/// </summary>
public sealed class InstanceDeployWorker(InstanceDeployService instance, ILogger<InstanceDeployWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }
        var recorded = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!recorded) recorded = instance.RecordRunningVersionIfEmpty() || instance.SourceProject is not null;
                instance.Refresh();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Instance deploy facts were not refreshed");
            }
            try { await instance.WaitForRefreshRequest(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
