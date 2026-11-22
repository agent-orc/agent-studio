using AgentStudio.Management;
using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// Sends every <see cref="StructureDelta"/> of <see cref="StructureSnapshotService"/>
/// to the views subscribed to that scope over the existing <see cref="TaskHub"/>
/// connection (AGT-3954). The payload holds only the nodes and links that
/// changed; a view whose revision does not match the delta's base reads the
/// structure again. Delivery is best effort: the 304-aware read is the fallback.
/// </summary>
public sealed class StructureHubBroadcaster
{
    public const string Method = "structureDelta";

    private readonly IHubContext<TaskHub> _hub;
    private readonly ILogger<StructureHubBroadcaster> _logger;

    public StructureHubBroadcaster(IHubContext<TaskHub> hub, StructureSnapshotService structure,
        ILogger<StructureHubBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
        structure.DeltaPublished += OnDelta;
    }

    private void OnDelta(StructureDelta delta)
    {
        try
        {
            var projectId = delta.Scope.StartsWith("project:", StringComparison.Ordinal) ? delta.Scope[8..] : null;
            _ = _hub.Clients.Group(StructureSnapshotService.GroupName(projectId, delta.Environment))
                .SendCoreAsync(Method, [delta]);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "TaskHub structureDelta broadcast failed for {Scope}", delta.Scope);
        }
    }
}
