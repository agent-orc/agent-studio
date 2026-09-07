namespace AgentStudio.Retention;

public sealed record RetentionFile(
    string RelativePath,
    long Size,
    DateTimeOffset LastWriteAt,
    ArtifactClassification Classification);

public sealed record RetentionTaskInventory(
    string Project,
    string TaskKey,
    string Id,
    string Lane,
    DateTimeOffset? TerminalAt,
    string StoreKey,
    IReadOnlyList<RetentionFile> Files);

public enum RetentionActionKind
{
    ArchiveHeavy,
    ArchiveTask,
    DeleteRuntime,
    RefuseOversize,
}

public sealed record RetentionAction(
    RetentionActionKind Kind,
    string RuleId,
    RetentionTaskInventory Task,
    IReadOnlyList<RetentionFile> Files,
    long Bytes,
    int Stage,
    string Reason);

public sealed record RetentionPlan(
    DateTimeOffset PlannedAt,
    int PolicyVersion,
    IReadOnlyList<RetentionAction> Actions)
{
    public long TotalBytes => Actions.Sum(action => action.Bytes);
    public int AffectedTasks => Actions.Select(action => action.Task.StoreKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed record ArchiveTransition(
    string ManifestPath,
    string PayloadPath,
    DateTimeOffset ArchivedAt,
    int Stage,
    long TotalBytes);

public interface IRetentionStore
{
    Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(CancellationToken cancellationToken = default);
    Task<Stream> ReadFileAsync(RetentionTaskInventory task, string relativePath, CancellationToken cancellationToken = default);
    Task WriteManifestAsync(RetentionTaskInventory task, ArchiveManifest manifest, CancellationToken cancellationToken = default);
    Task<ArchiveTransition?> MoveToColdAsync(RetentionAction action, RetentionPolicy policy, CancellationToken cancellationToken = default);
    Task WriteStubAsync(RetentionTaskInventory task, ArchivePointer pointer, CancellationToken cancellationToken = default);
    Task DeleteRuntimeAsync(RetentionAction action, CancellationToken cancellationToken = default);
    Task RestoreAsync(string taskKey, CancellationToken cancellationToken = default);
}

public sealed record ArchiveManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string TaskKey { get; init; }
    public required string Id { get; init; }
    public required string Project { get; init; }
    public required string Lane { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    public required IReadOnlyList<string> RuleIds { get; init; }
    public int PolicyVersion { get; init; }
    public required IReadOnlyList<ArchiveManifestFile> Files { get; init; }
    public long TotalBytes { get; init; }
    public string Compression { get; init; } = "zip-deflate";
    public required string PayloadSha256 { get; init; }
    public required DateTimeOffset ArchivedAt { get; init; }
    public required string ArchivedBy { get; init; }
    public int Stage { get; init; }
    public DateTimeOffset? RestoredAt { get; init; }
}

public sealed record ArchiveManifestFile(string RelativePath, long Size, string Sha256);

public sealed record ArchivePointer
{
    public int SchemaVersion { get; init; } = 1;
    public required string TaskKey { get; init; }
    public required IReadOnlyList<ArchiveTransition> Archives { get; init; }
    public DateTimeOffset ArchivedAt { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset? RestoredAt { get; init; }
}
