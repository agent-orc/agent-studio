namespace AgentStudio.TaskServer.Contracts;

public static class TaskServerProtocol
{
    public const int Current = 2;
    public const int MinimumSupported = 1;
    public const int MaximumSupported = 2;
    public const string HeaderName = "X-Task-Protocol-Version";
    public const string ClientVersionHeaderName = "X-Task-Client-Version";
    public const string EngineClientKind = "engine";
    public const string StudioClientKind = "studio";

    public static bool Supports(int version)
        => version >= MinimumSupported && version <= MaximumSupported;
}

/// <summary>
/// Version range of the replayable Studio event hub. The hub path carries the
/// major version, so a hub protocol change ships as a new path and a new range
/// here; the connector refuses to attach when the two sides share no version.
/// </summary>
public static class TaskServerHubProtocol
{
    public const string StudioHubName = "studio";
    public const string StudioHubPath = "/hubs/v1/studio";
    public const int Current = 1;
    public const int MinimumSupported = 1;
    public const int MaximumSupported = 1;

    public static HubProtocolRangeDto StudioRange()
        => new(StudioHubName, StudioHubPath, Current, MinimumSupported, MaximumSupported);
}

/// <summary>Pure version negotiation shared by both sides of the attach handshake.</summary>
public static class ProtocolNegotiation
{
    /// <summary>The highest version both inclusive ranges contain, or null when they do not overlap.</summary>
    public static int? HighestCommon(int clientMinimum, int clientMaximum, int serverMinimum, int serverMaximum)
    {
        if (clientMinimum > clientMaximum || serverMinimum > serverMaximum) return null;
        var highest = Math.Min(clientMaximum, serverMaximum);
        return highest >= Math.Max(clientMinimum, serverMinimum) ? highest : null;
    }
}

public sealed record HubProtocolRangeDto(
    string Hub,
    string Path,
    int Current,
    int MinimumSupported,
    int MaximumSupported);

public sealed record ProtocolRangeDto(
    int Current,
    int MinimumSupported,
    int MaximumSupported,
    string ServerVersion,
    string ServerId,
    IReadOnlyList<string> ClientKinds,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<HubProtocolRangeDto>? Hubs = null);

public sealed record ProtocolCompatibilityRequest(
    string ClientKind,
    string ClientVersion,
    int ProtocolVersion);

public sealed record ProtocolCompatibilityResponse(
    bool Supported,
    ProtocolRangeDto Server,
    string? Reason = null);

/// <summary>
/// Authenticated attach handshake of the loopback Studio connector. The
/// connector sends the inclusive <c>/api/v1</c> and Studio hub ranges it can
/// speak; the Task Server answers with the highest common version of each or
/// refuses with an operator-readable reason.
/// </summary>
public sealed record ProtocolAttachRequest(
    string ClientKind,
    string ClientVersion,
    int MinimumApiProtocol,
    int MaximumApiProtocol,
    int MinimumHubProtocol,
    int MaximumHubProtocol);

public sealed record ProtocolAttachResponse(
    bool Attached,
    string Code,
    ProtocolRangeDto Server,
    int? ApiProtocol,
    int? HubProtocol,
    string? HubPath,
    string? PrincipalId,
    string? Reason);

public static class ProtocolAttachCodes
{
    public const string Attached = "attached";
    public const string ClientKindUnsupported = "client-kind-unsupported";
    public const string PrincipalKindMismatch = "principal-kind-mismatch";
    public const string InvalidRange = "invalid-range";
    public const string ApiProtocolIncompatible = "api-protocol-incompatible";
    public const string HubProtocolIncompatible = "hub-protocol-incompatible";
}

public sealed record ApiError(string Code, string Message, object? Detail = null);
