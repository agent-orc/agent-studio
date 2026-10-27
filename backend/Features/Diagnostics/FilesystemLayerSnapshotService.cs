using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace AgentStudio.Diagnostics;

public record FilesystemLayerSnapshot(
    string SchemaVersion,
    string RootPath,
    string? GitCommit,
    string? GitBranch,
    DateTimeOffset GeneratedAt,
    bool FromCache,
    string SnapshotPath,
    string CoverageFingerprint,
    List<FilesystemLayerCoverageReport> CoverageReports,
    List<FilesystemLayerRow> Rows);

public record FilesystemLayerRow(
    string Path,
    int Level,
    int CodeLoc,
    int CodeFiles,
    int VisualEvidenceCount,
    int AgentFileCount,
    int TestLoc,
    int TestFiles,
    int? CoveragePercent,
    int CoveredLines,
    int CoverableLines,
    string CoverageSource,
    string Role,
    string Detail,
    List<string> Children);

public record FilesystemLayerCoverageReport(
    string Path,
    string Format,
    int Files,
    int CoveredLines,
    int CoverableLines);

/// <summary>
/// Builds the folder-tree metadata used by the filesystem-layer explorer.
/// The snapshot is bound to the current git commit and persisted under
/// .orchestrator/metadata so the UI can render a stable view without
/// re-counting files on every load.
/// </summary>
public class FilesystemLayerSnapshotService
{
    private const string SchemaVersion = "filesystem-layer.v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".html", ".scss", ".css", ".js", ".json", ".sh"
    };

    private static readonly HashSet<string> VisualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"
    };

    private static readonly HashSet<string> GeneratedOrExternalDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".orchestrator", ".angular", ".vite", ".cache", "bin", "obj", "dist", "node_modules",
        "playwright-report", "test-results", "coverage"
    };

    private static readonly HashSet<string> NonSourceCodeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json"
    };

    private readonly ILogger<FilesystemLayerSnapshotService> _logger;

    public FilesystemLayerSnapshotService(ILogger<FilesystemLayerSnapshotService> logger)
    {
        _logger = logger;
    }

    public FilesystemLayerSnapshot GetSnapshot(string? rootPath, bool refresh = false)
    {
        var root = ResolveRoot(rootPath);
        if (root == null)
            throw new DirectoryNotFoundException($"Root path does not exist: {rootPath}");

        var gitRoot = ResolveGitRoot(root) ?? root;
        var commit = RunGit(gitRoot, "rev-parse", "HEAD").Stdout.Trim();
        if (string.IsNullOrWhiteSpace(commit)) commit = null;
        var branch = RunGit(gitRoot, "rev-parse", "--abbrev-ref", "HEAD").Stdout.Trim();
        if (string.IsNullOrWhiteSpace(branch)) branch = null;
        var coverageReports = DiscoverCoverageReports(gitRoot);
        var coverageFingerprint = BuildCoverageFingerprint(coverageReports);

        var snapshotPath = Path.Combine(gitRoot, ".orchestrator", "metadata", "filesystem-layer-snapshot.json");
        if (!refresh && File.Exists(snapshotPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<FilesystemLayerSnapshot>(
                    File.ReadAllText(snapshotPath), JsonOptions);
                if (cached?.SchemaVersion == SchemaVersion &&
                    cached.GitCommit == commit &&
                    cached.CoverageFingerprint == coverageFingerprint)
                    return cached with { FromCache = true };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read filesystem-layer snapshot {Path}", snapshotPath);
            }
        }

        var coverage = LoadCoverage(gitRoot, coverageReports);
        var rows = BuildRows(gitRoot, coverage);
        var snapshot = new FilesystemLayerSnapshot(
            SchemaVersion,
            gitRoot,
            commit,
            branch,
            DateTimeOffset.UtcNow,
            false,
            snapshotPath,
            coverageFingerprint,
            coverage.Reports,
            rows);

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        return snapshot;
    }

    private List<FilesystemLayerRow> BuildRows(string root, CoverageIndex coverage)
    {
        var files = GetRepositoryFiles(root);
        var dirs = new Dictionary<string, FolderAccumulator>(StringComparer.Ordinal)
        {
            ["." ] = new(".")
        };

        FolderAccumulator Ensure(string path)
        {
            path = string.IsNullOrWhiteSpace(path) ? "." : Normalize(path);
            if (!dirs.TryGetValue(path, out var acc))
            {
                acc = new FolderAccumulator(path);
                dirs[path] = acc;
            }
            return acc;
        }

        foreach (var file in files)
        {
            var parts = file.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            for (var i = 0; i < parts.Length; i++)
            {
                var dir = i == 0 ? "." : string.Join('/', parts.Take(i));
                var acc = Ensure(dir);
                if (i < parts.Length - 1)
                    acc.Children.Add(string.Join('/', parts.Take(i + 1)));
            }

            var extension = Path.GetExtension(file);
            var isCode = IsCodeFile(file);
            var isVisual = VisualExtensions.Contains(extension);
            var isAgent = IsAgentFile(file);
            var isTest = IsTestFile(file);
            var loc = isCode ? CountLines(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))) : 0;
            coverage.Files.TryGetValue(file, out var fileCoverage);

            for (var i = 0; i < parts.Length; i++)
            {
                var dir = i == 0 ? "." : string.Join('/', parts.Take(i));
                var acc = Ensure(dir);
                if (isCode)
                {
                    acc.CodeLoc += loc;
                    acc.CodeFiles++;
                    if (isTest)
                    {
                        acc.TestLoc += loc;
                        acc.TestFiles++;
                    }
                }
                if (fileCoverage != null)
                {
                    acc.CoveredLines += fileCoverage.CoveredLines;
                    acc.CoverableLines += fileCoverage.CoverableLines;
                    acc.CoverageSources.Add(fileCoverage.Source);
                }
                if (isVisual) acc.VisualEvidenceCount++;
                if (isAgent) acc.AgentFileCount++;
            }
        }

        return dirs.Values
            .OrderBy(d => d.Path == "." ? "" : d.Path, StringComparer.Ordinal)
            .Select(d =>
            {
                var coveragePercent = d.CoverableLines == 0
                    ? (int?)null
                    : (int)Math.Round(d.CoveredLines * 100.0 / d.CoverableLines);
                return new FilesystemLayerRow(
                    d.Path,
                    d.Path == "." ? 0 : d.Path.Count(c => c == '/') + 1,
                    d.CodeLoc,
                    d.CodeFiles,
                    d.VisualEvidenceCount,
                    d.AgentFileCount,
                    d.TestLoc,
                    d.TestFiles,
                    coveragePercent,
                    d.CoveredLines,
                    d.CoverableLines,
                    d.CoverageSources.Count == 0 ? "no coverage report" : string.Join(", ", d.CoverageSources.OrderBy(s => s)),
                    GuessRole(d.Path),
                    BuildDetail(d),
                    d.Children.OrderBy(c => c, StringComparer.Ordinal).ToList());
            })
            .ToList();
    }

    private List<string> GetRepositoryFiles(string root)
    {
        var result = RunGit(root, "ls-files", "--cached", "--others", "--exclude-standard");
        if (result.ExitCode == 0)
        {
            return result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(path => !IsGeneratedOrExternalPath(path))
                .ToList();
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Normalize(Path.GetRelativePath(root, f)))
            .Where(path => !IsGeneratedOrExternalPath(path))
            .ToList();
    }

    private static string? ResolveRoot(string? rootPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath))
            return Directory.Exists(rootPath) ? Path.GetFullPath(rootPath) : null;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "agent-taskboard.sln")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? ResolveGitRoot(string root)
    {
        var result = RunGit(root, "rev-parse", "--show-toplevel");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout)
            ? Path.GetFullPath(result.Stdout.Trim())
            : null;
    }

    private static (string Stdout, string Stderr, int ExitCode) RunGit(string root, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            var result = AgentStudio.Git.GitProcessBoundary.Run(psi, timeout: TimeSpan.FromSeconds(15));
            return (result.StandardOutput, result.StandardError, result.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }
    }

    private static int CountLines(string path)
    {
        try
        {
            return File.ReadLines(path).Count();
        }
        catch
        {
            return 0;
        }
    }

    private List<CoverageReportFile> DiscoverCoverageReports(string root)
    {
        var reports = new List<CoverageReportFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Normalize(Path.GetRelativePath(root, file));
            if (IsGeneratedOrExternalPath(relativePath, allowCoverageDirectory: true)) continue;

            var name = Path.GetFileName(file);
            if (name.Equals("coverage.cobertura.xml", StringComparison.OrdinalIgnoreCase))
                reports.Add(new CoverageReportFile(file, relativePath, "cobertura"));
            else if (name.Equals("lcov.info", StringComparison.OrdinalIgnoreCase))
                reports.Add(new CoverageReportFile(file, relativePath, "lcov"));
            else if (name.Equals("coverage-final.json", StringComparison.OrdinalIgnoreCase))
                reports.Add(new CoverageReportFile(file, relativePath, "istanbul"));
        }

        return reports
            .OrderBy(r => r.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildCoverageFingerprint(IEnumerable<CoverageReportFile> reports)
        => string.Join("|", reports.Select(r =>
        {
            var info = new FileInfo(r.FullPath);
            return $"{r.RelativePath}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));

    private CoverageIndex LoadCoverage(string root, List<CoverageReportFile> reportFiles)
    {
        var files = new Dictionary<string, CoverageAccumulator>(StringComparer.OrdinalIgnoreCase);
        var reports = new List<FilesystemLayerCoverageReport>();

        foreach (var reportFile in reportFiles)
        {
            try
            {
                var before = files.Count;
                switch (reportFile.Format)
                {
                    case "cobertura":
                        LoadCobertura(root, reportFile, files);
                        break;
                    case "lcov":
                        LoadLcov(root, reportFile, files);
                        break;
                    case "istanbul":
                        LoadIstanbul(root, reportFile, files);
                        break;
                }

                var reportItems = files.Values
                    .Where(f => f.Sources.Contains(reportFile.RelativePath))
                    .ToList();
                reports.Add(new FilesystemLayerCoverageReport(
                    reportFile.RelativePath,
                    reportFile.Format,
                    files.Count - before,
                    reportItems.Sum(f => f.CoveredLines),
                    reportItems.Sum(f => f.CoverableLines)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read coverage report {Path}", reportFile.FullPath);
            }
        }

        return new CoverageIndex(
            files.ToDictionary(
                kvp => kvp.Key,
                kvp => new FileCoverage(
                    kvp.Value.CoveredLines,
                    kvp.Value.CoverableLines,
                    string.Join(", ", kvp.Value.Sources.OrderBy(s => s))),
                StringComparer.OrdinalIgnoreCase),
            reports);
    }

    private static void LoadCobertura(
        string root,
        CoverageReportFile reportFile,
        Dictionary<string, CoverageAccumulator> files)
    {
        var doc = XDocument.Load(reportFile.FullPath);
        foreach (var cls in doc.Descendants("class"))
        {
            var filename = cls.Attribute("filename")?.Value;
            var relativePath = ResolveCoverageFilePath(root, reportFile.FullPath, filename);
            if (relativePath == null) continue;

            var covered = 0;
            var coverable = 0;
            foreach (var line in cls.Descendants("line"))
            {
                var hitsText = line.Attribute("hits")?.Value;
                if (hitsText == null) continue;
                coverable++;
                if (int.TryParse(hitsText, out var hits) && hits > 0)
                    covered++;
            }

            AddCoverage(files, relativePath, covered, coverable, reportFile.RelativePath);
        }
    }

    private static void LoadLcov(
        string root,
        CoverageReportFile reportFile,
        Dictionary<string, CoverageAccumulator> files)
    {
        string? currentFile = null;
        var covered = 0;
        var coverable = 0;

        void Flush()
        {
            var relativePath = ResolveCoverageFilePath(root, reportFile.FullPath, currentFile);
            if (relativePath != null)
                AddCoverage(files, relativePath, covered, coverable, reportFile.RelativePath);
            covered = 0;
            coverable = 0;
        }

        foreach (var line in File.ReadLines(reportFile.FullPath))
        {
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                Flush();
                currentFile = line[3..].Trim();
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal))
            {
                var parts = line[3..].Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var hits))
                {
                    coverable++;
                    if (hits > 0) covered++;
                }
            }
            else if (line == "end_of_record")
            {
                Flush();
                currentFile = null;
            }
        }

        Flush();
    }

    private static void LoadIstanbul(
        string root,
        CoverageReportFile reportFile,
        Dictionary<string, CoverageAccumulator> files)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportFile.FullPath));
        foreach (var fileProperty in doc.RootElement.EnumerateObject())
        {
            var fileNode = fileProperty.Value;
            var filePath = fileNode.TryGetProperty("path", out var pathNode)
                ? pathNode.GetString()
                : fileProperty.Name;
            var relativePath = ResolveCoverageFilePath(root, reportFile.FullPath, filePath);
            if (relativePath == null) continue;

            if (!fileNode.TryGetProperty("statementMap", out var statementMap) ||
                !fileNode.TryGetProperty("s", out var statementHits))
                continue;

            var lineHits = new Dictionary<int, int>();
            foreach (var statement in statementMap.EnumerateObject())
            {
                if (!statement.Value.TryGetProperty("start", out var start) ||
                    !start.TryGetProperty("line", out var lineElement) ||
                    !lineElement.TryGetInt32(out var lineNumber))
                    continue;

                var hits = statementHits.TryGetProperty(statement.Name, out var hitElement) &&
                           hitElement.TryGetInt32(out var hitCount)
                    ? hitCount
                    : 0;
                lineHits[lineNumber] = Math.Max(lineHits.GetValueOrDefault(lineNumber), hits);
            }

            AddCoverage(
                files,
                relativePath,
                lineHits.Values.Count(hits => hits > 0),
                lineHits.Count,
                reportFile.RelativePath);
        }
    }

    private static void AddCoverage(
        Dictionary<string, CoverageAccumulator> files,
        string path,
        int coveredLines,
        int coverableLines,
        string source)
    {
        if (coverableLines == 0) return;
        if (!files.TryGetValue(path, out var acc))
        {
            acc = new CoverageAccumulator();
            files[path] = acc;
        }
        acc.CoveredLines += coveredLines;
        acc.CoverableLines += coverableLines;
        acc.Sources.Add(source);
    }

    private static string? ResolveCoverageFilePath(string root, string reportPath, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var normalized = Normalize(filePath);
        if (Path.IsPathRooted(filePath))
            normalized = Normalize(Path.GetRelativePath(root, filePath));
        else if (!File.Exists(Path.Combine(root, normalized)))
        {
            var reportDirectory = Path.GetDirectoryName(reportPath);
            if (reportDirectory != null)
            {
                var reportRelative = Path.GetFullPath(Path.Combine(reportDirectory, filePath));
                if (File.Exists(reportRelative))
                    normalized = Normalize(Path.GetRelativePath(root, reportRelative));
            }
        }

        return IsGeneratedOrExternalPath(normalized) ? null : normalized;
    }

    private static bool IsCodeFile(string path)
        => CodeExtensions.Contains(Path.GetExtension(path)) &&
           !NonSourceCodeFiles.Contains(Path.GetFileName(path));

    private static bool IsGeneratedOrExternalPath(string path, bool allowCoverageDirectory = false)
    {
        var parts = Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (allowCoverageDirectory && part.Equals("coverage", StringComparison.OrdinalIgnoreCase))
                continue;
            if (GeneratedOrExternalDirectories.Contains(part))
                return true;
        }
        return false;
    }

    private static bool IsAgentFile(string path)
        => path.EndsWith("/AGENTS.md", StringComparison.OrdinalIgnoreCase)
           || path == "AGENTS.md"
           || path.EndsWith("/CLAUDE.md", StringComparison.OrdinalIgnoreCase)
           || path == "CLAUDE.md"
           || path.EndsWith("/GEMINI.md", StringComparison.OrdinalIgnoreCase)
           || path == "GEMINI.md"
           || path.EndsWith("copilot-instructions.md", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(".github/prompts/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("prompts/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("agent-rules/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("docs/system/cli/skills/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestFile(string path)
        => path.StartsWith("backend.Tests/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("frontend/e2e/", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');

    private static string GuessRole(string path)
        => path switch
        {
            "." => "Repository root",
            "backend" => "Backend API and runtime",
            "backend/Endpoints" => "HTTP API endpoints",
            "backend/Services/Runner" => "Project Runner and orchestration",
            "backend/Services/Cli" => "CLI drivers",
            "backend.Tests" => "Backend tests",
            "frontend" => "Angular frontend",
            "frontend/src/app/components" => "Frontend component surface",
            "frontend/e2e" => "Playwright test suite",
            "docs" => "Documentation and agent skills",
            "prompts" => "Runtime prompts",
            _ => "Folder"
        };

    private static string BuildDetail(FolderAccumulator d)
    {
        var coverage = d.CoverableLines == 0
            ? "no concrete coverage report lines"
            : $"{d.CoveredLines}/{d.CoverableLines} covered line(s)";
        return $"{d.Path} contains {d.CodeFiles} code file(s), {d.VisualEvidenceCount} visual evidence artifact(s), " +
               $"{d.AgentFileCount} agent/prompt file(s), {d.TestFiles} test file(s), and {coverage} in its subtree.";
    }

    private sealed class FolderAccumulator
    {
        public FolderAccumulator(string path) => Path = path;
        public string Path { get; }
        public int CodeLoc { get; set; }
        public int CodeFiles { get; set; }
        public int VisualEvidenceCount { get; set; }
        public int AgentFileCount { get; set; }
        public int TestLoc { get; set; }
        public int TestFiles { get; set; }
        public int CoveredLines { get; set; }
        public int CoverableLines { get; set; }
        public HashSet<string> CoverageSources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CoverageReportFile(string FullPath, string RelativePath, string Format);

    private sealed record FileCoverage(int CoveredLines, int CoverableLines, string Source);

    private sealed record CoverageIndex(
        Dictionary<string, FileCoverage> Files,
        List<FilesystemLayerCoverageReport> Reports);

    private sealed class CoverageAccumulator
    {
        public int CoveredLines { get; set; }
        public int CoverableLines { get; set; }
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
    }
}
