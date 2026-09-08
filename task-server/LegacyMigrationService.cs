using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class LegacyMigrationService(TaskServerStore store)
{
    public async Task<LegacyMigrationInventory> InventoryAsync(LegacyMigrationRequest request, CancellationToken ct)
    {
        var root = ResolveLegacyRoot(request.LegacyRoot);
        var scan = await ScanAsync(root, includeContent: false, ct);
        RequireAuthorityWhenRequested(request, scan.Authority);
        return new LegacyMigrationInventory(
            scan.MigrationId,
            root,
            scan.Projects.Count,
            scan.Projects.Sum(project => project.Tasks.Count),
            scan.EventCount,
            scan.ArtifactCount,
            scan.EvidenceGitRoots,
            scan.Warnings,
            scan.Authority.RunnerIdentities.Count,
            scan.Authority.CodingAttempts.Count,
            scan.Authority.ReviewAttempts.Count,
            scan.Authority.LeaseCount,
            scan.Authority.AuthorityEpoch);
    }

    public async Task<LegacyMigrationResult> ImportAsync(LegacyMigrationRequest request, string actorId, CancellationToken ct)
    {
        if (!request.FreezeConfirmed)
            throw new TaskServerConflictException(
                "legacy-freeze-required",
                "Legacy import requires an explicit single-writer freeze confirmation.");
        if (store.Mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException(
                "maintenance-required",
                "Legacy import requires Task Server maintenance mode.");
        if (string.IsNullOrWhiteSpace(request.ExpectedMigrationId))
            throw new TaskServerConflictException(
                "legacy-inventory-required",
                "Legacy import requires the migration id from a completed inventory.");

        var root = ResolveLegacyRoot(request.LegacyRoot);
        var scan = await ScanAsync(root, includeContent: true, ct);
        RequireAuthorityWhenRequested(request, scan.Authority);
        if (!string.Equals(request.ExpectedMigrationId, scan.MigrationId, StringComparison.Ordinal))
        {
            throw new TaskServerConflictException(
                "legacy-inventory-changed",
                $"The frozen legacy source no longer matches inventory '{request.ExpectedMigrationId}'. " +
                $"Its current migration id is '{scan.MigrationId}'. Inventory it again before import.");
        }
        var backup = await store.CreateBackupAsync(new BackupRequest("before-legacy-import"), actorId, ct);
        await store.ImportLegacyBatchAsync(request.WorkspaceName, scan.Projects, scan.Authority, actorId, ct);

        if (request.PreserveEvidenceGit)
            await PreserveEvidenceGitAsync(scan.MigrationId, scan.EvidenceGitRoots, ct);

        // Heavy data older than the active policy's thresholds goes straight to the cold archive during
        // import, so a migrated workspace never lands with years of stale logs sitting hot.
        var archived = await store.ApplyRetentionDuringImportAsync(actorId, ct);
        var archiveCandidates = archived.Plan.Actions
            .Where(action => action.Kind is "ArchiveHeavy" or "ArchiveTask")
            .Select(action => action.TaskId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var archivedTaskCount = 0;
        foreach (var taskId in archiveCandidates)
            if (await store.GetRetentionManifestAsync(taskId, ct) is not null)
                archivedTaskCount++;

        var digest = await store.ComputeIntegrityDigestAsync(ct);
        return new LegacyMigrationResult(
            scan.MigrationId,
            true,
            scan.Projects.Count,
            scan.Projects.Sum(project => project.Tasks.Count),
            scan.EventCount,
            scan.ArtifactCount,
            digest,
            $"Restore backup '{backup.BackupId}' before enabling the new writer. The frozen legacy root remains untouched.",
            scan.EvidenceGitRoots,
            scan.Authority.RunnerIdentities.Count,
            scan.Authority.CodingAttempts.Count,
            scan.Authority.ReviewAttempts.Count,
            scan.Authority.LeaseCount,
            scan.Authority.AuthorityEpoch,
            archivedTaskCount,
            archived.AppliedBytes);
    }

    private static string ResolveLegacyRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Legacy root is required.");
        var root = Path.GetFullPath(value);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Legacy root '{root}' does not exist.");
        return root;
    }

    private static void RequireAuthorityWhenRequested(
        LegacyMigrationRequest request,
        LegacyAuthorityImport authority)
    {
        if (request.RequireAttemptAuthority && authority.AuthorityEpoch <= 0)
            throw new TaskServerConflictException(
                "legacy-attempt-authority-required",
                "This cutover requires .metadata/attempt-authority.json so leases and fences cannot be reset.");
    }

    private static async Task<LegacyScan> ScanAsync(string root, bool includeContent, CancellationToken ct)
    {
        var warnings = new List<string>();
        var taskFiles = EnumerateTaskMetadataFiles(root);
        var byProject = new Dictionary<string, List<LegacyTaskImport>>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new HashSet<string>(taskFiles, StringComparer.Ordinal);
        var eventCount = 0;
        var artifactCount = 0;

        foreach (var metadataFile in taskFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(metadataFile);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var rootElement = document.RootElement;
                var taskDirectory = Path.GetDirectoryName(metadataFile)!;
                var taskKey = ReadString(rootElement, "key")
                    ?? ReadString(rootElement, "taskKey")
                    ?? ReadString(rootElement, "id")
                    ?? ReadString(rootElement, "jobId")
                    ?? Path.GetFileName(taskDirectory);
                taskKey = taskKey.Trim().ToUpperInvariant();
                var title = ReadString(rootElement, "title") ?? taskKey;
                var state = ReadString(rootElement, "state") ?? InferState(taskDirectory);
                var body = await ReadBodyAsync(rootElement, taskDirectory, includeContent, ct);
                AddIfPresent(sourceFiles, Path.Combine(taskDirectory, "prompt.md"));
                AddIfPresent(sourceFiles, Path.Combine(taskDirectory, "timeline.jsonl"));
                var resultsDirectory = Path.Combine(taskDirectory, "results");
                if (Directory.Exists(resultsDirectory))
                    foreach (var resultFile in Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories))
                        sourceFiles.Add(resultFile);
                var info = new FileInfo(metadataFile);
                var created = ReadDate(rootElement, "createdAt") ?? info.CreationTimeUtc;
                var updated = ReadDate(rootElement, "updatedAt") ?? info.LastWriteTimeUtc;
                var projectName = InferProjectName(root, taskDirectory, rootElement, taskKey);
                var projectId = TaskServerStore.DeterministicId("prj", projectName);
                var taskId = TaskServerStore.DeterministicId("tsk", $"{projectId}:{taskKey}");
                var events = await ReadEventsAsync(taskDirectory, taskId, includeContent, ct);
                var artifacts = await ReadArtifactsAsync(taskDirectory, taskId, includeContent, ct);
                eventCount += events.Count;
                artifactCount += artifacts.Count;
                if (!byProject.TryGetValue(projectName, out var tasks))
                    byProject[projectName] = tasks = [];
                tasks.Add(new LegacyTaskImport(taskId, taskKey, title, body, state, created, updated, events, artifacts));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Skipped unreadable task metadata '{Path.GetRelativePath(root, metadataFile)}': {exception.Message}");
            }
        }

        var projects = byProject.Select(pair =>
        {
            var prefix = pair.Value.Select(task => task.TaskKey.Split('-', 2)[0]).FirstOrDefault() ?? "LEG";
            var next = pair.Value.Select(task => ParseTaskNumber(task.TaskKey)).DefaultIfEmpty(0).Max() + 1L;
            return new LegacyProjectImport(TaskServerStore.DeterministicId("prj", pair.Key), pair.Key, prefix, next, pair.Value);
        }).OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
        var evidenceGitRoots = FindEvidenceGitRoots(root);
        var authority = await ReadAuthorityAsync(root, sourceFiles, warnings, ct);
        var identity = new StringBuilder(root);
        foreach (var file in sourceFiles.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            await using var stream = File.OpenRead(file);
            var digest = await SHA256.HashDataAsync(stream, ct);
            identity.Append('|')
                .Append(Path.GetRelativePath(root, file))
                .Append(':')
                .Append(info.Length)
                .Append(':')
                .Append(Convert.ToHexString(digest));
        }
        var migrationId = TaskServerStore.DeterministicId("mig", identity.ToString());
        return new LegacyScan(migrationId, projects, eventCount, artifactCount, evidenceGitRoots, warnings, authority);
    }

    private static string[] EnumerateTaskMetadataFiles(string root)
    {
        var current = Directory.EnumerateFiles(root, "task.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase);
        foreach (var legacy in Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
            current.TryAdd(Path.GetDirectoryName(legacy)!, legacy);
        return current.Values.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<LegacyAuthorityImport> ReadAuthorityAsync(
        string root,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var path = Path.Combine(root, ".metadata", "attempt-authority.json");
        if (!File.Exists(path))
        {
            warnings.Add("No legacy attempt-authority store was found; the inventory contains no lease or fence authority.");
            return LegacyAuthorityImport.Empty with
            {
                RunnerIdentities = await ReadRunnerIdentitiesAsync(root, sourceFiles, warnings, ct),
            };
        }

        sourceFiles.Add(path);
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var json = document.RootElement;
            var epoch = ReadLong(json, "authorityEpoch", 1);
            var fences = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (json.TryGetProperty("lastFenceByTask", out var fenceJson))
                foreach (var property in fenceJson.EnumerateObject())
                    fences[property.Name.Trim().ToUpperInvariant()] = property.Value.GetInt64();

            var runs = ReadArray(json, "runAttempts")
                .Select(ReadCodingAttempt)
                .ToList();
            var reviews = ReadArray(json, "reviewAttempts")
                .Select(ReadReviewAttempt)
                .ToList();
            foreach (var archivePath in Directory
                         .EnumerateFiles(Path.GetDirectoryName(path)!, "attempt-authority.archive-*.json")
                         .Order(StringComparer.Ordinal))
            {
                sourceFiles.Add(archivePath);
                await using var archiveStream = File.OpenRead(archivePath);
                using var archive = await JsonDocument.ParseAsync(archiveStream, cancellationToken: ct);
                runs.AddRange(ReadArray(archive.RootElement, "runAttempts").Select(ReadCodingAttempt));
                reviews.AddRange(ReadArray(archive.RootElement, "reviewAttempts").Select(ReadReviewAttempt));
            }
            RequireUniqueAttemptIds(runs.Select(item => item.AttemptId), "coding");
            RequireUniqueAttemptIds(reviews.Select(item => item.AttemptId), "review");
            return new LegacyAuthorityImport(
                epoch,
                fences,
                await ReadRunnerIdentitiesAsync(root, sourceFiles, warnings, ct),
                runs,
                reviews);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new InvalidDataException(
                $"Legacy attempt authority '{path}' could not be inventoried; refusing a cutover that would reset fences.",
                exception);
        }
    }

    private static void RequireUniqueAttemptIds(IEnumerable<string> attemptIds, string kind)
    {
        var duplicate = attemptIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException(
                $"Legacy {kind} attempt '{duplicate.Key}' occurs more than once across live and archived authority.");
    }

    private static async Task<IReadOnlyList<LegacyRunnerIdentityImport>> ReadRunnerIdentitiesAsync(
        string root,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var directory = Path.Combine(root, "identities");
        if (!Directory.Exists(directory)) return [];
        var result = new List<LegacyRunnerIdentityImport>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            sourceFiles.Add(path);
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var json = document.RootElement;
                if (!IsRunnerIdentity(json)) continue;
                result.Add(new LegacyRunnerIdentityImport(
                    Required(json, "id"),
                    ReadString(json, "displayName") ?? Required(json, "id"),
                    ReadDate(json, "registeredAt") ?? DateTime.UnixEpoch,
                    ReadDate(json, "lastSeenAt")));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Skipped unreadable runner identity '{Path.GetFileName(path)}': {exception.Message}");
            }
        }
        return result;
    }

    private static bool IsRunnerIdentity(JsonElement json)
    {
        if (!json.TryGetProperty("kind", out var kind)) return false;
        if (kind.ValueKind == JsonValueKind.Number)
            return kind.TryGetInt32(out var number) && number is 1 or 3 or 4;
        var value = kind.GetString()?.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return value is "agentinstance" or "service" or "retired";
    }

    private static LegacyCodingAttemptImport ReadCodingAttempt(JsonElement json)
        => new(
            Required(json, "attemptId"),
            Required(json, "taskKey").Trim().ToUpperInvariant(),
            ReadString(json, "repositoryId") ?? "legacy-unknown",
            ReadState(json),
            ReadLease(json),
            ReadLong(json, "lastFence", 0),
            ReadLong(json, "authorityEpoch", 1),
            ReadDate(json, "createdAt") ?? DateTime.UnixEpoch,
            ReadDate(json, "terminalAt"),
            ReadString(json, "resultSha"));

    private static LegacyReviewAttemptImport ReadReviewAttempt(JsonElement json)
    {
        if (!json.TryGetProperty("subject", out var subject))
            throw new InvalidDataException("A legacy review attempt has no immutable subject.");
        return new LegacyReviewAttemptImport(
            Required(json, "attemptId"),
            Required(json, "taskKey").Trim().ToUpperInvariant(),
            ReadString(json, "repositoryId") ?? ReadString(subject, "repositoryId") ?? "legacy-unknown",
            Required(json, "sourceRunAttemptId"),
            ReadState(json),
            ReadLease(json),
            ReadLong(json, "lastFence", 0),
            ReadLong(json, "authorityEpoch", 1),
            ReadDate(json, "createdAt") ?? DateTime.UnixEpoch,
            ReadDate(json, "terminalAt"),
            ReadReviewOutcome(json),
            ReadString(json, "failureClassification"),
            new LegacyReviewSubjectImport(
                Required(subject, "subjectId"),
                ReadString(subject, "repositoryId") ?? "legacy-unknown",
                Required(subject, "expectedResultSha"),
                Required(subject, "sourceRunAttemptId"),
                ReadString(subject, "reviewPolicyHash") ?? "legacy",
                ReadString(subject, "repositoryUrl"),
                ReadString(subject, "resultRef"),
                subject.TryGetProperty("plan", out var plan) && plan.ValueKind != JsonValueKind.Null
                    ? plan.GetRawText()
                    : "{\"commands\":[],\"requiredAspects\":[]}",
                ReadDate(subject, "createdAt") ?? DateTime.UnixEpoch));
    }

    private static LegacyLeaseImport? ReadLease(JsonElement json)
    {
        if (!json.TryGetProperty("lease", out var lease) || lease.ValueKind == JsonValueKind.Null)
            return null;
        return new LegacyLeaseImport(
            Required(lease, "leaseId"),
            ReadLong(lease, "fence", 0),
            ReadLong(lease, "authorityEpoch", 1),
            Required(lease, "executorId"),
            Required(lease, "hostId"),
            ReadString(lease, "leaseInstanceId") ?? Required(lease, "leaseId"),
            ReadDate(lease, "acquiredAt") ?? DateTime.UnixEpoch,
            ReadDate(lease, "expiresAt") ?? DateTime.UnixEpoch,
            ReadString(lease, "executorDisplayName"));
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement json, string property)
        => json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone())
            : [];

    private static string Required(JsonElement json, string property)
        => ReadString(json, property) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Legacy authority property '{property}' is required.");

    private static long ReadLong(JsonElement json, string property, long fallback)
        => json.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : fallback;

    private static string ReadState(JsonElement json)
    {
        if (!json.TryGetProperty("state", out var value)) return "pending";
        if (value.ValueKind == JsonValueKind.String) return value.GetString()?.ToLowerInvariant() ?? "pending";
        return value.TryGetInt32(out var number) ? number switch
        {
            1 => "leased",
            2 => "completed",
            3 => "failed",
            4 => "cancelled",
            5 => "superseded",
            _ => "pending",
        } : "pending";
    }

    private static string? ReadReviewOutcome(JsonElement json)
    {
        if (!json.TryGetProperty("outcome", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.TryGetInt32(out var number) ? number switch
        {
            0 => "InfrastructureFailure",
            1 => "ProductFailure",
            2 => "Inconclusive",
            3 => "Pass",
            4 => "Cancellation",
            5 => "Superseded",
            _ => throw new InvalidDataException($"Unknown legacy review outcome '{number}'."),
        } : throw new InvalidDataException("Legacy review outcome must be a string or integer.");
    }

    private static async Task<string?> ReadBodyAsync(JsonElement json, string taskDirectory, bool includeContent, CancellationToken ct)
    {
        var body = ReadString(json, "promptMarkdown") ?? ReadString(json, "description");
        if (body is not null || !includeContent) return body;
        var prompt = Path.Combine(taskDirectory, "prompt.md");
        return File.Exists(prompt) ? await File.ReadAllTextAsync(prompt, ct) : null;
    }

    private static void AddIfPresent(ISet<string> files, string path)
    {
        if (File.Exists(path)) files.Add(path);
    }

    private static async Task<IReadOnlyList<LegacyEventImport>> ReadEventsAsync(
        string taskDirectory, string taskId, bool includeContent, CancellationToken ct)
    {
        var timeline = Path.Combine(taskDirectory, "timeline.jsonl");
        if (!File.Exists(timeline)) return [];
        var result = new List<LegacyEventImport>();
        var lines = await File.ReadAllLinesAsync(timeline, ct);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            var key = $"legacy:{taskId}:event:{index}";
            var payload = includeContent ? lines[index] : "{}";
            var kind = "legacy.timeline";
            var occurred = File.GetLastWriteTimeUtc(timeline);
            try
            {
                using var json = JsonDocument.Parse(lines[index]);
                kind = ReadString(json.RootElement, "type") ?? ReadString(json.RootElement, "kind") ?? kind;
                occurred = ReadDate(json.RootElement, "timestamp") ?? ReadDate(json.RootElement, "occurredAt") ?? occurred;
            }
            catch (JsonException)
            {
                kind = "legacy.timeline.unparsed";
            }
            result.Add(new LegacyEventImport(TaskServerStore.DeterministicId("evt", key), kind, payload, key, occurred));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyArtifactImport>> ReadArtifactsAsync(
        string taskDirectory, string taskId, bool includeContent, CancellationToken ct)
    {
        var resultsDirectory = Path.Combine(taskDirectory, "results");
        if (!Directory.Exists(resultsDirectory)) return [];
        var result = new List<LegacyArtifactImport>();
        foreach (var path in Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(resultsDirectory, path).Replace('\\', '/');
            var content = includeContent ? await File.ReadAllBytesAsync(path, ct) : [];
            var sha = includeContent
                ? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
                : string.Empty;
            var key = $"legacy:{taskId}:artifact:{relative}";
            result.Add(new LegacyArtifactImport(
                TaskServerStore.DeterministicId("art", key),
                relative,
                ContentType(relative),
                content,
                sha,
                key,
                File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }

    private async Task PreserveEvidenceGitAsync(string migrationId, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var destinationRoot = Path.Combine(store.DataDirectory, "migration-evidence", migrationId);
        Directory.CreateDirectory(destinationRoot);
        var manifest = new List<object>();
        for (var index = 0; index < roots.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var gitEntry = Path.Combine(roots[index], ".git");
            var destination = Path.Combine(destinationRoot, index.ToString("D3"), ".git");
            if (Directory.Exists(gitEntry))
                await CopyDirectoryAsync(gitEntry, destination, ct);
            else if (File.Exists(gitEntry))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(gitEntry, destination, overwrite: true);
            }
            manifest.Add(new { source = roots[index], preservedAt = Path.GetRelativePath(store.DataDirectory, destination) });
        }
        await File.WriteAllTextAsync(
            Path.Combine(destinationRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            await using var input = File.OpenRead(file);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, ct);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            await CopyDirectoryAsync(directory, Path.Combine(destination, info.Name), ct);
        }
    }

    private static IReadOnlyList<string> FindEvidenceGitRoots(string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, ".git", SearchOption.AllDirectories))
        {
            var parent = Path.GetDirectoryName(entry);
            if (parent is not null) result.Add(parent);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string InferProjectName(string root, string taskDirectory, JsonElement json, string taskKey)
    {
        var configured = ReadString(json, "projectName") ?? ReadString(json, "project");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var relative = Path.GetRelativePath(root, taskDirectory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectsIndex = Array.FindIndex(relative, part => string.Equals(part, "projects", StringComparison.OrdinalIgnoreCase));
        if (projectsIndex >= 0 && projectsIndex + 1 < relative.Length) return relative[projectsIndex + 1];
        return taskKey.Split('-', 2)[0];
    }

    private static string InferState(string taskDirectory)
    {
        var parent = Directory.GetParent(taskDirectory)?.Name;
        return parent is not null && parent.Length > 2 && char.IsDigit(parent[0]) && parent[1] == '-'
            ? parent
            : "0-backlog";
    }

    private static long ParseTaskNumber(string taskKey)
        => long.TryParse(taskKey.Split('-', 2).ElementAtOrDefault(1), out var value) ? value : 0;

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadDate(JsonElement element, string property)
        => DateTime.TryParse(ReadString(element, property), out var value) ? value.ToUniversalTime() : null;

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".md" or ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream",
    };

    private sealed record LegacyScan(
        string MigrationId,
        IReadOnlyList<LegacyProjectImport> Projects,
        int EventCount,
        int ArtifactCount,
        IReadOnlyList<string> EvidenceGitRoots,
        IReadOnlyList<string> Warnings,
        LegacyAuthorityImport Authority);
}
