namespace AgentStudio.TaskServer.Contracts;

/// <summary>Only a digest of a non-secret account identity crosses the host channel.</summary>
public sealed record HostQuotaClaimRequest(string InstanceId, string Provider, string SubscriptionKey);

public sealed record HostQuotaClaimResponse(bool Granted, string? ProbeId, DateTime? NextAt, string? Reason);

public sealed record HostQuotaWindowDto(string Label, double? UsedPct, DateTime? ResetAt, string? ResetLabel);

/// <summary>Parsed quota only. Terminal output and credentials have no wire field.</summary>
public sealed record HostQuotaReportRequest(
    string InstanceId, string Provider, string SubscriptionKey, string ProbeId,
    DateTime ObservedAt, string? CliVersion, string? Plan,
    IReadOnlyList<HostQuotaWindowDto> Windows, string? Error);

public sealed record HostQuotaReadingDto(
    string Provider, string SubscriptionKey, string HostId, DateTime ObservedAt,
    string? CliVersion, string? Plan, IReadOnlyList<HostQuotaWindowDto> Windows,
    string? Error, DateTime? ProbeFailedAt = null);
