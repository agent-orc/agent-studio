using System.Text.Json;
using AgentStudio.Secrets;

namespace AgentStudio.Management;

/// <summary>The non-secret record next to each local archive (<c>&lt;id&gt;.json</c>).</summary>
public sealed record BackupRecord(
    string Id,
    string FileName,
    string CreatedAt,
    long SizeBytes,
    string Sha256,
    string KeyId,
    string? SecretKeyId,
    int FileCount,
    long SourceBytes,
    BackupConsistency Consistency,
    BackupVerificationRecord? Verification = null,
    BackupUploadRecord? Upload = null);

public sealed record BackupVerificationRecord(string At, string Outcome, string Detail, int RestoredFiles);

/// <summary>Off-host copy of one archive. <c>State</c> is <c>pending</c>, <c>uploaded</c> or <c>failed</c>.</summary>
public sealed record BackupUploadRecord(
    string Target, string State, string? UploadedAt, string? LastAttemptAt, string? Error, int Attempts);

public sealed record BackupRunRecord(
    string At, string Trigger, string Outcome, string? BackupId, string? Error, long DurationMilliseconds);

/// <summary>Scheduler memory, kept in <c>backup-state.json</c> in the backup directory.</summary>
public sealed record BackupState(BackupRunRecord? LastRun, BackupRunRecord? LastVerification, string? LastRetentionAt)
{
    public static readonly BackupState Empty = new(null, null, null);
}

/// <summary>
/// The local backup directory: encrypted archives, their <c>.sha256</c>
/// files, the per-archive records and the scheduler state. All writes are
/// temp-file plus rename. The directory lies outside every backup source.
/// </summary>
public sealed class BackupCatalog(string directory)
{
    public const string ArchiveSuffix = ".tar.gz.sbk";
    private const string StateFile = "backup-state.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = directory;

    public string StagingDirectory => Path.Combine(Directory, ".staging");

    public string ArchivePath(string id) => Path.Combine(Directory, id + ArchiveSuffix);

    public string ChecksumPath(string id) => ArchivePath(id) + ".sha256";

    private string RecordPath(string id) => Path.Combine(Directory, id + ".json");

    public void EnsureCreated()
    {
        OwnerOnlyAccess.CreateDirectory(Directory);
    }

    /// <summary>Records whose archive exists, newest first.</summary>
    public IReadOnlyList<BackupRecord> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var records = new List<BackupRecord>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "studio-backup-*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(path), Json);
                if (record is not null && File.Exists(ArchivePath(record.Id))) records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable record is not a backup this catalog can vouch for.
                SilentCatch.Note(ex, "Backup record unreadable");
            }
        }
        return records.OrderByDescending(record => record.CreatedAt, StringComparer.Ordinal).ToArray();
    }

    public BackupRecord? Find(string id) => List().FirstOrDefault(record => record.Id == id);

    public void Save(BackupRecord record) => WriteJson(RecordPath(record.Id), record);

    public void WriteChecksum(BackupRecord record)
    {
        var temporary = ChecksumPath(record.Id) + ".tmp";
        File.WriteAllText(temporary, $"{record.Sha256}  {record.FileName}\n");
        File.Move(temporary, ChecksumPath(record.Id), overwrite: true);
    }

    public void Delete(string id)
    {
        foreach (var path in new[] { ArchivePath(id), ChecksumPath(id), RecordPath(id) })
            if (File.Exists(path)) File.Delete(path);
    }

    public BackupState ReadState()
    {
        var path = Path.Combine(Directory, StateFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(path), Json) ?? BackupState.Empty
                : BackupState.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return BackupState.Empty;
        }
    }

    public void WriteState(BackupState state)
    {
        EnsureCreated();
        WriteJson(Path.Combine(Directory, StateFile), state);
    }

    /// <summary>Removes leftovers of an interrupted run: the staging folder and partial archives.</summary>
    public void ClearInterrupted()
    {
        if (System.IO.Directory.Exists(StagingDirectory)) System.IO.Directory.Delete(StagingDirectory, recursive: true);
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var partial in System.IO.Directory.EnumerateFiles(Directory, "*" + ArchiveSuffix + ".partial"))
            File.Delete(partial);
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
