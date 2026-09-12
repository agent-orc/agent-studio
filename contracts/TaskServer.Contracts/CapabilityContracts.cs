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

/// <summary>One CLI installation observed by a runner capability probe.</summary>
public sealed record InstalledCliDto(
    string Name,
    string Version,
    string InstallPath,
    DateTime CheckedAt,
    string? TargetVersion = null,
    bool IsBelowTarget = false);

public static class CliUpdateStates
{
    public const string Draining = "draining";
    public const string Ready = "ready";
    public const string Upgrading = "upgrading";
    public const string Probing = "probing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record HostCliUpdateDto(
    string HostId,
    string State,
    string CodexTargetVersion,
    string ClaudeTargetVersion,
    DateTime RequestedAt,
    DateTime UpdatedAt,
    int ActiveSlots,
    bool CanCancel,
    string? Detail = null,
    DateTime? CompletedAt = null);

public sealed record HostCliUpdateResultRequest(
    string State,
    string? Detail = null);

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
    int? RoleMaxParallelism = null,
    DateTime? RestartedAt = null,
    int ReviewsLost = 0,
    IReadOnlyList<InstalledCliDto>? InstalledClis = null,
    HostCliUpdateDto? CliUpdate = null);

public static class InstalledCliProjection
{
    public static IReadOnlyList<InstalledCliDto> FromCapabilities(
        IEnumerable<CapabilityHealthDto> capabilities,
        string? codexTargetVersion = null,
        string? claudeTargetVersion = null)
        => capabilities
            .Where(item => item.Key.StartsWith("cli-execution:", StringComparison.Ordinal)
                           && !string.IsNullOrWhiteSpace(item.Version)
                           && !string.IsNullOrWhiteSpace(item.Identity))
            .Select(item =>
            {
                var name = item.Key["cli-execution:".Length..];
                var target = string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase)
                    ? codexTargetVersion
                    : string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase)
                        ? claudeTargetVersion
                        : null;
                return new InstalledCliDto(
                    name,
                    item.Version!,
                    item.Identity!,
                    item.AdvertisedAt,
                    target,
                    IsBelowTarget: InstalledCliVersionPolicy.IsBelow(item.Version, target));
            })
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

}

/// <summary>SemVer comparison shared by snapshot drift and host policy.</summary>
public static class InstalledCliVersionPolicy
{
    public static bool IsBelow(string? installed, string? target)
    {
        if (!TryParse(installed, out var left) || !TryParse(target, out var right)) return false;
        return Compare(left, right) < 0;
    }

    private static bool TryParse(string? value, out Parsed parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var withoutBuild = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
            var parts = withoutBuild.Split('-', 2);
            var core = parts[0].Split('.');
            if (core.Length is < 1 or > 3
                || !int.TryParse(core[0], out var major)
                || !int.TryParse(core.ElementAtOrDefault(1) ?? "0", out var minor)
                || !int.TryParse(core.ElementAtOrDefault(2) ?? "0", out var patch)) return false;
            parsed = new Parsed(major, minor, patch, parts.Length == 2 ? parts[1].Split('.') : []);
            return true;
        }

    private static int Compare(Parsed left, Parsed right)
        {
            var result = left.Major.CompareTo(right.Major);
            if (result != 0) return result;
            result = left.Minor.CompareTo(right.Minor);
            if (result != 0) return result;
            result = left.Patch.CompareTo(right.Patch);
            if (result != 0) return result;
            if (left.Pre.Length == 0) return right.Pre.Length == 0 ? 0 : 1;
            if (right.Pre.Length == 0) return -1;
            for (var index = 0; index < Math.Max(left.Pre.Length, right.Pre.Length); index++)
            {
                if (index >= left.Pre.Length) return -1;
                if (index >= right.Pre.Length) return 1;
                var ln = int.TryParse(left.Pre[index], out var li);
                var rn = int.TryParse(right.Pre[index], out var ri);
                result = ln && rn ? li.CompareTo(ri) : ln ? -1 : rn ? 1
                    : string.Compare(left.Pre[index], right.Pre[index], StringComparison.Ordinal);
                if (result != 0) return result;
            }
            return 0;
        }

    private readonly record struct Parsed(int Major, int Minor, int Patch, string[] Pre);
}

public sealed record OperatorHostDrainRequest(string Reason);

public sealed record ClearAutomaticHostDrainRequest(string Reason);
