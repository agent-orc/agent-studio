namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire shape of one <c>AgentStudio.Retention.RetentionRule</c>. Kept as plain data here so the contracts
/// package stays dependency-free; the Task Server maps to and from the retention library's policy type.
/// </summary>
public sealed record RetentionRuleDto(
    string Id,
    string ArtifactClass,
    long HotCapBytesPerFile,
    long HotBudgetBytesPerTask,
    long RefuseAboveBytes,
    int? ArchiveAfterDaysTerminal,
    int? ArchiveTaskAfterDaysTerminal,
    int? DeleteAfterDays,
    int? DeleteArchiveAfterDaysTerminal,
    bool DeleteArchiveEnabled,
    IReadOnlyList<string> NeverArchiveLanes);

/// <summary>One resolved policy scope: the workspace defaults, or one project's overridden rules.</summary>
public sealed record RetentionPolicyDto(
    string Scope,
    int Version,
    DateTime UpdatedAt,
    string UpdatedBy,
    IReadOnlyList<RetentionRuleDto> Rules);

public sealed record UpdateRetentionPolicyRequest(
    IReadOnlyList<RetentionRuleDto> Rules,
    long ExpectedVersion);

public sealed record RetentionActionDto(
    string Kind,
    string RuleId,
    string Project,
    string TaskKey,
    string TaskId,
    int Stage,
    long Bytes,
    int FileCount,
    string Reason);

public sealed record RetentionPlanDto(
    DateTime PlannedAt,
    int PolicyVersion,
    int ActionCount,
    long TotalBytes,
    int AffectedTasks,
    IReadOnlyList<RetentionActionDto> Actions);

public sealed record RunRetentionRequest(string? Project = null, string? TaskKey = null);

public sealed record RetentionApplyResultDto(
    string RunId,
    RetentionPlanDto Plan,
    int AppliedActions,
    long AppliedBytes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record RetentionRunSummaryDto(
    string Id,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Trigger,
    string Mode,
    int PolicyVersion,
    int ActionCount,
    long AppliedBytes,
    string ActorId);

public sealed record RetentionRunDetailDto(
    RetentionRunSummaryDto Summary,
    RetentionPlanDto Plan,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record RetentionArchiveFileDto(string Name, long Size, string Sha256);

public sealed record RetentionArchiveStageDto(
    int Stage,
    DateTime ArchivedAt,
    string PayloadPath,
    string PayloadSha256,
    long TotalBytes,
    IReadOnlyList<RetentionArchiveFileDto> Files);

public sealed record RetentionArchiveManifestDto(
    string TaskId,
    string TaskKey,
    string Project,
    DateTime ArchivedAt,
    string State,
    long TotalBytes,
    DateTime? RestoredAt,
    IReadOnlyList<RetentionArchiveStageDto> Stages);

public sealed record RetentionArchiveTaskRequest(int? Stage = null);
