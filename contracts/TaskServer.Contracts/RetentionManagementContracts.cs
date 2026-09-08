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
    IReadOnlyList<RetentionRuleDto> Rules,
    FullBackupRetentionDto? FullBackups = null,
    ArchiveStoragePolicyDto? ArchiveStorage = null);

public sealed record FullBackupRetentionDto(int Daily = 7, int Weekly = 4, int Monthly = 12);

public sealed record ArchiveStoragePolicyDto(
    string ArchiveTarget = "local",
    bool CopyToSecondary = false,
    bool DeleteLocalAfterVerification = false,
    int? DeleteArchivedAfterYears = null);

public sealed record UpdateRetentionPolicyRequest(
    IReadOnlyList<RetentionRuleDto> Rules,
    long ExpectedVersion,
    FullBackupRetentionDto? FullBackups = null,
    ArchiveStoragePolicyDto? ArchiveStorage = null);

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

public sealed record RunRetentionRequest(
    string? Project = null,
    string? TaskKey = null,
    bool ConfirmColdDelete = false);

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

public sealed record RetentionArchiveObjectDto(
    string Target,
    string ObjectKey,
    string? ETag,
    string Sha256,
    long Size,
    string? ServerChecksumSha256);

public sealed record RetentionArchiveStageDto(
    int Stage,
    DateTime ArchivedAt,
    string PayloadPath,
    string PayloadSha256,
    long TotalBytes,
    IReadOnlyList<RetentionArchiveFileDto> Files,
    int PolicyVersion,
    string ArchivedBy,
    IReadOnlyList<RetentionArchiveObjectDto>? Objects = null);

public sealed record RetentionArchiveManifestDto(
    string TaskId,
    string TaskKey,
    string Project,
    DateTime ArchivedAt,
    string State,
    long TotalBytes,
    DateTime? RestoredAt,
    IReadOnlyList<RetentionArchiveStageDto> Stages,
    DateTime? TombstonedAt);

public sealed record RetentionArchiveTaskRequest(int? Stage = null, bool ConfirmColdDelete = false);

public sealed record RetentionIntegrityCheckRequest(int? SampleCount = null);

public sealed record RetentionIntegrityCheckResultDto(
    string RunId,
    int SampledManifests,
    int VerifiedObjects,
    IReadOnlyList<string> Discrepancies);

public sealed record ArchiveTargetStatusDto(
    string ActiveTarget,
    bool CopyToSecondary,
    bool DeleteLocalAfterVerification,
    bool LocalConfigured,
    bool S3Configured,
    string? S3Endpoint,
    string? S3Bucket,
    string S3Prefix,
    int? DeleteArchivedAfterYears);
