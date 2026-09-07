using AgentStudio.Publishing;
using AgentStudio.Tasks;

namespace AgentStudio.Git;

/// <summary>
/// The git work one index run does for one repository. Everything a board
/// answer needs from git - the integration and release ancestor sets, the
/// publish derivation, the branch/worktree/history inventory - is computed
/// here, into the same ref-fingerprinted caches the request paths read.
///
/// <para>
/// Each of these projections was already cached and already single-flighted.
/// What changed in AGT-2726 is <em>who</em> fills them: previously whichever
/// request missed the cache, on a thread-pool thread, under a process-wide
/// gate; now this refresher, on the indexer's own bounded executor, before any
/// request asks.
/// </para>
/// </summary>
public sealed class GitRepositoryStateRefresher(
    GitService git,
    BoardMergeStatusService mergeStatus,
    TaskIntegrationStatusService integrationStatus,
    PublishTargetService publish,
    ProjectSettingsService settings,
    ILogger<GitRepositoryStateRefresher> logger) : IGitRepositoryStateRefresher
{
    public void Refresh(string repositoryRoot, string projectName, CancellationToken cancellationToken)
    {
        var branch = ResolveIntegrationBranch(projectName);

        // Ordered cheapest-first so a cancellation during shutdown still leaves
        // the board's most-read projection warm.
        mergeStatus.WarmRepository(repositoryRoot, branch);
        cancellationToken.ThrowIfCancellationRequested();

        integrationStatus.WarmRepository(repositoryRoot, branch);
        cancellationToken.ThrowIfCancellationRequested();

        WarmPublish(projectName);
        cancellationToken.ThrowIfCancellationRequested();

        WarmInventory(projectName, cancellationToken);
    }

    private string ResolveIntegrationBranch(string projectName)
    {
        try
        {
            return settings.Get(projectName).IntegrationBranch;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitRepositoryStateRefresher: integration branch lookup is best-effort");
            return "develop";
        }
    }

    private void WarmPublish(string projectName)
    {
        try
        {
            publish.GetComputation(projectName);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git-index publish warm failed for {Project}", projectName);
        }
    }

    private void WarmInventory(string projectName, CancellationToken cancellationToken)
    {
        try
        {
            // Coalesces with an inventory refresh already in flight rather than
            // starting a second one; the index run then waits for that result so
            // its spawn count and duration describe the real work.
            git.RefreshProjectInventoryAsync(projectName, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git-index inventory warm failed for {Project}", projectName);
        }
    }
}

/// <summary>
/// Seam for the index's per-repository work. Tests substitute a refresher that
/// records calls (or blocks) so coalescing, bounded concurrency, and
/// stale-while-revalidate are provable without a real repository.
/// </summary>
public interface IGitRepositoryStateRefresher
{
    void Refresh(string repositoryRoot, string projectName, CancellationToken cancellationToken);
}
