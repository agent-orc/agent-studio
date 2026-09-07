namespace AgentStudio.Git;

/// <summary>Owns every subprocess used to refresh Project Hub Git inventory.</summary>
public sealed class GitInventoryRefreshHostedService(
    GitService git,
    ProjectGitGraphService graph,
    ILogger<GitInventoryRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            git.InventoryRefreshed += graph.RefreshInventory;
            await git.RunInventoryRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Git inventory background refresher stopped");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Git inventory background refresher stopped unexpectedly");
        }
        finally
        {
            git.InventoryRefreshed -= graph.RefreshInventory;
        }
    }
}
