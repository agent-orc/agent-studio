using AgentStudio.Management;
using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// AGT-3954: pushes structure deltas as <c>structureChanged</c> on the Studio
/// hub, only to connections that joined <see cref="TaskHub.StructureGroup"/>
/// through <see cref="TaskHub.SubscribeToStructure"/>, which checks the same
/// permission as <c>GET /api/v1/operations/structure</c>.
/// </summary>
public sealed class StructureHubBroadcaster(IHubContext<TaskHub> hub, ILogger<StructureHubBroadcaster> logger)
    : IStructureDeltaPublisher
{
    public async Task PublishAsync(StructureDelta delta, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(TaskHub.StructureGroup).SendAsync("structureChanged", delta, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Structure delta broadcast cancelled at revision {Revision}", delta.Revision);
        }
        catch (Exception exception)
        {
            // Best effort: a client that misses a delta reads the snapshot again on its next revision gap.
            logger.LogWarning(exception, "Structure delta broadcast failed at revision {Revision}", delta.Revision);
        }
    }
}
