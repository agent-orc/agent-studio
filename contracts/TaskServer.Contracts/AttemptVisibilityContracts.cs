namespace AgentStudio.TaskServer.Contracts;

/// <summary>Metadata from Task Server attempt authority and executor receipts. Never carries credential material.</summary>
public sealed record AttemptVisibilityDto(
    string AttemptId, string Project, string Subject, string HostId, string Role,
    string Executor, string LeaseId, long Fence, string LeaseState,
    DateTime StartedAt, DateTime LeaseExpiresAt, string? ImageDigest,
    int? GrantHalfCores, string GrantSource, string CacheOutcome,
    string CleanupStatus, string Occupancy, string Provenance);

public sealed record AttemptVisibilitySnapshotDto(DateTime GeneratedAt,
    IReadOnlyList<AttemptVisibilityDto> Attempts);

public sealed record AttemptExecutorReceiptRequest(
    string RunnerId, string InstanceId, string LeaseId, long Fence,
    string Executor, string? ImageDigest, int? GrantHalfCores, string CacheOutcome,
    string CleanupStatus, DateTime StartedAt);
