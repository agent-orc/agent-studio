using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AgentStudio.ProjectGraph;

/// <summary>
/// Owns explicit portfolio captures and the persisted current snapshot. GET
/// callers only read that snapshot; repository walking is never hidden behind
/// Project Hub navigation.
/// </summary>
public sealed class ProjectGraphDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ILogger<ProjectGraphDiscoveryService> _logger;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly string _snapshotRoot;
    private readonly object _gate = new();
    private ProjectGraphSnapshot? _current;
    private bool _loaded;

    public ProjectGraphDiscoveryService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        IConfiguration configuration,
        ILogger<ProjectGraphDiscoveryService> logger,
        IAtomicJsonFileWriter? fileWriter = null)
    {
        _scanner = scanner;
        _registry = registry;
        _logger = logger;
        _fileWriter = fileWriter ?? new AtomicJsonFileWriter();
        var taskRepository = configuration["TaskRepository"];
        _snapshotRoot = !string.IsNullOrWhiteSpace(taskRepository)
            ? Path.Combine(taskRepository, ".metadata", "project-graph")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-taskboard", "project-graph");
    }

    public ProjectGraphSnapshot? GetCurrent(string projectName)
    {
        var focus = FindProject(projectName);
        if (focus is null) return null;

        lock (_gate)
        {
            EnsureLoaded();
            return _current is null
                || !_current.Projects.Any(project => string.Equals(project.Id, focus.Id, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : Focus(_current, focus);
        }
    }

    public ProjectGraphSnapshot? Capture(string projectName)
    {
        var focus = FindProject(projectName);
        if (focus is null) return null;

        var records = _registry.List().Where(project => !project.Archived).ToList();
        var watchPaths = _scanner.GetWatchPaths();
        var targets = records.Select(record =>
        {
            var entry = watchPaths.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, record.StorageLocation, StringComparison.OrdinalIgnoreCase))
                ?? watchPaths.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, record.DisplayName, StringComparison.OrdinalIgnoreCase));
            return new ProjectGraphTarget(record, ProjectRepoResolver.Resolve(record, entry));
        }).ToList();
        var catalog = ProjectGraphScanner.Scan(targets, _logger);

        lock (_gate)
        {
            EnsureLoaded();
            var capturedAt = catalog.CapturedAtUtc;
            var snapshot = new ProjectGraphSnapshot
            {
                SnapshotId = $"pg-{capturedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
                PreviousSnapshotId = _current?.SnapshotId,
                CaptureMode = "explicit-api",
                CapturedAtUtc = capturedAt,
                FocusProjectId = focus.Id,
                FocusProjectKey = ProjectGraphScanner.ProjectKey(focus),
                Projects = catalog.Projects,
                Components = catalog.Components,
                Dependencies = catalog.Dependencies,
            };
            Persist(snapshot);
            _current = snapshot;
            _loaded = true;
            return snapshot;
        }
    }

    private ProjectRecord? FindProject(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        return _registry.List().Where(project => !project.Archived).FirstOrDefault(project =>
            string.Equals(project.Id, projectName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.ShortCode, projectName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.DisplayName, projectName, StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectGraphSnapshot Focus(ProjectGraphSnapshot snapshot, ProjectRecord focus)
        => snapshot with
        {
            FocusProjectId = focus.Id,
            FocusProjectKey = ProjectGraphScanner.ProjectKey(focus),
        };

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var path = Path.Combine(_snapshotRoot, "current.json");
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return;
        try
        {
            _current = JsonSerializer.Deserialize<ProjectGraphSnapshot>(
                AtomicJsonFileWriter.ReadAllTextWithBackup(path,
                    source => _logger.LogWarning("project-graph-recovered source={Source}", source)), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "project-graph-current-load-failed path={Path}", path);
        }
    }

    private void Persist(ProjectGraphSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
        var timestamp = snapshot.CapturedAtUtc.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var historyPath = Path.Combine(_snapshotRoot, "history", $"{timestamp}-{snapshot.SnapshotId}.json");
        var currentPath = Path.Combine(_snapshotRoot, "current.json");
        try
        {
            // Archive first: every current pointer has an already-durable,
            // byte-equivalent history record carrying PreviousSnapshotId.
            _fileWriter.Write(historyPath, json);
            _fileWriter.Write(currentPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectPersistenceException("Could not persist the Project Graph capture.", ex);
        }
    }
}

internal sealed record ProjectGraphCatalog(
    DateTime CapturedAtUtc,
    IReadOnlyList<ProjectGraphProject> Projects,
    IReadOnlyList<ProjectGraphComponent> Components,
    IReadOnlyList<ProjectGraphDependency> Dependencies);

internal static class ProjectGraphScanner
{
    private const int MaxFilesPerRepository = 100_000;
    private const long MaxTextFileBytes = 2 * 1024 * 1024;
    private const int MaxManifestBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".angular", ".orchestrator", ".next", ".nuxt", ".cache",
        "node_modules", "bin", "obj", "dist", "coverage", "test-results",
        "playwright-report", "playwright-screenshots"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".json", ".jsonc", ".xml",
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets", ".md",
        ".yml", ".yaml", ".sh", ".ps1", ".cmd", ".bat", ".sql", ".py", ".go", ".rs",
        ".java", ".kt", ".swift", ".toml", ".ini", ".config"
    };

    public static ProjectGraphCatalog Scan(
        IReadOnlyList<ProjectGraphTarget> targets,
        ILogger? logger = null,
        DateTime? capturedAtUtc = null)
    {
        var projects = new List<ProjectDraft>();
        var components = new List<ComponentDraft>();

        foreach (var target in targets.OrderBy(item => item.Record.SortOrder).ThenBy(item => item.Record.DisplayName))
        {
            ScanProject(target, projects, components, logger);
        }

        var dependencies = ResolveDependencies(components);
        var componentModels = components
            .OrderBy(component => component.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
            .Select(component => component.ToModel())
            .ToList();
        var projectModels = projects.Select(project => project.ToModel()).ToList();

        return new ProjectGraphCatalog(
            capturedAtUtc ?? DateTime.UtcNow,
            projectModels,
            componentModels,
            dependencies);
    }

    public static string ProjectKey(ProjectRecord record)
        => string.IsNullOrWhiteSpace(record.ShortCode) ? record.Id : record.ShortCode.Trim().ToUpperInvariant();

    private static void ScanProject(
        ProjectGraphTarget target,
        ICollection<ProjectDraft> projects,
        ICollection<ComponentDraft> allComponents,
        ILogger? logger)
    {
        var key = ProjectKey(target.Record);
        var draft = new ProjectDraft(target.Record, key);
        projects.Add(draft);

        if (string.IsNullOrWhiteSpace(target.RepositoryRoot) || !Directory.Exists(target.RepositoryRoot))
        {
            draft.Warnings.Add("No repository checkout is available for discovery.");
            return;
        }

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.RepositoryRoot)); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            draft.Warnings.Add("The configured repository path is invalid.");
            return;
        }

        draft.RepositoryLabel = $"{target.Record.Id} · {target.Record.DisplayName}";
        (draft.SourceRevision, draft.SourceState) = ReadGitProvenance(root);
        try
        {
            var files = EnumerateRepositoryFiles(root, draft.Warnings).ToList();
            var manifests = files
                .Where(path => IsManifestInput(root, path))
                .Where(path => ManifestWithinLimit(root, path, draft.Warnings))
                .ToList();
            draft.Size = Measure(files);
            draft.Solutions.AddRange(manifests
                .Where(path => string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(path), ".slnx", StringComparison.OrdinalIgnoreCase))
                .Select(path => Relative(root, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            draft.Workflows.AddRange(manifests
                .Where(path => IsWorkflow(root, path))
                .Select(path => Relative(root, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            if (draft.Workflows.Count > 0) draft.Technologies.Add("GitHub Actions");

            foreach (var projectFile in manifests.Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase)))
            {
                var component = ReadDotNetComponent(target.Record.Id, key, root, projectFile, files, draft.Warnings);
                if (component is not null)
                {
                    draft.Components.Add(component);
                    allComponents.Add(component);
                }
            }

            foreach (var packageFile in manifests.Where(path => string.Equals(Path.GetFileName(path), "package.json", StringComparison.OrdinalIgnoreCase)))
            {
                var component = ReadPackageComponent(target.Record.Id, key, root, packageFile, files, draft.Warnings);
                if (component is not null)
                {
                    draft.Components.Add(component);
                    allComponents.Add(component);
                }
            }

            foreach (var angularFile in manifests.Where(path => string.Equals(Path.GetFileName(path), "angular.json", StringComparison.OrdinalIgnoreCase)))
            {
                ReadAngularProjects(target.Record.Id, key, root, angularFile, files, draft, allComponents);
            }

            foreach (var technology in draft.Components.SelectMany(component => component.Technologies))
                draft.Technologies.Add(technology);
            draft.Status = draft.Warnings.Count == 0 ? "ready" : "partial";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "project-graph-discovery-failed project={Project}", target.Record.DisplayName);
            draft.Warnings.Add("Repository discovery stopped after a filesystem or manifest error.");
            draft.Status = draft.Components.Count == 0 ? "unavailable" : "partial";
        }
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(string root, ICollection<string> warnings)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                files = Directory.EnumerateFiles(directory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped unreadable directory '{Relative(root, directory)}'.");
                continue;
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"Skipped linked file '{Relative(root, file)}'.");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped file with unreadable metadata '{Relative(root, file)}'.");
                    continue;
                }
                if (++count > MaxFilesPerRepository)
                {
                    warnings.Add($"Discovery stopped at the bounded {MaxFilesPerRepository:N0}-file limit.");
                    yield break;
                }
                yield return file;
            }

            foreach (var child in childDirectories.Reverse())
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(child))) continue;
                if (File.Exists(Path.Combine(child, ".git")) || Directory.Exists(Path.Combine(child, ".git"))) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static bool ManifestWithinLimit(string repositoryRoot, string manifest, ICollection<string> warnings)
    {
        try
        {
            if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Skipped linked file '{Relative(repositoryRoot, manifest)}'.");
                return false;
            }
            if (new FileInfo(manifest).Length <= MaxManifestBytes) return true;
            warnings.Add(OversizedManifestWarning(repositoryRoot, manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Skipped manifest with unreadable metadata '{Relative(repositoryRoot, manifest)}'.");
        }
        return false;
    }

    private static MemoryStream ReadBoundedManifest(string manifest)
    {
        using var input = new FileStream(
            manifest,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (input.Length > MaxManifestBytes) throw new ManifestSizeLimitException();

        var bounded = new MemoryStream((int)Math.Min(input.Length, MaxManifestBytes));
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (bounded.Length + read > MaxManifestBytes) throw new ManifestSizeLimitException();
                bounded.Write(buffer, 0, read);
            }
            bounded.Position = 0;
            return bounded;
        }
        catch
        {
            bounded.Dispose();
            throw;
        }
    }

    private static string OversizedManifestWarning(string repositoryRoot, string manifest)
        => $"Skipped oversized manifest '{Relative(repositoryRoot, manifest)}' (limit {MaxManifestBytes / (1024 * 1024)} MiB).";

    private static ComponentDraft? ReadDotNetComponent(
        string projectId,
        string projectKey,
        string repositoryRoot,
        string manifest,
        IReadOnlyList<string> repositoryFiles,
        ICollection<string> warnings)
    {
        try
        {
            using var input = ReadBoundedManifest(manifest);
            var document = XDocument.Load(input, LoadOptions.None);
            var sdk = document.Root?.Attribute("Sdk")?.Value ?? "";
            var assemblyName = FirstElementValue(document, "AssemblyName");
            var packageId = FirstElementValue(document, "PackageId");
            var targetFramework = FirstElementValue(document, "TargetFramework")
                ?? FirstElementValue(document, "TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var name = assemblyName ?? packageId ?? Path.GetFileNameWithoutExtension(manifest);
            var root = Path.GetDirectoryName(manifest) ?? repositoryRoot;
            var component = new ComponentDraft(
                StableId(projectId, "dotnet", Relative(repositoryRoot, manifest)),
                projectId,
                projectKey,
                name,
                "dotnet",
                Relative(repositoryRoot, manifest),
                manifest,
                root,
                Measure(UnderRoot(repositoryFiles, root)));
            component.PackageNames.Add(name);
            component.PackageNames.Add(Path.GetFileNameWithoutExtension(manifest));
            if (!string.IsNullOrWhiteSpace(packageId)) component.PackageNames.Add(packageId);
            component.Technologies.Add(DotNetBadge(targetFramework));
            component.Technologies.Add("C#");
            if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) component.Technologies.Add("ASP.NET Core");

            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                component.ProjectReferences.Add((ResolvePath(root, include), Path.GetFileNameWithoutExtension(include), DisplayProjectReference(include)));
            }
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                component.PackageReferences.Add(include.Trim());
                AddDotNetPackageTechnology(component.Technologies, include);
            }
            return component;
        }
        catch (ManifestSizeLimitException)
        {
            warnings.Add(OversizedManifestWarning(repositoryRoot, manifest));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            warnings.Add($"Could not parse '{Relative(repositoryRoot, manifest)}'.");
            return null;
        }
    }

    private static ComponentDraft? ReadPackageComponent(
        string projectId,
        string projectKey,
        string repositoryRoot,
        string manifest,
        IReadOnlyList<string> repositoryFiles,
        ICollection<string> warnings)
    {
        try
        {
            using var input = ReadBoundedManifest(manifest);
            var rootNode = JsonNode.Parse(input)?.AsObject();
            if (rootNode is null) return null;
            var name = rootNode["name"]?.GetValue<string>() ?? Path.GetFileName(Path.GetDirectoryName(manifest)) ?? "npm";
            var root = Path.GetDirectoryName(manifest) ?? repositoryRoot;
            var component = new ComponentDraft(
                StableId(projectId, "npm", Relative(repositoryRoot, manifest)),
                projectId,
                projectKey,
                name,
                "npm",
                Relative(repositoryRoot, manifest),
                manifest,
                root,
                Measure(UnderRoot(repositoryFiles, root)));
            component.PackageNames.Add(name);
            component.Technologies.Add("npm");
            foreach (var groupName in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (rootNode[groupName] is not JsonObject group) continue;
                foreach (var dependency in group)
                {
                    var spec = dependency.Value?.GetValue<string>() ?? "";
                    component.NpmReferences.Add((dependency.Key, spec, ResolveFileDependency(root, spec)));
                    AddNpmTechnology(component.Technologies, dependency.Key, spec);
                }
            }
            return component;
        }
        catch (ManifestSizeLimitException)
        {
            warnings.Add(OversizedManifestWarning(repositoryRoot, manifest));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            warnings.Add($"Could not parse '{Relative(repositoryRoot, manifest)}'.");
            return null;
        }
    }

    private static void ReadAngularProjects(
        string projectId,
        string projectKey,
        string repositoryRoot,
        string manifest,
        IReadOnlyList<string> repositoryFiles,
        ProjectDraft project,
        ICollection<ComponentDraft> allComponents)
    {
        try
        {
            using var input = ReadBoundedManifest(manifest);
            var document = JsonNode.Parse(input)?.AsObject();
            if (document?["projects"] is not JsonObject angularProjects) return;
            foreach (var pair in angularProjects)
            {
                if (pair.Value is not JsonObject definition) continue;
                var relativeRoot = definition["root"]?.GetValue<string>()?.Replace('\\', '/') ?? "";
                var workspaceRoot = Path.GetDirectoryName(manifest) ?? repositoryRoot;
                var absoluteRoot = ResolvePath(workspaceRoot, relativeRoot) ?? workspaceRoot;
                var existing = project.Components.FirstOrDefault(component => PathsEqual(component.AbsoluteRoot, absoluteRoot));
                if (existing is not null)
                {
                    existing.Technologies.Add("Angular");
                    continue;
                }

                var projectType = definition["projectType"]?.GetValue<string>();
                var kind = string.Equals(projectType, "library", StringComparison.OrdinalIgnoreCase)
                    ? "angular-library"
                    : "angular-app";
                var component = new ComponentDraft(
                    StableId(projectId, kind, $"{Relative(repositoryRoot, manifest)}#{pair.Key}"),
                    projectId,
                    projectKey,
                    pair.Key,
                    kind,
                    $"{Relative(repositoryRoot, manifest)}#{pair.Key}",
                    manifest,
                    absoluteRoot,
                    Measure(UnderRoot(repositoryFiles, absoluteRoot)));
                component.PackageNames.Add(pair.Key);
                component.Technologies.Add("Angular");
                component.Technologies.Add("TypeScript");
                project.Components.Add(component);
                allComponents.Add(component);
            }
        }
        catch (ManifestSizeLimitException)
        {
            project.Warnings.Add(OversizedManifestWarning(repositoryRoot, manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            project.Warnings.Add($"Could not parse '{Relative(repositoryRoot, manifest)}'.");
        }
    }

    private static IReadOnlyList<ProjectGraphDependency> ResolveDependencies(IReadOnlyList<ComponentDraft> components)
    {
        var byManifest = components
            .GroupBy(component => NormalizePath(component.AbsoluteManifest), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byRoot = components
            .GroupBy(component => NormalizePath(component.AbsoluteRoot), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byPackage = components
            .SelectMany(component => component.PackageNames.Select(name => (name, component)))
            .Where(item => !string.IsNullOrWhiteSpace(item.name))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.component).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ProjectGraphDependency>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            foreach (var reference in component.ProjectReferences)
            {
                ComponentDraft? target = null;
                if (reference.FullPath is not null)
                    byManifest.TryGetValue(NormalizePath(reference.FullPath), out target);
                target ??= UniquePackage(byPackage, reference.FileStem);
                AddDependency(
                    result,
                    component,
                    target,
                    "project-reference",
                    $"{component.RelativePath}: {reference.Display}",
                    includeUnresolved: true,
                    targetHint: reference.Display);
            }
            foreach (var package in component.PackageReferences)
            {
                AddDependency(result, component, UniquePackage(byPackage, package), "package", $"{component.RelativePath}: {package}");
            }
            foreach (var package in component.NpmReferences)
            {
                ComponentDraft? target = null;
                if (package.FullPath is not null)
                    byRoot.TryGetValue(NormalizePath(package.FullPath), out target);
                target ??= UniquePackage(byPackage, package.Name);
                AddDependency(
                    result,
                    component,
                    target,
                    "package",
                    $"{component.RelativePath}: {package.Name} {DisplayNpmSpec(package.Spec)}".Trim(),
                    includeUnresolved: package.FullPath is not null,
                    targetHint: package.FullPath is null ? null : $"{package.Name} file:<local-path>");
            }
        }

        return result.Values
            .OrderBy(edge => edge.FromComponentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.ToComponentId ?? edge.TargetHint, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ComponentDraft? UniquePackage(
        IReadOnlyDictionary<string, List<ComponentDraft>> byPackage,
        string name)
    {
        if (!byPackage.TryGetValue(name, out var matches) || matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];

        // An Angular workspace commonly names both its publishable npm
        // package and its angular.json project identically. That is one
        // internal destination, not an ambiguous cross-project match; pin the
        // dependency to the package manifest, which is the evidence source.
        if (matches.Select(match => match.ProjectKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return matches.FirstOrDefault(match => match.Kind == "npm") ?? matches[0];
        return null;
    }

    private static void AddDependency(
        IDictionary<string, ProjectGraphDependency> result,
        ComponentDraft source,
        ComponentDraft? target,
        string kind,
        string evidence,
        bool includeUnresolved = false,
        string? targetHint = null)
    {
        if (ReferenceEquals(source, target)) return;
        if (target is null && !includeUnresolved) return;
        var targetId = target?.Id;
        var resolution = target is null ? "unresolved" : "resolved";
        var key = $"{source.Id}|{targetId ?? targetHint}|{kind}|{resolution}";
        result.TryAdd(key, new ProjectGraphDependency
        {
            FromComponentId = source.Id,
            ToComponentId = targetId,
            Kind = kind,
            Resolution = resolution,
            TargetHint = target is null ? targetHint : null,
            Evidence = evidence,
        });
    }

    private static ProjectGraphSize Measure(IEnumerable<string> files)
    {
        var count = 0;
        long lines = 0;
        foreach (var file in files)
        {
            count++;
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxTextFileBytes) continue;
                using var stream = File.OpenRead(file);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is not null) lines++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "ProjectGraphScanner: rough line count is best-effort");
            }
        }
        return new ProjectGraphSize { Files = count, Lines = lines };
    }

    private static IEnumerable<string> UnderRoot(IEnumerable<string> files, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return files.Where(file =>
        {
            var normalized = Path.GetFullPath(file);
            return string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? FirstElementValue(XContainer document, string localName)
        => document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();

    private static string DotNetBadge(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework)) return ".NET";
        var value = targetFramework.Trim();
        if (value.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var version = new string(value[3..].TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
            if (!string.IsNullOrWhiteSpace(version)) return $".NET {version.Split('.')[0]}";
        }
        return ".NET";
    }

    private static void AddDotNetPackageTechnology(TechnologySet technologies, string package)
    {
        if (package.Contains("xunit", StringComparison.OrdinalIgnoreCase)) technologies.Add("xUnit");
        if (package.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) technologies.Add("SQLite");
        if (package.Contains("CodeAnalysis", StringComparison.OrdinalIgnoreCase)) technologies.Add("Roslyn");
    }

    private static void AddNpmTechnology(TechnologySet technologies, string package, string spec)
    {
        var major = VersionMajor(spec);
        if (package.Equals("@angular/core", StringComparison.OrdinalIgnoreCase)) technologies.Add(major is null ? "Angular" : $"Angular {major}");
        else if (package.Equals("typescript", StringComparison.OrdinalIgnoreCase)) technologies.Add("TypeScript");
        else if (package.Equals("vite", StringComparison.OrdinalIgnoreCase)) technologies.Add("Vite");
        else if (package.Equals("vitest", StringComparison.OrdinalIgnoreCase)) technologies.Add("Vitest");
        else if (package.Equals("@playwright/test", StringComparison.OrdinalIgnoreCase)) technologies.Add("Playwright");
        else if (package.Equals("react", StringComparison.OrdinalIgnoreCase)) technologies.Add("React");
        else if (package.Equals("next", StringComparison.OrdinalIgnoreCase)) technologies.Add("Next.js");
        else if (package.Equals("express", StringComparison.OrdinalIgnoreCase)) technologies.Add("Express");
    }

    private static string? VersionMajor(string spec)
    {
        var digits = new string(spec.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string StableId(string projectId, string kind, string source)
    {
        var slug = new string(source.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return $"{projectId.ToLowerInvariant()}:{kind}:{slug.Trim('-')}";
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePath(string root, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar), root); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
    }

    private static string? ResolveFileDependency(string root, string spec)
        => spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? ResolvePath(root, spec["file:".Length..])
            : null;

    private static string DisplayProjectReference(string include)
    {
        var normalized = include.Replace('\\', '/');
        return LooksAbsolutePath(normalized)
            ? $"<local-path>/{normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "project"}"
            : normalized;
    }

    private static string DisplayNpmSpec(string spec)
        => spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? "file:<local-path>" : spec;

    private static bool LooksAbsolutePath(string value)
        => Path.IsPathRooted(value)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '/');

    private static (string? Revision, string State) ReadGitProvenance(string root)
    {
        var revision = RunGit(root, "rev-parse", "HEAD");
        if (string.IsNullOrWhiteSpace(revision)) return (null, "unavailable");
        var status = RunGit(root, "status", "--porcelain=v1", "--untracked-files=normal");
        return status is null
            ? (revision, "unavailable")
            : (revision, status.Length == 0 ? "clean" : "dirty");
    }

    private static string? RunGit(string root, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(root);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException ex)
                {
                    SilentCatch.Note(ex, "ProjectGraphScanner: timed-out git process had already exited");
                }
                return null;
            }
            Task.WaitAll(output, error);
            return process.ExitCode == 0 ? output.Result.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static bool IsWorkflow(string root, string path)
    {
        var relative = Relative(root, path);
        return relative.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsManifestInput(string root, string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "angular.json", StringComparison.OrdinalIgnoreCase))
            return true;
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            || IsWorkflow(root, path);
    }

    private sealed class ManifestSizeLimitException : IOException
    {
    }

    private sealed class ProjectDraft(ProjectRecord record, string key)
    {
        public ProjectRecord Record { get; } = record;
        public string Key { get; } = key;
        public string Status { get; set; } = "unavailable";
        public string? RepositoryLabel { get; set; }
        public string? SourceRevision { get; set; }
        public string SourceState { get; set; } = "unavailable";
        public List<string> Solutions { get; } = [];
        public List<string> Workflows { get; } = [];
        public TechnologySet Technologies { get; } = new();
        public List<ComponentDraft> Components { get; } = [];
        public ProjectGraphSize Size { get; set; } = new();
        public List<string> Warnings { get; } = [];

        public ProjectGraphProject ToModel() => new()
        {
            Id = Record.Id,
            Key = Key,
            ShortCode = Record.ShortCode,
            DisplayName = Record.DisplayName,
            Status = Status,
            RepositoryLabel = RepositoryLabel,
            SourceRevision = SourceRevision,
            SourceState = SourceState,
            Solutions = Solutions,
            Workflows = Workflows,
            Technologies = Technologies.OrderBy(value => value.Slug, StringComparer.OrdinalIgnoreCase).ToList(),
            ComponentIds = Components.Select(component => component.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            Size = Size,
            Warnings = Warnings
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private sealed class ComponentDraft(
        string id,
        string projectId,
        string projectKey,
        string name,
        string kind,
        string relativePath,
        string absoluteManifest,
        string absoluteRoot,
        ProjectGraphSize size)
    {
        public string Id { get; } = id;
        public string ProjectId { get; } = projectId;
        public string ProjectKey { get; } = projectKey;
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public string RelativePath { get; } = relativePath;
        public string AbsoluteManifest { get; } = absoluteManifest;
        public string AbsoluteRoot { get; } = absoluteRoot;
        public ProjectGraphSize Size { get; } = size;
        public TechnologySet Technologies { get; } = new();
        public HashSet<string> PackageNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string? FullPath, string FileStem, string Display)> ProjectReferences { get; } = [];
        public List<string> PackageReferences { get; } = [];
        public List<(string Name, string Spec, string? FullPath)> NpmReferences { get; } = [];

        public ProjectGraphComponent ToModel() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            ProjectKey = ProjectKey,
            Name = Name,
            Kind = Kind,
            RelativePath = RelativePath,
            Technologies = Technologies.OrderBy(value => value.Slug, StringComparer.OrdinalIgnoreCase).ToList(),
            Size = Size,
        };
    }

    internal sealed class TechnologySet : IEnumerable<ProjectGraphTechnology>
    {
        private readonly Dictionary<string, ProjectGraphTechnology> _values = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string label)
        {
            var slug = TechnologySlug(label);
            _values.TryAdd(slug, new ProjectGraphTechnology { Slug = slug, Label = label });
        }

        public void Add(ProjectGraphTechnology technology)
            => _values.TryAdd(technology.Slug, technology);

        public IEnumerator<ProjectGraphTechnology> GetEnumerator() => _values.Values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string TechnologySlug(string label)
    {
        if (label.StartsWith(".NET", StringComparison.OrdinalIgnoreCase)) return "dotnet";
        if (label.Equals("C#", StringComparison.OrdinalIgnoreCase)) return "csharp";
        if (label.StartsWith("ASP.NET", StringComparison.OrdinalIgnoreCase)) return "aspnet-core";
        if (label.StartsWith("Angular", StringComparison.OrdinalIgnoreCase)) return "angular";
        if (label.Equals("TypeScript", StringComparison.OrdinalIgnoreCase)) return "typescript";
        if (label.Equals("GitHub Actions", StringComparison.OrdinalIgnoreCase)) return "github-actions";
        if (label.Equals("Next.js", StringComparison.OrdinalIgnoreCase)) return "nextjs";
        return new string(label.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
    }
}
