using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Connector;

/// <summary>The inclusive <c>/api/v1</c> and Studio hub protocol ranges this connector build speaks.</summary>
public sealed record ConnectorProtocolRange(
    int MinimumApi,
    int MaximumApi,
    int MinimumHub,
    int MaximumHub)
{
    public static ConnectorProtocolRange Supported { get; } = new(
        TaskServerProtocol.MinimumSupported,
        TaskServerProtocol.MaximumSupported,
        TaskServerHubProtocol.MinimumSupported,
        TaskServerHubProtocol.MaximumSupported);
}

public sealed record ConnectorAttachDecision(
    bool Attached,
    string? FailureCode,
    string? FailureReason,
    int? ApiProtocol,
    int? HubProtocol,
    string? ServerVersion);

public static class ConnectorAttachFailureCodes
{
    public const string CredentialRejected = "credential-rejected";
    public const string CredentialNotStudio = "credential-not-studio";
    public const string AttachUnsupported = "attach-unsupported";
    public const string HandshakeFailed = "attach-handshake-failed";
    public const string ProtocolIncompatible = "protocol-incompatible";
    public const string HubProtocolIncompatible = "hub-protocol-incompatible";
    public const string HubPathMismatch = "hub-path-mismatch";
    public const string AttachRefused = "attach-refused";
    public const string UpstreamNotReady = "upstream-not-ready";
    public const string UpstreamUnavailable = "upstream-unavailable";
}

/// <summary>
/// Pure connector side of the attach handshake. The Task Server proposes the
/// negotiated versions; the connector still verifies them against its own
/// ranges and against the hub path its route inventory forwards to, so a
/// server that answers outside the offer is refused rather than trusted.
/// </summary>
public static class ConnectorAttachPolicy
{
    public static ConnectorAttachDecision Evaluate(
        ConnectorProtocolRange range,
        int statusCode,
        ProtocolAttachResponse? response,
        string expectedHubPath)
    {
        // An ApiError body deserializes into a response without Server; it is
        // not a negotiation result.
        if (response?.Server is null) response = null;
        var serverVersion = response?.Server.ServerVersion;

        if (statusCode == StatusCodes.Status401Unauthorized)
        {
            return Refuse(
                ConnectorAttachFailureCodes.CredentialRejected,
                "The Task Server rejected the connector's Studio credential (HTTP 401). Store the current Studio principal " +
                "credential for this connector; it is re-read without a restart.",
                serverVersion);
        }

        if (response is null)
        {
            return statusCode switch
            {
                StatusCodes.Status403Forbidden => Refuse(
                    ConnectorAttachFailureCodes.CredentialNotStudio,
                    "The Task Server accepted the credential but denied the Studio attach (HTTP 403). The stored credential lacks Studio scopes.",
                    serverVersion),
                StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed => Refuse(
                    ConnectorAttachFailureCodes.AttachUnsupported,
                    "The Task Server does not implement the attach handshake (/api/v1/protocol/attach). Upgrade it to the release that matches this connector.",
                    serverVersion),
                _ => Refuse(
                    ConnectorAttachFailureCodes.HandshakeFailed,
                    $"The attach handshake answered HTTP {statusCode} without a negotiation result.",
                    serverVersion),
            };
        }

        if (!response.Attached)
        {
            var code = response.Code switch
            {
                ProtocolAttachCodes.PrincipalKindMismatch => ConnectorAttachFailureCodes.CredentialNotStudio,
                ProtocolAttachCodes.ApiProtocolIncompatible => ConnectorAttachFailureCodes.ProtocolIncompatible,
                ProtocolAttachCodes.HubProtocolIncompatible => ConnectorAttachFailureCodes.HubProtocolIncompatible,
                _ => ConnectorAttachFailureCodes.AttachRefused,
            };
            return Refuse(
                code,
                response.Reason ?? $"The Task Server refused the attach ({response.Code}).",
                serverVersion);
        }

        if (response.ApiProtocol is not { } api || api < range.MinimumApi || api > range.MaximumApi)
        {
            return Refuse(
                ConnectorAttachFailureCodes.ProtocolIncompatible,
                $"Task Server {serverVersion} negotiated /api/v1 protocol {response.ApiProtocol?.ToString() ?? "none"}, " +
                $"outside this connector's range {range.MinimumApi}-{range.MaximumApi}.",
                serverVersion);
        }

        if (response.HubProtocol is not { } hub || hub < range.MinimumHub || hub > range.MaximumHub)
        {
            return Refuse(
                ConnectorAttachFailureCodes.HubProtocolIncompatible,
                $"Task Server {serverVersion} negotiated Studio hub protocol {response.HubProtocol?.ToString() ?? "none"}, " +
                $"outside this connector's range {range.MinimumHub}-{range.MaximumHub}.",
                serverVersion);
        }

        if (!string.Equals(response.HubPath, expectedHubPath, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                ConnectorAttachFailureCodes.HubPathMismatch,
                $"Task Server {serverVersion} serves the Studio hub at {response.HubPath ?? "no path"}, " +
                $"but this connector forwards /hubs/jobs to {expectedHubPath}. Install matching releases.",
                serverVersion);
        }

        return new ConnectorAttachDecision(true, null, null, api, hub, serverVersion);
    }

    private static ConnectorAttachDecision Refuse(string code, string reason, string? serverVersion)
        => new(false, code, reason, null, null, serverVersion);
}
