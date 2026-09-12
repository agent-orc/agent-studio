using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Persistence;

namespace AgentStudio.Docs;

/// <summary>
/// Read-only Wiki-source discovery for Dossiers. Canonical items
/// are folders that carry a <c>workbench.json</c> descriptor and live anywhere
/// under docs/ in the same checkout or configured Git-ref snapshot used by the
/// Wiki viewer. Each Dossier sits with its own theme, for example
/// docs/operations/&lt;id&gt;/ or docs/quality/&lt;id&gt;/. The recursive scan skips
/// dot-directories and node_modules-like folders. The small legacy list is an
/// explicit migration bridge for named, already-existing artifacts, never a
/// heuristic scan of arbitrary HTML.
/// </summary>
public sealed class WorkbenchCatalogueService
{
    private const long MaxHtmlBytes = 20L * 1024 * 1024;

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly GitService _git;
    private readonly ManagedRepositoryMutationService _repositoryMutations;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly ConcurrentDictionary<string, DecisionCountSnapshot> _decisionCounts =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, object> KeyAssignmentGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> CurrentStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending", "decided" };
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending", "decided", "documented", "archived" };
    private static readonly HashSet<string> AllowedPhases = new(StringComparer.Ordinal)
        { "informational", "shaping", "testing", "decision-ready" };

    private sealed record LegacyWorkbench(
        string Id, string Title, string Summary, string RepoRelPath, string Phase,
        string[] SourceTaskKeys);
    private sealed record DecisionCountSnapshot(long Length, DateTime LastWriteUtc, int Count);

    private static readonly LegacyWorkbench[] LegacyPilot =
    [
        // "Pipeline Dossier" removed 2026-07-24: idea discarded by the operator.
        new("workbench-mockup-family", "Dossier mockup family",
            "Shape the Dossier host, list, viewer, and later decision surfaces.",
            "docs/concepts/mockups/experimentier-workbench.html", "testing", ["AGT-2122"]),
        new("app-survey", "Application survey",
            "Understand the current product surfaces through the visual survey findings.",
            "docs/quality/design/app-survey-2026-07-11.html", "decision-ready", []),
        // "Decoupled lifecycles" removed 2026-08-08: wiki page deleted by the operator.
    ];

    public WorkbenchCatalogueService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        GitService git,
        IAtomicJsonFileWriter? fileWriter = null,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _scanner = scanner;
        _registry = registry;
        _git = git;
        _fileWriter = fileWriter ?? new AtomicJsonFileWriter();
        _repositoryMutations = repositoryMutations
            ?? new ManagedRepositoryMutationService(git);
    }

    public WorkbenchCatalogue? List(string projectName, bool includeHistory = false)
    {
        var source = ResolveSource(projectName);
        if (source == null) return null;
        return ListFromSource(projectName, source, includeHistory);
    }

    /// <summary>
    /// Builds the catalogue from an already-selected Wiki source. The central
    /// Wiki snapshot uses this overload so a moving configured ref cannot be
    /// resolved a second time midway through one cache fill.
    /// </summary>
    internal WorkbenchCatalogue ListFromSource(
        string projectName,
        WikiSourceContext source,
        bool includeHistory = false)
    {
        var root = source.BaseDir;

        var project = ResolveProject(projectName);
        if (project != null && source.Info.Writable) EnsureCanonicalKeys(root, project);

        var items = DiscoverCanonical(root, project, requireKey: source.Info.Writable);
        foreach (var duplicate in items
                     .Where(item => item.Valid)
                     .GroupBy(item => item.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .SelectMany(group => group)
                     .ToList())
        {
            var index = items.IndexOf(duplicate);
            items[index] = duplicate with
            {
                Valid = false,
                Status = "invalid",
                Error = "Dossier id is duplicated by another canonical descriptor.",
            };
        }
        foreach (var duplicate in items
                     .Where(item => item.Valid && item.Key != null)
                     .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .SelectMany(group => group)
                     .ToList())
        {
            var index = items.IndexOf(duplicate);
            items[index] = duplicate with
            {
                Valid = false,
                Status = "invalid",
                Error = "Document reference key is duplicated by another canonical descriptor.",
            };
        }
        var ids = items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var legacy in LegacyPilot)
        {
            if (ids.Contains(legacy.Id)) continue;
            var full = ContainedPath(root, legacy.RepoRelPath);
            if (full == null || !File.Exists(full)) continue;
            if (!IsHtmlWithinLimit(full))
            {
                items.Add(new WorkbenchListItem(
                    legacy.Id, legacy.Title, legacy.Summary, "invalid", legacy.Phase,
                    File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, false,
                    $"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Dossier limit.",
                    legacy.SourceTaskKeys));
                continue;
            }
            items.Add(new WorkbenchListItem(
                legacy.Id, legacy.Title, legacy.Summary, "active", legacy.Phase,
                File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, true, null,
                legacy.SourceTaskKeys));
        }

        ApplyDocumentationProjection(items);

        return FilterCatalogue(
            new WorkbenchCatalogue(projectName, true, items.Count, items),
            includeHistory);
    }

    /// <summary>All valid document reference keys currently owned by one project.</summary>
    public IReadOnlySet<string> KnownKeys(string projectName) =>
        (List(projectName, includeHistory: true)?.Items ?? [])
        .Where(item => item.Valid && !string.IsNullOrWhiteSpace(item.Key))
        .Select(item => item.Key!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns every card that points at <paramref name="key"/> through
    /// <c>references.workbenches</c>. Legacy descriptor keys stay separate so
    /// the next viewer slice can merge them explicitly without presenting
    /// hand-maintained data as a derived edge.
    /// </summary>
    public WorkbenchTaskReferences? References(string projectName, string key)
    {
        var item = List(projectName, includeHistory: true)?.Items.SingleOrDefault(candidate =>
            candidate.Valid && string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item == null || item.Key == null) return null;
        var links = _scanner.GetReferenceIndex()
            .Dependents(item.Key, TaskReferenceKinds.Workbenches);
        return new WorkbenchTaskReferences(
            projectName,
            item.Key,
            item.Id,
            item.SourceTaskKeys
                .Concat(item.RelatedTaskKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            links);
    }

    /// <summary>
    /// Returns every configured, non-archived project handle that can own a
    /// Workbench. Registry records are authoritative when present; WatchPaths
    /// remain the compatibility source for local and test configurations.
    /// </summary>
    public IReadOnlyList<string> ListProjectNames()
    {
        var names = _registry.List()
            .Where(project => !project.Archived)
            .Select(project => project.DisplayName)
            .Concat(_scanner.GetWatchPaths().Select(entry => entry.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names;
    }

    /// <summary>
    /// One read model shared by the workspace-wide and project-scoped list
    /// pages. History is included so the client can render discarded and
    /// completed groups independently without issuing a second request.
    /// </summary>
    public WorkbenchOverview ListOverview(IEnumerable<string> projectNames, string? projectName = null)
    {
        var items = new List<WorkbenchOverviewItem>();
        foreach (var name in projectNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var catalogue = List(name, includeHistory: true);
            if (catalogue == null) continue;
            items.AddRange(catalogue.Items.Select(item => new WorkbenchOverviewItem(name, item)));
        }
        return BuildOverview(items, projectName);
    }

    public WorkbenchDocument? Read(string projectName, string id)
    {
        if (!SafeId(id)) return null;
        var source = ResolveSource(projectName);
        if (source == null) return null;
        return ReadFromSource(projectName, id, source);
    }

    /// <summary>
    /// Reads a Workbench from the exact source already published by the Wiki
    /// cache. Supplying the cached catalogue keeps list validation and document
    /// resolution on one immutable view.
    /// </summary>
    internal WorkbenchDocument? ReadFromSource(
        string projectName,
        string id,
        WikiSourceContext source,
        WorkbenchCatalogue? catalogue = null)
    {
        if (!SafeId(id)) return null;
        var root = source.BaseDir;
        var item = (catalogue ?? ListFromSource(projectName, source, includeHistory: true)).Items
            .FirstOrDefault(x => x.Id == id);
        if (item is not { Valid: true }) return null;
        var full = ContainedPath(root, item.EntryPath);
        if (full == null || !File.Exists(full)) return null;
        var html = ReadHtmlWithinLimit(full);
        if (html == null) return null;
        var docsRoot = ContainedPath(root, "docs");
        string? descriptorPath = null;
        if (docsRoot != null)
        {
            var descriptorMatches = EnumerateWorkbenchDescriptors(docsRoot)
                .Where(path =>
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), id, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (descriptorMatches.Count == 1) descriptorPath = descriptorMatches[0];
        }
        var provenancePaths = new List<string> { item.EntryPath };
        if (descriptorPath != null)
            provenancePaths.Add(Path.GetRelativePath(root, descriptorPath).Replace('\\', '/'));
        var status = source.Info.Writable ? _git.GetStatusForRepoRoot(root) : null;
        var workingTreeModified = status?.IsRepo == true && status.Files.Any(change =>
            provenancePaths.Any(path => ChangeTouchesPath(change.Path, path)));
        var revision = source.Info.Writable
            ? status?.IsRepo == true && status.Error == null && !workingTreeModified
                ? _git.GetHeadShaCached(root)
                : null
            : source.Info.Commit;
        var fingerprint = descriptorPath == null
            ? null
            : ComputeWorkbenchFingerprint(descriptorPath, full);
        var entryFingerprint = ComputeEntryFingerprint(full);
        return new WorkbenchDocument(
            item,
            html,
            source.Info.Writable ? status?.Branch : source.Info.Branch,
            revision,
            workingTreeModified,
            fingerprint,
            entryFingerprint);
    }

    /// <summary>
    /// Applies the public current/history projection to a complete cached
    /// catalogue without scanning or resolving a repository source again.
    /// </summary>
    internal static WorkbenchCatalogue FilterCatalogue(
        WorkbenchCatalogue catalogue,
        bool includeHistory)
    {
        var visible = WorkbenchOverviewPolicy.Sort(catalogue.Items
                .Where(item => !item.Valid || includeHistory || CurrentStatuses.Contains(item.Status))
                .Select(item => new WorkbenchOverviewItem(catalogue.ProjectName, item)))
            .Select(item => item.Workbench)
            .ToList();
        return new WorkbenchCatalogue(
            catalogue.ProjectName,
            includeHistory,
            visible.Count,
            visible);
    }

    internal static WorkbenchOverview BuildOverview(
        IEnumerable<WorkbenchOverviewItem> items,
        string? projectName)
    {
        var sorted = WorkbenchOverviewPolicy.Sort(items);
        return new WorkbenchOverview(
            ProjectName: projectName,
            Count: sorted.Count,
            CurrentCount: sorted.Count(item => CurrentStatuses.Contains(item.Workbench.Status)),
            HistoryCount: sorted.Count(item => item.Workbench.Status is "archived" or "documented"),
            Items: sorted);
    }

    /// <summary>
    /// Resolves the one canonical descriptor owned by a Workbench. Decision
    /// mutations use the catalogue's containment and validation rules; legacy
    /// pilot rows never acquire mutation authority. Both descriptor schemas are
    /// resolvable (AGT-2375): schema v1 still carries the majority of the
    /// repository's Workbenches, and its visibility hangs on the same file.
    /// </summary>
    internal WorkbenchMutationSnapshot? ResolveCanonicalForMutation(string projectName, string id)
    {
        if (!SafeId(id)) return null;
        var root = ResolveWritableRoot(projectName);
        if (root == null) return null;
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return null;
        var matches = EnumerateWorkbenchDescriptors(docsRoot)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), id, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1) return null;

        var item = List(projectName, includeHistory: true)?.Items
            .Where(candidate => candidate.Valid && candidate.Id == id)
            .ToList();
        if (item is not { Count: 1 }) return null;

        var descriptorPath = ContainedPath(root, Path.GetRelativePath(root, matches[0]));
        var entryPath = ContainedPath(root, item[0].EntryPath);
        if (descriptorPath == null || entryPath == null) return null;
        try
        {
            var descriptorText = File.ReadAllText(descriptorPath);
            var descriptor = JsonNode.Parse(descriptorText) as JsonObject;
            var schemaVersion = descriptor?["schemaVersion"]?.GetValue<int>();
            if (descriptor == null || schemaVersion is not (1 or 2)) return null;
            var status = _git.GetStatusForRepoRoot(root);
            var descriptorRel = Path.GetRelativePath(root, descriptorPath).Replace('\\', '/');
            var entryRel = Path.GetRelativePath(root, entryPath).Replace('\\', '/');
            var dirty = status.IsRepo && status.Files.Any(change =>
                ChangeTouchesPath(change.Path, descriptorRel)
                || ChangeTouchesPath(change.Path, entryRel));
            return new WorkbenchMutationSnapshot(
                root,
                descriptorPath,
                descriptorRel,
                entryPath,
                entryRel,
                descriptor,
                schemaVersion!.Value,
                ComputeDescriptorFingerprint(descriptorText),
                ComputeWorkbenchFingerprint(descriptorPath, entryPath),
                status.IsRepo ? _git.ReadHeadShaAt(root) : null,
                dirty,
                item[0]);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    internal bool OperationIdOwnedByAnotherWorkbench(
        string projectName, string operationId, string workbenchId)
    {
        var root = ResolveWritableRoot(projectName);
        var docsRoot = root == null ? null : ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return false;
        foreach (var descriptorPath in EnumerateWorkbenchDescriptors(docsRoot))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptorPath));
                var obj = json.RootElement;
                // Both schemas store the receipt under the same key, so both
                // can own an operationId (AGT-2375).
                if (RequiredInt(obj, "schemaVersion") is not (1 or 2)
                    || !obj.TryGetProperty("decision", out var decision)
                    || decision.ValueKind != JsonValueKind.Object
                    || OptionalString(decision, "operationId") != operationId)
                    continue;
                return RequiredString(obj, "id") != workbenchId;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                SilentCatch.Note(ex, "Dossier operation-id ownership scan skipped an invalid descriptor.");
            }
        }
        return false;
    }

    /// <summary>
    /// Generic Wiki classification must not stand in for the reasoned,
    /// explicitly confirmed Workbench archive decision. Both descriptor schemas
    /// own their folder: the sidecar archive bug is a property of the
    /// <c>workbench.json</c> descriptor, not of its version, and schema v1 still
    /// carries the large majority of the repository's Workbenches - gating on
    /// v2 alone would have left every one of them exposed (AGT-2375).
    /// </summary>
    public bool OwnsCanonicalPath(string projectName, string relPath)
    {
        var root = ResolveReadRoot(projectName);
        var docsRoot = root == null ? null : ContainedPath(root, "docs");
        if (root == null || docsRoot == null || !Directory.Exists(docsRoot)) return false;
        var normalized = relPath.Replace('\\', '/').TrimStart('/');
        foreach (var descriptorPath in EnumerateWorkbenchDescriptors(docsRoot))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptorPath));
                var schema = RequiredInt(json.RootElement, "schemaVersion");
                if (schema is not (1 or 2)) continue;
                // pageKind is a schema v2 field; a v1 descriptor is a Workbench
                // by the presence of workbench.json alone.
                if (schema >= 2 && RequiredString(json.RootElement, "pageKind") != "workbench")
                    continue;
                var folder = Path.GetRelativePath(root, Path.GetDirectoryName(descriptorPath)!)
                    .Replace('\\', '/').TrimEnd('/');
                var docsRelativeFolder = Path.GetRelativePath(
                        docsRoot, Path.GetDirectoryName(descriptorPath)!)
                    .Replace('\\', '/').TrimEnd('/');
                if (PathIsWithin(normalized, folder)
                    || PathIsWithin(normalized, docsRelativeFolder))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                SilentCatch.Note(ex, "Dossier Wiki-classification ownership scan skipped an invalid descriptor.");
            }
        }
        return false;
    }

    private static bool PathIsWithin(string candidate, string folder) =>
        candidate.Equals(folder, PathComparison)
        || candidate.StartsWith(folder + "/", PathComparison);

    private List<WorkbenchListItem> DiscoverCanonical(
        string root,
        ProjectRecord? project,
        bool requireKey)
    {
        var result = new List<WorkbenchListItem>();
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return result;
        foreach (var found in EnumerateWorkbenchDescriptors(docsRoot))
        {
            var dir = Path.GetDirectoryName(found)!;
            var folder = Path.GetFileName(dir);
            var descriptorRel = Path.GetRelativePath(root, found).Replace('\\', '/');
            var safeDir = ContainedPath(root, Path.GetRelativePath(root, dir));
            if (safeDir == null)
            {
                result.Add(Invalid(folder, "Dossier folder is a symbolic link or reparse point.", descriptorRel));
                continue;
            }
            var descriptor = ContainedPath(root,
                Path.GetRelativePath(root, Path.Combine(safeDir, "workbench.json")));
            if (descriptor == null || !File.Exists(descriptor))
            {
                result.Add(Invalid(folder, descriptor == null
                    ? "workbench.json is a symbolic link or reparse point."
                    : "Missing workbench.json.", descriptorRel));
                continue;
            }
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptor));
                var obj = json.RootElement;
                var schema = RequiredInt(obj, "schemaVersion");
                if (schema is not (1 or 2)) throw new InvalidDataException("schemaVersion must be 1 or 2.");
                var id = RequiredString(obj, "id");
                var key = OptionalString(obj, "key");
                var title = RequiredString(obj, "title");
                var summary = RequiredString(obj, "summary");
                var entrypoint = RequiredString(obj, "entrypoint");
                var lifecycleState = schema >= 2 ? RequiredString(obj, "lifecycleState") : null;
                if (schema >= 2 && !AllowedLifecycleStates.Contains(lifecycleState!))
                    throw new InvalidDataException($"Unsupported lifecycleState '{lifecycleState}'.");
                var updatedText = schema >= 2 ? RequiredString(obj, "editedAt") : RequiredString(obj, "updatedAt");
                var editedBy = schema >= 2 ? RequiredString(obj, "editedBy") : OptionalString(obj, "editedBy");
                var lifecycleHistory = schema >= 2
                    ? RequiredLifecycleHistory(obj, lifecycleState!, editedBy!, updatedText)
                    : [];
                if (schema >= 2 && RequiredString(obj, "pageKind") != "workbench")
                    throw new InvalidDataException("pageKind must be workbench.");
                if (schema >= 2 && (obj.TryGetProperty("status", out _) || obj.TryGetProperty("updatedAt", out _)))
                    throw new InvalidDataException("schemaVersion 2 must not store legacy status or updatedAt fields.");
                // Both schemas store the receipt; only v2 couples it to
                // lifecycleState (null below = the reduced v1 projection).
                var decision = ReadDecision(obj, lifecycleState);
                var status = schema >= 2
                    ? StatusFromDecision(lifecycleState!, decision)
                    : RequiredString(obj, "status");
                var phase = OptionalString(obj, "phase");
                if (!SafeId(id) || id != folder) throw new InvalidDataException("id must match the containing folder.");
                if (project != null && requireKey && string.IsNullOrWhiteSpace(key))
                    throw new InvalidDataException("key is required after project discovery.");
                if (key != null && !TryWorkbenchKeyNumber(key, null, out _))
                    throw new InvalidDataException(
                        "key must use the project reference form 'PROJECT-W<number>'.");
                if (!AllowedStatuses.Contains(status)) throw new InvalidDataException($"Unsupported status '{status}'.");
                if (phase != null && !AllowedPhases.Contains(phase)) throw new InvalidDataException($"Unsupported phase '{phase}'.");
                if (!IsUtcLifecycleTimestamp(updatedText, out var updated))
                    throw new InvalidDataException($"{(schema >= 2 ? "editedAt" : "updatedAt")} must be an ISO UTC timestamp ending in Z.");
                var extension = Path.GetExtension(entrypoint);
                if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("entrypoint must be HTML.");
                var full = ContainedPath(safeDir, entrypoint);
                if (full == null || !File.Exists(full)) throw new InvalidDataException("entrypoint is missing or escapes its Dossier folder.");
                if (!IsHtmlWithinLimit(full))
                    throw new InvalidDataException($"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Dossier limit.");
                var repoRel = Path.GetRelativePath(root, full).Replace('\\', '/');
                result.Add(new WorkbenchListItem(id, title, summary, status, phase,
                    updated.UtcDateTime, repoRel, true, null, DescriptorTaskKeys(obj))
                {
                    Key = key,
                    Pattern = ArticlePatterns.Normalize(OptionalString(obj, "pattern")),
                    DescriptorSourceTaskKeys = StringArray(obj, "sourceTaskKeys"),
                    RelatedTaskKeys = StringArray(obj, "relatedTaskKeys"),
                    LifecycleState = lifecycleState ?? LifecycleFromStatus(status, phase),
                    EditedBy = editedBy,
                    LifecycleHistory = lifecycleHistory,
                    Decision = decision,
                    DecisionStage = DecisionStage(decision),
                    OpenDecisionCount = OpenDecisionCount(full, status),
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                result.Add(Invalid(folder, ex.Message, descriptorRel, descriptor));
            }
        }
        return result;
    }

    /// <summary>
    /// Backfills missing keys before the descriptor is projected. Assignment is
    /// serialized per project, derives the registry floor from the descriptors,
    /// reserves a monotonic sequence, then swaps the complete JSON document into
    /// place through the same atomic persistence boundary as decision writes.
    /// Existing keys are never rewritten.
    /// </summary>
    private void EnsureCanonicalKeys(string root, ProjectRecord project)
    {
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return;
        var gateKey = $"{project.Id}:{Path.GetFullPath(root)}";
        lock (KeyAssignmentGates.GetOrAdd(gateKey, _ => new object()))
        {
            var descriptors = EnumerateWorkbenchDescriptors(docsRoot)
                .OrderBy(path => path, PathComparer)
                .ToList();
            var highest = 0;
            var assignments = new List<(string Path, string RelativePath, JsonObject Descriptor)>();
            foreach (var descriptorPath in descriptors)
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(descriptorPath));
                    var key = OptionalString(json.RootElement, "key");
                    if (key != null && TryWorkbenchKeyNumber(key, null, out var number))
                        highest = Math.Max(highest, number);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    SilentCatch.Note(ex, "Document key floor skipped an unreadable descriptor.");
                }
            }
            _registry.EnsureWorkbenchKeyFloor(project.Id, highest + 1);

            foreach (var descriptorPath in descriptors)
            {
                try
                {
                    var text = File.ReadAllText(descriptorPath);
                    var descriptor = JsonNode.Parse(text) as JsonObject;
                    if (descriptor == null || descriptor["key"] is JsonValue) continue;
                    var schema = descriptor["schemaVersion"]?.GetValue<int>();
                    var id = descriptor["id"]?.GetValue<string>();
                    var folder = Path.GetFileName(Path.GetDirectoryName(descriptorPath));
                    if (schema is not (1 or 2) || id == null || !SafeId(id) || id != folder)
                        continue;

                    var seq = _registry.IssueNextWorkbenchKey(project.Id);
                    descriptor["key"] = $"{project.ShortCode}-W{seq}";
                    assignments.Add((
                        descriptorPath,
                        Path.GetRelativePath(root, descriptorPath).Replace('\\', '/'),
                        descriptor));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    SilentCatch.Note(ex, "Document reference key assignment failed.");
                }
            }

            if (assignments.Count == 0) return;
            var persisted = _repositoryMutations.Execute(
                project.DisplayName,
                root,
                "workbench-key-discovery",
                "chore(workbench): assign document keys",
                assignments.Select(assignment => assignment.RelativePath).ToArray(),
                () =>
                {
                    foreach (var assignment in assignments)
                    {
                        _fileWriter.Write(assignment.Path, assignment.Descriptor.ToJsonString(
                            new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            if (!persisted.Success)
            {
                SilentCatch.Note(
                    new InvalidOperationException(persisted.Error ?? "Document key persistence failed."),
                    "Document reference keys were not persisted because the managed commit boundary failed.");
            }
        }
    }

    /// <summary>
    /// Recursively yields every <c>workbench.json</c> descriptor under docs/,
    /// skipping dot-directories, node_modules-like folders, the shared assets
    /// tree, and reparse points so a Workbench can live with its own theme.
    /// </summary>
    private static IEnumerable<string> EnumerateWorkbenchDescriptors(string docsRoot)
    {
        var stack = new Stack<string>();
        stack.Push(docsRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files = Directory.GetFiles(dir, "workbench.json");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("assets", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isReparse;
                try { isReparse = (new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (isReparse)
                {
                    // A symlinked / junctioned Workbench folder must be surfaced
                    // (and later refused by the ContainedPath guard) rather than
                    // silently skipped - never recurse through the reparse point.
                    var linkedDescriptor = Path.Combine(sub, "workbench.json");
                    if (File.Exists(linkedDescriptor)) yield return linkedDescriptor;
                    continue;
                }
                stack.Push(sub);
            }
        }
    }

    private static WorkbenchListItem Invalid(string folder, string error, string entryPath, string? safePath = null) =>
        new(SafeId(folder) ? folder : "invalid-workbench", folder, "Descriptor needs repair.",
            "invalid", null, safePath != null && File.Exists(safePath)
                ? File.GetLastWriteTimeUtc(safePath)
                : DateTime.UtcNow,
            entryPath, false, error, []);

    private WikiSourceContext? ResolveSource(string projectName) =>
        ProjectWikiSourceResolver.Resolve(projectName, _scanner, _registry, _git);

    private string? ResolveReadRoot(string projectName) => ResolveSource(projectName)?.BaseDir;

    private string? ResolveWritableRoot(string projectName)
    {
        var source = ResolveSource(projectName);
        return source?.Info.Writable == true ? source.BaseDir : null;
    }

    private ProjectRecord? ResolveProject(string projectName) =>
        ProjectWikiSourceResolver.ResolveProject(projectName, _scanner, _registry);

    private static string? ContainedPath(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
        try
        {
            var rootFull = Path.GetFullPath(root);
            var rootPrefix = Path.EndsInDirectorySeparator(rootFull)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, PathComparison)) return null;

            var current = rootFull;
            var segments = Path.GetRelativePath(rootFull, full)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsHtmlWithinLimit(string path)
    {
        try { return new FileInfo(path).Length <= MaxHtmlBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ReadHtmlWithinLimit(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxHtmlBytes) return null;
            using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxHtmlBytes));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxHtmlBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private int OpenDecisionCount(string entrypoint, string status)
    {
        if (status != "decision-pending") return 0;
        try
        {
            var info = new FileInfo(entrypoint);
            if (_decisionCounts.TryGetValue(entrypoint, out var cached)
                && cached.Length == info.Length
                && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Count;

            var html = ReadHtmlWithinLimit(entrypoint);
            // A pending legacy document without inline markup still represents
            // one Workbench-level gate. Once points exist, expose their exact
            // count so the queue can prioritize the operator's real workload.
            var count = Math.Max(1, WorkbenchDecisionPointCounter.Count(html ?? ""));
            _decisionCounts[entrypoint] = new(info.Length, info.LastWriteTimeUtc, count);
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Dossier decision count fell back to its pending lifecycle gate.");
            return 1;
        }
    }

    private static bool ChangeTouchesPath(string changedPath, string targetPath)
    {
        var normalizedTarget = targetPath.Replace('\\', '/').TrimStart('/');
        var normalizedChange = changedPath.Replace('\\', '/').Trim().Trim('"');
        var renameSeparator = normalizedChange.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            normalizedChange = normalizedChange[(renameSeparator + 4)..].Trim().Trim('"');
        return normalizedChange.Equals(normalizedTarget, PathComparison)
            || normalizedChange.EndsWith('/')
                && normalizedTarget.StartsWith(normalizedChange, PathComparison);
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 80
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool TryWorkbenchKeyNumber(string key, string? shortCode, out int number)
    {
        number = 0;
        var trimmed = key.Trim();
        if (!string.Equals(key, trimmed, StringComparison.Ordinal)
            || !string.Equals(trimmed, trimmed.ToUpperInvariant(), StringComparison.Ordinal))
            return false;
        var separator = trimmed.LastIndexOf("-W", StringComparison.OrdinalIgnoreCase);
        if (separator <= 0) return false;
        var prefix = trimmed[..separator].ToUpperInvariant();
        if (!ShortCodeGenerator.ValidateFormat(prefix)) return false;
        if (shortCode != null
            && !string.Equals(prefix, shortCode, StringComparison.OrdinalIgnoreCase))
            return false;
        var tail = trimmed[(separator + 2)..];
        return tail.Length > 0
            && tail.All(char.IsAsciiDigit)
            && int.TryParse(tail, out number)
            && number > 0;
    }
    private static string RequiredString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"{name} is required.");
    private static int RequiredInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : throw new InvalidDataException($"{name} is required.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] StringArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Projects a stored decision receipt. <paramref name="lifecycleState"/> is
    /// null for schema v1 descriptors, which have no lifecycle field: the
    /// receipt is then validated on its own terms and simply carries its
    /// provenance (above all the <c>operationId</c> the decision service needs
    /// to answer a retry idempotently). Everything else - shape, outcome,
    /// settled-state invariants - is the same contract for both schemas, so a
    /// receipt accepted on write is never rejected on the next read.
    /// </summary>
    private static WorkbenchDecisionProjection? ReadDecision(JsonElement obj, string? lifecycleState)
    {
        if (!obj.TryGetProperty("decision", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (lifecycleState is "decided" or "done")
                throw new InvalidDataException("Settled lifecycleState requires a decision receipt.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("decision must be an object or null.");

        var outcome = RequiredString(value, "outcome");
        if (outcome is not ("feature-spawn" or "archive" or "rework"))
            throw new InvalidDataException($"Unsupported decision outcome '{outcome}'.");
        var state = RequiredString(value, "state");
        if (state is not ("pending" or "failed" or "succeeded"))
            throw new InvalidDataException($"Unsupported decision state '{state}'.");
        var operationId = RequiredString(value, "operationId");
        if (!WorkbenchDecisionContracts.SafeOperationId(operationId))
            throw new InvalidDataException("decision operationId is malformed.");
        var preparedAt = RequiredString(value, "preparedAt");
        var preparedBy = RequiredString(value, "preparedBy");
        if (!IsUtcLifecycleTimestamp(preparedAt, out _))
            throw new InvalidDataException("decision preparedAt must be an ISO UTC timestamp ending in Z.");
        var sourceRevision = OptionalString(value, "sourceRevision");
        var sourceFingerprint = OptionalString(value, "sourceFingerprint");
        var sourceEntryFingerprint = OptionalString(value, "sourceEntryFingerprint");
        if (string.IsNullOrWhiteSpace(sourceRevision) && string.IsNullOrWhiteSpace(sourceFingerprint))
            throw new InvalidDataException("decision needs sourceRevision or sourceFingerprint.");
        if (sourceRevision != null
            && (sourceRevision.Length is < 7 or > 64 || !sourceRevision.All(Uri.IsHexDigit)))
            throw new InvalidDataException("decision sourceRevision is malformed.");
        if (sourceFingerprint != null
            && (sourceFingerprint.Length != 64 || !sourceFingerprint.All(Uri.IsHexDigit)))
            throw new InvalidDataException("decision sourceFingerprint is malformed.");
        if (sourceEntryFingerprint != null
            && (sourceEntryFingerprint.Length != 64 || !sourceEntryFingerprint.All(Uri.IsHexDigit)))
            throw new InvalidDataException("decision sourceEntryFingerprint is malformed.");
        var action = OptionalString(value, "action")
            ?? (outcome == "feature-spawn" ? "created-card" : outcome == "rework" ? "rework-requested" : "archived");
        var cliType = OptionalString(value, "cliType");
        var model = OptionalString(value, "model");
        var thinkingLevel = OptionalString(value, "thinkingLevel");
        var confirmedAt = OptionalString(value, "confirmedAt");
        var confirmedBy = OptionalString(value, "confirmedBy");
        if ((confirmedAt == null) != (confirmedBy == null)
            || confirmedAt != null && !IsUtcLifecycleTimestamp(confirmedAt, out _))
            throw new InvalidDataException("decision confirmation provenance is malformed.");
        var decidedAt = OptionalString(value, "decidedAt");
        if (decidedAt != null && !IsUtcLifecycleTimestamp(decidedAt, out _))
            throw new InvalidDataException("decision decidedAt must be an ISO UTC timestamp ending in Z.");
        var reason = OptionalString(value, "reason");
        var failure = OptionalString(value, "failure");
        var spawned = StringArray(value, "spawnedTaskKeys");
        List<WorkbenchDecisionResponse> responses = [];
        if (value.TryGetProperty("responses", out var responsesValue))
        {
            if (responsesValue.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("decision responses must be an array.");
            try
            {
                responses = responsesValue.Deserialize<List<WorkbenchDecisionResponse>>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("decision responses are malformed.", ex);
            }
            var responseError = WorkbenchDecisionContracts.ValidateResponses(
                responses,
                allowPartial: outcome == "rework");
            if (responseError != null)
                throw new InvalidDataException($"Decision {responseError}");
        }

        if (outcome == "archive" && string.IsNullOrWhiteSpace(reason))
            throw new InvalidDataException("Archive decision reason is required.");
        if (outcome == "archive" && value.TryGetProperty("taskDraft", out _))
            throw new InvalidDataException("Archive decisions cannot carry a task draft.");
        WorkbenchTaskDraft? parsedTaskDraft = null;
        if (outcome == "feature-spawn")
        {
            if (value.TryGetProperty("reason", out _))
                throw new InvalidDataException("Feature decisions cannot carry an archive reason.");
            if (!value.TryGetProperty("taskDraft", out var taskDraft)
                || taskDraft.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Feature decision taskDraft is required.");
            try
            {
                parsedTaskDraft = taskDraft.Deserialize<WorkbenchTaskDraft>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Feature decision taskDraft is malformed.", ex);
            }
            var draftError = parsedTaskDraft == null
                ? "task draft is missing."
                : WorkbenchDecisionContracts.ValidateTaskDraft(parsedTaskDraft);
            if (draftError != null)
                throw new InvalidDataException($"Feature decision {draftError}");
        }
        if (outcome == "rework")
        {
            if (value.TryGetProperty("taskDraft", out _))
                throw new InvalidDataException("Rework requests cannot carry a task draft.");
            if (!responses.Any(response => !string.IsNullOrWhiteSpace(response.Comment)))
                throw new InvalidDataException("Rework requests need a comment.");
            if (state != "pending")
                throw new InvalidDataException("Rework requests stay pending until a new revision lands.");
        }
        if (state == "failed" && (confirmedAt == null || string.IsNullOrWhiteSpace(failure)))
            throw new InvalidDataException("Failed decision needs confirmation provenance and failure.");
        if (state == "succeeded")
        {
            if (confirmedAt == null || decidedAt == null)
                throw new InvalidDataException("Succeeded decision needs confirmation and decidedAt provenance.");
            // A settled feature decision may legitimately carry no spawned key:
            // the backend does not create cards, the client does it through the
            // existing task API and reports the keys back opportunistically
            // (AGT-2375). The receipt records the decision, not the card.
            if (outcome == "archive" && spawned.Length != 0)
                throw new InvalidDataException("Archive decisions cannot carry spawned task receipts.");
            var lifecycleMatches = outcome == "archive"
                ? lifecycleState == "done"
                : lifecycleState is "decided" or "documented";
            if (lifecycleState != null && !lifecycleMatches)
                throw new InvalidDataException(outcome == "archive"
                    ? "Succeeded archive decision requires lifecycleState 'done'."
                    : "Succeeded feature decision requires lifecycleState 'decided' or 'documented'.");
        }
        else if (lifecycleState is "decided" or "documented" or "done")
        {
            throw new InvalidDataException("Pending or failed decisions must remain in a current lifecycle state.");
        }
        if (state != "succeeded" && (decidedAt != null || spawned.Length != 0))
            throw new InvalidDataException("Unsettled decisions cannot carry a settled receipt.");

        return new WorkbenchDecisionProjection(
            outcome, state, operationId, sourceRevision, sourceFingerprint,
            preparedAt, preparedBy, confirmedAt, confirmedBy, decidedAt,
            reason, failure, spawned, responses, parsedTaskDraft,
            action, cliType, model, thinkingLevel, sourceEntryFingerprint);
    }

    private static string[] DescriptorTaskKeys(JsonElement descriptor) =>
        StringArray(descriptor, "sourceTaskKeys")
            .Concat(StringArray(descriptor, "relatedTaskKeys"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string StatusFromDecision(
        string lifecycleState, WorkbenchDecisionProjection? decision)
    {
        if (lifecycleState == "documented") return "documented";
        if (decision == null) return StatusFromLifecycle(lifecycleState);
        if (decision.State is "pending" or "failed") return "decision-pending";
        return decision.Outcome == "archive" ? "archived" : "decided";
    }

    private static string? DecisionStage(WorkbenchDecisionProjection? decision) =>
        decision switch
        {
            null => null,
            { State: "pending", ConfirmedAt: null } => "prepared",
            { State: "pending" } => "pending",
            { State: "failed" } => "failed",
            { State: "succeeded", Outcome: "archive" } => "archived",
            { State: "succeeded" } => "succeeded",
            _ => null,
        };

    internal static string ComputeDescriptorFingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static string? ComputeEntryFingerprint(string entryPath)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entryPath))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Dossier entrypoint fingerprint could not be read.");
            return null;
        }
    }

    internal static string? ComputeWorkbenchFingerprint(string descriptorPath, string entryPath)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(descriptorPath));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(entryPath));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> AllowedLifecycleStates = new(StringComparer.Ordinal)
        { "in-progress", "review-requested", "decided", "documented", "done" };
    private static string StatusFromLifecycle(string state) => state switch
    {
        "in-progress" or "review-requested" => "active",
        "decided" => "decided",
        "documented" => "documented",
        "done" => "archived",
        _ => "invalid",
    };
    private static string LifecycleFromStatus(string status, string? phase) => status switch
    {
        "decision-pending" => "review-requested",
        "decided" => "decided",
        "documented" => "documented",
        "archived" => "done",
        _ when phase == "decision-ready" => "review-requested",
        _ => "in-progress",
    };
    private static List<WikiLifecycleHistoryEntry> RequiredLifecycleHistory(
        JsonElement obj, string currentState, string currentEditor, string currentEditedAt)
    {
        if (!obj.TryGetProperty("lifecycleHistory", out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() == 0)
            throw new InvalidDataException("lifecycleHistory needs at least one entry.");
        var result = new List<WikiLifecycleHistoryEntry>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every lifecycleHistory entry must be an object.");
            var state = RequiredString(entry, "state");
            var editedBy = RequiredString(entry, "editedBy");
            var editedAt = RequiredString(entry, "editedAt");
            if (!AllowedLifecycleStates.Contains(state))
                throw new InvalidDataException($"Unsupported lifecycleHistory state '{state}'.");
            if (!IsUtcLifecycleTimestamp(editedAt, out _))
                throw new InvalidDataException("lifecycleHistory editedAt must be an ISO UTC timestamp ending in Z.");
            result.Add(new(state, editedBy, editedAt, OptionalString(entry, "note")));
        }
        var latest = result[^1];
        if (latest.State != currentState || latest.EditedBy != currentEditor || latest.EditedAtUtc != currentEditedAt)
            throw new InvalidDataException("The latest lifecycleHistory entry must match lifecycleState, editedBy, and editedAt.");
        return result;
    }

    private void ApplyDocumentationProjection(List<WorkbenchListItem> items)
    {
        var referenceIndex = _scanner.GetReferenceIndex();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!item.Valid) continue;

            var references = new Dictionary<string, WorkbenchDocumentationReference>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var key in item.SourceTaskKeys
                         .Concat(item.RelatedTaskKeys)
                         .Concat(item.Decision?.SpawnedTaskKeys ?? []))
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var normalized = key.Trim();
                var task = referenceIndex.Resolve(normalized);
                references[normalized] = new WorkbenchDocumentationReference(
                    normalized,
                    Exists: task != null,
                    Terminal: task != null && WaitsOnEvaluator.IsFulfilledState(task.State),
                    Lane: task?.State);
            }

            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                foreach (var link in referenceIndex.Dependents(item.Key, TaskReferenceKinds.Workbenches))
                {
                    var key = (link.SourceKey ?? link.SourceJobId).Trim();
                    if (key.Length == 0) continue;
                    references[key] = new WorkbenchDocumentationReference(
                        key,
                        Exists: true,
                        Terminal: WaitsOnEvaluator.IsFulfilledState(link.SourceState),
                        Lane: link.SourceState);
                }
            }

            items[index] = item with
            {
                Documentation = WorkbenchDocumentationPolicy.Evaluate(item.Status, references.Values),
            };
        }
    }

    private static bool IsUtcLifecycleTimestamp(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return value.EndsWith('Z')
            && DateTimeOffset.TryParse(value, out parsed)
            && parsed.Offset == TimeSpan.Zero;
    }
}

public record WorkbenchCatalogue(string ProjectName, bool IncludesHistory, int Count, List<WorkbenchListItem> Items);
public record WorkbenchListItem(string Id, string Title, string Summary, string Status, string? Phase,
    DateTime UpdatedAtUtc, string EntryPath, bool Valid, string? Error, string[] SourceTaskKeys)
{
    public string? Key { get; init; }
    public string Pattern { get; init; } = ArticlePatterns.Concept;
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] DescriptorSourceTaskKeys { get; init; } = [];
    public string[] RelatedTaskKeys { get; init; } = [];
    public string? LifecycleState { get; init; }
    public string? EditedBy { get; init; }
    public List<WikiLifecycleHistoryEntry>? LifecycleHistory { get; init; }
    public WorkbenchDecisionProjection? Decision { get; init; }
    public string? DecisionStage { get; init; }
    /// <summary>
    /// Open operator decisions discovered from valid inline decision-point
    /// markup. Pending legacy documents without points retain one Workbench-
    /// level gate so the queue never hides required operator action.
    /// </summary>
    public int OpenDecisionCount { get; init; }
    public WorkbenchDocumentationProjection? Documentation { get; init; }
}
public record WorkbenchTaskReferences(
    string ProjectName,
    string WorkbenchKey,
    string WorkbenchId,
    string[] LegacyTaskKeys,
    IReadOnlyList<TaskReferenceLink> Items);
public sealed record WorkbenchOverviewItem(string ProjectName, WorkbenchListItem Workbench);
public sealed record WorkbenchOverview(
    string? ProjectName,
    int Count,
    int CurrentCount,
    int HistoryCount,
    List<WorkbenchOverviewItem> Items);
public record WorkbenchDocument(WorkbenchListItem Workbench, string Html, string? Branch, string? Revision,
    bool WorkingTreeModified, string? Fingerprint, string? EntryFingerprint);

public sealed record WorkbenchDecisionProjection(
    string Outcome,
    string State,
    string OperationId,
    string? SourceRevision,
    string? SourceFingerprint,
    string PreparedAt,
    string PreparedBy,
    string? ConfirmedAt,
    string? ConfirmedBy,
    string? DecidedAt,
    string? Reason,
    string? Failure,
    string[] SpawnedTaskKeys,
    List<WorkbenchDecisionResponse> Responses,
    WorkbenchTaskDraft? TaskDraft,
    string? Action,
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    string? SourceEntryFingerprint);

internal sealed record WorkbenchMutationSnapshot(
    string Root,
    string DescriptorPath,
    string DescriptorRelPath,
    string EntryPath,
    string EntryRelPath,
    JsonObject Descriptor,
    int SchemaVersion,
    string DescriptorFingerprint,
    string? Fingerprint,
    string? Revision,
    bool Dirty,
    WorkbenchListItem Item);
