using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed record StudioAttachDecision(
    string Code,
    int StatusCode,
    int? ApiProtocol,
    int? HubProtocol,
    string? Reason)
{
    public bool Attached => Code == ProtocolAttachCodes.Attached;
}

/// <summary>
/// Pure Task Server side of the Studio connector attach handshake: given the
/// ranges the connector offers, the ranges this server speaks, and the
/// authenticated principal kind, decide the negotiated <c>/api/v1</c> and
/// Studio hub versions or an operator-readable refusal.
/// </summary>
public static class StudioAttachPolicy
{
    public static StudioAttachDecision Decide(
        ProtocolAttachRequest request,
        string? principalKind,
        bool authenticationRequired,
        ProtocolRangeDto server,
        HubProtocolRangeDto studioHub)
    {
        if (!string.Equals(request.ClientKind, TaskServerProtocol.StudioClientKind, StringComparison.Ordinal))
        {
            return Refuse(
                ProtocolAttachCodes.ClientKindUnsupported,
                StatusCodes.Status400BadRequest,
                $"Only the '{TaskServerProtocol.StudioClientKind}' client kind attaches through this handshake; received '{request.ClientKind}'.");
        }

        if (authenticationRequired
            && !string.Equals(principalKind, TaskServerPrincipalKinds.Studio, StringComparison.Ordinal))
        {
            return Refuse(
                ProtocolAttachCodes.PrincipalKindMismatch,
                StatusCodes.Status403Forbidden,
                $"The connector presented a '{principalKind ?? "unknown"}' credential. Store the Studio principal credential for this connector, not a Runner, Engine, or management credential.");
        }

        if (request.MinimumApiProtocol > request.MaximumApiProtocol
            || request.MinimumHubProtocol > request.MaximumHubProtocol)
        {
            return Refuse(
                ProtocolAttachCodes.InvalidRange,
                StatusCodes.Status400BadRequest,
                "The connector sent an empty protocol range (minimum above maximum).");
        }

        var api = ProtocolNegotiation.HighestCommon(
            request.MinimumApiProtocol,
            request.MaximumApiProtocol,
            server.MinimumSupported,
            server.MaximumSupported);
        if (api is null)
        {
            return Refuse(
                ProtocolAttachCodes.ApiProtocolIncompatible,
                StatusCodes.Status426UpgradeRequired,
                $"The connector speaks /api/v1 protocol {request.MinimumApiProtocol}-{request.MaximumApiProtocol}, " +
                $"but Task Server {server.ServerVersion} speaks {server.MinimumSupported}-{server.MaximumSupported}. " +
                "Install a connector and Task Server from releases that share a protocol version.");
        }

        var hub = ProtocolNegotiation.HighestCommon(
            request.MinimumHubProtocol,
            request.MaximumHubProtocol,
            studioHub.MinimumSupported,
            studioHub.MaximumSupported);
        if (hub is null)
        {
            return Refuse(
                ProtocolAttachCodes.HubProtocolIncompatible,
                StatusCodes.Status426UpgradeRequired,
                $"The connector speaks Studio hub protocol {request.MinimumHubProtocol}-{request.MaximumHubProtocol}, " +
                $"but Task Server {server.ServerVersion} serves {studioHub.Path} with {studioHub.MinimumSupported}-{studioHub.MaximumSupported}. " +
                "Install a connector and Task Server from releases that share a hub protocol version.");
        }

        return new StudioAttachDecision(ProtocolAttachCodes.Attached, StatusCodes.Status200OK, api, hub, null);
    }

    private static StudioAttachDecision Refuse(string code, int statusCode, string reason)
        => new(code, statusCode, null, null, reason);
}
