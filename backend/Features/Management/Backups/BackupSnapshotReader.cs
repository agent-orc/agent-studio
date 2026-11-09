using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Management;

/// <summary>How one file was captured.</summary>
public enum BackupCaptureKind
{
    /// <summary>Size and modification time were unchanged across the read.</summary>
    Stable,
    /// <summary>An append-only journal, cut after its last complete line.</summary>
    JournalPrefix,
    /// <summary>A SQLite database copied through the SQLite online backup API.</summary>
    SqliteSnapshot,
    /// <summary>Kept changing on every attempt; the last complete read is archived and listed in the manifest.</summary>
    Unsettled,
    /// <summary>Deleted while the backup ran; left out.</summary>
    Vanished,
}

public sealed record BackupCapture(BackupCaptureKind Kind, string SpoolPath, long Length, string Sha256, int Attempts);

/// <summary>
/// Reads one store file into a private spool file without ever archiving a
/// half-written state:
/// <list type="bullet">
///   <item>SQLite databases are copied with the online backup API, which
///   takes a transactionally consistent snapshot and folds the WAL in. The
///   <c>-wal</c>, <c>-shm</c> and <c>-journal</c> siblings are skipped.</item>
///   <item>JSON stores are replaced by atomic rename, so an open handle reads
///   one complete version. Size and modification time are compared before and
///   after the read; a change means the file was replaced or appended during
///   the read, and it is read again.</item>
///   <item>Append-only journals (<c>.jsonl</c>, <c>.ndjson</c>, <c>.log</c>)
///   are cut after their last newline, so a line that a writer is appending is
///   never archived half-written.</item>
/// </list>
/// </summary>
public static class BackupSnapshotReader
{
    public const int MaxAttempts = 5;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private static readonly string[] JournalExtensions = [".jsonl", ".ndjson", ".log"];
    private static readonly string[] SqliteSidecars = ["-wal", "-shm", "-journal"];

    /// <summary>A file the archive leaves out because its SQLite database is captured as a whole.</summary>
    public static bool IsSqliteSidecar(string path)
    {
        foreach (var suffix in SqliteSidecars)
            if (path.EndsWith(suffix, StringComparison.Ordinal)
                && IsSqliteDatabase(path[..^suffix.Length]))
                return true;
        return false;
    }

    public static bool IsJournal(string path)
        => JournalExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public static bool IsSqliteDatabase(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < SqliteHeader.Length) return false;
            var header = new byte[SqliteHeader.Length];
            stream.ReadExactly(header);
            return header.AsSpan().SequenceEqual(SqliteHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static BackupCapture Capture(string source, string spool, Action<int>? afterRead = null)
    {
        if (!File.Exists(source)) return new(BackupCaptureKind.Vanished, spool, 0, "", 0);
        if (IsSqliteDatabase(source)) return CaptureSqlite(source, spool);

        var journal = IsJournal(source);
        for (var attempt = 1; ; attempt++)
        {
            FileInfo before;
            try
            {
                before = new FileInfo(source);
                if (!before.Exists) return new(BackupCaptureKind.Vanished, spool, 0, "", attempt);
                CopyTo(source, spool);
            }
            catch (FileNotFoundException) { return new(BackupCaptureKind.Vanished, spool, 0, "", attempt); }
            catch (DirectoryNotFoundException) { return new(BackupCaptureKind.Vanished, spool, 0, "", attempt); }
            afterRead?.Invoke(attempt);
            var after = new FileInfo(source);
            if (!after.Exists) return new(BackupCaptureKind.Vanished, spool, 0, "", attempt);
            var copied = new FileInfo(spool).Length;
            var stable = before.Length == after.Length
                         && before.LastWriteTimeUtc == after.LastWriteTimeUtc
                         && copied == before.Length;
            if (journal && (stable || attempt >= MaxAttempts))
            {
                TrimToLastNewline(spool);
                return Finish(BackupCaptureKind.JournalPrefix, spool, attempt);
            }
            if (stable) return Finish(BackupCaptureKind.Stable, spool, attempt);
            if (attempt >= MaxAttempts) return Finish(BackupCaptureKind.Unsettled, spool, attempt);
            Thread.Sleep(25 * attempt);
        }
    }

    /// <summary>
    /// True when a small file holds exactly a 32-byte base64 key whose
    /// fingerprint is one of <paramref name="fingerprints"/>: a copy of the
    /// secret store key or the backup key that lies inside a source never
    /// enters an archive, whatever its name.
    /// </summary>
    public static bool HoldsKeyMaterial(string path, IReadOnlyCollection<Func<byte[], bool>> fingerprints)
    {
        if (fingerprints.Count == 0) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 40 or > 256) return false;
            var text = File.ReadAllText(path, Encoding.ASCII).Trim();
            byte[] material;
            try { material = Convert.FromBase64String(text); }
            catch (FormatException) { return false; }
            try { return material.Length == 32 && fingerprints.Any(matches => matches(material)); }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static BackupCapture CaptureSqlite(string source, string spool)
    {
        if (File.Exists(spool)) File.Delete(spool);
        // Pooling off: a pooled handle would keep the source and spool files open.
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = spool, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
            using var journal = to.CreateCommand();
            // The restored copy is a single file with no WAL to carry.
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            journal.ExecuteNonQuery();
        }
        return Finish(BackupCaptureKind.SqliteSnapshot, spool, 1);
    }

    private static void CopyTo(string source, string spool)
    {
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.SequentialScan);
        using var to = new FileStream(spool, FileMode.Create, FileAccess.Write, FileShare.None);
        from.CopyTo(to);
    }

    private static void TrimToLastNewline(string spool)
    {
        using var stream = new FileStream(spool, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var length = stream.Length;
        var buffer = new byte[8192];
        var end = length;
        while (end > 0)
        {
            var start = Math.Max(0, end - buffer.Length);
            var count = (int)(end - start);
            stream.Position = start;
            stream.ReadExactly(buffer, 0, count);
            var newline = Array.LastIndexOf(buffer, (byte)'\n', count - 1, count);
            if (newline >= 0)
            {
                stream.SetLength(start + newline + 1);
                return;
            }
            end = start;
        }
        stream.SetLength(0);
    }

    private static BackupCapture Finish(BackupCaptureKind kind, string spool, int attempts)
    {
        using var stream = File.OpenRead(spool);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new(kind, spool, stream.Length, hash, attempts);
    }
}
