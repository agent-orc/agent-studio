namespace AgentStudio.TaskServer.Contracts;

/// <summary>One physical host's tested placement facts. Repository mirrors are hints only.</summary>
public sealed record HostPlacementAdvertisement(
    string HostId,
    string RunnerId,
    string InstanceId,
    IReadOnlyList<string> Roles,
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<string> Executors,
    IReadOnlyList<string> ToolchainDigests,
    IReadOnlyList<string> ProviderBindings,
    IReadOnlyList<string> RepositoryIds,
    IReadOnlyList<string> WarmRepositoryIds,
    long Version,
    DateTime UpdatedAt);

public sealed record UpsertHostPlacementRequest(
    string RunnerId,
    string InstanceId,
    IReadOnlyList<string> Roles,
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<string> Executors,
    IReadOnlyList<string> ToolchainDigests,
    IReadOnlyList<string> ProviderBindings,
    IReadOnlyList<string> RepositoryIds,
    IReadOnlyList<string> WarmRepositoryIds,
    long ExpectedVersion);

public sealed record PlacementShadowReceipt(
    long Id,
    string TaskId,
    string RunnerId,
    string HostId,
    string ActualStatus,
    string ShadowStatus,
    IReadOnlyList<string> MatchedRequirements,
    IReadOnlyList<string> RejectedReasons,
    long AllocationVersion,
    int GrantHalfCores,
    string? LeaseId,
    long? Fence,
    long AuthorityEpoch,
    DateTime ObservedAt);
