

namespace AgentStudio.Docs;

/// <summary>
/// Project-level Steering Docs surface. Lists the agent-facing instruction
/// files that actually exist for a watched project (AGENTS.md, CLAUDE.md,
/// GEMINI.md, Copilot instructions, and scoped AGENTS.md files), reports their
/// last-modified timestamp, size, and CLI scope, and produces a small heuristic
/// warning set the UI can render alongside.
///
/// The service does not summarize or rewrite docs. The "Summarize Steering
/// Docs", "Check Docs Drift", "Analyze Recurring Failures", and
/// "Propose ... Update" actions live in the UI and queue normal
/// 1-preparation tasks; this layer only inventories what is on disk.
/// </summary>
public class ProjectSteeringDocsService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<ProjectSteeringDocsService> _logger;

    /// <summary>
    /// Files older than this are flagged as "stale" relative to the most
    /// recent steering edit. The threshold is a heuristic: when one file
    /// in the set has moved recently and another sat untouched for many
    /// times longer, the latter is at higher risk of being out of sync.
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(120);

    public ProjectSteeringDocsService(TaskScannerService scanner, ILogger<ProjectSteeringDocsService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveBaseDir(WatchPathEntry entry)
    {
        var b = !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath : entry.RootPath;
        if (string.IsNullOrWhiteSpace(b)) b = entry.Path;
        return string.IsNullOrWhiteSpace(b) ? null : b;
    }

    /// <summary>
    /// Inventory the steering sources that actually exist for a watched
    /// project. <see cref="SteeringDocsOverview.BaseDir"/> is the resolved repo
    /// root the relative paths are evaluated against.
    /// </summary>
    public SteeringDocsOverview? GetOverview(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var sources = EnumerateAgentDocFiles(baseDir)
            .Select(path => InspectAgentDoc(baseDir, path))
            .OrderBy(s => s.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var warnings = BuildWarnings(baseDir, sources);
        var lastUpdated = sources
            .Where(s => s.UpdatedAt.HasValue)
            .Select(s => s.UpdatedAt!.Value)
            .DefaultIfEmpty(default)
            .Max();

        return new SteeringDocsOverview(
            ProjectName: projectName,
            BaseDir: baseDir,
            Sources: sources,
            Warnings: warnings,
            LastUpdated: lastUpdated == default ? null : lastUpdated);
    }

    public SteeringFileContent? ReadFile(string projectName, string relPath)
    {
        if (!IsSafeRelPath(relPath)) return null;
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;
        var full = Path.GetFullPath(Path.Combine(baseDir, relPath));
        var root = Path.GetFullPath(baseDir);
        if (!IsUnderRoot(root, full) || !ProjectDocsService.IsLinkFreePath(root, full)) return null;
        if (!File.Exists(full)) return null;
        // Only allow files that the inventory exposes. Reading arbitrary
        // repository files would turn this into a generic file browser,
        // which is out of scope for the Steering Docs surface.
        var overview = GetOverview(projectName);
        var allowed = overview?.Sources.Any(s =>
            string.Equals(NormalizeRel(s.RelPath), NormalizeRel(relPath), StringComparison.OrdinalIgnoreCase)) == true;
        if (!allowed) return null;
        var content = File.ReadAllText(full);
        return new SteeringFileContent(NormalizeRel(relPath), content);
    }

    // ----------------------------------------------------------------------
    // Inventory implementation
    // ----------------------------------------------------------------------

    private static readonly string[] IgnoredDirectoryNames =
    {
        ".git",
        ".angular",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "coverage",
        "playwright-report",
        "test-results",
    };

    private static IEnumerable<string> EnumerateAgentDocFiles(string baseDir)
    {
        var root = Path.GetFullPath(baseDir);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var rel = NormalizeRel(Path.GetRelativePath(root, file));
                if (IsAgentDocRelPath(rel) && ProjectDocsService.IsLinkFreePath(root, file)) yield return file;
            }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!ProjectDocsService.IsLinkFreePath(root, child)
                    || IgnoredDirectoryNames.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static SteeringDocsSource InspectAgentDoc(string baseDir, string fullPath)
    {
        var root = Path.GetFullPath(baseDir);
        var rel = NormalizeRel(Path.GetRelativePath(root, fullPath));
        var fi = new FileInfo(fullPath);
        return new SteeringDocsSource(
            Id: SourceId(rel),
            Label: Path.GetFileName(rel),
            RelPath: rel,
            Kind: SourceKindFor(rel),
            Why: WhyFor(rel),
            Exists: true,
            UpdatedAt: fi.LastWriteTimeUtc,
            Size: fi.Length,
            AppliesToClis: AppliesToClisFor(rel),
            Children: null);
    }

    private static List<SteeringDocsWarning> BuildWarnings(string baseDir, IList<SteeringDocsSource> sources)
    {
        var warnings = new List<SteeringDocsWarning>();
        // Stale file detection: if a source exists, but is older than the
        // staleness threshold and at least one other source has been
        // touched recently, surface a "may be out of date" warning.
        var anyRecent = sources.Any(s => s.UpdatedAt is { } u && (DateTime.UtcNow - u) < TimeSpan.FromDays(30));
        if (anyRecent)
        {
            foreach (var s in sources.Where(s => s.UpdatedAt is { } u && (DateTime.UtcNow - u) > StaleThreshold))
            {
                warnings.Add(new SteeringDocsWarning(
                    Severity: SteeringDocsWarningSeverity.Warn,
                    Kind: SteeringDocsWarningKind.Stale,
                    Message: $"{s.Label} hasn't been updated in over {(int)StaleThreshold.TotalDays} days while other steering files have moved recently.",
                    SourceId: s.Id,
                    EvidenceRefs: new List<string> { s.RelPath }));
            }
        }
        // Conflict heuristic: AGENTS shim files (CLAUDE.md, copilot
        // instructions) larger than 1 KB suggest the shim has drifted from
        // its three-line contract.
        foreach (var s in sources.Where(s => s.Kind == SteeringDocsSourceKind.AgentCliShim && s.Size > 1024))
        {
            warnings.Add(new SteeringDocsWarning(
                Severity: SteeringDocsWarningSeverity.Warn,
                Kind: SteeringDocsWarningKind.PossibleConflict,
                Message: $"{s.Label} is {s.Size:N0} bytes; compatibility shims should stay tiny and point at AGENTS.md.",
                SourceId: s.Id,
                EvidenceRefs: new List<string> { s.RelPath }));
        }
        foreach (var s in sources.Where(s => s.Size > 1800))
        {
            var content = TryReadText(Path.Combine(baseDir, s.RelPath));
            if (content == null) continue;
            var wikiLinks = CountOccurrences(content, "docs/");
            if (wikiLinks >= 2) continue;
            warnings.Add(new SteeringDocsWarning(
                Severity: SteeringDocsWarningSeverity.Warn,
                Kind: SteeringDocsWarningKind.GatewayTooHeavy,
                Message: $"{s.RelPath} carries {s.Size:N0} bytes of local instructions but links to only {wikiLinks} wiki page(s). Agent docs should stay gateway-style and route durable detail into the project wiki.",
                SourceId: s.Id,
                EvidenceRefs: new List<string> { s.RelPath, "docs/" }));
        }
        return warnings;
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static string NormalizeRel(string rel) =>
        (rel ?? "").Replace('\\', '/').TrimStart('/');

    private static bool IsAgentDocRelPath(string rel)
    {
        var normalized = NormalizeRel(rel);
        var name = Path.GetFileName(normalized);
        if (string.Equals(name, "AGENTS.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "CLAUDE.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "GEMINI.md", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(normalized, ".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase);
    }

    private static SteeringDocsSourceKind SourceKindFor(string rel)
    {
        var name = Path.GetFileName(rel);
        if (string.Equals(name, "CLAUDE.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "GEMINI.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeRel(rel), ".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase))
        {
            return SteeringDocsSourceKind.AgentCliShim;
        }
        return SteeringDocsSourceKind.AgentInstructions;
    }

    private static string WhyFor(string rel)
    {
        var normalized = NormalizeRel(rel);
        var name = Path.GetFileName(normalized);
        if (string.Equals(name, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                ? "Project-level agent instructions loaded from the repository root."
                : "Scoped agent instructions loaded for work below this folder.";
        }
        if (string.Equals(name, "CLAUDE.md", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude Code compatibility file; should route to AGENTS.md or wiki pages instead of carrying divergent rules.";
        }
        if (string.Equals(name, "GEMINI.md", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini CLI instruction file; should route to shared project guidance where possible.";
        }
        return "GitHub Copilot coding-agent instruction file; should stay aligned with AGENTS.md.";
    }

    private static List<string> AppliesToClisFor(string rel)
    {
        var normalized = NormalizeRel(rel);
        var name = Path.GetFileName(normalized);
        if (string.Equals(name, "CLAUDE.md", StringComparison.OrdinalIgnoreCase)) return new List<string> { "claude" };
        if (string.Equals(name, "GEMINI.md", StringComparison.OrdinalIgnoreCase)) return new List<string> { "gemini" };
        if (string.Equals(normalized, ".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase)) return new List<string> { "copilot" };
        return new List<string> { "codex", "claude", "copilot" };
    }

    private static string SourceId(string rel)
    {
        var normalized = NormalizeRel(rel).ToLowerInvariant();
        var chars = normalized.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string? TryReadText(string fullPath)
    {
        try { return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static bool IsUnderRoot(string root, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = Path.GetFullPath(fullPath);
        return string.Equals(normalizedFull, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedFull.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedFull.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRelPath(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        if (relPath.Contains("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(relPath)) return false;
        var ext = Path.GetExtension(relPath);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase);
    }
}

public enum SteeringDocsSourceKind
{
    ProjectReadme,
    AgentInstructions,
    AgentCliShim,
    Roadmap,
    TaskContract,
    SkillsLookup,
    AdrIndex,
    RuntimePrompt,
    ProjectSettings,
    SteeringNote,
}

public enum SteeringDocsWarningSeverity { Info, Warn, High }

public enum SteeringDocsWarningKind { MissingSource, Stale, PossibleConflict, RecurringFailure, GatewayTooHeavy }

public record SteeringDocsSourceChild(string Name, string RelPath, DateTime UpdatedAt, long Size);

public record SteeringDocsSource(
    string Id,
    string Label,
    string RelPath,
    SteeringDocsSourceKind Kind,
    string Why,
    bool Exists,
    DateTime? UpdatedAt,
    long Size,
    List<string> AppliesToClis,
    List<SteeringDocsSourceChild>? Children);

public record SteeringDocsWarning(
    SteeringDocsWarningSeverity Severity,
    SteeringDocsWarningKind Kind,
    string Message,
    string? SourceId,
    List<string> EvidenceRefs);

public record SteeringDocsOverview(
    string ProjectName,
    string BaseDir,
    List<SteeringDocsSource> Sources,
    List<SteeringDocsWarning> Warnings,
    DateTime? LastUpdated);

public record SteeringFileContent(string RelPath, string Content);
