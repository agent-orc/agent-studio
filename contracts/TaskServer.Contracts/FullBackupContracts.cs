namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// A full backup set bundles the verified SQLite snapshot, every cold archive payload the current state
/// references, and an analysis export, so a set alone can rebuild the whole store on an empty machine.
/// </summary>
public sealed record FullBackupSummaryDto(
    string Id,
    DateTime CreatedAt,
    long TotalBytes,
    int TaskCount,
    int ColdPayloadCount,
    string SetSha256,
    IReadOnlyList<string> Warnings,
    string RemoteState = "not-configured");

public sealed record ListFullBackupsResponse(IReadOnlyList<FullBackupSummaryDto> Backups);

public sealed record VerifyFullBackupResult(string BackupId, bool Verified, FullBackupSummaryDto Summary);

public sealed record RestoreFullBackupRequest(string BackupId);

public sealed record RestoreFullBackupResult(string BackupId, bool Restored, string Message);
