using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class LegacyMigrationService(TaskServerStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<LegacyMigrationInventory> InventoryAsync(LegacyMigrationRequest request, CancellationToken ct)
    {
        var root = ResolveLegacyRoot(request.LegacyRoot);
        var scan = await ScanAsync(root, includeContent: false, ct);
        RequireAuthorityWhenRequested(request, scan.Authority);
        return BuildInventory(root, scan);
    }

    public async Task<LegacyMigrationResult> ImportAsync(LegacyMigrationRequest request, string actorId, CancellationToken ct)
        => await ImportAsync(request, expectedInventory: null, actorId, ct);

    public async Task<LegacyMigrationResult> ImportAsync(
        LegacyMigrationRequest request,
        LegacyMigrationInventory? expectedInventory,
        string actorId,
        CancellationToken ct)
    {
        if (!request.FreezeConfirmed)
            throw new TaskServerConflictException(
                "legacy-freeze-required",
                "Legacy import requires an explicit single-writer freeze confirmation.");
        if (store.Mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException(
                "maintenance-required",
                "Legacy import requires Task Server maintenance mode. Re-run the offline command with '--mode maintenance' or set Maintenance through the management API.");
        if (string.IsNullOrWhiteSpace(request.ExpectedMigrationId) && expectedInventory is null)
            throw new TaskServerConflictException(
                "legacy-inventory-required",
                "Legacy import requires the migration id from a completed inventory.");

        var root = ResolveLegacyRoot(request.LegacyRoot);
        if (expectedInventory is not null) ValidateInventoryHash(expectedInventory);
        var scan = await ScanAsync(root, includeContent: true, ct);
        RequireAuthorityWhenRequested(request, scan.Authority);
        var actualInventory = BuildInventory(root, scan);
        var expectedMigrationId = expectedInventory?.MigrationId ?? request.ExpectedMigrationId;
        var expectedHash = expectedInventory?.InventorySha256;
        if (!string.Equals(expectedMigrationId, scan.MigrationId, StringComparison.Ordinal)
            || expectedHash is { Length: > 0 }
            && !string.Equals(expectedHash, actualInventory.InventorySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskServerConflictException(
                "legacy-inventory-mismatch",
                $"The frozen legacy source no longer matches inventory '{expectedMigrationId}'. " +
                $"Its current migration id is '{scan.MigrationId}' and inventory SHA-256 is '{actualInventory.InventorySha256}'.");
        }

        if (await store.GetLegacyMigrationReportAsync(scan.MigrationId, ct) is { } existing)
            return ResultFromReport(existing, idempotent: true);

        var startedAt = DateTime.UtcNow;
        var backup = await store.CreateBackupAsync(new BackupRequest("before-legacy-import"), actorId, ct);
        await store.ImportLegacyBatchAsync(
            request.WorkspaceName,
            scan.Projects,
            scan.Authority,
            scan.MigrationId,
            scan.Supplementary,
            scan.Orphans,
            actualInventory,
            actorId,
            ct);
        await store.ValidateLegacyImportAsync(scan.MigrationId, actualInventory, ct);

        if (request.PreserveEvidenceGit)
            await PreserveEvidenceGitAsync(scan.MigrationId, scan.EvidenceGitRoots, ct);

        // Apply the active retention policy before cutover so stale heavy data
        // does not enter normal operation as hot store content.
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
        var completedAt = DateTime.UtcNow;
        var report = await WriteSignedReportAsync(
            actualInventory,
            actualInventory with { LegacyRoot = store.DataDirectory },
            backup,
            digest,
            startedAt,
            completedAt,
            ct);
        await store.SaveLegacyMigrationReportAsync(report, actorId, ct);
        return ResultFromReport(
            report,
            idempotent: false,
            archivedTaskCount,
            archived.AppliedBytes);
    }

    public static void ValidateInventoryHash(LegacyMigrationInventory inventory)
    {
        var calculated = ComputeInventorySha256(inventory);
        if (string.IsNullOrWhiteSpace(inventory.InventorySha256)
            || !string.Equals(calculated, inventory.InventorySha256, StringComparison.OrdinalIgnoreCase))
            throw new TaskServerConflictException(
                "legacy-inventory-invalid",
                $"Inventory SHA-256 is invalid; expected '{calculated}'.");
    }

    private static LegacyMigrationInventory BuildInventory(string root, LegacyScan scan)
    {
        var projectCounts = scan.Projects.Select(project =>
        {
            var tasks = project.Tasks;
            return new LegacyMigrationProjectCounts(
                project.Name,
                tasks.GroupBy(task => task.State, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                tasks.Count,
                tasks.Count(task => task.IsEpic),
                tasks.Sum(task => task.Events.Count),
                tasks.Sum(task => task.Artifacts.Count),
                tasks.Sum(task => task.GitCommits),
                tasks.Sum(task => task.IntegrationRecords),
                tasks.Sum(task => task.PendingIntegrationRecords),
                tasks.Sum(task => task.ResultRefs),
                tasks.Sum(task => task.DeliveryRefs));
        }).OrderBy(project => project.Project, StringComparer.Ordinal).ToArray();

        var draft = new LegacyMigrationInventory(
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
            scan.Authority.AuthorityEpoch,
            CreatedAt: DateTime.UtcNow,
            ProjectCounts: projectCounts,
            Epics: projectCounts.Sum(project => project.Epics),
            Dossiers: scan.Supplementary.Count(item => item.Kind == "dossier"),
            OrchestratorSessions: scan.Supplementary.Count(item => item.Kind == "orchestrator-session"),
            ContextChats: scan.Supplementary.Count(item => item.Kind == "context-chat"),
            ContextChatTurns: scan.Supplementary.Count(item => item.Kind == "context-chat-turn"),
            AttemptAuthorityRecords: scan.Authority.CodingAttempts.Count + scan.Authority.ReviewAttempts.Count,
            PendingIntegrationRecords: projectCounts.Sum(project => project.PendingIntegrationRecords),
            ResultRefs: projectCounts.Sum(project => project.ResultRefs),
            GitCommits: projectCounts.Sum(project => project.GitCommits),
            IntegrationRecords: projectCounts.Sum(project => project.IntegrationRecords),
            DeliveryRefs: projectCounts.Sum(project => project.DeliveryRefs),
            BusLogFiles: scan.Supplementary.Count(item => item.Kind == "bus-log-reference"),
            BusLogBytes: scan.Supplementary.Where(item => item.Kind == "bus-log-reference").Sum(item => item.SizeBytes),
            SourceFiles: scan.SourceFiles,
            OrphanedReferences: new LegacyMigrationOrphanCounts(
                scan.Orphans.Count(item => item.Kind == "coding-attempt"),
                scan.Orphans.Count(item => item.Kind == "review-attempt"),
                scan.Orphans.Count(item => item.Kind == "lease"),
                scan.Orphans.Count(item => item.Kind == "fence-counter"),
                scan.Orphans.Count(item => item.Kind == "integration-record")));
        return draft with { InventorySha256 = ComputeInventorySha256(draft) };
    }

    private static string ComputeInventorySha256(LegacyMigrationInventory inventory)
    {
        var canonical = new
        {
            schemaVersion = 1,
            projectCounts = (inventory.ProjectCounts ?? []).OrderBy(item => item.Project, StringComparer.Ordinal).Select(item => new
            {
                item.Project,
                states = item.States.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                item.Tasks,
                item.Epics,
                item.Events,
                item.Artifacts,
                item.GitCommits,
                item.IntegrationRecords,
                item.PendingIntegrationRecords,
                item.ResultRefs,
                item.DeliveryRefs,
            }),
            inventory.Projects,
            inventory.Tasks,
            inventory.Events,
            inventory.Artifacts,
            inventory.Epics,
            inventory.Dossiers,
            inventory.OrchestratorSessions,
            inventory.ContextChats,
            inventory.ContextChatTurns,
            inventory.AttemptAuthorityRecords,
            inventory.RunnerIdentities,
            inventory.CodingAttempts,
            inventory.ReviewAttempts,
            inventory.Leases,
            inventory.AuthorityEpoch,
            inventory.PendingIntegrationRecords,
            inventory.ResultRefs,
            inventory.GitCommits,
            inventory.IntegrationRecords,
            inventory.DeliveryRefs,
            inventory.BusLogFiles,
            inventory.BusLogBytes,
            orphanedReferences = inventory.OrphanedReferences ?? new LegacyMigrationOrphanCounts(),
            files = (inventory.SourceFiles ?? []).OrderBy(item => item.Path, StringComparer.Ordinal),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task<LegacyMigrationReport> WriteSignedReportAsync(
        LegacyMigrationInventory before,
        LegacyMigrationInventory after,
        BackupResult backup,
        string integritySha,
        DateTime startedAt,
        DateTime completedAt,
        CancellationToken ct)
    {
        var directory = Path.Combine(store.DataDirectory, "migration-reports");
        Directory.CreateDirectory(directory);
        var reportId = $"legacy-{before.MigrationId}";
        var relativePath = Path.Combine("migration-reports", reportId + ".json").Replace('\\', '/');
        var path = Path.Combine(store.DataDirectory, relativePath);
        var unsigned = new LegacyMigrationReport(
            reportId,
            before.MigrationId,
            startedAt,
            completedAt,
            Math.Max(0, (completedAt - startedAt).TotalSeconds),
            store.ServerId,
            TaskServerBuildIdentity.Current.DisplayVersion,
            TaskServerStore.CurrentSchemaVersion,
            before.InventorySha256,
            after.InventorySha256,
            before,
            after,
            backup.BackupId,
            backup.Sha256,
            integritySha,
            before.LegacyRoot,
            "Task result, attachment, and log files are SHA-256 verified references to the frozen source; bodies are not copied.",
            "Closed coding and review history is imported. Open leases are retained as process-unknown with their fences. References to removed tasks are retained in the explicit legacy orphan ledger with orphanedTaskKey markers.",
            "Bus JSONL files are counted, hashed, and referenced. Their messages are not imported into the Task Server event stream.",
            relativePath,
            "",
            "HMAC-SHA256",
            "");
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, Json);
        var reportSha = Convert.ToHexString(SHA256.HashData(unsignedBytes)).ToLowerInvariant();
        var key = await store.GetOrCreateMigrationSigningKeyAsync(ct);
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(reportSha))).ToLowerInvariant();
        var report = unsigned with { ReportSha256 = reportSha, Signature = signature };
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json), ct);
        File.Move(temporary, path, overwrite: true);
        return report;
    }

    private static LegacyMigrationResult ResultFromReport(
        LegacyMigrationReport report,
        bool idempotent,
        int archivedTasks = 0,
        long archivedBytes = 0)
        => new(
            report.MigrationId,
            true,
            report.After.Projects,
            report.After.Tasks,
            report.After.Events,
            report.After.Artifacts,
            report.StoreIntegritySha256,
            $"Restore backup '{report.PreImportBackupId}' before enabling the new writer. The frozen legacy root remains untouched.",
            report.Before.EvidenceGitRoots,
            report.After.RunnerIdentities,
            report.After.CodingAttempts,
            report.After.ReviewAttempts,
            report.After.Leases,
            report.After.AuthorityEpoch,
            archivedTasks,
            archivedBytes,
            report.InventorySha256,
            report.AfterInventorySha256,
            idempotent,
            report.ReportId,
            report.ReportPath,
            report.ReportSha256,
            report.Signature,
            report.Before,
            report.After,
            report.After.OrphanedReferences);

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
        var registeredProjects = ReadRegisteredProjects(root);
        var byProject = new Dictionary<string, List<LegacyTaskImport>>(StringComparer.OrdinalIgnoreCase);
        foreach (var registered in registeredProjects.Values)
            byProject.TryAdd(registered.Name, []);
        var sourceFiles = new HashSet<string>(taskFiles, StringComparer.Ordinal);
        AddIfPresent(sourceFiles, Path.Combine(root, ".metadata", "projects.json"));
        AddIfPresent(sourceFiles, Path.Combine(root, ".metadata", "workspaces.json"));
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
                var info = new FileInfo(metadataFile);
                var created = ReadDate(rootElement, "createdAt") ?? info.CreationTimeUtc;
                var updated = ReadDate(rootElement, "updatedAt") ?? info.LastWriteTimeUtc;
                var projectName = InferProjectName(root, taskDirectory, rootElement, taskKey, registeredProjects);
                var projectId = registeredProjects.Values.FirstOrDefault(item =>
                                    string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase))?.Id
                                ?? TaskServerStore.DeterministicId("prj", projectName);
                var taskId = TaskServerStore.DeterministicId("tsk", $"{projectId}:{taskKey}");
                var events = await ReadEventsAsync(taskDirectory, taskId, includeContent, ct);
                var artifacts = await ReadArtifactsAsync(taskDirectory, taskId, includeContent, ct);
                foreach (var artifact in artifacts) sourceFiles.Add(artifact.SourcePath);
                foreach (var timeline in TimelinePaths(taskDirectory)) sourceFiles.Add(timeline);
                eventCount += events.Count;
                artifactCount += artifacts.Count;
                if (!byProject.TryGetValue(projectName, out var tasks))
                    byProject[projectName] = tasks = [];
                var gitCommits = ArrayLength(rootElement, "commits");
                var integrationRecords = ArrayLength(rootElement, "integrationRecords");
                var pendingIntegration = HasString(rootElement, "tags", "integrationpending") ? 1 : 0;
                var resultRefs = CountNamedStrings(rootElement, "resultRef", "immutableRemoteRef");
                var deliveryRefs = CountNamedStrings(rootElement, "deliveryRef");
                tasks.Add(new LegacyTaskImport(
                    taskId,
                    taskKey,
                    title,
                    body,
                    state,
                    created,
                    updated,
                    events,
                    artifacts,
                    rootElement.GetRawText(),
                    metadataFile,
                    string.Equals(ReadString(rootElement, "kind"), "epic", StringComparison.OrdinalIgnoreCase),
                    gitCommits,
                    integrationRecords,
                    pendingIntegration,
                    resultRefs,
                    deliveryRefs));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Skipped unreadable task metadata '{Path.GetRelativePath(root, metadataFile)}': {exception.Message}");
            }
        }

        var projects = byProject.Select(pair =>
        {
            var registered = registeredProjects.Values.FirstOrDefault(item =>
                string.Equals(item.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
            var prefix = registered?.Prefix
                         ?? pair.Value.Select(task => task.TaskKey.Split('-', 2)[0]).FirstOrDefault()
                         ?? "LEG";
            var next = Math.Max(registered?.NextTaskNumber ?? 1,
                pair.Value.Select(task => ParseTaskNumber(task.TaskKey)).DefaultIfEmpty(0).Max() + 1L);
            return new LegacyProjectImport(
                registered?.Id ?? TaskServerStore.DeterministicId("prj", pair.Key),
                pair.Key,
                prefix,
                next,
                pair.Value);
        }).OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
        projects = EnsureUniqueTaskKeyPrefixes(projects, warnings);
        var supplementary = await DiscoverSupplementaryAsync(root, sourceFiles, ct);
        AddTaskEvidenceEntities(projects, supplementary);
        var evidenceGitRoots = FindEvidenceGitRoots(root)
            .Concat(FindRegisteredRepositoryRoots(root))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var authority = await ReadAuthorityAsync(root, sourceFiles, warnings, ct);
        var orphans = FindOrphanedAuthority(projects, authority)
            .Concat(await FindOrphanedIntegrationRecordsAsync(root, projects, sourceFiles, warnings, ct))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        AddOrphanWarnings(orphans, warnings);
        AddAuthorityEntities(root, authority, supplementary);
        var identity = new StringBuilder();
        var sourceManifest = new List<LegacyMigrationSourceFile>();
        foreach (var file in sourceFiles.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            await using var stream = File.OpenRead(file);
            var digest = await SHA256.HashDataAsync(stream, ct);
            var relative = InventoryPath(root, file);
            var sha = Convert.ToHexString(digest).ToLowerInvariant();
            sourceManifest.Add(new LegacyMigrationSourceFile(relative, info.Length, sha, SourceKind(root, file)));
            identity.Append('|')
                .Append(relative)
                .Append(':')
                .Append(info.Length)
                .Append(':')
                .Append(sha);
        }
        var migrationId = TaskServerStore.DeterministicId("mig", identity.ToString());
        return new LegacyScan(
            migrationId,
            projects,
            eventCount,
            artifactCount,
            evidenceGitRoots,
            warnings,
            authority,
            supplementary,
            sourceManifest,
            orphans);
    }

    private static string[] EnumerateTaskMetadataFiles(string root)
    {
        var current = Directory.EnumerateFiles(root, "task.json", SearchOption.AllDirectories)
            .Where(IsCanonicalTaskMetadata)
            .ToDictionary(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase);
        foreach (var legacy in Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
            if (IsCanonicalTaskMetadata(legacy)) current.TryAdd(Path.GetDirectoryName(legacy)!, legacy);
        return current.Values.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsCanonicalTaskMetadata(string path)
    {
        var parts = Path.GetDirectoryName(path)!
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(part => part.Equals("results", StringComparison.OrdinalIgnoreCase)
                                  || part.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                                  || part.Equals("logs", StringComparison.OrdinalIgnoreCase)
                                  || part.Equals("test-results", StringComparison.OrdinalIgnoreCase));
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
        var result = new List<LegacyEventImport>();
        foreach (var timeline in TimelinePaths(taskDirectory))
        {
            var lines = await File.ReadAllLinesAsync(timeline, ct);
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                var key = $"legacy:{taskId}:event:{Path.GetFileName(Path.GetDirectoryName(timeline))}:{index}";
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
        }
        return result;
    }

    private static IEnumerable<string> TimelinePaths(string taskDirectory)
        => new[]
            {
                Path.Combine(taskDirectory, "timeline.jsonl"),
                Path.Combine(taskDirectory, "logs", "timeline.jsonl"),
            }
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal);

    private static async Task<IReadOnlyList<LegacyArtifactImport>> ReadArtifactsAsync(
        string taskDirectory, string taskId, bool includeContent, CancellationToken ct)
    {
        var result = new List<LegacyArtifactImport>();
        foreach (var (directory, prefix) in new[]
                 {
                     (Path.Combine(taskDirectory, "results"), "results"),
                     (Path.Combine(taskDirectory, "attachments"), "attachments"),
                     (Path.Combine(taskDirectory, "logs"), "logs"),
                 })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (TimelinePaths(taskDirectory).Contains(path, StringComparer.Ordinal)) continue;
                var relative = prefix + "/" + Path.GetRelativePath(directory, path).Replace('\\', '/');
                await using var stream = File.OpenRead(path);
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                var key = $"legacy:{taskId}:artifact:{relative}";
                result.Add(new LegacyArtifactImport(
                    TaskServerStore.DeterministicId("art", key),
                    relative,
                    ContentType(relative),
                    sha,
                    new FileInfo(path).Length,
                    path,
                    key,
                    File.GetLastWriteTimeUtc(path)));
            }
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
            var headPath = Directory.Exists(gitEntry) ? Path.Combine(gitEntry, "HEAD") : null;
            var head = headPath is not null && File.Exists(headPath)
                ? (await File.ReadAllTextAsync(headPath, ct)).Trim()
                : null;
            manifest.Add(new
            {
                source = roots[index],
                gitPointer = gitEntry,
                head,
                policy = "referenced-not-copied",
            });
        }
        await File.WriteAllTextAsync(
            Path.Combine(destinationRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);
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

    private static async Task<List<LegacySupplementaryImport>> DiscoverSupplementaryAsync(
        string root,
        ISet<string> sourceFiles,
        CancellationToken ct)
    {
        var result = new List<LegacySupplementaryImport>();
        // A registered repository can live inside the legacy root, so the roots overlap and the
        // same descriptor is enumerated twice. Deduplicate the files, not just the root strings:
        // the ledger keys on the path, so a repeated file would inflate the inventory count above
        // the number of rows the import can insert.
        var dossierRoots = new[] { root }.Concat(FindRegisteredRepositoryRoots(root)).Distinct(StringComparer.Ordinal);
        var dossierPaths = dossierRoots
            .Where(Directory.Exists)
            .SelectMany(dossierRoot => Directory.EnumerateFiles(dossierRoot, "workbench.json", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var path in dossierPaths)
        {
            ct.ThrowIfCancellationRequested();
            sourceFiles.Add(path);
            var payload = await File.ReadAllTextAsync(path, ct);
            result.Add(new LegacySupplementaryImport(
                "dossier",
                TaskServerStore.DeterministicId("dos", path),
                InferProjectFromPath(root, path),
                null,
                payload,
                path,
                await HashPathAsync(path, ct),
                new FileInfo(path).Length,
                true));
        }

        var sessionsRoot = Path.Combine(root, ".metadata", "orchestrator-sessions");
        if (Directory.Exists(sessionsRoot))
        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "session.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sourceFiles.Add(path);
            var payload = await File.ReadAllTextAsync(path, ct);
            result.Add(new LegacySupplementaryImport(
                "orchestrator-session",
                TaskServerStore.DeterministicId("ses", InventoryPath(root, path)),
                null,
                null,
                payload,
                path,
                await HashPathAsync(path, ct),
                new FileInfo(path).Length,
                false));
            var history = Path.Combine(Path.GetDirectoryName(path)!, "history.jsonl");
            if (File.Exists(history)) sourceFiles.Add(history);
        }

        var chatPaths = Directory.EnumerateFiles(root, "orchestrator-chat.jsonl", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Where(path => path.Replace('\\', '/').Contains("/.orchestrator/context-chats/", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var path in chatPaths)
        {
            sourceFiles.Add(path);
            var project = InferProjectFromPath(root, path);
            var chatKey = TaskServerStore.DeterministicId("cht", InventoryPath(root, path));
            result.Add(new LegacySupplementaryImport(
                "context-chat", chatKey, project, null, "{}", path,
                await HashPathAsync(path, ct), new FileInfo(path).Length, true));
            var lines = await File.ReadAllLinesAsync(path, ct);
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                result.Add(new LegacySupplementaryImport(
                    "context-chat-turn", $"{chatKey}:{index}", project, null,
                    lines[index], path, "", Encoding.UTF8.GetByteCount(lines[index]), false));
            }
        }

        var busRoot = Path.Combine(root, "logs", "bus");
        if (Directory.Exists(busRoot))
        foreach (var path in Directory.EnumerateFiles(busRoot, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sourceFiles.Add(path);
            result.Add(new LegacySupplementaryImport(
                "bus-log-reference",
                TaskServerStore.DeterministicId("bus", InventoryPath(root, path)),
                InferProjectFromPath(root, path),
                null,
                "{}",
                path,
                await HashPathAsync(path, ct),
                new FileInfo(path).Length,
                true));
        }
        return result;
    }

    private static void AddAuthorityEntities(
        string root,
        LegacyAuthorityImport authority,
        ICollection<LegacySupplementaryImport> entities)
    {
        var source = Path.Combine(root, ".metadata", "attempt-authority.json");
        foreach (var item in authority.RunnerIdentities)
            entities.Add(new LegacySupplementaryImport("runner-identity", item.RunnerId, null, null,
                JsonSerializer.Serialize(item, Json), source, "", 0, false));
        foreach (var item in authority.CodingAttempts)
        {
            entities.Add(new LegacySupplementaryImport("coding-attempt", item.AttemptId, null, null,
                JsonSerializer.Serialize(item, Json), source, "", 0, false));
            if (item.Lease is { } lease)
                entities.Add(new LegacySupplementaryImport("lease", lease.LeaseId, null, null,
                    JsonSerializer.Serialize(lease, Json), source, "", 0, false));
        }
        foreach (var item in authority.ReviewAttempts)
        {
            entities.Add(new LegacySupplementaryImport("review-attempt", item.AttemptId, null, null,
                JsonSerializer.Serialize(item, Json), source, "", 0, false));
            if (item.Lease is { } lease)
                entities.Add(new LegacySupplementaryImport("lease", lease.LeaseId, null, null,
                    JsonSerializer.Serialize(lease, Json), source, "", 0, false));
        }
    }

    private static void AddTaskEvidenceEntities(
        IReadOnlyList<LegacyProjectImport> projects,
        ICollection<LegacySupplementaryImport> entities)
    {
        foreach (var project in projects)
        {
            entities.Add(new LegacySupplementaryImport(
                "project", project.ProjectId, project.Name, null, "{}", "", "", 0, false));
            foreach (var task in project.Tasks)
            {
                void AddMany(string kind, int count)
                {
                    for (var index = 0; index < count; index++)
                        entities.Add(new LegacySupplementaryImport(
                            kind, $"{task.TaskId}:{index}", project.Name, task.State,
                            task.MetadataJson, task.SourcePath, "", 0, false));
                }
                if (task.IsEpic) AddMany("epic", 1);
                AddMany("git-commit", task.GitCommits);
                AddMany("integration-record", task.IntegrationRecords);
                AddMany("pending-integration-record", task.PendingIntegrationRecords);
                AddMany("result-ref", task.ResultRefs);
                AddMany("delivery-ref", task.DeliveryRefs);
            }
        }
    }

    private static IReadOnlyList<LegacyOrphanImport> FindOrphanedAuthority(
        IReadOnlyList<LegacyProjectImport> projects,
        LegacyAuthorityImport authority)
    {
        var taskKeys = projects.SelectMany(project => project.Tasks)
            .Select(task => task.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var importableRunIds = authority.CodingAttempts
            .Where(attempt => taskKeys.Contains(attempt.TaskKey))
            .Select(attempt => attempt.AttemptId)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<LegacyOrphanImport>();

        foreach (var attempt in authority.CodingAttempts.Where(attempt => !taskKeys.Contains(attempt.TaskKey)))
        {
            result.Add(new LegacyOrphanImport(
                "coding-attempt", attempt.AttemptId, attempt.TaskKey,
                attempt.State == "leased" ? "process-unknown" : attempt.State,
                JsonSerializer.Serialize(attempt, Json)));
            if (attempt.Lease is { } lease)
                result.Add(new LegacyOrphanImport(
                    "lease", lease.LeaseId, attempt.TaskKey,
                    attempt.State == "leased" ? "process-unknown" : "completed",
                    JsonSerializer.Serialize(lease, Json)));
        }

        foreach (var attempt in authority.ReviewAttempts.Where(attempt =>
                     !taskKeys.Contains(attempt.TaskKey)
                     || !importableRunIds.Contains(attempt.SourceRunAttemptId)))
        {
            result.Add(new LegacyOrphanImport(
                "review-attempt", attempt.AttemptId, attempt.TaskKey,
                attempt.State == "leased" ? "process-unknown" : attempt.State,
                JsonSerializer.Serialize(attempt, Json)));
            if (attempt.Lease is { } lease)
                result.Add(new LegacyOrphanImport(
                    "lease", lease.LeaseId, attempt.TaskKey,
                    attempt.State == "leased" ? "process-unknown" : "completed",
                    JsonSerializer.Serialize(lease, Json)));
        }

        foreach (var (taskKey, fence) in authority.LastFenceByTask.Where(item => !taskKeys.Contains(item.Key)))
            result.Add(new LegacyOrphanImport(
                "fence-counter", taskKey, taskKey, "historical",
                JsonSerializer.Serialize(new { taskKey, lastFence = fence }, Json)));

        return result.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Task key prefixes are unique in the store. Legacy projects without a shortCode all fall
    /// back to the same literal, so a collision is a data condition of real workspaces rather
    /// than a programming error. Resolve it deterministically and report it, so the import
    /// degrades with a named warning instead of surfacing a raw unique-constraint failure.
    /// </summary>
    private static LegacyProjectImport[] EnsureUniqueTaskKeyPrefixes(
        IReadOnlyList<LegacyProjectImport> projects,
        ICollection<string> warnings)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new LegacyProjectImport[projects.Count];
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            if (taken.Add(project.Prefix))
            {
                result[index] = project;
                continue;
            }
            var suffix = 2;
            string candidate;
            do candidate = project.Prefix + suffix++.ToString(CultureInfo.InvariantCulture);
            while (!taken.Add(candidate));
            warnings.Add(
                $"Legacy degradation: project '{project.Name}' shares task key prefix '{project.Prefix}' " +
                $"with an earlier project and is imported as '{candidate}'.");
            result[index] = project with { Prefix = candidate };
        }
        return result;
    }

    private static void AddOrphanWarnings(
        IReadOnlyList<LegacyOrphanImport> orphans,
        ICollection<string> warnings)
    {
        foreach (var group in orphans.GroupBy(item => item.Kind, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
            warnings.Add(
                $"Legacy degradation: {group.Count()} {group.Key} record(s) reference removed tasks and will be retained in the orphan ledger.");
    }

    private static async Task<IReadOnlyList<LegacyOrphanImport>> FindOrphanedIntegrationRecordsAsync(
        string root,
        IReadOnlyList<LegacyProjectImport> projects,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var taskKeys = projects.SelectMany(project => project.Tasks)
            .Select(task => task.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = projects.SelectMany(project => project.Tasks).Select(task => task.SourcePath)
            .Concat(Directory.Exists(Path.Combine(root, ".metadata"))
                ? Directory.EnumerateFiles(Path.Combine(root, ".metadata"), "*.json", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).Contains("integration", StringComparison.OrdinalIgnoreCase))
                : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var result = new List<LegacyOrphanImport>();
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var resultCountBeforeFile = result.Count;
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var index = 0;
                foreach (var record in EnumerateIntegrationRecords(document.RootElement))
                {
                    var taskKey = ReadString(record, "taskKey") ?? ReadString(record, "jobId");
                    if (string.IsNullOrWhiteSpace(taskKey) || taskKeys.Contains(taskKey))
                    {
                        index++;
                        continue;
                    }
                    taskKey = taskKey.Trim().ToUpperInvariant();
                    result.Add(new LegacyOrphanImport(
                        "integration-record",
                        TaskServerStore.DeterministicId("int", $"{InventoryPath(root, path)}:{index}"),
                        taskKey,
                        "historical",
                        record.GetRawText()));
                    index++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Drop whatever this file contributed so the inventory stays a whole-file decision.
                result.RemoveRange(resultCountBeforeFile, result.Count - resultCountBeforeFile);
                warnings.Add(
                    $"Skipped unreadable integration records '{InventoryPath(root, path)}': {exception.Message}");
                continue;
            }
            if (result.Count > resultCountBeforeFile)
                sourceFiles.Add(path);
        }
        return result;
    }

    private static IEnumerable<JsonElement> EnumerateIntegrationRecords(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (property.Name.Contains("integration", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object) yield return item.Clone();
                    continue;
                }
                foreach (var item in EnumerateIntegrationRecords(property.Value)) yield return item;
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in json.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Object
                    && (ReadString(value, "taskKey") is not null || ReadString(value, "jobId") is not null))
                {
                    yield return value.Clone();
                    continue;
                }
                foreach (var item in EnumerateIntegrationRecords(value)) yield return item;
            }
        }
    }

    private static IReadOnlyList<string> FindRegisteredRepositoryRoots(string root)
    {
        var path = Path.Combine(root, ".metadata", "projects.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Array) return [];
            return projects.EnumerateArray()
                .Select(project => ReadString(project, "repositoryPath"))
                .Where(value => !string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                .Select(value => Path.GetFullPath(value!))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? InferProjectFromPath(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var index = Array.FindIndex(parts, part => string.Equals(part, "projects", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
    }

    private static string InventoryPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith("../", StringComparison.Ordinal)
            ? relative
            : "external/" + Path.GetFullPath(path).Replace('\\', '/').Replace(':', '_');
    }

    private static string SourceKind(string root, string path)
    {
        var relative = InventoryPath(root, path);
        if (relative.StartsWith("logs/bus/", StringComparison.Ordinal)) return "bus-log-reference";
        if (relative.Contains("/results/", StringComparison.Ordinal)
            || relative.Contains("/attachments/", StringComparison.Ordinal)
            || relative.Contains("/logs/", StringComparison.Ordinal)) return "artifact-reference";
        if (relative.EndsWith("workbench.json", StringComparison.Ordinal)) return "dossier";
        if (relative.Contains("orchestrator-sessions", StringComparison.Ordinal)) return "orchestrator-session";
        if (relative.EndsWith(".jsonl", StringComparison.Ordinal)) return "event-stream";
        return "metadata";
    }

    private static async Task<string> HashPathAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static int ArrayLength(JsonElement json, string property)
        => json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static bool HasString(JsonElement json, string property, string expected)
        => json.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Array
           && value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
               && string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase));

    private static int CountNamedStrings(JsonElement json, params string[] names)
    {
        var count = 0;
        if (json.ValueKind == JsonValueKind.Object)
            foreach (var property in json.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString())) count++;
                count += CountNamedStrings(property.Value, names);
            }
        else if (json.ValueKind == JsonValueKind.Array)
            foreach (var item in json.EnumerateArray()) count += CountNamedStrings(item, names);
        return count;
    }

    private static string InferProjectName(
        string root,
        string taskDirectory,
        JsonElement json,
        string taskKey,
        IReadOnlyDictionary<string, RegisteredLegacyProject> registeredProjects)
    {
        var configured = ReadString(json, "projectName") ?? ReadString(json, "project");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var relative = Path.GetRelativePath(root, taskDirectory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectsIndex = Array.FindIndex(relative, part => string.Equals(part, "projects", StringComparison.OrdinalIgnoreCase));
        if (projectsIndex >= 0 && projectsIndex + 1 < relative.Length)
        {
            var folder = relative[projectsIndex + 1];
            return registeredProjects.TryGetValue(folder, out var registered) ? registered.Name : folder;
        }
        return taskKey.Split('-', 2)[0];
    }

    private static IReadOnlyDictionary<string, RegisteredLegacyProject> ReadRegisteredProjects(string root)
    {
        var path = Path.Combine(root, ".metadata", "projects.json");
        if (!File.Exists(path)) return new Dictionary<string, RegisteredLegacyProject>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
            return new Dictionary<string, RegisteredLegacyProject>(StringComparer.OrdinalIgnoreCase);
        return projects.EnumerateArray()
            .Select(item => new RegisteredLegacyProject(
                ReadString(item, "id") ?? throw new InvalidDataException("A registered legacy project has no id."),
                ReadString(item, "displayName") ?? ReadString(item, "id")!,
                ReadString(item, "shortCode") ?? "LEG",
                ReadLong(item, "nextTaskKeySeq", 1)))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
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
        LegacyAuthorityImport Authority,
        IReadOnlyList<LegacySupplementaryImport> Supplementary,
        IReadOnlyList<LegacyMigrationSourceFile> SourceFiles,
        IReadOnlyList<LegacyOrphanImport> Orphans);

    private sealed record RegisteredLegacyProject(string Id, string Name, string Prefix, long NextTaskNumber);
}
