using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.Registry;

namespace AgentStudio.Docs;

/// <summary>Outcome of the startup-only historical CLI-log initialization.</summary>
public sealed record WikiAgentReadBackfillResult(bool AlreadyCompleted, int LogsScanned, int ReadsApplied, string MarkerPath);

/// <summary>
/// Extracts docs-page reads from rendered activity markers and older raw JSON
/// frames. It intentionally recognizes reads only: edits and writes never count.
/// </summary>
public static partial class WikiAgentReadLogParser
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm", ".json" };

    public static IReadOnlyList<string> ExtractDocsRelativePaths(string? text)
    {
        var value = AnsiText.Strip(text ?? string.Empty).Trim();
        if (value.Length == 0) return [];

        var candidates = new List<string>();
        if (value[0] == '{')
        {
            ExtractJsonCandidates(value, candidates);
        }
        else
        {
            var marker = ReadMarkerRegex().Match(value);
            if (marker.Success)
            {
                candidates.Add(marker.Groups["argument"].Value);
            }
            else
            {
                var run = RunMarkerRegex().Match(value);
                if (run.Success) ExtractShellReadCandidates(run.Groups["command"].Value, candidates);
            }
        }

        // One read command can name several pages. Distinct within that tool
        // event, but never across lines: two reads emitted at the same timestamp
        // are still two observed reads.
        return candidates
            .Select(NormalizeDocsRelativePath)
            .Where(path => path != null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? NormalizeDocsRelativePath(string candidate)
    {
        var path = candidate.Trim().Trim('"', '\'', '`').Replace('\\', '/');
        if (path.Length == 0) return null;

        var marker = path.LastIndexOf("/docs/", StringComparison.OrdinalIgnoreCase);
        string rel;
        if (marker >= 0)
            rel = path[(marker + "/docs/".Length)..];
        else if (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
            rel = path["docs/".Length..];
        else
            return null;

        if (rel.Contains(".meta.json", StringComparison.OrdinalIgnoreCase)
            || rel.Contains(".report.html", StringComparison.OrdinalIgnoreCase)
            || rel.Contains(".report.htm", StringComparison.OrdinalIgnoreCase)) return null;
        var extension = PageExtensionRegex().Match(rel);
        if (!extension.Success) return null;
        rel = rel[..extension.Index] + extension.Value;
        rel = rel.TrimStart('/');
        if (rel.Length == 0 || rel.Contains("..", StringComparison.Ordinal)) return null;
        if (rel.Equals("app", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("app/", StringComparison.OrdinalIgnoreCase)) return null;
        if (rel.Split('/').Any(segment => segment.StartsWith('.'))) return null;
        if (rel.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".report.html", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".report.htm", StringComparison.OrdinalIgnoreCase)) return null;
        return SupportedExtensions.Contains(Path.GetExtension(rel)) ? rel : null;
    }

    private static IEnumerable<string> ExtractPathTokens(string text)
    {
        foreach (Match match in DocsPathRegex().Matches(text))
            yield return match.Groups["path"].Value;
    }

    private static void ExtractJsonCandidates(string json, List<string> candidates)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            Visit(doc.RootElement, candidates);
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "WikiAgentReadLogParser: malformed JSON tool-use line ignored.");
        }
    }

    private static void Visit(JsonElement element, List<string> candidates)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, candidates);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var tool = JsonString(element, "name") ?? JsonString(element, "tool_name")
            ?? JsonString(element, "tool") ?? JsonString(element, "type");
        var argumentObject = JsonObject(element, "input") ?? JsonObject(element, "parameters")
            ?? JsonObject(element, "args") ?? element;

        if (tool is not null && IsReadTool(tool))
        {
            AddJsonString(argumentObject, candidates, "file_path", "absolute_path", "path");
        }
        else if (tool is not null && IsShellTool(tool))
        {
            foreach (var name in new[] { "command", "cmd" })
                if (JsonString(argumentObject, name) is { Length: > 0 } command)
                    ExtractShellReadCandidates(command, candidates);
        }

        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Visit(property.Value, candidates);
    }

    private static bool IsReadTool(string tool) => tool.Equals("Read", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("ReadFile", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("read_file", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("read-file", StringComparison.OrdinalIgnoreCase);

    private static bool IsShellTool(string tool) => tool.Equals("Bash", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("command_call", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("command_execution", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("local_shell_call", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("local_shell", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("run_shell_command", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("exec_command", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("shell", StringComparison.OrdinalIgnoreCase);

    private static void ExtractShellReadCandidates(string command, List<string> candidates)
    {
        // Reuse the established Agent Docs classifier so Wiki reads and
        // instruction-file analytics agree on what constitutes a read-only
        // shell access. The complete command scan is safe only after that
        // classification and preserves multi-file commands such as
        // `cat docs/a.md docs/b.md`.
        var classified = AgentDocReadClassifier.Classify("shell", command);
        if (classified == null) return;
        candidates.AddRange(classified.Paths);
        candidates.AddRange(ExtractPathTokens(command));
    }

    private static JsonElement? JsonObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddJsonString(JsonElement element, List<string> candidates, params string[] names)
    {
        foreach (var name in names)
            if (JsonString(element, name) is { Length: > 0 } value)
                candidates.Add(value);
    }

    [GeneratedRegex(@"^[●*•]\s*Read\s+(?<argument>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadMarkerRegex();

    [GeneratedRegex(@"^[●*•]\s*Run\s+(?<command>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RunMarkerRegex();

    [GeneratedRegex("(?<path>(?:[A-Za-z]:)?[^\\s\\\"'`;|<>]*?(?:^|[/\\\\])docs[/\\\\][^\\s\\\"'`;|<>]+\\.(?:md|html?|json))", RegexOptions.IgnoreCase)]
    private static partial Regex DocsPathRegex();

    [GeneratedRegex(@"\.(?:md|html?|json)", RegexOptions.IgnoreCase)]
    private static partial Regex PageExtensionRegex();
}

/// <summary>
/// Persists observed wiki reads into runtime-only project state. Live local CLI
/// output and remote runner ingestion call the same method; startup backfill
/// folds the durable cli-output.log inventory once behind an atomic marker.
/// </summary>
public sealed class WikiAgentReadService
{
    private const string MarkerSchema = "wiki-agent-read-backfill/v1";
    private const string MarkerFileName = "wiki-agent-reads-backfill-v1.json";

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly WikiAgentReadStore _agentReads;
    private readonly ProjectDocsService _docs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WikiAgentReadService> _logger;
    private readonly object _backfillGate = new();
    private readonly ConcurrentDictionary<string, TaskTarget> _targetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingRefreshes = new(StringComparer.OrdinalIgnoreCase);

    public WikiAgentReadService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        WikiAgentReadStore agentReads,
        ProjectDocsService docs,
        IConfiguration configuration,
        ILogger<WikiAgentReadService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _agentReads = agentReads;
        _docs = docs;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Processes one or more already-rendered output lines.</summary>
    public int ProcessOutput(string taskKey, IEnumerable<CliOutputLine> lines)
    {
        var observations = lines
            .SelectMany(line => WikiAgentReadLogParser.ExtractDocsRelativePaths(line.Text)
                .Select(rel => new ReadObservation(rel, NormalizeAt(line.Timestamp))))
            .ToList();
        if (observations.Count == 0) return 0;

        if (!_targetCache.TryGetValue(taskKey, out var target))
        {
            target = ResolveTaskTarget(taskKey);
            if (target == null) return 0;
            _targetCache.TryAdd(taskKey, target);
        }

        var applied = 0;
        foreach (var observation in observations)
        {
            if (!PageExists(target.WikiDir, observation.RelPath)) continue;
            try
            {
                _agentReads.Increment(target.WikiDir, observation.RelPath, observation.At, target.TaskKey);
                applied++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "wiki-agent-read-write-failed task={TaskKey} page={Page}", target.TaskKey, observation.RelPath);
            }
        }
        if (applied > 0) ScheduleWikiRefresh(target.ProjectName);
        return applied;
    }

    /// <summary>
    /// Delay before a live read reaches the Wiki snapshot. Reads arrive in
    /// bursts while an agent explores docs/; one refresh per burst is enough
    /// for an observational counter.
    /// </summary>
    internal TimeSpan LiveReadRefreshDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Rebuilds the read project's Wiki snapshot once per burst. Before
    /// AGT-3793 every applied CLI line rebuilt the snapshot of every project
    /// synchronously on the CLI output path: 133 fills in 30 minutes on an
    /// idle host, each a full docs/ projection.
    /// </summary>
    private void ScheduleWikiRefresh(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName) || !_pendingRefreshes.TryAdd(projectName, 0)) return;
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Delay(LiveReadRefreshDelay).ContinueWith(delay =>
            {
                _pendingRefreshes.TryRemove(projectName, out _);
                try
                {
                    _docs.InvalidateWikiContent(projectName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "wiki-agent-read-refresh-failed project={Project}", projectName);
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Scans every current/archived task log once. A durable marker makes later
    /// startups and repeated calls no-ops; runtime baseline writes themselves
    /// are monotonic so a crash-resumed scan cannot inflate totals.
    /// </summary>
    public WikiAgentReadBackfillResult EnsureBackfilled()
    {
        lock (_backfillGate)
        {
            var markerPath = MarkerPath();
            if (File.Exists(markerPath)) return new WikiAgentReadBackfillResult(true, 0, 0, markerPath);

            var logsScanned = 0;
            var byPage = new Dictionary<string, BackfillPage>(StringComparer.OrdinalIgnoreCase);
            // The normal board snapshot deliberately omits 7-archive when the
            // production index cache is active. Historical initialization must
            // include that partition because archived logs are still durable
            // evidence and often make up most of the available inventory.
            foreach (var task in _scanner.ScanAllAutomationJobsWithArchive()
                         .Where(task => !string.IsNullOrWhiteSpace(task.FolderPath))
                         .GroupBy(task => Path.GetFullPath(task.FolderPath), StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                var logPath = TaskPaths.CliOutputLog(task.FolderPath);
                if (!File.Exists(logPath)) continue;
                logsScanned++;
                var target = ResolveTaskTarget(task);
                if (target == null) continue;

                try
                {
                    // Backfill is a one-time inventory fold, not a UI hot path.
                    // Stream every persisted line so reads near the beginning
                    // of logs larger than CliOutputLogParser.MaxLinesCap are
                    // not silently omitted.
                    var fallbackDate = File.GetLastWriteTimeUtc(logPath).Date;
                    foreach (var raw in File.ReadLines(logPath))
                    {
                        var line = CliOutputLogParser.ParseLine(raw, fallbackDate);
                        var at = NormalizeAt(line.Timestamp);
                        foreach (var rel in WikiAgentReadLogParser.ExtractDocsRelativePaths(line.Text))
                        {
                            var key = target.WikiDir + "|" + rel;
                            if (!byPage.TryGetValue(key, out var page))
                            {
                                if (!PageExists(target.WikiDir, rel)) continue;
                                page = new BackfillPage(target.WikiDir, rel);
                                byPage[key] = page;
                            }
                            page.Total++;
                            page.Recent.Add(new WikiAgentReadRecent(at, target.TaskKey));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "wiki-agent-read-backfill-log-failed task={TaskKey} log={Log}", target.TaskKey, logPath);
                    continue;
                }
            }

            var readsApplied = 0;
            foreach (var page in byPage.Values)
            {
                _agentReads.ApplyBackfill(page.WikiDir, page.RelPath, page.Total, page.Recent);
                readsApplied += page.Total;
            }

            WriteMarker(markerPath, logsScanned, readsApplied);
            if (readsApplied > 0) _docs.InvalidateWikiTreeCache();
            _logger.LogInformation(
                "wiki-agent-read-backfill-complete logsScanned={LogsScanned} pages={Pages} readsApplied={ReadsApplied} marker={Marker}",
                logsScanned, byPage.Count, readsApplied, markerPath);
            return new WikiAgentReadBackfillResult(false, logsScanned, readsApplied, markerPath);
        }
    }

    private TaskTarget? ResolveTaskTarget(string taskKey)
    {
        var task = _scanner.FindJob(taskKey);
        return task == null ? null : ResolveTaskTarget(task);
    }

    private TaskTarget? ResolveTaskTarget(TaskInfo task)
    {
        var repo = ProjectRepoResolver.ResolveForProject(task.ProjectName, _scanner, _registry);
        if (string.IsNullOrWhiteSpace(repo)) return null;
        var wikiDir = Path.Combine(repo, ProjectDocsService.WikiRel);
        if (!Directory.Exists(wikiDir)) return null;
        // Recent history is operator-facing. Prefer the stable display key
        // (AGT-123) over TaskInfo.TaskKey, whose value is the internal
        // watchPath::slug lookup identity.
        var key = !string.IsNullOrWhiteSpace(task.Key) ? task.Key!
            : !string.IsNullOrWhiteSpace(task.Id) ? task.Id
            : task.TaskKey;
        return new TaskTarget(wikiDir, key, task.ProjectName);
    }

    private static bool PageExists(string wikiDir, string relPath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(wikiDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(wikiDir);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath);
    }

    private string MarkerPath()
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = _scanner.GetWatchPaths().Select(entry => entry.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        root = string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
        return Path.Combine(Path.GetFullPath(root), ".metadata", MarkerFileName);
    }

    private static void WriteMarker(string markerPath, int logsScanned, int readsApplied)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = MarkerSchema,
            completedAt = DateTime.UtcNow,
            logsScanned,
            readsApplied,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var temp = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, markerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { SilentCatch.Note(ex, "WikiAgentReadService: backfill marker temp cleanup failed."); }
        }
    }

    private static DateTime NormalizeAt(DateTime at) =>
        at == default ? DateTime.UtcNow : at.ToUniversalTime();

    private sealed record TaskTarget(string WikiDir, string TaskKey, string ProjectName);
    private sealed record ReadObservation(string RelPath, DateTime At);
    private sealed class BackfillPage(string wikiDir, string relPath)
    {
        public string WikiDir { get; } = wikiDir;
        public string RelPath { get; } = relPath;
        public int Total { get; set; }
        public List<WikiAgentReadRecent> Recent { get; } = [];
    }
}
