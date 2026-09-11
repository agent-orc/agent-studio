namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P1 "hosts" bundle: remote-host ("client")
/// lifecycle and management-command routes. A Studio "client" is the same
/// durable identity as a Task Server execution host/runner (<c>runners.host_id</c>);
/// these contracts add Studio-facing shape (defaults, retirement, preflight
/// cache generation, provider-auth handshake history) on top of the existing
/// runner/capability/runtime-capacity/project-policy infrastructure rather
/// than re-inventing host identity.
/// </summary>
public sealed record StudioClientSummaryDto(
    string ClientId,
    string HostId,
    string Status,
    string AdmissionState,
    RemoteHostAdmissionDto Admission,
    RuntimeCapacitySettingsDto? RuntimeCapacity,
    HostTelemetrySnapshotDto? Telemetry,
    int RunnerCount,
    DateTime? LastSeenAt,
    DateTime? RetiredAt,
    string? RetiredReason,
    DateTime? PermanentlyDeletedAt);

public sealed record StudioClientListResponse(IReadOnlyList<StudioClientSummaryDto> Clients);

/// <summary>
/// Default execution settings applied to future runs started on a host when
/// the caller does not specify them explicitly (mirrors the optional fields
/// already accepted by <see cref="StartTaskRequest"/>/<see cref="ContinueTaskRequest"/>).
/// </summary>
public sealed record StudioHostDefaultsSettings(
    string? Model = null,
    string? CliType = null,
    string? ThinkingLevel = null,
    int? MaxParallelism = null);

public sealed record StudioHostDefaultsDto(
    string HostId,
    StudioHostDefaultsSettings Defaults,
    long Version,
    DateTime UpdatedAt);

public sealed record UpdateStudioHostDefaultsRequest(
    StudioHostDefaultsSettings Defaults,
    long ExpectedVersion);

public sealed record StudioHostDrainRequest(string Reason);

public sealed record StudioHostReviveRequest(string? Reason = null);

public sealed record StudioHostRetireRequest(string? Reason = null);

/// <summary>
/// Durable, soft (never hard-deleting) lifecycle tombstone for a host. A
/// retired host should stop being claimable for new work; a permanently
/// deleted host additionally stops appearing as an active client, but its
/// underlying <c>runners</c>/<c>runner_capabilities</c> rows are preserved
/// for audit/history.
/// </summary>
public sealed record StudioHostLifecycleDto(
    string HostId,
    DateTime? RetiredAt,
    string? RetiredReason,
    DateTime? PermanentlyDeletedAt,
    long Version,
    DateTime UpdatedAt);

public sealed record StudioPreflightInvalidateResponse(
    string HostId,
    long Generation,
    DateTime InvalidatedAt);

/// <summary>
/// Named "run a management command" operations exposed generically through
/// <c>POST /api/v1/management/commands</c>, distinct from the existing
/// <c>/api/v1/management/*</c> CRUD routes. Every command dispatches to the
/// same store method a dedicated Studio hosts route already exposes, so the
/// generic endpoint never re-implements host lifecycle logic.
/// </summary>
public static class StudioManagementCommands
{
    public const string HostsDrain = "hosts.drain";
    public const string HostsRevive = "hosts.revive";
    public const string HostsRetire = "hosts.retire";
    public const string HostsPermanentDelete = "hosts.permanent-delete";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        [HostsDrain, HostsRevive, HostsRetire, HostsPermanentDelete], StringComparer.Ordinal);
}

public sealed record StudioManagementCommandRequest(string Command, string? ArgumentsJson = null);

public sealed record StudioManagementCommandResponse(string Command, string Status, string? ResultJson = null);

/// <summary>
/// Typed shape for <see cref="StudioManagementCommandRequest.ArgumentsJson"/>.
/// Every currently supported command only needs a target host id and an
/// optional human reason, so one shared argument shape is enough.
/// </summary>
public sealed record StudioManagementCommandArguments(string? HostId = null, string? Reason = null);

public sealed record StudioProviderAuthEventRequest(
    string HostId,
    string Provider,
    string Status,
    string? DetailJson = null);

public sealed record StudioProviderAuthEventDto(
    string Id,
    string HostId,
    string Provider,
    string Status,
    string? DetailJson,
    DateTime CreatedAt);
