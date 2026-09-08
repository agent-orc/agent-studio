using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>Application boundary for the full backup set: a verified SQLite snapshot plus every cold archive payload it references.</summary>
public sealed class FullBackupManagementService(TaskServerStore store)
{
    public Task<FullBackupSummaryDto> CreateAsync(string actorId, CancellationToken ct) => store.CreateFullBackupAsync(actorId, ct);

    public Task<IReadOnlyList<FullBackupSummaryDto>> ListAsync(CancellationToken ct) => store.ListFullBackupsAsync(ct);

    public Task<VerifyFullBackupResult> VerifyAsync(string backupId, CancellationToken ct) => store.VerifyFullBackupAsync(backupId, ct);

    public Task<RestoreFullBackupResult> RestoreAsync(string backupId, string actorId, CancellationToken ct)
        => store.RestoreFullBackupAsync(backupId, actorId, ct);
}

public sealed partial class TaskServerStore
{
    private sealed record FullBackupFileEntry(string RelativePath, long Size, string Sha256);

    private sealed record FullBackupInventory(
        int SchemaVersion, DateTime CreatedAt, IReadOnlyList<FullBackupFileEntry> Files,
        int TaskCount, int ColdPayloadCount, long TotalBytes, string SetSha256, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Builds a self-contained set: the verified SQLite snapshot already produced by
    /// <see cref="CreateBackupAsync"/>, every cold payload the current archive manifests reference, and an
    /// analysis export. <c>complete.json</c> is written last so a partial set is never mistaken for a whole one.
    /// </summary>
    internal async Task<FullBackupSummaryDto> CreateFullBackupAsync(string actorId, CancellationToken ct)
    {
        var backup = await CreateBackupAsync(new BackupRequest("full-set"), actorId, ct);
        var root = Path.Combine(_options.ResolveFullBackupDirectory(), backup.BackupId);
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(backup.Path, Path.Combine(root, "snapshot.db"), overwrite: true);

            var coldSources = await ListReferencedColdPayloadsAsync(ct);
            var warnings = new List<string>();
            foreach (var source in coldSources)
            {
                if (!File.Exists(source))
                {
                    warnings.Add($"referenced cold payload is missing: {source}");
                    continue;
                }
                var relative = ColdPayloadRelativePath(source);
                var target = Path.Combine(root, "cold", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }

            var taskCount = await WriteAnalysisExportAsync(root, ct);
            var files = await InventoryFullBackupSetAsync(root, ct);
            var setHash = FullBackupSetHash(files);
            var inventory = new FullBackupInventory(
                1, UtcNow, files, taskCount,
                files.Count(file => file.RelativePath.StartsWith("cold/", StringComparison.Ordinal) && file.RelativePath.EndsWith(".zip", StringComparison.Ordinal)),
                files.Sum(file => file.Size), setHash, warnings);
            await File.WriteAllTextAsync(Path.Combine(root, "inventory.json"), JsonSerializer.Serialize(inventory, RetentionJson) + Environment.NewLine, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "complete.json"),
                JsonSerializer.Serialize(new { schemaVersion = 1, completedAt = UtcNow, setSha256 = setHash }, RetentionJson) + Environment.NewLine, ct);

            await InWriteTransactionAsync(async (connection, transaction)
                => await AuditAsync(connection, transaction, actorId, "backup-full.created", "backup-full", backup.BackupId,
                    JsonSerializer.Serialize(new { path = root, totalBytes = inventory.TotalBytes, taskCount, warnings = warnings.Count }), ct), ct);

            return new FullBackupSummaryDto(backup.BackupId, inventory.CreatedAt, inventory.TotalBytes, taskCount, inventory.ColdPayloadCount, setHash, warnings);
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal Task<IReadOnlyList<FullBackupSummaryDto>> ListFullBackupsAsync(CancellationToken ct)
    {
        var root = _options.ResolveFullBackupDirectory();
        if (!Directory.Exists(root)) return Task.FromResult<IReadOnlyList<FullBackupSummaryDto>>([]);
        var result = new List<FullBackupSummaryDto>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var completePath = Path.Combine(directory, "complete.json");
            var inventoryPath = Path.Combine(directory, "inventory.json");
            if (!File.Exists(completePath) || !File.Exists(inventoryPath)) continue;
            var inventory = JsonSerializer.Deserialize<FullBackupInventory>(File.ReadAllText(inventoryPath), RetentionJson);
            if (inventory is null) continue;
            result.Add(new FullBackupSummaryDto(
                Path.GetFileName(directory), inventory.CreatedAt, inventory.TotalBytes, inventory.TaskCount, inventory.ColdPayloadCount,
                inventory.SetSha256, inventory.Warnings));
        }
        return Task.FromResult<IReadOnlyList<FullBackupSummaryDto>>(result);
    }

    internal async Task<VerifyFullBackupResult> VerifyFullBackupAsync(string backupId, CancellationToken ct)
    {
        var root = ResolveFullBackupPath(backupId);
        var completePath = Path.Combine(root, "complete.json");
        if (!File.Exists(completePath)) throw new InvalidDataException($"Full backup '{backupId}' is incomplete: complete.json is missing.");
        var inventory = JsonSerializer.Deserialize<FullBackupInventory>(
                             await File.ReadAllTextAsync(Path.Combine(root, "inventory.json"), ct), RetentionJson)
                         ?? throw new InvalidDataException($"Full backup '{backupId}' inventory is invalid.");
        var actual = await InventoryFullBackupSetAsync(root, ct);
        var actualHash = FullBackupSetHash(actual);
        var verified = actualHash == inventory.SetSha256 && actual.SequenceEqual(inventory.Files);
        return new VerifyFullBackupResult(backupId, verified,
            new FullBackupSummaryDto(backupId, inventory.CreatedAt, inventory.TotalBytes, inventory.TaskCount, inventory.ColdPayloadCount, actualHash, inventory.Warnings));
    }

    /// <summary>
    /// Restores the snapshot and cold payloads onto this host. This does not rewrite archive pointers, so it
    /// only supports restoring back onto the same <c>ARCHIVE_PATH</c> the set was created from.
    /// </summary>
    internal async Task<RestoreFullBackupResult> RestoreFullBackupAsync(string backupId, string actorId, CancellationToken ct)
    {
        var verification = await VerifyFullBackupAsync(backupId, ct);
        if (!verification.Verified)
            return new RestoreFullBackupResult(backupId, false, "Full backup set failed verification; restore refused.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Full backup restore requires maintenance mode.");

        var root = ResolveFullBackupPath(backupId);
        var snapshotPath = Path.Combine(root, "snapshot.db");
        var restoreId = Path.GetFileNameWithoutExtension(snapshotPath) + "-" + backupId;
        var staged = Path.Combine(BackupDirectory, $"{backupId}.restore-staging.db");
        File.Copy(snapshotPath, staged, overwrite: true);
        var restoreResult = await RestoreBackupAsync(new RestoreRequest(Path.GetFileNameWithoutExtension(staged)), actorId, ct);
        File.Delete(staged);
        if (!restoreResult.Restored)
            return new RestoreFullBackupResult(backupId, false, restoreResult.Message);

        var coldRoot = Path.Combine(root, "cold");
        var archivePath = _options.ResolveRetentionArchivePath();
        if (Directory.Exists(coldRoot))
            foreach (var file in Directory.EnumerateFiles(coldRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(coldRoot, file);
                var target = Path.Combine(archivePath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

        await InWriteTransactionAsync(async (connection, transaction)
            => await AuditAsync(connection, transaction, actorId, "backup-full.restored", "backup-full", backupId, "{}", ct), ct);
        return new RestoreFullBackupResult(backupId, true, "Full backup restored. Cold payloads were copied back onto the configured archive path.");
    }

    private string ResolveFullBackupPath(string backupId)
    {
        if (!string.Equals(Path.GetFileName(backupId), backupId, StringComparison.Ordinal) || backupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Full backup id is invalid.");
        var path = Path.Combine(_options.ResolveFullBackupDirectory(), backupId);
        if (!Directory.Exists(path)) throw new KeyNotFoundException($"Full backup '{backupId}' was not found.");
        return path;
    }

    private async Task<List<string>> ListReferencedColdPayloadsAsync(CancellationToken ct)
    {
        var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT manifest_json FROM archive_manifests;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var envelope = JsonSerializer.Deserialize<RetentionManifestEnvelope>(reader.GetString(0), RetentionJson);
            if (envelope is null) continue;
            foreach (var stage in envelope.Stages) payloads.Add(stage.PayloadPath);
        }
        return payloads.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ColdPayloadRelativePath(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(parts[^3..]);
    }

    /// <summary>
    /// Best-effort v1 export: fields the current schema does not track yet (model, duration, tokens, cost)
    /// are emitted as null rather than fabricated, and documented as such in setup/task-server.md.
    /// </summary>
    private async Task<int> WriteAnalysisExportAsync(string root, CancellationToken ct)
    {
        var exportRoot = Path.Combine(root, "export");
        Directory.CreateDirectory(exportRoot);
        await using var connection = await OpenReadyAsync(ct);

        var taskCount = 0;
        await using (var writer = new StreamWriter(Path.Combine(exportRoot, "tasks.jsonl")))
        {
            await using var command = Command(connection, """
                SELECT t.id, t.task_key, p.name, t.state, t.archive_state, t.created_at, t.updated_at
                  FROM tasks t JOIN projects p ON p.id = t.project_id ORDER BY t.id;
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                taskCount++;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    taskId = reader.GetString(0),
                    taskKey = reader.GetString(1),
                    project = reader.GetString(2),
                    lane = reader.GetString(3),
                    archiveState = reader.IsDBNull(4) ? null : reader.GetString(4),
                    createdAt = reader.GetString(5),
                    updatedAt = reader.GetString(6),
                }, RetentionJson));
            }
        }

        await using (var writer = new StreamWriter(Path.Combine(exportRoot, "runs.jsonl")))
        {
            await using var command = Command(connection, """
                SELECT r.id, r.task_id, r.status, r.runner_id, r.created_at, r.started_at, r.finished_at
                  FROM runs r ORDER BY r.id;
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    runId = reader.GetString(0),
                    taskId = reader.GetString(1),
                    status = reader.GetString(2),
                    runnerId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    createdAt = reader.GetString(4),
                    startedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    finishedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                    model = (string?)null,
                    durationSeconds = (double?)null,
                    tokens = (long?)null,
                }, RetentionJson));
        }

        await using (var writer = new StreamWriter(Path.Combine(exportRoot, "reviews.jsonl")))
        {
            await using var command = Command(connection, """
                SELECT id, subject_id, status, outcome, created_at, reported_at
                  FROM review_attempts ORDER BY id;
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    attemptId = reader.GetString(0),
                    subjectId = reader.GetString(1),
                    status = reader.GetString(2),
                    outcome = reader.IsDBNull(3) ? null : reader.GetString(3),
                    createdAt = reader.GetString(4),
                    reportedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                }, RetentionJson));
        }

        return taskCount;
    }

    private static async Task<List<FullBackupFileEntry>> InventoryFullBackupSetAsync(string root, CancellationToken ct)
    {
        var result = new List<FullBackupFileEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith("inventory.json", StringComparison.OrdinalIgnoreCase)
                                    && !path.EndsWith("complete.json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            result.Add(new FullBackupFileEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, hash));
        }
        return result;
    }

    private static string FullBackupSetHash(IEnumerable<FullBackupFileEntry> files)
    {
        var content = string.Concat(files.Select(file => $"{file.RelativePath}:{file.Size}:{file.Sha256}\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
