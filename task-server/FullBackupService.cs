using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Retention;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>Application boundary for verified, self-contained Task Server backup sets.</summary>
public sealed class FullBackupManagementService(TaskServerStore store)
{
    public Task<FullBackupSummaryDto> CreateAsync(string actorId, CancellationToken ct)
        => store.CreateFullBackupAsync(actorId, ct);

    public Task<IReadOnlyList<FullBackupSummaryDto>> ListAsync(CancellationToken ct)
        => store.ListFullBackupsAsync(ct);

    public Task<VerifyFullBackupResult> VerifyAsync(string backupId, CancellationToken ct)
        => store.VerifyFullBackupAsync(backupId, ct);

    public Task<RestoreFullBackupResult> RestoreAsync(string backupId, string actorId, CancellationToken ct)
        => store.RestoreFullBackupAsync(backupId, actorId, ct);

    internal Task<int> ThinAsync(FullBackupRetentionPolicy policy, string actorId, CancellationToken ct)
        => store.ThinFullBackupsAsync(policy, actorId, ct);
}

public sealed partial class TaskServerStore
{
    private sealed record FullBackupFileEntry(string RelativePath, long Size, string Sha256);

    private sealed record FullBackupInventory(
        int SchemaVersion,
        DateTime CreatedAt,
        IReadOnlyList<FullBackupFileEntry> Files,
        int TaskCount,
        int ColdPayloadCount,
        long TotalBytes,
        string SetSha256,
        IReadOnlyList<string> Warnings);

    private sealed record FullBackupCompletion(int SchemaVersion, DateTime CompletedAt, string SetSha256);

    private sealed record FullBackupArchiveManifest(
        int SchemaVersion,
        string TaskId,
        DateTime ArchivedAt,
        string State,
        DateTime? RestoredAt,
        DateTimeOffset? TombstonedAt,
        long TotalBytes,
        IReadOnlyList<FullBackupArchiveStage> Stages);

    private sealed record FullBackupArchiveStage(
        int Stage,
        DateTimeOffset ArchivedAt,
        string? RelativePayloadPath,
        string PayloadSha256,
        long TotalBytes,
        IReadOnlyList<ArchiveManifestFile> Files,
        int PolicyVersion,
        string ArchivedBy);

    /// <summary>
    /// Writes the SQLite snapshot first, then derives every other member from that immutable snapshot.
    /// <c>complete.json</c> is written last, so interrupted sets are never listed as restorable.
    /// </summary>
    internal async Task<FullBackupSummaryDto> CreateFullBackupAsync(string actorId, CancellationToken ct)
    {
        var backup = await CreateBackupAsync(new BackupRequest("full-set"), actorId, ct);
        var root = Path.Combine(_options.ResolveFullBackupDirectory(), backup.BackupId);
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.db");
            File.Move(backup.Path, snapshotPath);

            var manifests = await ReadBackupArchiveManifestsAsync(snapshotPath, ct);
            await WriteBackupArchiveManifestsAsync(root, manifests, ct);
            await CopyAndVerifyColdPayloadsAsync(root, manifests, ct);
            var taskCount = await WriteAnalysisExportAsync(root, snapshotPath, ct);

            var files = await InventoryFullBackupSetAsync(root, ct);
            var setHash = FullBackupSetHash(files);
            var inventory = new FullBackupInventory(
                1,
                UtcNow,
                files,
                taskCount,
                manifests.SelectMany(item => item.Stages)
                    .Where(stage => stage.RelativePayloadPath is not null)
                    .Select(stage => stage.RelativePayloadPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                files.Sum(file => file.Size),
                setHash,
                []);
            await File.WriteAllTextAsync(
                Path.Combine(root, "inventory.json"),
                JsonSerializer.Serialize(inventory, RetentionJson) + Environment.NewLine,
                ct);
            await File.WriteAllTextAsync(
                Path.Combine(root, "complete.json"),
                JsonSerializer.Serialize(new FullBackupCompletion(1, UtcNow, setHash), RetentionJson) + Environment.NewLine,
                ct);

            await InWriteTransactionAsync(
                async (connection, transaction) => await AuditAsync(
                    connection,
                    transaction,
                    actorId,
                    "backup-full.created",
                    "backup-full",
                    backup.BackupId,
                    JsonSerializer.Serialize(new
                    {
                        path = root,
                        inventory.TotalBytes,
                        taskCount,
                        inventory.ColdPayloadCount,
                    }),
                    ct),
                ct);

            return ToFullBackupSummary(backup.BackupId, inventory);
        }
        catch
        {
            var snapshotPath = Path.Combine(root, "snapshot.db");
            if (File.Exists(snapshotPath) && !File.Exists(backup.Path))
                File.Move(snapshotPath, backup.Path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal Task<IReadOnlyList<FullBackupSummaryDto>> ListFullBackupsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = _options.ResolveFullBackupDirectory();
        if (!Directory.Exists(root)) return Task.FromResult<IReadOnlyList<FullBackupSummaryDto>>([]);

        var result = new List<FullBackupSummaryDto>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var completePath = Path.Combine(directory, "complete.json");
            var inventoryPath = Path.Combine(directory, "inventory.json");
            if (!File.Exists(completePath) || !File.Exists(inventoryPath)) continue;
            var inventory = JsonSerializer.Deserialize<FullBackupInventory>(File.ReadAllText(inventoryPath), RetentionJson);
            if (inventory is not null) result.Add(ToFullBackupSummary(Path.GetFileName(directory), inventory));
        }
        return Task.FromResult<IReadOnlyList<FullBackupSummaryDto>>(result);
    }

    internal async Task<VerifyFullBackupResult> VerifyFullBackupAsync(string backupId, CancellationToken ct)
    {
        var root = ResolveFullBackupPath(backupId);
        var completePath = Path.Combine(root, "complete.json");
        var inventoryPath = Path.Combine(root, "inventory.json");
        if (!File.Exists(completePath) || !File.Exists(inventoryPath))
            throw new InvalidDataException($"Full backup '{backupId}' is incomplete.");

        var completion = JsonSerializer.Deserialize<FullBackupCompletion>(
                             await File.ReadAllTextAsync(completePath, ct), RetentionJson)
                         ?? throw new InvalidDataException($"Full backup '{backupId}' completion marker is invalid.");
        var inventory = JsonSerializer.Deserialize<FullBackupInventory>(
                            await File.ReadAllTextAsync(inventoryPath, ct), RetentionJson)
                        ?? throw new InvalidDataException($"Full backup '{backupId}' inventory is invalid.");
        var actual = await InventoryFullBackupSetAsync(root, ct);
        var actualHash = FullBackupSetHash(actual);
        var verified = string.Equals(actualHash, inventory.SetSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(actualHash, completion.SetSha256, StringComparison.OrdinalIgnoreCase)
                       && actual.SequenceEqual(inventory.Files);
        return new VerifyFullBackupResult(backupId, verified, ToFullBackupSummary(backupId, inventory) with { SetSha256 = actualHash });
    }

    /// <summary>
    /// Restores the database and replaces the configured archive tree as one recoverable operation. Manifest
    /// paths are rewritten for the receiving host, so a verified set can restore onto a different archive root.
    /// </summary>
    internal async Task<RestoreFullBackupResult> RestoreFullBackupAsync(string backupId, string actorId, CancellationToken ct)
    {
        var verification = await VerifyFullBackupAsync(backupId, ct);
        if (!verification.Verified)
            return new RestoreFullBackupResult(backupId, false, "Full backup set failed verification; restore refused.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Full backup restore requires maintenance mode.");

        var root = ResolveFullBackupPath(backupId);
        var manifests = await ReadBackupManifestFilesAsync(root, ct);
        var archiveRoot = ValidateManagedArchiveRoot(_options.ResolveRetentionArchivePath());
        var stagedArchive = archiveRoot + $".restore-{Guid.NewGuid():N}";
        var previousArchive = archiveRoot + $".pre-restore-{Guid.NewGuid():N}";
        var stagedSnapshot = Path.Combine(BackupDirectory, $"{backupId}.restore-staging.db");
        BackupResult? rollbackBackup = null;
        var databaseRestored = false;
        var archiveMoved = false;
        var previousArchiveMoved = false;

        try
        {
            Directory.CreateDirectory(stagedArchive);
            CopyDirectory(Path.Combine(root, "cold"), stagedArchive);
            rollbackBackup = await CreateBackupAsync(new BackupRequest("before-full-restore"), actorId, ct);
            File.Copy(Path.Combine(root, "snapshot.db"), stagedSnapshot, overwrite: true);
            var restore = await RestoreBackupAsync(
                new RestoreRequest(Path.GetFileNameWithoutExtension(stagedSnapshot)), actorId, ct);
            if (!restore.Restored)
                return new RestoreFullBackupResult(backupId, false, restore.Message);
            databaseRestored = true;

            await RewriteRestoredArchiveManifestPathsAsync(manifests, archiveRoot, ct);

            if (Directory.Exists(archiveRoot))
            {
                Directory.Move(archiveRoot, previousArchive);
                previousArchiveMoved = true;
            }
            Directory.Move(stagedArchive, archiveRoot);
            archiveMoved = true;

            await InWriteTransactionAsync(
                async (connection, transaction) => await AuditAsync(
                    connection, transaction, actorId, "backup-full.restored", "backup-full", backupId,
                    JsonSerializer.Serialize(new { archiveRoot, manifests = manifests.Count }), ct),
                ct);
            if (previousArchiveMoved && Directory.Exists(previousArchive)) Directory.Delete(previousArchive, recursive: true);
            return new RestoreFullBackupResult(backupId, true, "Full backup restored and archive manifest paths were rebound to this host.");
        }
        catch (Exception restoreException)
        {
            try
            {
                if (archiveMoved && Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
                if (previousArchiveMoved && Directory.Exists(previousArchive)) Directory.Move(previousArchive, archiveRoot);
                if (databaseRestored && rollbackBackup is not null)
                    await RestoreBackupAsync(new RestoreRequest(rollbackBackup.BackupId), actorId + "-rollback", ct);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Full backup restore and rollback both failed.", restoreException, rollbackException);
            }
            throw;
        }
        finally
        {
            if (File.Exists(stagedSnapshot)) File.Delete(stagedSnapshot);
            if (Directory.Exists(stagedArchive)) Directory.Delete(stagedArchive, recursive: true);
            if (Directory.Exists(previousArchive) && !previousArchiveMoved) Directory.Delete(previousArchive, recursive: true);
        }
    }

    internal async Task<int> ThinFullBackupsAsync(FullBackupRetentionPolicy policy, string actorId, CancellationToken ct)
    {
        policy.Validate();
        var backups = (await ListFullBackupsAsync(ct)).OrderByDescending(item => item.CreatedAt).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var backup in backups.Take(policy.Daily)) keep.Add(backup.Id);
        foreach (var backup in backups
                     .GroupBy(item => (ISOWeek.GetYear(item.CreatedAt), ISOWeek.GetWeekOfYear(item.CreatedAt)))
                     .Select(group => group.First())
                     .Take(policy.Weekly))
            keep.Add(backup.Id);
        foreach (var backup in backups
                     .GroupBy(item => (item.CreatedAt.Year, item.CreatedAt.Month))
                     .Select(group => group.First())
                     .Take(policy.Monthly))
            keep.Add(backup.Id);

        var removed = 0;
        foreach (var backup in backups.Where(item => !keep.Contains(item.Id)))
        {
            var path = ResolveFullBackupPath(backup.Id);
            Directory.Delete(path, recursive: true);
            removed++;
        }
        if (removed > 0)
            await InWriteTransactionAsync(
                async (connection, transaction) => await AuditAsync(
                    connection, transaction, actorId, "backup-full.thinned", "backup-full", "policy",
                    JsonSerializer.Serialize(new { removed, policy.Daily, policy.Weekly, policy.Monthly }), ct),
                ct);
        return removed;
    }

    private string ResolveFullBackupPath(string backupId)
    {
        if (!string.Equals(Path.GetFileName(backupId), backupId, StringComparison.Ordinal)
            || backupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Full backup id is invalid.");
        var fullBackupRoot = Path.GetFullPath(_options.ResolveFullBackupDirectory()) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullBackupRoot, backupId));
        if (!path.StartsWith(fullBackupRoot, StringComparison.Ordinal))
            throw new ArgumentException("Full backup id resolves outside the full backup root.");
        if (!Directory.Exists(path)) throw new KeyNotFoundException($"Full backup '{backupId}' was not found.");
        return path;
    }

    private async Task<List<FullBackupArchiveManifest>> ReadBackupArchiveManifestsAsync(string snapshotPath, CancellationToken ct)
    {
        var archiveRoot = Path.GetFullPath(_options.ResolveRetentionArchivePath());
        var manifests = new List<FullBackupArchiveManifest>();
        await using var connection = await OpenSnapshotAsync(snapshotPath, ct);
        await using var command = Command(connection, """
            SELECT task_id, archived_at, manifest_json, state, restored_at, total_bytes
              FROM archive_manifests ORDER BY task_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var envelope = JsonSerializer.Deserialize<RetentionManifestEnvelope>(reader.GetString(2), RetentionJson)
                           ?? throw new InvalidDataException($"Archive manifest for task '{reader.GetString(0)}' is invalid.");
            var stages = envelope.Stages.Select(stage => new FullBackupArchiveStage(
                stage.Stage,
                stage.ArchivedAt,
                string.IsNullOrWhiteSpace(stage.PayloadPath) ? null : RelativeArchivePayloadPath(archiveRoot, stage.PayloadPath),
                stage.PayloadSha256,
                stage.TotalBytes,
                stage.Files,
                stage.PolicyVersion,
                stage.ArchivedBy)).ToList();
            manifests.Add(new FullBackupArchiveManifest(
                1,
                reader.GetString(0),
                Parse(reader.GetString(1)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                envelope.TombstonedAt,
                reader.GetInt64(5),
                stages));
        }
        return manifests;
    }

    private static async Task WriteBackupArchiveManifestsAsync(
        string root,
        IReadOnlyList<FullBackupArchiveManifest> manifests,
        CancellationToken ct)
    {
        var manifestRoot = Path.Combine(root, "manifests");
        Directory.CreateDirectory(manifestRoot);
        foreach (var manifest in manifests)
            await File.WriteAllTextAsync(
                Path.Combine(manifestRoot, ManifestFileName(manifest.TaskId)),
                JsonSerializer.Serialize(manifest, RetentionJson) + Environment.NewLine,
                ct);
    }

    private async Task<List<FullBackupArchiveManifest>> ReadBackupManifestFilesAsync(string root, CancellationToken ct)
    {
        var manifestRoot = Path.Combine(root, "manifests");
        if (!Directory.Exists(manifestRoot)) return [];
        var result = new List<FullBackupArchiveManifest>();
        foreach (var path in Directory.EnumerateFiles(manifestRoot, "*.json").Order(StringComparer.Ordinal))
            result.Add(JsonSerializer.Deserialize<FullBackupArchiveManifest>(await File.ReadAllTextAsync(path, ct), RetentionJson)
                       ?? throw new InvalidDataException($"Full backup manifest '{Path.GetFileName(path)}' is invalid."));
        return result;
    }

    private async Task CopyAndVerifyColdPayloadsAsync(
        string root,
        IReadOnlyList<FullBackupArchiveManifest> manifests,
        CancellationToken ct)
    {
        var archiveRoot = Path.GetFullPath(_options.ResolveRetentionArchivePath());
        foreach (var stage in manifests.SelectMany(item => item.Stages)
                     .Where(stage => stage.RelativePayloadPath is not null)
                     .DistinctBy(stage => stage.RelativePayloadPath, StringComparer.OrdinalIgnoreCase))
        {
            var source = Path.Combine(archiveRoot, stage.RelativePayloadPath!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) throw new InvalidDataException($"Referenced cold payload is missing: {source}");
            var sourceHash = await HashFileAsync(source, ct);
            if (!string.Equals(sourceHash, stage.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Referenced cold payload hash mismatch: {source}");
            var target = Path.Combine(root, "cold", stage.RelativePayloadPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private async Task RewriteRestoredArchiveManifestPathsAsync(
        IReadOnlyList<FullBackupArchiveManifest> manifests,
        string archiveRoot,
        CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            foreach (var manifest in manifests)
            {
                var stages = manifest.Stages.Select(stage => new RetentionManifestStage(
                    stage.Stage,
                    stage.ArchivedAt,
                    stage.RelativePayloadPath is null
                        ? string.Empty
                        : Path.Combine(archiveRoot, stage.RelativePayloadPath.Replace('/', Path.DirectorySeparatorChar)),
                    stage.PayloadSha256,
                    stage.TotalBytes,
                    stage.Files,
                    stage.PolicyVersion,
                    stage.ArchivedBy)).ToList();
                var envelope = new RetentionManifestEnvelope(stages, manifest.TombstonedAt);
                var latest = stages.LastOrDefault(stage => !string.IsNullOrWhiteSpace(stage.PayloadPath));
                var affected = await ExecuteAsync(connection, """
                    UPDATE archive_manifests
                       SET manifest_json = $json,
                           payload_path = $path,
                           payload_sha256 = $sha,
                           total_bytes = $bytes,
                           state = $state,
                           restored_at = $restored
                     WHERE task_id = $task;
                    """, ct, transaction,
                    ("$json", JsonSerializer.Serialize(envelope, RetentionJson)),
                    ("$path", latest?.PayloadPath ?? string.Empty),
                    ("$sha", latest?.PayloadSha256 ?? manifest.Stages.LastOrDefault()?.PayloadSha256 ?? string.Empty),
                    ("$bytes", manifest.TotalBytes),
                    ("$state", manifest.State),
                    ("$restored", manifest.RestoredAt is null ? null : Iso(manifest.RestoredAt.Value)),
                    ("$task", manifest.TaskId));
                if (affected != 1)
                    throw new InvalidDataException($"Restored archive manifest task '{manifest.TaskId}' is missing from the snapshot.");
            }
        }, ct);
    }

    /// <summary>
    /// Export schema v1 records every durable field available today. Run model and token attribution is task
    /// context scoped because the Task Server does not yet persist a run-to-model-usage relation. Monetary
    /// cost is null until a pricing ledger is introduced; raw token totals remain available per project.
    /// </summary>
    private static async Task<int> WriteAnalysisExportAsync(string root, string snapshotPath, CancellationToken ct)
    {
        var exportRoot = Path.Combine(root, "export");
        Directory.CreateDirectory(exportRoot);
        await using var connection = await OpenSnapshotAsync(snapshotPath, ct);

        var taskCount = await WriteJsonLinesAsync(connection, Path.Combine(exportRoot, "tasks.jsonl"), """
            SELECT json_object(
                'schemaVersion', 1, 'taskId', t.id, 'taskKey', t.task_key, 'projectId', p.id,
                'project', p.name, 'lane', t.state, 'archiveState', t.archive_state,
                'createdAt', t.created_at, 'updatedAt', t.updated_at)
              FROM tasks t JOIN projects p ON p.id = t.project_id ORDER BY t.id;
            """, ct);
        await WriteJsonLinesAsync(connection, Path.Combine(exportRoot, "runs.jsonl"), """
            SELECT json_object(
                'schemaVersion', 1, 'runId', r.id, 'taskId', r.task_id, 'projectId', p.id,
                'status', r.status, 'runnerId', r.runner_id, 'createdAt', r.created_at,
                'startedAt', r.started_at, 'finishedAt', r.finished_at,
                'durationSeconds', CASE WHEN r.started_at IS NULL OR r.finished_at IS NULL THEN NULL
                    ELSE round((julianday(r.finished_at) - julianday(r.started_at)) * 86400.0, 3) END,
                'models', (SELECT json_group_array(DISTINCT oct.model)
                    FROM orchestrator_contexts oc JOIN orchestrator_context_turns oct ON oct.context_key = oc.context_key
                    WHERE oc.task_id = r.task_id AND oct.model IS NOT NULL),
                'inputTokens', coalesce((SELECT sum(oct.input_tokens) FROM orchestrator_contexts oc
                    JOIN orchestrator_context_turns oct ON oct.context_key = oc.context_key WHERE oc.task_id = r.task_id), 0),
                'outputTokens', coalesce((SELECT sum(oct.output_tokens) FROM orchestrator_contexts oc
                    JOIN orchestrator_context_turns oct ON oct.context_key = oc.context_key WHERE oc.task_id = r.task_id), 0),
                'tokenAttribution', 'task-context')
              FROM runs r JOIN tasks t ON t.id = r.task_id JOIN projects p ON p.id = t.project_id ORDER BY r.id;
            """, ct);
        await WriteJsonLinesAsync(connection, Path.Combine(exportRoot, "reviews.jsonl"), """
            SELECT json_object(
                'schemaVersion', 1, 'attemptId', ra.id, 'subjectId', ra.subject_id,
                'taskId', ra.task_id, 'projectId', p.id, 'status', ra.status,
                'verdict', ra.outcome, 'createdAt', ra.created_at, 'reportedAt', ra.reported_at)
              FROM review_attempts ra JOIN tasks t ON t.id = ra.task_id
              JOIN projects p ON p.id = t.project_id ORDER BY ra.id;
            """, ct);
        await WriteJsonLinesAsync(connection, Path.Combine(exportRoot, "integrations.jsonl"), """
            SELECT json_object(
                'schemaVersion', 1, 'runId', h.run_id, 'taskId', h.task_id, 'projectId', p.id,
                'repositoryId', h.repository_id, 'repositoryUrl', h.repository_url,
                'baseSha', h.base_sha, 'resultSha', h.result_sha,
                'immutableRemoteRef', h.immutable_remote_ref, 'acknowledgedAt', h.acknowledged_at,
                'retainUntil', h.retain_until)
              FROM result_handoffs h JOIN tasks t ON t.id = h.task_id
              JOIN projects p ON p.id = t.project_id ORDER BY h.run_id;
            """, ct);
        await WriteJsonLinesAsync(connection, Path.Combine(exportRoot, "project-costs.jsonl"), """
            SELECT json_object(
                'schemaVersion', 1, 'projectId', p.id, 'project', p.name,
                'inputTokens', coalesce(sum(oct.input_tokens), 0),
                'outputTokens', coalesce(sum(oct.output_tokens), 0),
                'cacheReadTokens', coalesce(sum(oct.cache_read_tokens), 0),
                'cacheCreationTokens', coalesce(sum(oct.cache_creation_tokens), 0),
                'estimatedCostUsd', NULL, 'pricingStatus', 'not-recorded-by-task-server-v1')
              FROM projects p LEFT JOIN orchestrator_contexts oc ON oc.project_id = p.id
              LEFT JOIN orchestrator_context_turns oct ON oct.context_key = oc.context_key
             GROUP BY p.id, p.name ORDER BY p.id;
            """, ct);
        return taskCount;
    }

    private static async Task<int> WriteJsonLinesAsync(
        SqliteConnection connection,
        string path,
        string sql,
        CancellationToken ct)
    {
        var count = 0;
        await using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        await using var command = Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            count++;
            await writer.WriteLineAsync(reader.GetString(0).AsMemory(), ct);
        }
        return count;
    }

    private static async Task<SqliteConnection> OpenSnapshotAsync(string snapshotPath, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", ct);
        return connection;
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
            result.Add(new FullBackupFileEntry(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                hash));
        }
        return result;
    }

    private static string FullBackupSetHash(IEnumerable<FullBackupFileEntry> files)
    {
        var content = string.Concat(files.Select(file => $"{file.RelativePath}:{file.Size}:{file.Sha256}\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static string RelativeArchivePayloadPath(string archiveRoot, string payloadPath)
    {
        var relative = Path.GetRelativePath(archiveRoot, Path.GetFullPath(payloadPath)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive payload is outside the configured archive root: {payloadPath}");
        return relative;
    }

    private static string ManifestFileName(string taskId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskId))) + ".json";

    private static string ValidateManagedArchiveRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(full);
        if (parent?.Parent is null)
            throw new InvalidOperationException($"Configured archive path is too broad for full restore: {full}");
        return full;
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static FullBackupSummaryDto ToFullBackupSummary(string id, FullBackupInventory inventory)
        => new(id, inventory.CreatedAt, inventory.TotalBytes, inventory.TaskCount, inventory.ColdPayloadCount,
            inventory.SetSha256, inventory.Warnings);
}
