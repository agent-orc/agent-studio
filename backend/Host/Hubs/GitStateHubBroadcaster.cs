using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// Bridges <see cref="AgentStudio.Git.GitStateIndexService.RepositoryIndexed"/>
/// onto the <see cref="TaskHub"/> SignalR fan-out, so a connected board tab
/// converges on a fresh <c>gitStateAt</c> stamp by push instead of waiting for
/// its next poll (AGT-2726). The payload is deliberately minimal - project and
/// timestamp only - the client already has the mechanism to re-pull
/// list/grouped from a push signal (mirrors <c>jobsBulkChanged</c>); this
/// event just tells it a specific project's Git-derived state moved forward
/// instead of nudging the whole board.
/// </summary>
public sealed class GitStateHubBroadcaster
{
    private readonly IHubContext<TaskHub> _hub;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly ILogger<GitStateHubBroadcaster> _logger;

    public GitStateHubBroadcaster(
        IHubContext<TaskHub> hub,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Git.GitStateIndexService indexer,
        ILogger<GitStateHubBroadcaster> logger)
    {
        _hub = hub;
        _projects = projects;
        _logger = logger;
        indexer.RepositoryIndexed += OnRepositoryIndexed;
    }

    private void OnRepositoryIndexed(string projectName, DateTimeOffset gitStateAt)
    {
        try
        {
            _ = _hub.Clients.Group(TaskHub.ProjectGroup(projectName, _projects))
                .SendCoreAsync("gitStateChanged", [new { projectName, gitStateAt }]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobsHub gitStateChanged broadcast failed for {Project}", projectName);
        }
    }
}
