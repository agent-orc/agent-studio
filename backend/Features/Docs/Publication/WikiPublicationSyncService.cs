namespace AgentStudio.Docs;

/// <summary>
/// The scheduled trigger for hosted wiki publication. It performs no decision
/// of its own: every tick calls the same
/// <see cref="WikiPublicationService.Synchronize"/> the operator endpoint
/// calls, so a manual trigger and the timer cannot diverge.
///
/// <para>The interval is the deployment freshness budget. With the default of
/// 120 seconds, an accepted documentation change is fetched, validated, and
/// promoted well inside the documented five-minute SLO; the configuration
/// reader clamps the interval so a misconfiguration cannot silently widen
/// it past the SLO.</para>
///
/// <para>Like the wiki cache warmup this must not run on the startup path: the
/// first sync fetches from the remote and can materialize a multi-hundred-file
/// docs tree, and the HTTP listener (and with it the health probe) must not
/// wait for that.</para>
/// </summary>
public sealed class WikiPublicationSyncService : BackgroundService
{
    private readonly WikiPublicationService _publication;
    private readonly ILogger<WikiPublicationSyncService> _logger;

    public WikiPublicationSyncService(
        WikiPublicationService publication,
        ILogger<WikiPublicationSyncService> logger)
    {
        _publication = publication;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        if (!_publication.Options.Enabled)
        {
            _logger.LogInformation("wiki-publication-disabled reason=configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(_publication.Options.IntervalSeconds);
        _logger.LogInformation(
            "wiki-publication-started intervalSeconds={IntervalSeconds} remote={Remote}",
            _publication.Options.IntervalSeconds,
            _publication.Options.Remote);

        while (!stoppingToken.IsCancellationRequested)
        {
            SyncAllProjects("scheduled", stoppingToken);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal void SyncAllProjects(string trigger, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projects;
        try
        {
            projects = _publication.PublicationProjectNames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "wiki-publication could not enumerate projects");
            return;
        }

        foreach (var projectName in projects)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                // Never forced: a forced sync releases a rollback hold, and only an
                // operator may do that.
                _publication.Synchronize(projectName, trigger, force: false, cancellationToken);
            }
            catch (Exception ex)
            {
                // One unreachable project must not stop the others, and it must
                // not take the published revision of any project offline.
                _logger.LogError(ex, "wiki-publication-attempt-crashed project={Project}", projectName);
            }
        }
    }
}
