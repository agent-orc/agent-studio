namespace AgentStudio.Management;

public sealed record ManagementStatus(
    ServerIdentity Server,
    ServerHealth Health,
    StoreStatus Store,
    EvidenceStatus Evidence,
    MaintenanceStatus Maintenance,
    IReadOnlyList<MigrationStatus> Migrations,
    IReadOnlyList<RunnerManagementStatus> Runners,
    SecurityManagementStatus Security,
    BackupStatus Backups,
    StatisticsStatus Statistics);

public sealed record StatisticsStatus(string State, string? RefreshedAt);

public sealed record ServerIdentity(
    string Id, string Url, string Version, string ProtocolMinimum,
    string ProtocolMaximum, long UptimeSeconds);

public sealed record ServerHealth(string State, bool Live, bool Ready, IReadOnlyList<string> Reasons);

public sealed record StoreStatus(
    long SizeBytes, int ProjectCount, int TaskCount, int ArchivedTaskCount,
    long? EventCount, long ArtifactCount, int IdentityCount);

public sealed record EvidenceStatus(string State, long EventFiles, long ArtifactFiles, string? LastWriteAt);

public sealed record MaintenanceStatus(
    string Mode, bool DrainRequested, bool ShutdownPrepared,
    string? Reason, string? ChangedAt, string? ChangedBy);

public sealed record MigrationStatus(string Id, string State, string? StartedAt, string? Detail);

public sealed record RunnerManagementStatus(
    string Id, string DisplayName, string State, string? LastUsedAt,
    string? LastClaimAt, int ActiveSlots, int AvailableSlots,
    bool DrainRequested, bool RetireRequested, string CredentialManagementUrl);

public sealed record SecurityManagementStatus(
    bool Available, int UserCount, int CredentialRunnerCount,
    string SessionUrl, string UsersUrl, string RunnerCredentialsUrl,
    string Integration);

public sealed record BackupStatus(
    string Directory, int RetentionCount, IReadOnlyList<BackupSummary> Items,
    string? LastFailure);

public sealed record BackupSummary(
    string Id, string FileName, long SizeBytes, string CreatedAt,
    string Sha256, string VerificationState, int EntryCount);

public sealed record ManagementCommandRequest(
    string Kind, bool DryRun = true, string? Confirmation = null,
    string? IdempotencyKey = null, int? RetentionCount = null,
    string? BackupId = null, string? Reason = null,
    string? RunnerId = null, string? CredentialId = null,
    string? RunnerName = null, IReadOnlyList<string>? Scopes = null,
    DateTime? ExpiresAt = null);

public sealed record ManagementCommandResult(
    string CommandId, string Kind, bool DryRun, string State,
    int Matched, int Affected, string Summary, string CompletedAt,
    string Actor, string IdempotencyKey, object? Detail = null);

public sealed record ManagementAuditEvent(
    string Timestamp, string CommandId, string Actor, string Kind,
    bool DryRun, string IdempotencyKey, string Outcome, int Matched,
    int Affected, string Summary, string? RequestFingerprint = null);

public sealed record RecoveryDiagnostics(
    string GeneratedAt, string Health, bool Ready, string MaintenanceMode,
    bool DataDirectoryExists, bool DataDirectoryWritable,
    long FreeSpaceBytes, string? LatestBackupId,
    string? LatestBackupVerification, IReadOnlyList<string> Findings,
    string LifecycleOwner);
