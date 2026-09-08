using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed class FileTreeRetentionStore : IRetentionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ArtifactClassifier _classifier;
    private readonly RetentionExcerptWriter _excerptWriter;

    public FileTreeRetentionStore(string workspacePath, string? archivePath = null)
    {
        WorkspacePath = Path.GetFullPath(workspacePath);
        ArchivePath = Path.GetFullPath(archivePath ?? Path.Combine(
            Directory.GetParent(WorkspacePath)?.FullName ?? throw new ArgumentException("Workspace needs a parent directory."),
            "agent-taskboard-archive"));
        if (IsInside(ArchivePath, WorkspacePath))
            throw new ArgumentException("Archive path must be outside the workspace repository.", nameof(archivePath));
        _classifier = new ArtifactClassifier();
        _excerptWriter = new RetentionExcerptWriter();
    }

    public string WorkspacePath { get; }
    public string ArchivePath { get; }

    public async Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(WorkspacePath))
            throw new DirectoryNotFoundException($"Workspace does not exist: {WorkspacePath}");
        var result = new List<RetentionTaskInventory>();
        foreach (var folder in EnumerateTaskFolders(WorkspacePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await InventoryTaskAsync(
                folder.ProjectPath, folder.BucketPath, folder.TaskPath, Path.Combine(folder.TaskPath, "task.json"), cancellationToken));
        }

        var runtime = EnumerateWorkspaceRuntime();
        if (runtime.Count > 0)
            result.Add(new RetentionTaskInventory("_workspace", "_runtime", "_runtime", "runtime", null, ".", runtime));
        return result;
    }

    /// <summary>
    /// Walks exactly <c>projects/&lt;project&gt;/tasks/&lt;bucket&gt;/&lt;task&gt;</c> and no deeper. Anything that
    /// recurses the whole tree also finds foreign files a task carries under <c>results/</c> or
    /// <c>attachments/</c> - fixture workspaces, nested archives - and mistakes them for real task data.
    /// </summary>
    public static IEnumerable<TaskFolder> EnumerateTaskFolders(string workspacePath)
    {
        var projectsPath = Path.Combine(workspacePath, "projects");
        if (!Directory.Exists(projectsPath)) yield break;
        foreach (var projectPath in Directory.EnumerateDirectories(projectsPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            var tasksPath = Path.Combine(projectPath, "tasks");
            if (!Directory.Exists(tasksPath)) continue;
            foreach (var bucketPath in Directory.EnumerateDirectories(tasksPath).Order(StringComparer.OrdinalIgnoreCase))
                foreach (var taskPath in Directory.EnumerateDirectories(bucketPath).Order(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(taskPath, "task.json")))
                        yield return new TaskFolder(projectPath, bucketPath, taskPath);
        }
    }

    public Task<Stream> ReadFileAsync(
        RetentionTaskInventory task,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = File.OpenRead(SafeTaskPath(task, relativePath));
        return Task.FromResult(stream);
    }

    public async Task WriteManifestAsync(
        RetentionTaskInventory task,
        ArchiveManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(ArchivePath, task.Project, task.TaskKey,
            manifest.ArchivedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ"), "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteJsonAtomicallyAsync(path, manifest, cancellationToken);
    }

    public async Task<ArchiveTransition?> MoveToColdAsync(
        RetentionAction action,
        RetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var existing = action.Files.Where(file => File.Exists(SafeTaskPath(action.Task, file.RelativePath))).ToList();
        if (existing.Count == 0)
            return null;

        var archivedAt = DateTimeOffset.UtcNow;
        var archiveDirectory = UniqueArchiveDirectory(action.Task, archivedAt);
        Directory.CreateDirectory(archiveDirectory);
        var payloadPath = Path.Combine(archiveDirectory, "payload.zip");
        var taskRoot = TaskRoot(action.Task);
        var manifestFiles = new List<ArchiveManifestFile>();
        var coldIsDurable = false;
        try
        {
            await using (var payload = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in existing.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SafeTaskPath(action.Task, file.RelativePath);
                    var hash = await Sha256Async(source, cancellationToken);
                    var info = new FileInfo(source);
                    manifestFiles.Add(new ArchiveManifestFile(file.RelativePath.Replace('\\', '/'), info.Length, hash));
                    var entry = zip.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.SmallestSize);
                    await using var input = File.OpenRead(source);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            var payloadHash = await Sha256Async(payloadPath, cancellationToken);
            var manifest = new ArchiveManifest
            {
                TaskKey = action.Task.TaskKey,
                Id = action.Task.Id,
                Project = action.Task.Project,
                Lane = action.Task.Lane,
                TerminalAt = action.Task.TerminalAt,
                RuleIds = [action.RuleId],
                PolicyVersion = policy.Version,
                Files = manifestFiles,
                TotalBytes = manifestFiles.Sum(file => file.Size),
                PayloadSha256 = payloadHash,
                ArchivedAt = archivedAt,
                ArchivedBy = policy.UpdatedBy,
                Stage = action.Stage,
            };
            var manifestPath = Path.Combine(archiveDirectory, "manifest.json");
            await WriteJsonAtomicallyAsync(manifestPath, manifest, cancellationToken);

            if (existing.Any(file => file.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData))
            {
                var excerpt = await _excerptWriter.WriteAsync(taskRoot, existing, cancellationToken);
                var excerptPath = Path.Combine(taskRoot, $"retention-excerpt-stage-{action.Stage}.md");
                if (!File.Exists(excerptPath))
                    await File.WriteAllTextAsync(excerptPath, excerpt, cancellationToken);
            }

            var transition = new ArchiveTransition(
                manifestPath, payloadPath, archivedAt, action.Stage, manifest.TotalBytes);
            var pointer = await ReadPointerAsync(action.Task, cancellationToken);
            var archives = pointer?.Archives.ToList() ?? [];
            archives.Add(transition);
            await WriteStubAsync(action.Task, new ArchivePointer
            {
                TaskKey = action.Task.TaskKey,
                Archives = archives,
                ArchivedAt = archivedAt,
                TotalBytes = archives.Sum(item => item.TotalBytes),
            }, cancellationToken);
            coldIsDurable = true;

            foreach (var file in existing)
                File.Delete(SafeTaskPath(action.Task, file.RelativePath));
            RemoveEmptyDirectories(taskRoot);
            return transition;
        }
        catch
        {
            if (!coldIsDurable && Directory.Exists(archiveDirectory))
                Directory.Delete(archiveDirectory, recursive: true);
            throw;
        }
    }

    public Task WriteStubAsync(
        RetentionTaskInventory task,
        ArchivePointer pointer,
        CancellationToken cancellationToken = default)
        => WriteJsonAtomicallyAsync(Path.Combine(TaskRoot(task), "archive-manifest.json"), pointer, cancellationToken);

    public Task DeleteColdAsync(RetentionAction action, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Cold payload deletion is owned by the SQLite Task Server adapter.");

    public Task DeleteRuntimeAsync(RetentionAction action, CancellationToken cancellationToken = default)
    {
        foreach (var file in action.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafeTaskPath(action.Task, file.RelativePath);
            if (File.Exists(path)) File.Delete(path);
        }
        RemoveEmptyDirectories(TaskRoot(action.Task));
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string taskKey, CancellationToken cancellationToken = default)
    {
        var task = (await EnumerateTasksAndFilesAsync(cancellationToken))
            .SingleOrDefault(item => string.Equals(item.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase));
        if (task is null)
            throw new InvalidOperationException($"Task '{taskKey}' was not found in the workspace.");
        var pointer = await ReadPointerAsync(task, cancellationToken)
                      ?? throw new InvalidOperationException($"Task '{taskKey}' is not archived.");

        foreach (var transition in pointer.Archives.OrderBy(item => item.ArchivedAt))
        {
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(
                               await File.ReadAllTextAsync(transition.ManifestPath, cancellationToken), JsonOptions)
                           ?? throw new InvalidDataException($"Invalid archive manifest: {transition.ManifestPath}");
            var payloadHash = await Sha256Async(transition.PayloadPath, cancellationToken);
            if (!string.Equals(payloadHash, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Payload hash mismatch for {transition.PayloadPath}");
            using var zip = ZipFile.OpenRead(transition.PayloadPath);
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry(file.RelativePath)
                            ?? throw new InvalidDataException($"Archive entry is missing: {file.RelativePath}");
                var destination = SafeTaskPath(task, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".retention-restore.tmp";
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, cancellationToken);
                var hash = await Sha256Async(temporary, cancellationToken);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temporary);
                    throw new InvalidDataException($"Restored file hash mismatch: {file.RelativePath}");
                }
                File.Move(temporary, destination, overwrite: true);
            }
            await WriteJsonAtomicallyAsync(transition.ManifestPath, manifest with { RestoredAt = DateTimeOffset.UtcNow }, cancellationToken);
        }
        await WriteStubAsync(task, pointer with { RestoredAt = DateTimeOffset.UtcNow }, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the hot excerpts from the cold payloads. Needed after an excerpt-writer fix, because the
    /// originals are no longer in the hot tree and only the archive still holds the full content.
    /// </summary>
    public async Task<IReadOnlyList<ReExcerptResult>> ReExcerptAsync(
        string? taskKey = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ReExcerptResult>();
        var tasks = (await EnumerateTasksAndFilesAsync(cancellationToken))
            .Where(task => taskKey is null || string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase));

        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivePointer? pointer;
            try
            {
                pointer = await ReadPointerAsync(task, cancellationToken);
            }
            catch (Exception exception)
            {
                results.Add(ReExcerptResult.Failed(task, $"unreadable archive pointer: {exception.Message}"));
                continue;
            }
            if (pointer is null) continue;

            foreach (var transition in pointer.Archives.OrderBy(item => item.ArchivedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(await ReExcerptTransitionAsync(task, transition, cancellationToken));
                }
                catch (Exception exception)
                {
                    results.Add(ReExcerptResult.Failed(task, $"stage {transition.Stage}: {exception.Message}"));
                }
            }
        }
        return results;
    }

    private async Task<ReExcerptResult> ReExcerptTransitionAsync(
        RetentionTaskInventory task,
        ArchiveTransition transition,
        CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(
                           await File.ReadAllTextAsync(transition.ManifestPath, cancellationToken), JsonOptions)
                       ?? throw new InvalidDataException($"Invalid archive manifest: {transition.ManifestPath}");

        var staging = Path.Combine(Path.GetTempPath(), "retention-re-excerpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<RetentionFile>();
            using (var zip = ZipFile.OpenRead(transition.PayloadPath))
            {
                foreach (var file in manifest.Files)
                {
                    var entry = zip.GetEntry(file.RelativePath);
                    if (entry is null) continue;
                    var destination = Path.GetFullPath(Path.Combine(staging, file.RelativePath));
                    if (!IsInside(destination, staging))
                        throw new InvalidDataException($"Archive entry escapes staging root: {file.RelativePath}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, cancellationToken);
                    files.Add(new RetentionFile(file.RelativePath, file.Size,
                        new FileInfo(destination).LastWriteTimeUtc, _classifier.Classify(file.RelativePath)));
                }
            }

            if (files.Count == 0)
                return ReExcerptResult.Failed(task, $"stage {transition.Stage}: payload holds no manifest files");

            var excerpt = await _excerptWriter.WriteAsync(staging, files, cancellationToken);
            var excerptPath = Path.Combine(TaskRoot(task), $"retention-excerpt-stage-{transition.Stage}.md");
            var previous = File.Exists(excerptPath) ? new FileInfo(excerptPath).Length : 0;
            await File.WriteAllTextAsync(excerptPath, excerpt, cancellationToken);
            return new ReExcerptResult(task.Project, task.TaskKey, transition.Stage,
                Path.GetRelativePath(WorkspacePath, excerptPath).Replace('\\', '/'),
                previous, new FileInfo(excerptPath).Length, null);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<RetentionTaskInventory> InventoryTaskAsync(
        string projectPath,
        string bucket,
        string taskPath,
        string taskJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(taskJson, cancellationToken));
        var root = document.RootElement;
        var folderName = Path.GetFileName(taskPath);
        var taskKey = String(root, "key") ?? String(root, "taskKey") ?? String(root, "id") ?? folderName;
        var id = String(root, "id") ?? folderName;
        var bucketName = Path.GetFileName(bucket);
        var state = String(root, "state");
        var lane = !string.IsNullOrWhiteSpace(state)
            ? state
            : LooksLikeLane(bucketName) ? bucketName : "unknown";
        DateTimeOffset? terminalAt = IsTerminalLane(lane)
            ? Date(root, "enteredLaneAt") ?? Date(root, "completedAt") ?? new FileInfo(taskJson).LastWriteTimeUtc
            : null;
        var files = Directory.EnumerateFiles(taskPath, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".retention-restore.tmp", StringComparison.OrdinalIgnoreCase))
            .Select(path => FileInventory(taskPath, path)).ToList();
        var storeKey = Path.GetRelativePath(WorkspacePath, taskPath).Replace('\\', '/');
        return new RetentionTaskInventory(Path.GetFileName(projectPath), taskKey, id, lane, terminalAt, storeKey, files);
    }

    private List<RetentionFile> EnumerateWorkspaceRuntime()
    {
        var result = new List<RetentionFile>();
        foreach (var relativeRoot in new[] { Path.Combine("logs", "bus"), ".metadata", ".runtime" })
        {
            var root = Path.Combine(WorkspacePath, relativeRoot);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(WorkspacePath, path).Replace('\\', '/');
                var classification = _classifier.Classify(relative);
                if (classification.ArtifactClass == ArtifactClass.Runtime)
                    result.Add(new RetentionFile(relative, new FileInfo(path).Length,
                        new FileInfo(path).LastWriteTimeUtc, classification));
            }
        }
        return result;
    }

    private RetentionFile FileInventory(string taskRoot, string path)
    {
        var relative = Path.GetRelativePath(taskRoot, path).Replace('\\', '/');
        var info = new FileInfo(path);
        return new RetentionFile(relative, info.Length, info.LastWriteTimeUtc, _classifier.Classify(relative));
    }

    private string TaskRoot(RetentionTaskInventory task)
        => task.StoreKey == "." ? WorkspacePath : SafeWorkspacePath(task.StoreKey);

    private string SafeTaskPath(RetentionTaskInventory task, string relativePath)
    {
        var root = TaskRoot(task);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInside(path, root))
            throw new InvalidOperationException($"Artifact path escapes task root: {relativePath}");
        return path;
    }

    private string SafeWorkspacePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(WorkspacePath, relativePath));
        if (!IsInside(path, WorkspacePath))
            throw new InvalidOperationException($"Store path escapes workspace: {relativePath}");
        return path;
    }

    private string UniqueArchiveDirectory(RetentionTaskInventory task, DateTimeOffset archivedAt)
    {
        var root = Path.Combine(ArchivePath, Sanitize(task.Project), Sanitize(task.TaskKey));
        var stamp = archivedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ");
        var candidate = Path.Combine(root, stamp);
        for (var suffix = 1; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(root, $"{stamp}-{suffix}");
        return candidate;
    }

    private async Task<ArchivePointer?> ReadPointerAsync(RetentionTaskInventory task, CancellationToken cancellationToken)
    {
        var path = Path.Combine(TaskRoot(task), "archive-manifest.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;

    private static bool IsTerminalLane(string lane)
        => lane.StartsWith("6-", StringComparison.OrdinalIgnoreCase)
           || lane.StartsWith("7-", StringComparison.OrdinalIgnoreCase)
           || string.Equals(lane, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(lane, "archive", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLane(string value)
        => (value.Length > 2 && char.IsDigit(value[0]) && value[1] == '-')
           || string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "archive", StringComparison.OrdinalIgnoreCase);

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static bool IsInside(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
}
