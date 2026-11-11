using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Diagnostics;
using Serilog;

namespace AgentStudio.Pipeline;

/// <summary>The batch record families the store quarantines per record (AGT-3681).</summary>
public static class BatchGateRecordKinds
{
    public const string Manifest = "manifest";
    public const string State = "state";
    public const string Run = "run";
}

/// <summary>
/// One batch record the store could not read. <see cref="BatchId"/> and
/// <see cref="Project"/> come from the record's batch, or from what a damaged
/// manifest still names; <see cref="Project"/> is null when nothing names it.
/// </summary>
public sealed record BatchGateQuarantinedRecord(
    string BatchId, string? Project, string Kind, string File, string Error,
    DateTimeOffset DetectedAtUtc, DateTimeOffset FileWrittenAtUtc)
{
    /// <summary>The batch folder the record lives in.</summary>
    public string Folder => Kind == BatchGateRecordKinds.Manifest
        ? Path.GetDirectoryName(File) ?? File
        : Path.GetDirectoryName(Path.GetDirectoryName(File)) ?? File;

    /// <summary>
    /// What the operator does about it. A manifest or state record takes the
    /// whole batch out of recovery, planning and the panel; a run record only
    /// hides that run.
    /// </summary>
    public string NextStep => Kind == BatchGateRecordKinds.Run
        ? $"Restore this run file from a backup, or delete it if the run is not needed. "
            + $"Batch {BatchId} stays visible without this run until then."
        : $"Restore this file from a backup, or move the batch folder {Folder} out of the batch store. "
            + $"Until then the pilot skips batch {BatchId}; every other batch keeps running.";
}

/// <summary>
/// Raised when a caller reads one named batch record that is quarantined. It is
/// a <see cref="JsonException"/>, which these reads raised before AGT-3681.
/// </summary>
public sealed class BatchGateRecordUnreadableException(BatchGateQuarantinedRecord record, Exception inner)
    : JsonException($"Batch {record.Kind} record of {record.BatchId} is unreadable: {record.Error}", inner)
{
    public BatchGateQuarantinedRecord Record { get; } = record;
}

/// <summary>
/// AGT-3681: a manifest, state or run record that does not deserialize, for
/// example one an older or newer binary wrote, is quarantined per record. A
/// listing skips it, the latest state of a batch whose newest state row is
/// unreadable reads <see cref="BatchPhase.Unknown"/>, and every other batch
/// stays readable. The quarantine is a live projection of the files: a record
/// that reads again, is replaced, or is removed leaves it.
/// </summary>
public sealed partial class BatchGateStore
{
    /// <summary>The phase reason of a batch whose newest state row is unreadable.</summary>
    public const string StateUnreadableReason = "batch-state-record-unreadable";

    private sealed record QuarantineEntry(BatchGateQuarantinedRecord Record, long Length);

    private readonly ConcurrentDictionary<string, QuarantineEntry> _quarantine = new(StringComparer.Ordinal);

    /// <summary>The quarantined records whose file is still present, unchanged and current.</summary>
    public IReadOnlyList<BatchGateQuarantinedRecord> ListQuarantined()
    {
        foreach (var (path, entry) in _quarantine.ToArray())
        {
            var file = new FileInfo(path);
            if (!file.Exists || entry.Record.Kind == BatchGateRecordKinds.State && !IsLatestState(path))
            {
                _quarantine.TryRemove(path, out _);
                continue;
            }
            if (file.Length == entry.Length
                && file.LastWriteTimeUtc == entry.Record.FileWrittenAtUtc.UtcDateTime) continue;
            // The file changed on disk; it leaves the quarantine once it reads.
            try
            {
                _ = entry.Record.Kind switch
                {
                    BatchGateRecordKinds.Manifest => (object)Read<BatchGateManifest>(path),
                    BatchGateRecordKinds.State => Read<BatchGateState>(path),
                    _ => Read<BatchGateRunRecord>(path),
                };
                _quarantine.TryRemove(path, out _);
            }
            catch (Exception exception) when (IsUnreadable(exception))
            {
                Quarantine(path, entry.Record.Kind, entry.Record.BatchId, exception);
            }
        }
        return _quarantine.Values.Select(entry => entry.Record)
            .OrderBy(record => record.DetectedAtUtc).ThenBy(record => record.File, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Whether a manifest or state record of the batch is quarantined.</summary>
    public bool IsQuarantined(string batchId)
        => _quarantine.Values.Any(entry => entry.Record.BatchId == batchId
            && entry.Record.Kind != BatchGateRecordKinds.Run);

    private bool TryReadRecord<T>(string path, string kind, string? batchId, out T value) where T : class
    {
        try
        {
            value = Read<T>(path);
            if (!_quarantine.IsEmpty) _quarantine.TryRemove(path, out _);
            return true;
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            Quarantine(path, kind, batchId, exception);
            value = null!;
            return false;
        }
    }

    private T ReadRecord<T>(string path, string kind, string? batchId) where T : class
    {
        try
        {
            var value = Read<T>(path);
            if (!_quarantine.IsEmpty) _quarantine.TryRemove(path, out _);
            return value;
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            throw new BatchGateRecordUnreadableException(Quarantine(path, kind, batchId, exception), exception);
        }
    }

    // A file that vanished during the scan is a removed record, not a damaged one.
    private static bool IsUnreadable(Exception exception)
        => exception is JsonException or InvalidDataException or NotSupportedException
            or UnauthorizedAccessException
            || exception is IOException and not FileNotFoundException and not DirectoryNotFoundException;

    private BatchGateQuarantinedRecord Quarantine(string path, string kind, string? batchId, Exception exception)
    {
        var file = new FileInfo(path);
        var written = file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.MinValue;
        var length = file.Exists ? file.Length : -1;
        var error = exception.Message;
        if (_quarantine.TryGetValue(path, out var known) && known.Length == length
            && known.Record.FileWrittenAtUtc == written && known.Record.Error == error)
            return known.Record;
        var folder = kind == BatchGateRecordKinds.Manifest
            ? Path.GetDirectoryName(path)!
            : Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
        var (namedBatch, project) = Identity(kind, folder, path, batchId);
        var record = new BatchGateQuarantinedRecord(namedBatch, project, kind, path, error,
            known?.Record.DetectedAtUtc ?? DateTimeOffset.UtcNow, written);
        _quarantine[path] = new QuarantineEntry(record, length);
        if (known is null)
            Log.Warning(exception,
                "Batch gate {Kind} record {File} of batch {BatchId} (project {Project}) is unreadable and quarantined",
                kind, path, namedBatch, project ?? "unknown");
        return record;
    }

    // The batch id and project of a damaged record. A state or run record
    // belongs to a batch whose manifest usually still reads; a damaged manifest
    // often still names both before the point where it breaks off.
    private (string BatchId, string? Project) Identity(string kind, string folder, string path, string? batchId)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (kind != BatchGateRecordKinds.Manifest)
        {
            try
            {
                var manifest = Read<BatchGateManifest>(manifestPath);
                return (batchId ?? manifest.BatchId, manifest.Scope.Project);
            }
            catch (Exception exception) when (IsUnreadable(exception) || exception is FileNotFoundException)
            {
                SilentCatch.Note(exception, "Batch gate quarantine reads identity from a damaged manifest");
            }
        }
        var (scannedBatch, project) = ScanIdentity(manifestPath);
        if (scannedBatch is null && kind == BatchGateRecordKinds.Manifest)
            scannedBatch = FirstReadableStateBatch(folder);
        return (batchId ?? scannedBatch ?? Path.GetFileName(folder), project);
    }

    private string? FirstReadableStateBatch(string folder)
    {
        var states = Path.Combine(folder, "state");
        if (!Directory.Exists(states)) return null;
        foreach (var path in Directory.EnumerateFiles(states, "*.json").Order(StringComparer.Ordinal))
        {
            try { return Read<BatchGateState>(path).BatchId; }
            catch (Exception exception) when (IsUnreadable(exception))
            {
                SilentCatch.Note(exception, "Batch gate quarantine skips an unreadable state row");
            }
        }
        return null;
    }

    /// <summary>The top-level batch id and the scope's project a manifest names before it breaks off.</summary>
    internal static (string? BatchId, string? Project) ScanIdentity(string manifestPath)
    {
        string? batchId = null, project = null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(manifestPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        });
        var inScope = false;
        try
        {
            while ((batchId is null || project is null) && reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1) inScope = false;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();
                var depth = reader.CurrentDepth;
                if (!reader.Read()) break;
                if (depth == 1 && string.Equals(name, "batchId", StringComparison.OrdinalIgnoreCase)
                    && reader.TokenType == JsonTokenType.String)
                    batchId = reader.GetString();
                else if (depth == 1 && string.Equals(name, "scope", StringComparison.OrdinalIgnoreCase)
                         && reader.TokenType == JsonTokenType.StartObject)
                    inScope = true;
                else if (inScope && depth == 2 && string.Equals(name, "project", StringComparison.OrdinalIgnoreCase)
                         && reader.TokenType == JsonTokenType.String)
                    project = reader.GetString();
            }
        }
        catch (JsonException exception)
        {
            // The record breaks off here; keep what it named before.
            SilentCatch.Note(exception, "Batch gate quarantine read a damaged manifest up to where it breaks off");
        }
        return (string.IsNullOrWhiteSpace(batchId) ? null : batchId,
            string.IsNullOrWhiteSpace(project) ? null : project);
    }

    private static bool IsLatestState(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is not null && Directory.Exists(folder)
            && string.Equals(Directory.EnumerateFiles(folder, "*.json")
                .OrderBy(item => item, StringComparer.Ordinal).LastOrDefault(), path, StringComparison.Ordinal);
    }
}
