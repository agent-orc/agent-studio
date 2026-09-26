using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace AgentStudio.Operations.Server.Features.Agents;

public static class AgentPolicy
{
    public static string? Registration(OperationsPrincipal principal, AgentCapability capability)
    {
        var denied = OperationsAccessPolicy.Denial(principal, "agent", "agents.connect", capability.AgentId);
        if (denied is not null) return denied;
        if (!OperationPolicy.ValidText(capability.AgentId) || !OperationPolicy.ValidText(capability.BootId)
            || !OperationPolicy.ValidText(capability.HostClass) || capability.Capacity != 1
            || capability.Roots is null || capability.Tools is null || capability.Operations is null
            || capability.Roots.Length > 32 || capability.Tools.Length > 64 || capability.Operations.Count > 64)
            return "invalid-capability";
        if (capability.ProtocolMin > OperationsProtocol.Version || capability.ProtocolMax < OperationsProtocol.Version)
            return "protocol-incompatible";
        if (capability.Roots.Any(root => !principal.Roots.Contains(root, StringComparer.Ordinal))) return "root-not-enrolled";
        if (capability.Operations.Any(operation => OperationCatalogue.Find(operation.Key, operation.Value) is null
            || !principal.Scopes.Contains(operation.Key, StringComparer.Ordinal))) return "capability-not-enrolled";
        return null;
    }
}
