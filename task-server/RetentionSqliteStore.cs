using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Retention;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// The Phase B adapter for <see cref="IRetentionStore"/>: it runs the same classifier, planner, and executor
/// as the legacy file-tree adapter, but against the artifacts table instead of a task folder. Class A rows
/// (tasks, events, leases, audit, review attempts) are never enumerated here, so the planner and executor can
/// never touch them.
/// </summary>
public sealed class SqliteRetentionStore(TaskServerStore store) : IRetentionStore
{
    public Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(CancellationToken cancellationToken = default)
        => store.EnumerateRetentionInventoryAsync(cancellationToken);

    public Task<Stream> ReadFileAsync(RetentionTaskInventory task, string relativePath, CancellationToken cancellationToken = default)
        => store.ReadRetentionArtifactContentAsync(relativePath, cancellationToken);

    public Task WriteManifestAsync(RetentionTaskInventory task, ArchiveManifest manifest, CancellationToken cancellationToken = default)
        => store.AppendRetentionManifestAsync(task.StoreKey, manifest, cancellationToken);

    public Task<ArchiveTransition?> MoveToColdAsync(RetentionAction action, RetentionPolicy policy, CancellationToken cancellationToken = default)
        => store.MoveRetentionActionToColdAsync(action, cancellationToken);

    public Task WriteStubAsync(RetentionTaskInventory task, ArchivePointer pointer, CancellationToken cancellationToken = default)
        => store.SetTaskArchiveStubAsync(task.StoreKey, pointer, cancellationToken);

    /// <summary>
    /// Class D runtime data (bus logs, attempt-authority rotation) has no artifact-row representation in the
    /// SQLite store; it only exists in the legacy file tree. There is nothing here to delete.
    /// </summary>
    public Task DeleteRuntimeAsync(RetentionAction action, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RestoreAsync(string taskKey, CancellationToken cancellationToken = default)
        => store.RestoreRetentionTaskAsync(taskKey, cancellationToken);
}

/// <summary>One cold-payload transition recorded for a task; <see cref="Files"/> use the composite artifact key.</summary>
public sealed record RetentionManifestStage(
    int Stage,
    DateTimeOffset ArchivedAt,
    string PayloadPath,
    string PayloadSha256,
    long TotalBytes,
    IReadOnlyList<ArchiveManifestFile> Files);

public sealed record RetentionManifestEnvelope(IReadOnlyList<RetentionManifestStage> Stages);

/// <summary>
/// <see cref="RetentionRule.NeverArchiveLanes"/> is typed as <see cref="IReadOnlySet{T}"/>, which
/// System.Text.Json cannot instantiate on its own for deserialization.
/// </summary>
internal sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new HashSet<string>(JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [], StringComparer.OrdinalIgnoreCase);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToList(), options);
}

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions RetentionJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new ReadOnlyStringSetJsonConverter() },
    };

    /// <summary>
    /// Composite artifact identity encoded as a retention file path: classification and every existing
    /// path-pattern check in <see cref="ArtifactClassifier"/> operate on suffixes and segments, so embedding
    /// the run and artifact id as leading segments leaves every existing check unaffected.
    /// </summary>
    private static string RetentionFileKey(string runId, string artifactId, string name)
        => $"{runId}/{artifactId}/{name.Replace('\\', '/').TrimStart('/')}";

    private static (string RunId, string ArtifactId, string Name) ParseRetentionFileKey(string relativePath)
    {
        var parts = relativePath.Split('/', 3);
        if (parts.Length != 3)
            throw new InvalidOperationException($"Malformed retention file key: {relativePath}");
        return (parts[0], parts[1], parts[2]);
    }

    private static bool IsTerminalTaskState(string state)
        => state.StartsWith("6-", StringComparison.OrdinalIgnoreCase)
           || state.StartsWith("7-", StringComparison.OrdinalIgnoreCase);

    internal async Task<IReadOnlyList<RetentionTaskInventory>> EnumerateRetentionInventoryAsync(CancellationToken ct)
    {
        var classifier = new ArtifactClassifier();
        var tasks = new Dictionary<string, (string TaskKey, string Project, string State, DateTime UpdatedAt, List<RetentionFile> Files)>(StringComparer.Ordinal);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.id, t.task_key, p.name, t.state, t.updated_at,
                   a.run_id, a.id, a.name, a.size_bytes, a.created_at
              FROM tasks t
              JOIN projects p ON p.id = t.project_id
              LEFT JOIN runs r ON r.task_id = t.id
              LEFT JOIN artifacts a ON a.run_id = r.id AND a.archived = 0
             ORDER BY t.id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var taskId = reader.GetString(0);
            if (!tasks.TryGetValue(taskId, out var entry))
                tasks[taskId] = entry = (reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)), []);
            if (reader.IsDBNull(6)) continue;
            var key = RetentionFileKey(reader.GetString(5), reader.GetString(6), reader.GetString(7));
            entry.Files.Add(new RetentionFile(key, reader.GetInt64(8), new DateTimeOffset(Parse(reader.GetString(9))), classifier.Classify(reader.GetString(7))));
        }
        return tasks.Select(pair =>
        {
            var (taskKey, project, state, updatedAt, files) = pair.Value;
            DateTimeOffset? terminalAt = IsTerminalTaskState(state) ? new DateTimeOffset(updatedAt) : null;
            return new RetentionTaskInventory(project, taskKey, pair.Key, state, terminalAt, pair.Key, files);
        }).ToList();
    }

    internal static IReadOnlyList<RetentionTaskInventory> FilterRetentionInventory(
        IReadOnlyList<RetentionTaskInventory> inventory, string? project, string? taskKey)
        => inventory.Where(item =>
                (string.IsNullOrWhiteSpace(project) || string.Equals(item.Project, project, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(taskKey) || string.Equals(item.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    internal async Task<Stream> ReadRetentionArtifactContentAsync(string relativePath, CancellationToken ct)
    {
        var (_, artifactId, _) = ParseRetentionFileKey(relativePath);
        await using var connection = await OpenReadyAsync(ct);
        var content = await ScalarAsync(connection, "SELECT content FROM artifacts WHERE id = $id;", ct, ("$id", artifactId));
        if (content is null or DBNull)
            throw new InvalidOperationException($"Artifact '{artifactId}' has no hot content.");
        return new MemoryStream((byte[])content);
    }

    internal async Task<ArchiveTransition?> MoveRetentionActionToColdAsync(RetentionAction action, CancellationToken ct)
    {
        if (action.Files.Count == 0) return null;
        var keys = action.Files.Select(file => (File: file, Parsed: ParseRetentionFileKey(file.RelativePath))).ToList();
        var contents = await ReadRetentionArtifactContentsAsync(keys.Select(item => item.Parsed.ArtifactId), ct);
        if (contents.Count == 0) return null;

        var archivePath = _options.ResolveRetentionArchivePath();
        var archivedAt = UtcNow;
        var directory = RetentionArchiveDirectory(archivePath, action.Task.Project, action.Task.TaskKey, archivedAt);
        Directory.CreateDirectory(directory);
        var payloadPath = Path.Combine(directory, "payload.zip");
        var manifestFiles = new List<ArchiveManifestFile>();
        var archivedArtifactIds = new List<string>();
        try
        {
            await using (var payload = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (file, parsed) in keys.OrderBy(item => item.Parsed.ArtifactId, StringComparer.Ordinal))
                {
                    if (!contents.TryGetValue(parsed.ArtifactId, out var found)) continue;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(found));
                    manifestFiles.Add(new ArchiveManifestFile(file.RelativePath, found.LongLength, hash));
                    var entry = zip.CreateEntry(file.RelativePath, CompressionLevel.SmallestSize);
                    await using var output = entry.Open();
                    await output.WriteAsync(found, ct);
                    archivedArtifactIds.Add(parsed.ArtifactId);
                }
            }
            if (manifestFiles.Count == 0)
            {
                Directory.Delete(directory, recursive: true);
                return null;
            }

            var payloadHash = await HashFileAsync(payloadPath, ct);
            var totalBytes = manifestFiles.Sum(file => file.Size);
            var manifest = new ArchiveManifest
            {
                TaskKey = action.Task.TaskKey,
                Id = action.Task.Id,
                Project = action.Task.Project,
                Lane = action.Task.Lane,
                TerminalAt = action.Task.TerminalAt,
                RuleIds = [action.RuleId],
                Files = manifestFiles,
                TotalBytes = totalBytes,
                PayloadSha256 = payloadHash,
                ArchivedAt = archivedAt,
                ArchivedBy = "retention-engine",
                Stage = action.Stage,
            };

            byte[]? excerptContent = null;
            string? excerptRunId = null;
            if (action.Files.Any(file => file.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData))
            {
                var excerpt = await BuildRetentionExcerptAsync(action, contents, ct);
                if (excerpt is not null)
                {
                    excerptRunId = excerpt.Value.RunId;
                    excerptContent = excerpt.Value.Content;
                }
            }

            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                await UpsertArchiveManifestStageAsync(connection, transaction, action.Task.StoreKey, manifest, payloadPath, ct);
                if (excerptContent is not null && excerptRunId is not null)
                    await UpsertRetentionExcerptArtifactAsync(connection, transaction, action.Task.StoreKey, excerptRunId, action.Stage, excerptContent, ct);
                if (action.Kind == RetentionActionKind.ArchiveTask)
                    await ExecuteAsync(connection, "UPDATE tasks SET archive_state = 'archived', archived_at = $at WHERE id = $id;", ct, transaction,
                        ("$at", Iso(archivedAt)), ("$id", action.Task.StoreKey));
            }, ct);

            // The cold payload and its manifest row are durable before any hot content is cleared, so a crash
            // between these two steps leaves both copies intact rather than neither.
            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                foreach (var artifactId in archivedArtifactIds)
                    await ExecuteAsync(connection, "UPDATE artifacts SET content = NULL, archived = 1 WHERE id = $id;", ct, transaction, ("$id", artifactId));
            }, ct);

            return new ArchiveTransition(directory, payloadPath, archivedAt, action.Stage, totalBytes);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal async Task AppendRetentionManifestAsync(string taskId, ArchiveManifest manifest, CancellationToken ct)
    {
        var archivePath = _options.ResolveRetentionArchivePath();
        var directory = RetentionArchiveDirectory(archivePath, manifest.Project, manifest.TaskKey, manifest.ArchivedAt);
        var payloadPath = Path.Combine(directory, "payload.zip");
        await InWriteTransactionAsync((connection, transaction)
            => UpsertArchiveManifestStageAsync(connection, transaction, taskId, manifest, payloadPath, ct), ct);
    }

    internal async Task SetTaskArchiveStubAsync(string taskId, ArchivePointer pointer, CancellationToken ct)
    {
        var archived = pointer.Archives.Any(transition => transition.Stage >= 2);
        await InWriteTransactionAsync(async (connection, transaction)
            => await ExecuteAsync(connection, "UPDATE tasks SET archive_state = $state, archived_at = $at WHERE id = $id;", ct, transaction,
                ("$state", archived ? "archived" : null),
                ("$at", archived ? Iso(pointer.ArchivedAt.UtcDateTime) : null),
                ("$id", taskId)), ct);
    }

    internal async Task RestoreRetentionTaskAsync(string taskKey, CancellationToken ct)
    {
        string taskId;
        RetentionManifestEnvelope envelope;
        await using (var connection = await OpenReadyAsync(ct))
        {
            taskId = Convert.ToString(
                    await ScalarAsync(connection, "SELECT id FROM tasks WHERE task_key = upper($key);", ct, ("$key", taskKey)),
                    CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException($"Task '{taskKey}' was not found.");
            envelope = await ReadManifestEnvelopeAsync(connection, null, taskId, ct)
                ?? throw new InvalidOperationException($"Task '{taskKey}' is not archived.");
        }

        foreach (var stage in envelope.Stages.OrderBy(item => item.ArchivedAt))
        {
            if (!File.Exists(stage.PayloadPath))
                throw new InvalidDataException($"Cold payload is missing: {stage.PayloadPath}");
            var payloadHash = await HashFileAsync(stage.PayloadPath, ct);
            if (!string.Equals(payloadHash, stage.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Payload hash mismatch for {stage.PayloadPath}");

            using var zip = ZipFile.OpenRead(stage.PayloadPath);
            var restored = new List<(string ArtifactId, byte[] Content)>();
            foreach (var file in stage.Files)
            {
                var entry = zip.GetEntry(file.RelativePath) ?? throw new InvalidDataException($"Archive entry is missing: {file.RelativePath}");
                await using var entryStream = entry.Open();
                using var memory = new MemoryStream();
                await entryStream.CopyToAsync(memory, ct);
                var bytes = memory.ToArray();
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Restored file hash mismatch: {file.RelativePath}");
                var (_, artifactId, _) = ParseRetentionFileKey(file.RelativePath);
                restored.Add((artifactId, bytes));
            }

            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                foreach (var (artifactId, content) in restored)
                    await ExecuteAsync(connection, "UPDATE artifacts SET content = $content, archived = 0 WHERE id = $id;", ct, transaction,
                        ("$content", content), ("$id", artifactId));
            }, ct);
        }

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, "UPDATE archive_manifests SET state = 'hot', restored_at = $at WHERE task_id = $task;", ct, transaction,
                ("$at", Iso(UtcNow)), ("$task", taskId));
            await ExecuteAsync(connection, "UPDATE tasks SET archive_state = NULL, archived_at = NULL WHERE id = $id;", ct, transaction, ("$id", taskId));
        }, ct);
    }

    private async Task<(string RunId, byte[] Content)?> BuildRetentionExcerptAsync(
        RetentionAction action, IReadOnlyDictionary<string, byte[]> contents, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "retention-excerpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var heavy = new List<RetentionFile>();
            string? ownerRunId = null;
            foreach (var file in action.Files.Where(item => item.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData))
            {
                var (runId, artifactId, _) = ParseRetentionFileKey(file.RelativePath);
                if (!contents.TryGetValue(artifactId, out var found)) continue;
                ownerRunId ??= runId;
                var destination = Path.Combine(staging, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, found, ct);
                heavy.Add(file);
            }
            if (heavy.Count == 0 || ownerRunId is null) return null;
            var excerpt = await new RetentionExcerptWriter().WriteAsync(staging, heavy, ct);
            return (ownerRunId, Encoding.UTF8.GetBytes(excerpt));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<Dictionary<string, byte[]>> ReadRetentionArtifactContentsAsync(IEnumerable<string> artifactIds, CancellationToken ct)
    {
        var ids = artifactIds.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;
        await using var connection = await OpenReadyAsync(ct);
        foreach (var chunk in ids.Chunk(200))
        {
            var placeholders = string.Join(",", chunk.Select((_, index) => $"$id{index}"));
            var parameters = chunk.Select((id, index) => ($"$id{index}", (object?)id)).ToArray();
            await using var command = Command(connection, $"SELECT id, content FROM artifacts WHERE id IN ({placeholders}) AND archived = 0;", parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(1))
                    result[reader.GetString(0)] = (byte[])reader[1];
        }
        return result;
    }

    private static async Task UpsertRetentionExcerptArtifactAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, string runId, int stage, byte[] content, CancellationToken ct)
    {
        var name = $"retention-excerpt-stage-{stage}.md";
        var id = DeterministicId("art", $"{taskId}:excerpt:{stage}");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var idempotencyKey = $"retention:{taskId}:excerpt:{stage}";
        await ExecuteAsync(connection, """
            INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key, fence, created_at, archived)
            VALUES ($id, $run, $name, 'text/markdown', $sha, $content, $size, $key, 0, $now, 0)
            ON CONFLICT(idempotency_key) DO UPDATE SET
                content = excluded.content, sha256 = excluded.sha256, size_bytes = excluded.size_bytes, archived = 0;
            """, ct, transaction,
            ("$id", id), ("$run", runId), ("$name", name), ("$sha", sha), ("$content", content),
            ("$size", (long)content.LongLength), ("$key", idempotencyKey), ("$now", Iso(DateTime.UtcNow)));
    }

    private static async Task UpsertArchiveManifestStageAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, ArchiveManifest manifest, string payloadPath, CancellationToken ct)
    {
        var existing = await ReadManifestEnvelopeAsync(connection, transaction, taskId, ct);
        var stage = new RetentionManifestStage(manifest.Stage, manifest.ArchivedAt, payloadPath, manifest.PayloadSha256, manifest.TotalBytes, manifest.Files);
        var stages = (existing?.Stages ?? []).Where(item => item.Stage != stage.Stage).Append(stage).OrderBy(item => item.Stage).ToList();
        var envelope = new RetentionManifestEnvelope(stages);
        await ExecuteAsync(connection, """
            INSERT INTO archive_manifests(task_id, archived_at, manifest_json, payload_path, payload_sha256, total_bytes, state, restored_at)
            VALUES ($task, $at, $manifest, $path, $sha, $bytes, 'cold', NULL)
            ON CONFLICT(task_id) DO UPDATE SET
                archived_at = excluded.archived_at,
                manifest_json = excluded.manifest_json,
                payload_path = excluded.payload_path,
                payload_sha256 = excluded.payload_sha256,
                total_bytes = excluded.total_bytes,
                state = 'cold',
                restored_at = NULL;
            """, ct, transaction,
            ("$task", taskId), ("$at", Iso(manifest.ArchivedAt.UtcDateTime)), ("$manifest", JsonSerializer.Serialize(envelope, RetentionJson)),
            ("$path", payloadPath), ("$sha", manifest.PayloadSha256), ("$bytes", stages.Sum(item => item.TotalBytes)));
    }

    private static async Task<RetentionManifestEnvelope?> ReadManifestEnvelopeAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, "SELECT manifest_json FROM archive_manifests WHERE task_id = $task;", transaction, ("$task", taskId));
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : JsonSerializer.Deserialize<RetentionManifestEnvelope>((string)value, RetentionJson);
    }

    private static string RetentionArchiveDirectory(string archiveRoot, string project, string taskKey, DateTimeOffset archivedAt)
        => Path.Combine(archiveRoot, SanitizeArchiveSegment(project), SanitizeArchiveSegment(taskKey),
            archivedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));

    private static string SanitizeArchiveSegment(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
