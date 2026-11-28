using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

public static class CapabilityProtocol
{
    public const int CurrentSchemaVersion = 2;
    public const int LegacySchemaVersion = 1;

    public const string CodingExecutor = "executor:coding";
    public const string ReviewExecutor = "executor:review";
    public const string GitFetch = "git:fetch";
    public const string GitPush = "git:push";
    public const string GitWorkflowPush = "git:workflow-push";
    public const string RepositoryAccess = "repository:access";
    public const string DotNet = "toolchain:dotnet";
    public const string Node = "toolchain:node";
    public const string Playwright = "toolchain:playwright";

    /// <summary>
    /// The host can render the Compose stack: a Docker CLI with the compose
    /// plugin answers <c>docker compose version</c>. No Docker daemon is
    /// implied. Required by the Compose-render gate step (AGT-2981).
    /// </summary>
    public const string ComposeRender = "toolchain:compose-render";
    public const string Vision = "review:vision";
    public const string Disk = "host:disk";
    public const string TaskServerConnectivity = "task-server:connectivity";
    public const string LeaseAuthority = "task-server:lease-authority";
    public const string HostNetwork = "host:network";
    public const string RepositoryFileSystem = "repository:filesystem";
    public const string TaskServerAuthority = "task-server:authority";

    /// <summary>
    /// Informational: the zone network policy version and zone the host
    /// enforces, from the state <c>agent-host zone-policy apply --execute</c>
    /// recorded (AGT-3231). Never a claim requirement.
    /// </summary>
    public const string ZonePolicy = "zone-policy";

    /// <summary>
    /// SM-11: the host's per-attempt containment decision (executor and
    /// reason). Advertised by every host; a claim requirement only after the
    /// host's work-container canary verdict is go, so a host that lost a
    /// containment prerequisite stops claiming instead of running unconfined.
    /// </summary>
    public const string AttemptContainment = "attempt-containment";

    /// <summary>
    /// The runner can execute a card through the named coding CLI binary.
    /// Authentication remains a separate capability so binary loss and expired
    /// provider credentials stay independently diagnosable.
    /// </summary>
    public static string CliExecution(string cliType)
        => $"cli-execution:{cliType.Trim().ToLowerInvariant()}";

    public static string ProviderAuthentication(string provider)
        => $"provider-auth:{provider.Trim().ToLowerInvariant()}";

    public static string ModelAcceptance(string provider, string model)
        => $"model-acceptance:{provider.Trim().ToLowerInvariant()}:{model.Trim().ToLowerInvariant()}";
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
    DateTime? CredentialModifiedAt = null,
    string? EvidenceId = null,
    string? EvidenceExcerpt = null,
    IReadOnlyList<string>? SupportedModels = null,
    string? CredentialGeneration = null,
    DateTime? CredentialObservedAt = null,
    DateTime? LastRealSuccessAt = null,
    string? ExpiryProvenance = null,
    DateTime? AccessTokenExpiresAt = null,
    string? EffectiveSource = null,
    bool? NativeFileShadowed = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    string? HealthOutcome = null,
    string? ServiceAvailability = null,
    string? CredentialHealth = null,
    // Runner-local causal order of model-acceptance evidence. A ready entry
    // carries the sequence its verdict was written at; any other entry carries
    // the runner's current sequence. Comparable only within one instance.
    long? ProbeSequence = null,
    DateTime? Since = null,
    int? HttpStatus = null,
    string? Request = null,
    // AGT-3948: provider-auth only. True while the CLI's own status command
    // confirms an active session; Since then names when the outcome became
    // indeterminate behind that session.
    bool? StatusSessionActive = null)
{
    // Keep the previous in-process constructor available during rolling
    // upgrades. Rich fields are negotiated on the HTTP wire separately.
    [JsonConstructor]
    public AdvertisedCapabilityDto(
        string key, string category, string status, string? version, string? identity,
        string? detail, string? signal, DateTime? expiresAt, DateTime? limitedUntil,
        DateTime? credentialModifiedAt, string? evidenceId, string? evidenceExcerpt,
        IReadOnlyList<string>? supportedModels)
        : this(key, category, status, version, identity, detail, signal, expiresAt,
            limitedUntil, credentialModifiedAt, evidenceId, evidenceExcerpt, supportedModels,
            null, null, null, null, null, null, null, null, null, null, null) { }
}

public sealed record CapabilityAdvertisementRequest(
    string RunnerId,
    string InstanceId,
    int SchemaVersion,
    DateTime AdvertisedAt,
    int FreshForSeconds,
    long Generation,
    IReadOnlyList<AdvertisedCapabilityDto> Capabilities,
    HostTelemetrySnapshotDto? Telemetry = null,
    RunnerReleaseIdentityDto? Release = null,
    int? CredentialHealthVersion = null)
{
    [JsonConstructor]
    public CapabilityAdvertisementRequest(
        string runnerId, string instanceId, int schemaVersion, DateTime advertisedAt,
        int freshForSeconds, long generation, IReadOnlyList<AdvertisedCapabilityDto> capabilities,
        HostTelemetrySnapshotDto? telemetry, RunnerReleaseIdentityDto? release)
        : this(runnerId, instanceId, schemaVersion, advertisedAt, freshForSeconds,
            generation, capabilities, telemetry, release, null) { }
}

public sealed record ProviderCanaryPermitDto(bool Allowed, DateTime? NextRetryAt, string Reason);
public sealed record ProviderHealthItemDto(string CohortKey, string CredentialGeneration,
    string Kind, string? RunbookId, DateTime OpenedAt, DateTime? ClosedAt);

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
    long? CliProcessesReaped = null,
    ReviewPlaneBudgetDto? ReviewPlane = null,
    SalvageStoreDto? SalvageStore = null,
    int? DotnetProcesses = null,
    int? StaleBuildNodes = null,
    long? StaleBuildNodesReaped = null,
    DateTime? BuildNodeSweepAt = null,
    IReadOnlyList<HostContainerSummaryDto>? Containers = null,
    ClaimLoadGateDto? ClaimLoadGate = null,
    OneShotClaimRouteConditionDto? OneShotClaimRoute = null,
    IReadOnlyList<HostFilesystemUsageDto>? Filesystems = null,
    // AGT-3954: optional structure facts. Older hosts omit them and still show, flat.
    HostPlacementDto? Placement = null,
    long? NetworkReceiveBytesPerSecond = null,
    long? NetworkTransmitBytesPerSecond = null,
    double? TaskServerRoundTripMs = null, HostStructureReading? Structure = null, HeartbeatPeer? Peer = null,
    long? SwapUsedBytes = null, long? SwapTotalBytes = null);

/// <summary>
/// The one-shot claim route as this runner observed it while it answered
/// 404 or said networked one-shot execution needs the standalone Task Server.
/// Absent once the route answers normally again.
/// </summary>
public sealed record OneShotClaimRouteConditionDto(
    string Condition,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    int BackoffSeconds,
    int StatusCode,
    string? Detail);

/// <summary>Current coding claim admission, observed by the runner process.</summary>
public sealed record ClaimLoadGateDto(
    string State,
    double LoadPerCore,
    double Threshold,
    long SustainedSeconds,
    DateTime? Since,
    int ActiveSlots);

/// <summary>
/// One recorded claim gate transition for the Execution Hosts history.
/// <c>Event</c> is <c>claim-stopped</c> or <c>claim-resumed</c>. The duration is
/// how long load stayed above the threshold before claims stopped, or how
/// long claims stayed stopped before they resumed.
/// </summary>
public sealed record ClaimGateEventDto(
    long Sequence,
    DateTime OccurredAt,
    string RunnerId,
    string? HostId,
    string Event,
    double LoadPerCore,
    double Threshold,
    long DurationSeconds,
    int ActiveSlots);

/// <param name="Id">Short container id, which is the default host name inside the container (AGT-3954).</param>
public sealed record HostContainerSummaryDto(string Name, string? ImageDigest, string State, string? Id = null);

/// <summary>
/// The coding host's salvage store (AGT-2999): the tarball directory the
/// retention sweep owns, measured on the host, and the outcome of the last
/// sweep over tarballs and this runner's <c>agent-studio/salvage/*</c> refs.
/// </summary>
public sealed record SalvageStoreDto(
    DateTime ObservedAt,
    string Path,
    bool Exists,
    long SizeBytes,
    int EntryCount,
    int TarballCount,
    int UnrecognizedCount,
    string? OldestEntry,
    DateTime? OldestEntryAt,
    string Mode,
    int RetentionDays,
    int MaxPerCard,
    SalvageSweepDto? LastSweep = null);

/// <summary>
/// One retention sweep. <c>Eligible</c> counts what the policy selected;
/// <c>Deleted</c> counts what was removed, which is zero in <c>report</c> mode.
/// </summary>
public sealed record SalvageSweepDto(
    DateTime StartedAt,
    DateTime CompletedAt,
    string Mode,
    string Status,
    int TarballsInspected,
    int TarballsEligible,
    long TarballBytesEligible,
    int TarballsDeleted,
    long TarballBytesDeleted,
    int RefsInspected,
    int RefsEligible,
    int RefsDeleted,
    int ProtectedByActiveRun,
    int Failures,
    string? Error = null);

/// <summary>
/// Review-role capacity observed from the role unit's cgroup. The raw
/// <c>cpu.max</c> value is retained so an unlimited quota is distinguishable
/// from a missing probe; <see cref="PlaneCpuCores"/> is the effective budget
/// used by admission and central parallelism policy.
/// </summary>
public sealed record ReviewPlaneBudgetDto(
    DateTime ObservedAt,
    string CpuMax,
    double PlaneCpuCores,
    double? CpuQuotaPercent,
    int HostCores,
    double WorkerEnvelopeCores,
    int WorkerEnvelopeCpuQuotaPercent,
    int CurrentCeiling,
    double? RollingReviewDurationSeconds,
    double? ThrottledShare,
    bool SustainedThrottling,
    string? AlarmSuggestion = null);

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
    DateTime? CredentialModifiedAt = null,
    string? EvidenceId = null,
    string? EvidenceExcerpt = null,
    IReadOnlyList<string>? SupportedModels = null,
    string? CredentialGeneration = null,
    DateTime? CredentialObservedAt = null,
    DateTime? LastRealSuccessAt = null,
    string? ExpiryProvenance = null,
    DateTime? AccessTokenExpiresAt = null,
    string? EffectiveSource = null,
    bool? NativeFileShadowed = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    string? HealthOutcome = null,
    string? ServiceAvailability = null,
    string? CredentialHealth = null,
    DateTime? Since = null,
    int? HttpStatus = null,
    string? Request = null,
    bool? StatusSessionActive = null)
{
    [JsonConstructor]
    public CapabilityHealthDto(
        string key, string category, string advertisedStatus, string healthState,
        string? reason, DateTime advertisedAt, DateTime freshUntil, bool isFresh,
        DateTime? firstFailureAt, DateTime? lastFailureAt, DateTime? cooldownUntil,
        string? canaryClaimId, int consecutiveFailures, string? version, string? identity,
        string? detail, IReadOnlyList<string> affectedClaims,
        IReadOnlyList<CapabilityRecoveryEventDto> recoveryHistory, string? signal,
        DateTime? expiresAt, DateTime? limitedUntil, DateTime? credentialModifiedAt,
        string? evidenceId, string? evidenceExcerpt, IReadOnlyList<string>? supportedModels)
        : this(key, category, advertisedStatus, healthState, reason, advertisedAt, freshUntil,
            isFresh, firstFailureAt, lastFailureAt, cooldownUntil, canaryClaimId,
            consecutiveFailures, version, identity, detail, affectedClaims, recoveryHistory,
            signal, expiresAt, limitedUntil, credentialModifiedAt, evidenceId,
            evidenceExcerpt, supportedModels, null, null, null, null, null, null, null, null, null, null, null) { }
}

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
    HostCliUpdateDto? CliUpdate = null,
    RunnerReleaseIdentityDto? Release = null,
    int ActiveGateCount = 0,
    IReadOnlyList<ConfiguredModelRouteDto>? ConfiguredModelRoutes = null,
    IReadOnlyList<HostContainerSummaryDto>? Containers = null,
    IReadOnlyList<ReviewDaemonRestartDto>? ReviewRestarts = null,
    IReadOnlyList<HeldCapabilityDto>? HeldCapabilities = null);

/// <summary>
/// AGT-3840: a capability key this role cannot claim with right now, and why.
/// A role can read Online while a capability is held: an indeterminate
/// provider probe or an unregistered key keeps work off the role without
/// turning its status degraded. Execution Hosts lists these per role so the
/// operator sees the held key, the probe detail, and a re-probe action.
/// </summary>
/// <param name="State">One of <see cref="HeldCapabilityStates"/>.</param>
/// <param name="Cause">One of <see cref="HeldCapabilityCauses"/>.</param>
/// <param name="Detail">The probe's own detail, or the reason the key is held.</param>
/// <param name="ObservedAt">When the advertisement or the refused claim was observed.</param>
/// <param name="WaitingTaskKeys">Pending work that needs this key, when known.</param>
public sealed record HeldCapabilityDto(
    string Key,
    string State,
    string Cause,
    string? Detail,
    DateTime? ObservedAt,
    IReadOnlyList<string>? WaitingTaskKeys = null);

public static class HeldCapabilityStates
{
    /// <summary>The key is known to be unusable on this role.</summary>
    public const string Held = "held";
    /// <summary>The role cannot tell whether the key works; claims treat it as unusable.</summary>
    public const string Indeterminate = "indeterminate";
}

public static class HeldCapabilityCauses
{
    /// <summary>Pending work needs the key and this role never registered it.</summary>
    public const string NotRegistered = "not-registered";
    /// <summary>The advertisement expired.</summary>
    public const string Stale = "stale";
    /// <summary>The role advertised the key with a status that does not admit claims.</summary>
    public const string NotReady = "not-ready";
    /// <summary>The provider probe could not decide whether the credential works.</summary>
    public const string ProbeIndeterminate = "probe-indeterminate";
    /// <summary>The provider probe found the credential invalid.</summary>
    public const string CredentialInvalid = "credential-invalid";
    /// <summary>The provider reported an incident.</summary>
    public const string ProviderIncident = "provider-incident";
    /// <summary>The AI Operator routed the provider away from this host.</summary>
    public const string RouteExcluded = "route-excluded";
    /// <summary>A registered repository-read key has no fresh read proof.</summary>
    public const string NoReadProof = "no-read-proof";
}

/// <summary>AGT-3840: operator request to re-probe one capability on one runner identity.</summary>
public sealed record CapabilityReprobeRequest(string Capability);

/// <summary>
/// AGT-3690: one review daemon restart of a runner identity, with the review
/// attempts it lost. <see cref="RunnerCapabilitySnapshotDto.RestartedAt"/> and
/// <see cref="RunnerCapabilitySnapshotDto.ReviewsLost"/> describe only the
/// newest restart, so a later clean restart used to overwrite the one that
/// lost a review; the history keeps both.
/// </summary>
public sealed record ReviewDaemonRestartDto(
    DateTime RestartedAt,
    string HostId,
    string InstanceId,
    int ReviewsLost,
    IReadOnlyList<ReviewRestartLostAttemptDto> LostAttempts);

/// <summary>A review attempt a daemon restart lost, and the card it reviewed.</summary>
public sealed record ReviewRestartLostAttemptDto(
    string AttemptId,
    string TaskKey,
    string Status);

/// <summary>
/// The latest non-ready model-acceptance entry a host advertised after the
/// provider refused that model at launch.
/// </summary>
public sealed record ModelRejectionAcknowledgement(string? InstanceId, long? Sequence)
{
    public static ModelRejectionAcknowledgement None { get; } = new(null, null);
}

/// <summary>
/// Clears a launch rejection only on causal evidence. Runner and server clocks
/// are not comparable, so a ready entry proves recovery only when the same
/// runner instance wrote it after it advertised the model as not ready.
/// </summary>
public static class ModelAcceptanceRecovery
{
    public static (bool Cleared, ModelRejectionAcknowledgement Acknowledgement) Evaluate(
        ModelRejectionAcknowledgement acknowledgement,
        string instanceId,
        string advertisedStatus,
        long? probeSequence)
    {
        if (probeSequence is not { } sequence) return (false, acknowledgement);
        if (!string.Equals(advertisedStatus, "ready", StringComparison.OrdinalIgnoreCase))
            return (false, new ModelRejectionAcknowledgement(instanceId, sequence));
        var cleared = string.Equals(acknowledgement.InstanceId, instanceId, StringComparison.Ordinal)
                      && acknowledgement.Sequence is { } floor
                      && sequence > floor;
        return (cleared, acknowledgement);
    }
}

/// <summary>An operator-configured primary or fallback model the host should probe.</summary>
public sealed record ConfiguredModelRouteDto(string CliType, string Model);

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
