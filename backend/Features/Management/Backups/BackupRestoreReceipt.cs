using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>
/// What an offline restore put back (AGT-3734): which archive, made when and
/// where, restored when and by whom, with which keys. Everything that happened
/// on the old host after <see cref="ArchiveCreatedAt"/> is not in the restored
/// installation.
/// </summary>
/// <param name="KeyId">Fingerprint of the backup key copy that opened the archive.</param>
/// <param name="SecretKeyId">Fingerprint of the secret store key recorded in the archive manifest.</param>
/// <param name="SecretKeyCopyId">Fingerprint of the escrowed secret store key copy the restore checked, if one was given.</param>
public sealed record BackupRestoreReceipt(
    string BackupId,
    string ArchiveFileName,
    string ArchiveSha256,
    string ArchiveCreatedAt,
    string ArchiveHost,
    string ArchiveStudioVersion,
    string RestoredAt,
    string RestoredBy,
    string KeyId,
    string? SecretKeyId,
    string? SecretKeyCopyId,
    int FilesRestored,
    int CheckoutsCloned,
    IReadOnlyList<string> SkippedSources);

/// <summary>
/// Restore receipts in the restored workspace: one line per restore in
/// <c>.audit/restore-receipts.jsonl</c>, plus a <c>backup-restore</c> row in
/// the management audit (<c>.audit/management.jsonl</c>). Both files are part
/// of the workspace, so the next backup carries them.
/// </summary>
public static class BackupRestoreReceipts
{
    public const string AuditKind = "backup-restore";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ReceiptPath(string workspace) => Path.Combine(workspace, ".audit", "restore-receipts.jsonl");

    public static string ManagementAuditPath(string workspace) => Path.Combine(workspace, ".audit", "management.jsonl");

    public static void Write(string workspace, BackupRestoreReceipt receipt)
    {
        Directory.CreateDirectory(Path.Combine(workspace, ".audit"));
        Append(ReceiptPath(workspace), JsonSerializer.Serialize(receipt, Json));
        var row = new ManagementAuditEvent(receipt.RestoredAt, "restore-" + receipt.BackupId, receipt.RestoredBy,
            AuditKind, false, $"restore:{receipt.BackupId}:{receipt.RestoredAt}", "completed",
            1, receipt.FilesRestored, Summary(receipt));
        Append(ManagementAuditPath(workspace), JsonSerializer.Serialize(row, Json));
    }

    /// <summary>The newest receipt, or null when this installation was never restored or the file is unreadable.</summary>
    public static BackupRestoreReceipt? Latest(string workspace)
    {
        var path = ReceiptPath(workspace);
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path).Reverse())
            {
                if (line.Trim().Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<BackupRestoreReceipt>(line, Json) is { BackupId.Length: > 0 } receipt)
                        return receipt;
                }
                catch (JsonException ex) { SilentCatch.Note(ex, "Restore receipt line unreadable"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Restore receipts unreadable");
        }
        return null;
    }

    public static string Summary(BackupRestoreReceipt receipt)
        => $"Restored backup {receipt.BackupId} made at {receipt.ArchiveCreatedAt} on {receipt.ArchiveHost}; "
           + $"work after {receipt.ArchiveCreatedAt} is not in this installation. "
           + $"Backup key {receipt.KeyId}, secret store key {receipt.SecretKeyCopyId ?? receipt.SecretKeyId ?? "none"}.";

    private static void Append(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
