using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Reads the repository-owned <c>docs/quality</c> style-guide family and
/// projects only guides that apply to the selected project. All filesystem
/// reads are bounded and reject descendant reparse points. The resulting
/// catalogue feeds both the Project Hub and intake prompt enrichment.
/// </summary>
public sealed partial class ProjectStyleGuideService
{
    internal const int MaxGuideFileBytes = 32 * 1024;
    internal const int MaxPackageJsonBytes = 256 * 1024;
    internal const int MaxGuideFiles = 64;
    internal const int MaxPackageFiles = 32;

    private const int MaxScanDepth = 3;
    private const int MaxScannedDirectories = 512;
    private const int MaxScannedEntries = 8_192;
    private const int MaxWarnings = 64;
    private const int MaxGuideIdChars = 64;
    private const int MaxTitleChars = 160;
    private const int MaxSummaryChars = 600;
    private const int MaxPromptSummaryChars = 600;
    private const int MaxVersionChars = 32;
    private static readonly TimeSpan CatalogueFreshness = TimeSpan.FromMinutes(5);

    private static readonly (string Key, string DisplayLabel)[] TechnologyContract =
    [
        ("angular", "Angular"),
        ("csharp", "C#"),
        ("dotnet", ".NET")
    ];

    private static readonly Dictionary<string, string> TechnologyLabels =
        TechnologyContract.ToDictionary(item => item.Key, item => item.DisplayLabel,
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".angular", ".idea", ".vs", ".vscode", "bin", "coverage",
        "dist", "node_modules", "obj", "test-results"
    };

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ILogger<ProjectStyleGuideService> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CatalogueCacheEntry> _catalogueCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _catalogueLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectStyleGuideService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        ILogger<ProjectStyleGuideService> logger,
        TimeProvider? time = null)
    {
        _scanner = scanner;
        _registry = registry;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public ProjectStyleGuideCatalogue? GetCatalogue(string projectName, bool refresh = false)
    {
        var project = ResolveProject(projectName);
        if (project == null) return null;

        var cacheKey = $"{project.ProjectKey}|{Path.GetFullPath(project.RepositoryRoot)}";
        var now = _time.GetUtcNow().UtcDateTime;
        if (!refresh
            && _catalogueCache.TryGetValue(cacheKey, out var cached)
            && now < cached.RefreshAfterUtc)
            return cached.Catalogue;

        var gate = _catalogueLocks.GetOrAdd(cacheKey, _ => new object());
        lock (gate)
        {
            now = _time.GetUtcNow().UtcDateTime;
            if (!refresh
                && _catalogueCache.TryGetValue(cacheKey, out cached)
                && now < cached.RefreshAfterUtc)
                return cached.Catalogue;

            ProjectStyleGuideCatalogue catalogue;
            try
            {
                catalogue = BuildCatalogue(
                    project.ProjectKey,
                    project.DisplayName,
                    project.RepositoryRoot,
                    project.SelectorAliases);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or ArgumentException
                                       or NotSupportedException
                                       or DecoderFallbackException)
            {
                _logger.LogWarning(ex,
                    "Failed to build project style-guide catalogue for {ProjectKey}",
                    project.ProjectKey);
                catalogue = new ProjectStyleGuideCatalogue(
                    project.ProjectKey,
                    project.DisplayName,
                    [],
                    [],
                    [new ProjectStyleGuideWarning("quality", "The style-guide catalogue could not be loaded safely.")]);
            }

            var refreshAfter = now + CatalogueFreshness;
            catalogue = catalogue with
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                CapturedAtUtc = now,
                RefreshAfterUtc = refreshAfter
            };
            _catalogueCache[cacheKey] = new CatalogueCacheEntry(catalogue, refreshAfter);
            return catalogue;
        }
    }

    public string? GetRepositoryRoot(string projectName)
    {
        var root = ResolveProject(projectName)?.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.GetFullPath(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Bounded test seam over a repository fixture.</summary>
    internal static ProjectStyleGuideCatalogue BuildCatalogue(
        string projectKey,
        string projectDisplayName,
        string repositoryRoot,
        IReadOnlyCollection<string>? projectSelectorAliases = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var warnings = new List<ProjectStyleGuideWarning>();
        var technologies = DetectTechnologies(root, warnings);
        var guidesRoot = Path.Combine(root, "docs", "quality");
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectKey };
        foreach (var alias in projectSelectorAliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(alias)) aliases.Add(alias.Trim());
        }

        if (!Directory.Exists(guidesRoot))
            return new ProjectStyleGuideCatalogue(projectKey, projectDisplayName, technologies, [], warnings);

        if (!IsSafeDescendant(root, guidesRoot))
        {
            AddWarning(warnings, "quality",
                "The style-guide directory is a symbolic/reparse path and was excluded.");
            return new ProjectStyleGuideCatalogue(projectKey, projectDisplayName, technologies, [], warnings);
        }

        string[] candidatePaths;
        try
        {
            candidatePaths = Directory.GetFiles(guidesRoot, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ProjectStyleGuideService: could not enumerate docs/quality");
            AddWarning(warnings, "quality", "The style-guide directory could not be enumerated safely.");
            return new ProjectStyleGuideCatalogue(projectKey, projectDisplayName, technologies, [], warnings);
        }

        Array.Sort(candidatePaths, StringComparer.OrdinalIgnoreCase);
        if (candidatePaths.Length > MaxGuideFiles)
        {
            AddWarning(warnings, "quality",
                $"Only the first {MaxGuideFiles} style-guide files in deterministic path order were inspected; the remainder were excluded.");
        }

        var guides = new List<ProjectStyleGuide>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidatePaths.Take(MaxGuideFiles))
        {
            var relPath = RelativeTo(Path.Combine(root, "docs"), path);
            if (!IsSafeDescendant(guidesRoot, path))
            {
                AddWarning(warnings, relPath,
                    "Symbolic/reparse style-guide files are excluded.");
                continue;
            }

            if (!TryReadBoundedUtf8(path, MaxGuideFileBytes, out var raw, out var readFailure))
            {
                AddWarning(warnings, relPath,
                    $"The style-guide file was excluded: {readFailure}");
                continue;
            }

            var parsed = ParseGuideText(raw, path, root);
            if (parsed.Guide == null)
            {
                if (parsed.Declared)
                {
                    AddWarning(warnings, relPath,
                        $"Invalid style-guide frontmatter; the guide was excluded. {parsed.Error}");
                }
                continue;
            }

            if (!seenIds.Add(parsed.Guide.Id))
            {
                AddWarning(warnings, relPath,
                    $"Duplicate styleGuideId '{parsed.Guide.Id}' was excluded; the first path in deterministic order wins.");
                continue;
            }

            var match = MatchGuide(parsed.Guide.AppliesTo, aliases, technologies);
            if (match != null)
                guides.Add(parsed.Guide with { Match = match });
        }

        return new ProjectStyleGuideCatalogue(projectKey, projectDisplayName, technologies, guides, warnings);
    }

    internal static ProjectStyleGuide? ParseGuide(string path, string repositoryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var guidesRoot = Path.Combine(root, "docs", "quality");
        if (!IsSafeDescendant(guidesRoot, path)) return null;
        if (!TryReadBoundedUtf8(path, MaxGuideFileBytes, out var raw, out _)) return null;
        return ParseGuideText(raw, path, root).Guide;
    }

    internal static List<ProjectTechnology> DetectTechnologies(string repositoryRoot)
        => DetectTechnologies(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            []);

    private ProjectContext? ResolveProject(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;

        var entry = _scanner.GetWatchPaths().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));
        var record = entry != null
            ? _registry.FindByStorageLocation(entry.Path)
            : _registry.FindByShortCode(projectName) ?? _registry.FindByIdOrDisplayName(projectName);
        if (record == null && entry == null) return null;

        var repositoryRoot = ProjectRepoResolver.Resolve(record, entry);
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;

        var displayName = !string.IsNullOrWhiteSpace(record?.DisplayName)
            ? record.DisplayName.Trim()
            : (entry?.Name ?? projectName).Trim();
        var projectKey = !string.IsNullOrWhiteSpace(record?.Id)
            ? record.Id.Trim().ToUpperInvariant()
            : LegacyProjectKey(entry?.Name ?? projectName);
        var aliases = new List<string> { projectKey };
        if (!string.IsNullOrWhiteSpace(record?.ShortCode))
            aliases.Add(record.ShortCode.Trim().ToUpperInvariant());

        return new ProjectContext(projectKey, displayName, repositoryRoot, aliases);
    }

    private static List<ProjectTechnology> DetectTechnologies(
        string repositoryRoot,
        List<ProjectStyleGuideWarning> warnings)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(repositoryRoot)) return [];

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((repositoryRoot, 0));
        var visitedDirectories = 0;
        var scannedEntries = 0;
        var packageFiles = 0;
        var scanLimitWarned = false;
        var packageLimitWarned = false;

        while (queue.Count > 0 && visitedDirectories++ < MaxScannedDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, $"ProjectStyleGuideService: skipped unreadable directory {directory}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    if (!scanLimitWarned)
                    {
                        AddWarning(warnings, ".",
                            $"Technology detection stopped after {MaxScannedEntries} filesystem entries.");
                        scanLimitWarned = true;
                    }
                    queue.Clear();
                    break;
                }

                if (!TryGetAttributes(entry, out var attributes)) continue;
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    AddWarning(warnings, RelativeTo(repositoryRoot, entry),
                        "A symbolic/reparse entry was skipped during technology detection.");
                    continue;
                }

                if (!IsLexicalDescendant(repositoryRoot, entry)) continue;
                if (isDirectory)
                {
                    if (depth < MaxScanDepth && !IgnoredDirectories.Contains(Path.GetFileName(entry)))
                        queue.Enqueue((entry, depth + 1));
                    continue;
                }

                var name = Path.GetFileName(entry);
                var extension = Path.GetExtension(entry);
                if (string.Equals(name, "angular.json", StringComparison.OrdinalIgnoreCase))
                    keys.Add("angular");
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add("dotnet");
                    keys.Add("csharp");
                }

                if (!string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (++packageFiles > MaxPackageFiles)
                {
                    if (!packageLimitWarned)
                    {
                        AddWarning(warnings, RelativeTo(repositoryRoot, entry),
                            $"Only {MaxPackageFiles} package.json files are inspected; later files were excluded.");
                        packageLimitWarned = true;
                    }
                    continue;
                }
                DetectPackageTechnologies(entry, repositoryRoot, keys, warnings);
            }
        }

        if (queue.Count > 0)
        {
            AddWarning(warnings, ".",
                $"Technology detection stopped after {MaxScannedDirectories} directories.");
        }

        return TechnologyContract
            .Where(item => keys.Contains(item.Key))
            .Select(item => new ProjectTechnology(item.Key, item.DisplayLabel))
            .ToList();
    }

    private static void DetectPackageTechnologies(
        string path,
        string repositoryRoot,
        HashSet<string> technologies,
        List<ProjectStyleGuideWarning> warnings)
    {
        var relPath = RelativeTo(repositoryRoot, path);
        if (!TryReadBoundedUtf8(path, MaxPackageJsonBytes, out var raw, out var readFailure))
        {
            AddWarning(warnings, relPath, $"package.json was excluded: {readFailure}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var sectionName in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(sectionName, out var section)
                    || section.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in section.EnumerateObject())
                {
                    if (property.Name.StartsWith("@angular/", StringComparison.OrdinalIgnoreCase))
                        technologies.Add("angular");
                }
            }
        }
        catch (JsonException)
        {
            AddWarning(warnings, relPath, "Malformed package.json was excluded from technology detection.");
        }
    }

    private static GuideParseResult ParseGuideText(string raw, string path, string repositoryRoot)
    {
        var declared = raw.Contains("styleGuideId:", StringComparison.OrdinalIgnoreCase);
        var frontmatter = FrontmatterRegex().Match(raw);
        if (!frontmatter.Success)
            return new GuideParseResult(null, declared, "Top-level Markdown frontmatter is required.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Groups["body"].Value.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(clean) || clean.TrimStart().StartsWith('#')) continue;
            var colon = clean.IndexOf(':');
            if (colon <= 0) continue;
            fields[clean[..colon].Trim()] = Unquote(clean[(colon + 1)..].Trim());
        }

        var required = new[] { "styleGuideId", "title", "version", "summary", "promptSummary", "appliesTo" };
        var missing = required.Where(field =>
            !fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
        if (missing.Count > 0)
            return new GuideParseResult(null, declared,
                $"Missing required field(s): {string.Join(", ", missing)}.");

        var id = fields["styleGuideId"].Trim();
        var title = fields["title"].Trim();
        var version = fields["version"].Trim();
        var summary = fields["summary"].Trim();
        var promptSummary = fields["promptSummary"].Trim();
        if (!StyleGuideIdRegex().IsMatch(id))
            return new GuideParseResult(null, declared, "styleGuideId must use lowercase letters, digits, and hyphens.");
        if (id.Length > MaxGuideIdChars || title.Length > MaxTitleChars
            || summary.Length > MaxSummaryChars || promptSummary.Length > MaxPromptSummaryChars
            || version.Length > MaxVersionChars)
            return new GuideParseResult(null, declared, "One or more frontmatter values exceed their bounded length.");

        StyleGuideAppliesTo? applies;
        try
        {
            applies = JsonSerializer.Deserialize<StyleGuideAppliesTo>(fields["appliesTo"],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new GuideParseResult(null, declared, "appliesTo must be a valid inline JSON object.");
        }
        if (applies == null)
            return new GuideParseResult(null, declared, "appliesTo must be a valid inline JSON object.");

        var normalized = Normalize(applies);
        var unknownTechnology = normalized.Technologies.FirstOrDefault(value =>
            value != "*" && !TechnologyLabels.ContainsKey(value));
        if (unknownTechnology != null)
            return new GuideParseResult(null, declared,
                $"Unknown technology key '{unknownTechnology}'. Use the canonical technology-key contract.");

        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var guide = new ProjectStyleGuide(
            id,
            title,
            RelativeTo(docsRoot, path),
            summary,
            promptSummary,
            version,
            normalized);
        return new GuideParseResult(guide, true, null);
    }

    private static ProjectStyleGuideMatch? MatchGuide(
        StyleGuideAppliesTo appliesTo,
        IReadOnlySet<string> projectAliases,
        IReadOnlyList<ProjectTechnology> technologies)
    {
        if (appliesTo.Projects.Count == 0 || appliesTo.Technologies.Count == 0) return null;

        var projectWildcard = appliesTo.Projects.Contains("*", StringComparer.OrdinalIgnoreCase);
        var projectSelector = projectWildcard
            ? "*"
            : appliesTo.Projects.FirstOrDefault(projectAliases.Contains);
        if (projectSelector == null) return null;

        var technologyWildcard = appliesTo.Technologies.Contains("*", StringComparer.OrdinalIgnoreCase);
        var matchedTechnologies = technologyWildcard
            ? technologies.ToList()
            : technologies.Where(technology =>
                appliesTo.Technologies.Contains(technology.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!technologyWildcard && matchedTechnologies.Count == 0) return null;

        return new ProjectStyleGuideMatch(
            projectWildcard,
            projectSelector,
            technologyWildcard,
            matchedTechnologies);
    }

    private static bool TryReadBoundedUtf8(
        string path,
        int maxBytes,
        out string text,
        out string failure)
    {
        text = string.Empty;
        failure = string.Empty;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: Math.Min(maxBytes, 8_192), FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
            {
                failure = $"file size exceeds the {maxBytes}-byte limit.";
                return false;
            }

            var buffer = new byte[(int)stream.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            if (stream.ReadByte() != -1)
            {
                failure = $"file size exceeds the {maxBytes}-byte limit.";
                return false;
            }

            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer, 0, read)
                .TrimStart('\uFEFF');
            return true;
        }
        catch (DecoderFallbackException)
        {
            failure = "content is not valid UTF-8.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, $"ProjectStyleGuideService: bounded read failed for {path}");
            failure = "file could not be read safely.";
            return false;
        }
    }

    private static bool IsSafeDescendant(string root, string path)
    {
        if (!IsLexicalDescendant(root, path)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
            var current = fullRoot;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLexicalDescendant(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.GetFullPath(path));
            return relative == "."
                   || (!Path.IsPathRooted(relative)
                       && relative != ".."
                       && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                       && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
    }

    private static StyleGuideAppliesTo Normalize(StyleGuideAppliesTo appliesTo)
        => new(
            Clean(appliesTo.Projects),
            Clean(appliesTo.Technologies),
            Clean(appliesTo.TaskAreas));

    private static List<string> Clean(IEnumerable<string>? values)
        => (values ?? [])
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string LegacyProjectKey(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
        return normalized.Length == 0 ? "LEGACY-PROJECT" : $"LEGACY-{normalized}";
    }

    private static string RelativeTo(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void AddWarning(
        List<ProjectStyleGuideWarning> warnings,
        string relPath,
        string message)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(new ProjectStyleGuideWarning(relPath, message));
            return;
        }
        if (warnings.Count == MaxWarnings)
        {
            warnings[^1] = new ProjectStyleGuideWarning(".",
                $"Additional style-guide warnings were omitted after the {MaxWarnings}-warning limit.");
        }
    }

    private static string Unquote(string value)
        => value.Length >= 2
           && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    [GeneratedRegex(@"\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|\z)", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.Compiled)]
    private static partial Regex StyleGuideIdRegex();

    private sealed record ProjectContext(
        string ProjectKey,
        string DisplayName,
        string RepositoryRoot,
        IReadOnlyCollection<string> SelectorAliases);

    private sealed record GuideParseResult(ProjectStyleGuide? Guide, bool Declared, string? Error);

    private sealed record CatalogueCacheEntry(
        ProjectStyleGuideCatalogue Catalogue,
        DateTime RefreshAfterUtc);
}

public sealed record StyleGuideAppliesTo(
    List<string> Projects,
    List<string> Technologies,
    List<string> TaskAreas)
{
    public StyleGuideAppliesTo() : this([], [], []) { }
}

public sealed record ProjectTechnology(string Key, string DisplayLabel);

public sealed record ProjectStyleGuideMatch(
    bool ProjectWildcard,
    string ProjectSelector,
    bool TechnologyWildcard,
    List<ProjectTechnology> Technologies);

public sealed record ProjectStyleGuide(
    string Id,
    string Title,
    string RelPath,
    string Summary,
    string PromptSummary,
    string Version,
    StyleGuideAppliesTo AppliesTo)
{
    public ProjectStyleGuideMatch Match { get; init; } = new(false, "", false, []);
}

public sealed record ProjectStyleGuideCatalogue(
    string ProjectKey,
    string ProjectDisplayName,
    List<ProjectTechnology> Technologies,
    List<ProjectStyleGuide> Guides,
    List<ProjectStyleGuideWarning> Warnings)
{
    public string SnapshotId { get; init; } = "uncaptured";
    public DateTime CapturedAtUtc { get; init; }
    public DateTime RefreshAfterUtc { get; init; }
}

public sealed record ProjectStyleGuideWarning(string RelPath, string Message);
