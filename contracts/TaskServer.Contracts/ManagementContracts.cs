namespace AgentStudio.TaskServer.Contracts;

public enum TaskServerMode
{
    Normal,
    Draining,
    ReadOnly,
    Maintenance,
}

public sealed record TaskServerStatusDto(
    string ServerId,
    string ServerVersion,
    int SchemaVersion,
    TaskServerMode Mode,
    bool AuthorityReady,
    string DataDirectory,
    ProtocolRangeDto Protocol,
    DateTime StartedAt,
    int OutboxBacklog = 0,
    long? OldestUnacknowledgedSequence = null,
    IReadOnlyDictionary<string, int>? FinalHandoffStates = null);

public sealed record ChangeModeRequest(TaskServerMode Mode, string Reason);
public sealed record PrepareShutdownRequest(string Reason);
public sealed record PrepareShutdownResult(bool SafeToStop, int UnresolvedAttempts, TaskServerMode Mode, string Message);
public sealed record BackupRequest(string? Name = null);
public sealed record BackupResult(
    string BackupId,
    string Path,
    string Sha256,
    DateTime CreatedAt,
    long SizeBytes,
    string? InventorySha256 = null);
public sealed record RestoreRequest(string BackupId, bool VerifyOnly = false);
public sealed record RestoreResult(
    string BackupId,
    bool Verified,
    bool Restored,
    string Sha256,
    string Message,
    string? InventorySha256 = null);
public sealed record ResolveUnknownAttemptRequest(string ContainmentProof, string Resolution = "requeue");

public sealed record InvariantDefinitionDto(
    string Key,
    string Category,
    string ComparedTruths,
    string Violation,
    string SelfHealingAction);

public sealed record InvariantRegistryDto(
    IReadOnlyList<InvariantDefinitionDto> Definitions,
    IReadOnlyList<AuditRecordDto> RecentViolations,
    int PendingRunnerActions);

public sealed record LegacyMigrationRequest(
    string LegacyRoot,
    string WorkspaceName,
    bool FreezeConfirmed,
    bool PreserveEvidenceGit = true,
    string? ExpectedMigrationId = null,
    bool RequireAttemptAuthority = false);

public sealed record LegacyMigrationInventory(
    string MigrationId,
    string LegacyRoot,
    int Projects,
    int Tasks,
    int Events,
    int Artifacts,
    IReadOnlyList<string> EvidenceGitRoots,
    IReadOnlyList<string> Warnings,
    int RunnerIdentities = 0,
    int CodingAttempts = 0,
    int ReviewAttempts = 0,
    int Leases = 0,
    long AuthorityEpoch = 0,
    string InventorySha256 = "",
    DateTime? CreatedAt = null,
    IReadOnlyList<LegacyMigrationProjectCounts>? ProjectCounts = null,
    int Epics = 0,
    int Dossiers = 0,
    int OrchestratorSessions = 0,
    int ContextChats = 0,
    int ContextChatTurns = 0,
    int AttemptAuthorityRecords = 0,
    int PendingIntegrationRecords = 0,
    int ResultRefs = 0,
    int GitCommits = 0,
    int IntegrationRecords = 0,
    int DeliveryRefs = 0,
    int BusLogFiles = 0,
    long BusLogBytes = 0,
    IReadOnlyList<LegacyMigrationSourceFile>? SourceFiles = null,
    LegacyMigrationOrphanCounts? OrphanedReferences = null);

public sealed record LegacyMigrationOrphanCounts(
    int CodingAttempts = 0,
    int ReviewAttempts = 0,
    int Leases = 0,
    int FenceCounters = 0,
    int IntegrationRecords = 0)
{
    public int Total => CodingAttempts + ReviewAttempts + Leases + FenceCounters + IntegrationRecords;
}

public sealed record LegacyMigrationProjectCounts(
    string Project,
    IReadOnlyDictionary<string, int> States,
    int Tasks,
    int Epics,
    int Events,
    int Artifacts,
    int GitCommits,
    int IntegrationRecords,
    int PendingIntegrationRecords,
    int ResultRefs,
    int DeliveryRefs);

public sealed record LegacyMigrationSourceFile(
    string Path,
    long SizeBytes,
    string Sha256,
    string Kind);

public sealed record LegacyMigrationResult(
    string MigrationId,
    bool Imported,
    int Projects,
    int Tasks,
    int Events,
    int Artifacts,
    string IntegritySha256,
    string RollbackBoundary,
    IReadOnlyList<string> EvidenceGitRoots,
    int RunnerIdentities = 0,
    int CodingAttempts = 0,
    int ReviewAttempts = 0,
    int Leases = 0,
    long AuthorityEpoch = 0,
    int ArchivedTasks = 0,
    long ArchivedBytes = 0,
    string InventorySha256 = "",
    string AfterInventorySha256 = "",
    bool Idempotent = false,
    string? ReportId = null,
    string? ReportPath = null,
    string? ReportSha256 = null,
    string? ReportSignature = null,
    LegacyMigrationInventory? Before = null,
    LegacyMigrationInventory? After = null,
    LegacyMigrationOrphanCounts? OrphanedReferences = null);

public sealed record LegacyMigrationReport(
    string ReportId,
    string MigrationId,
    DateTime StartedAt,
    DateTime CompletedAt,
    double DurationSeconds,
    string ServerId,
    string ServerVersion,
    int SchemaVersion,
    string InventorySha256,
    string AfterInventorySha256,
    LegacyMigrationInventory Before,
    LegacyMigrationInventory After,
    string PreImportBackupId,
    string PreImportBackupSha256,
    string StoreIntegritySha256,
    string FrozenSource,
    string ArtifactPolicy,
    string AttemptAuthorityPolicy,
    string BusLogPolicy,
    string ReportPath,
    string ReportSha256,
    string SignatureAlgorithm,
    string Signature);
