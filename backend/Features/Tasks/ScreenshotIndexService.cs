using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Read-only index over the screenshot files that live under each
/// job's <c>results/</c> folder. Surfaces the per-job strip and the
/// workspace-wide visual evidence reel without inventing a new
/// retention policy: the files are exactly the ones the runner and
/// the Playwright artifact harvester already drop on disk per
/// <c>docs/system/contracts/protocol-style.md</c>.
///
/// Two top-level shapes:
///
/// 1. <see cref="ListJobScreenshots"/> walks <c>results/</c> recursively
///    for the requested job, returns every <c>.png</c>/<c>.jpg</c> ordered
///    oldest first, captions each by the parent folder name (so the
///    Playwright spec name appears next to its screenshots), and
///    attaches a pass/fail badge by reading
///    <c>results/playwright/index.json</c> when present.
///
/// 2. <see cref="ListWorkspaceScreenshots"/> folds the same walk over every
///    watched job whose <c>LastActivity</c> falls inside the requested
///    window, and supports a project filter.
///
/// File serving stays inside the existing
/// <c>/api/tasks/{id}/results/{name}</c> endpoint for top-level files
/// and the new <c>/api/tasks/{id}/screenshot</c> sub-path serving
/// endpoint for nested artifacts (results/playwright/&lt;spec&gt;/...).
/// Path-traversal is rejected here so the endpoint stays a thin
/// dispatcher.
/// </summary>
public class ScreenshotIndexService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private readonly TaskScannerService _scanner;
    private readonly ILogger<ScreenshotIndexService> _logger;

    public ScreenshotIndexService(TaskScannerService scanner, ILogger<ScreenshotIndexService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public IReadOnlyList<TaskScreenshot> ListJobScreenshots(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];

        var resultsDir = Path.Combine(info.FolderPath, "results");
        if (!Directory.Exists(resultsDir)) return [];

        var passFailIndex = ReadPlaywrightIndex(resultsDir);

        var entries = new List<TaskScreenshot>();
        foreach (var path in EnumerateImages(resultsDir))
        {
            var rel = Path.GetRelativePath(resultsDir, path).Replace('\\', '/');
            var ts = SafeLastWriteUtc(path);
            var (caption, status) = DescribeEntry(rel, passFailIndex);
            var fileName = Path.GetFileName(path);
            var source = ScreenshotSourceParser.Parse(fileName);
            var metadata = ReadMetadata(path);
            entries.Add(new TaskScreenshot
            {
                JobId = info.Id,
                JobTitle = info.Title,
                ProjectName = info.ProjectName,
                WatchPath = info.WatchPath,
                FileName = fileName,
                RelativePath = $"results/{rel}",
                Url = BuildServeUrl(info.Id, rel, info.WatchPath),
                SizeBytes = metadata.SizeBytes,
                Width = metadata.Header?.Width,
                Height = metadata.Header?.Height,
                Caption = caption,
                Status = status,
                Source = source.Source,
                CompositeParts = source.Parts.ToList(),
                LocalPath = path,
                TimestampUtc = ts
            });
        }

        return entries.OrderBy(e => e.TimestampUtc).ToList();
    }

    public IReadOnlyList<TaskScreenshot> ListWorkspaceScreenshots(int windowHours, string? projectFilter)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, windowHours));
        var jobs = _scanner.ScanAllAutomationJobs();
        var collected = new List<TaskScreenshot>();

        foreach (var job in jobs)
        {
            if (!string.IsNullOrWhiteSpace(projectFilter) &&
                !string.Equals(job.ProjectName, projectFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var resultsDir = Path.Combine(job.FolderPath, "results");
            if (!Directory.Exists(resultsDir)) continue;

            // Cheap pre-filter: skip the job entirely if its results folder
            // hasn't been touched inside the window. The detailed timestamp
            // check still runs per-file so a stale parent folder with a
            // single fresh screenshot is not lost.
            try
            {
                var folderTouched = Directory.GetLastWriteTimeUtc(resultsDir);
                if (folderTouched < cutoff) continue;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "ScreenshotIndexService: best-effort, fall through and let the per-file walk decide");
                // best-effort, fall through and let the per-file walk decide
            }

            var passFailIndex = ReadPlaywrightIndex(resultsDir);

            foreach (var path in EnumerateImages(resultsDir))
            {
                var ts = SafeLastWriteUtc(path);
                if (ts < cutoff) continue;

                var rel = Path.GetRelativePath(resultsDir, path).Replace('\\', '/');
                var (caption, status) = DescribeEntry(rel, passFailIndex);
                var fileName = Path.GetFileName(path);
                var source = ScreenshotSourceParser.Parse(fileName);
                var metadata = ReadMetadata(path);
                collected.Add(new TaskScreenshot
                {
                    JobId = job.Id,
                    JobTitle = job.Title,
                    ProjectName = job.ProjectName,
                    WatchPath = job.WatchPath,
                    FileName = fileName,
                    RelativePath = $"results/{rel}",
                    Url = BuildServeUrl(job.Id, rel, job.WatchPath),
                    SizeBytes = metadata.SizeBytes,
                    Width = metadata.Header?.Width,
                    Height = metadata.Header?.Height,
                    Caption = caption,
                    Status = status,
                    Source = source.Source,
                    CompositeParts = source.Parts.ToList(),
                    LocalPath = path,
                    TimestampUtc = ts
                });
            }
        }

        return collected.OrderByDescending(e => e.TimestampUtc).ToList();
    }

    /// <summary>
    /// Resolves an inline screenshot path (relative to <c>&lt;job&gt;/results</c>)
    /// to an absolute file path, with traversal guards that match
    /// <see cref="TaskScannerService.ResolveResult"/>: the resolved file must
    /// stay inside the results folder, and only the known image
    /// extensions are served. Returns null when the path is invalid or
    /// the file is missing.
    /// </summary>
    public (string? Path, string? ContentType) ResolveScreenshotFile(string jobId, string relativePath, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains(".."))
            return (null, null);

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, null);

        var resultsDir = Path.GetFullPath(Path.Combine(info.FolderPath, "results"));
        var combined = Path.GetFullPath(Path.Combine(resultsDir, relativePath));
        if (!combined.StartsWith(resultsDir, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        if (!File.Exists(combined)) return (null, null);

        var ext = Path.GetExtension(combined).ToLowerInvariant();
        if (Array.IndexOf(ImageExtensions, ext) < 0) return (null, null);

        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        return (combined, contentType);
    }

    private IEnumerable<string> EnumerateImages(string dir)
    {
        IEnumerable<string> all;
        try
        {
            all = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate screenshots in {Dir}", dir);
            yield break;
        }

        foreach (var p in all)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (Array.IndexOf(ImageExtensions, ext) >= 0) yield return p;
        }
    }

    private static string BuildServeUrl(string jobId, string relativeWithinResults, string watchPath)
    {
        var watchQs = string.IsNullOrEmpty(watchPath) ? "" : $"&watchPath={Uri.EscapeDataString(watchPath)}";
        // Use the sub-path serving endpoint for everything so the
        // frontend has a single URL shape, including playwright/<spec>/...
        return $"/api/tasks/{Uri.EscapeDataString(jobId)}/screenshot?path={Uri.EscapeDataString(relativeWithinResults)}{watchQs}";
    }

    private static (long SizeBytes, ImageHeader? Header) ReadMetadata(string path)
    {
        try { return (new FileInfo(path).Length, ImageHeaderReader.ReadFile(path)); }
        catch (IOException) { return (0, null); }
        catch (UnauthorizedAccessException) { return (0, null); }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.UtcNow; }
    }

    private static (string Caption, string? Status) DescribeEntry(string relWithinResults, PlaywrightIndex? index)
    {
        var firstSlash = relWithinResults.IndexOf('/');
        if (firstSlash < 0) return (Path.GetFileNameWithoutExtension(relWithinResults), null);

        var topFolder = relWithinResults[..firstSlash];
        var rest = relWithinResults[(firstSlash + 1)..];

        // Playwright harvest layout: results/playwright/<spec>/<file>
        if (string.Equals(topFolder, "playwright", StringComparison.OrdinalIgnoreCase))
        {
            var nextSlash = rest.IndexOf('/');
            if (nextSlash < 0) return (Path.GetFileNameWithoutExtension(rest), null);
            var specName = rest[..nextSlash];
            var status = index?.SpecStatus(specName);
            return (specName, status);
        }

        // Other nested layout: caption with the immediate parent folder.
        var parent = Path.GetFileName(Path.GetDirectoryName(relWithinResults) ?? "");
        return (string.IsNullOrEmpty(parent) ? Path.GetFileNameWithoutExtension(relWithinResults) : parent, null);
    }

    private PlaywrightIndex? ReadPlaywrightIndex(string resultsDir)
    {
        var indexPath = Path.Combine(resultsDir, "playwright", "index.json");
        if (!File.Exists(indexPath)) return null;

        try
        {
            var json = File.ReadAllText(indexPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            return PlaywrightIndex.TryParse(doc);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Playwright index at {Path}", indexPath);
            return null;
        }
    }

    private sealed class PlaywrightIndex
    {
        private readonly Dictionary<string, string> _statusBySpec;
        private PlaywrightIndex(Dictionary<string, string> statusBySpec) { _statusBySpec = statusBySpec; }

        public string? SpecStatus(string specName)
            => _statusBySpec.TryGetValue(specName, out var s) ? s : null;

        public static PlaywrightIndex? TryParse(JsonElement doc)
        {
            if (doc.ValueKind != JsonValueKind.Object) return null;
            if (!doc.TryGetProperty("specs", out var specs) || specs.ValueKind != JsonValueKind.Object) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in specs.EnumerateObject())
            {
                var specName = entry.Name;
                if (entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.TryGetProperty("status", out var st) &&
                    st.ValueKind == JsonValueKind.String)
                {
                    var raw = st.GetString() ?? "";
                    var normalized = raw switch
                    {
                        "✓" or "passed" or "pass" => "passed",
                        "✗" or "failed" or "fail" => "failed",
                        "⊘" or "skipped" or "skip" => "skipped",
                        _ => "unknown"
                    };
                    dict[specName] = normalized;
                }
            }
            return new PlaywrightIndex(dict);
        }
    }
}
