namespace AgentStudio.TaskServer.Contracts;

public static class CapabilityProtocol
{
    public const int CurrentSchemaVersion = 1;

    public const string CodingExecutor = "executor:coding";
    public const string ReviewExecutor = "executor:review";
    public const string GitFetch = "git:fetch";
    public const string GitPush = "git:push";
    public const string GitWorkflowPush = "git:workflow-push";
    public const string RepositoryAccess = "repository:access";
    public const string DotNet = "toolchain:dotnet";
    public const string Node = "toolchain:node";
    public const string Playwright = "toolchain:playwright";
    public const string Vision = "review:vision";
    public const string Disk = "host:disk";
    public const string TaskServerConnectivity = "task-server:connectivity";
    public const string LeaseAuthority = "task-server:lease-authority";
    public const string HostNetwork = "host:network";
    public const string RepositoryFileSystem = "repository:filesystem";
    public const string TaskServerAuthority = "task-server:authority";

    /// <summary>
    /// The runner can execute a card through the named coding CLI binary.
    /// Authentication remains a separate capability so binary loss and expired
    /// provider credentials stay independently diagnosable.
    /// </summary>
    public static string CliExecution(string cliType)
        => $"cli-execution:{cliType.Trim().ToLowerInvariant()}";

    public static string ProviderAuthentication(string provider)
        => $"provider-auth:{provider.Trim().ToLowerInvariant()}";
}

public static class CapabilityHealthStates
{
    public const string Healthy = "healthy";
    public const string Suspect = "suspect";
    public const string Draining = "draining";
    public const string HalfOpen = "half-open";
}

public sealed record AdvertisedCapabilityDto(
    string Key,
    string Category,
    string Status = "ready",
    string? Version = null,
    string? Identity = null,
    string? Detail = null,
    string? Signal = null,
    DateTime? ExpiresAt = null,
    DateTime? LimitedUntil = null,
    DateTime? CredentialModifiedAt = null);

public sealed record CapabilityAdvertisementRequest(
    string RunnerId,
    string InstanceId,
    int SchemaVersion,
    DateTime AdvertisedAt,
    int FreshForSeconds,
    long Generation,
    IReadOnlyList<AdvertisedCapabilityDto> Capabilities,
    HostTelemetrySnapshotDto? Telemetry = null);

public sealed record HostTelemetrySnapshotDto(
    DateTime ObservedAt,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? SwapInBytesPerSecond,
    long? SwapOutBytesPerSecond,
    double? CpuStealPercent,
    double? IoWaitPercent,
    int CpuCores,
    int ActiveSlots,
    long? DiskFreeBytes = null,
    long? DiskTotalBytes = null,
    string TaskServerConnectionStatus = "unknown",
    DateTime? TaskServerConnectionObservedAt = null,
    DateTime? TaskServerConnectionFailureStartedAt = null,
    int TaskServerConnectionConsecutiveFailures = 0,
    DateTime? TaskServerConnectionEscalatedAt = null,
    string? TaskServerConnectionLastError = null,
    DateTime? TaskServerConnectionLastRecoveredAt = null,
    long? CliProcessesReaped = null);

public sealed record CapabilityFailureRequest(
    string RunnerId,
    string InstanceId,
    string CapabilityKey,
    string Classification,
    string Reason,
    DateTime OccurredAt,
    string IdempotencyKey,
    string? ClaimKind = null,
    string? ClaimId = null,
    long? Fence = null);

public sealed record CapabilityFailureResponse(
    string Status,
    string CapabilityKey,
    string HealthState,
    DateTime? CooldownUntil,
    bool WholeHostDraining,
    string? Message = null);

public sealed record CapabilityRecoveryEventDto(
    DateTime OccurredAt,
    string FromState,
    string ToState,
    string Reason,
    string? ClaimId = null);

public sealed record CapabilityHealthDto(
    string Key,
    string Category,
    string AdvertisedStatus,
    string HealthState,
    string? Reason,
    DateTime AdvertisedAt,
    DateTime FreshUntil,
    bool IsFresh,
    DateTime? FirstFailureAt,
    DateTime? LastFailureAt,
    DateTime? CooldownUntil,
    string? CanaryClaimId,
    int ConsecutiveFailures,
    string? Version,
    string? Identity,
    string? Detail,
    IReadOnlyList<string> AffectedClaims,
    IReadOnlyList<CapabilityRecoveryEventDto> RecoveryHistory,
    string? Signal = null,
    DateTime? ExpiresAt = null,
    DateTime? LimitedUntil = null,
    DateTime? CredentialModifiedAt = null);

public sealed record RemoteHostAdmissionDto(
    string HostId,
    string AdmissionState,
    string? AutomaticDrainReason,
    DateTime? AutomaticDrainAt,
    string? OperatorDrainReason,
    DateTime? OperatorDrainAt);

public sealed record RunnerCapabilitySnapshotDto(
    string RunnerId,
    string Name,
    string HostId,
    string InstanceId,
    string RunnerVersion,
    int ProtocolVersion,
    string Status,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    RemoteHostAdmissionDto HostAdmission,
    IReadOnlyList<CapabilityHealthDto> Capabilities,
    HostTelemetrySnapshotDto? Telemetry,
    RuntimeCapacitySettingsDto? RuntimeCapacity = null,
    int? EffectiveMaxParallelism = null,
    DateTime? RuntimeCapacityAppliedAt = null,
    long? RuntimeCapacityAppliedVersion = null,
    HostProjectPolicyDto? ProjectPolicy = null,
    int? RoleMaxParallelism = null);

public sealed record OperatorHostDrainRequest(string Reason);

public sealed record ClearAutomaticHostDrainRequest(string Reason);
