using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Durable, restart-safe store for <see cref="WatcherCase"/> records. One
/// JSON file per case under <c>&lt;workspace&gt;/logs/watcher/cases/&lt;fingerprint&gt;.json</c>,
/// written atomically (temp file + move) so a crash mid-write never corrupts
/// an existing case. Cases are loaded eagerly into memory on first use and
/// kept in sync on every write, mirroring the file-backed-plus-in-memory-index
/// shape of <c>AgentMessageBusStore</c>.
/// </summary>
public sealed class WatcherCaseStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<WatcherCaseStore> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WatcherCase>> _byWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _idLocks = new(StringComparer.OrdinalIgnoreCase);

    public WatcherCaseStore(ILogger<WatcherCaseStore> logger)
    {
        _logger = logger;
    }

    private static string CasesDir(string workspaceRoot) => Path.Combine(workspaceRoot, "logs", "watcher", "cases");
    private static string CounterPath(string workspaceRoot) => Path.Combine(workspaceRoot, "logs", "watcher", "next-case-id.txt");
    private static string CasePath(string workspaceRoot, string fingerprint) =>
        Path.Combine(CasesDir(workspaceRoot), $"{Sanitize(fingerprint)}.json");

    private static string Sanitize(string fingerprint) =>
        string.Concat(fingerprint.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    public IReadOnlyCollection<WatcherCase> All(string workspaceRoot) =>
        Index(workspaceRoot).Values.ToList();

    public WatcherCase? Find(string workspaceRoot, string fingerprint) =>
        Index(workspaceRoot).TryGetValue(fingerprint, out var found) ? found : null;

    public WatcherCase? FindById(string workspaceRoot, string caseId) =>
        Index(workspaceRoot).Values.FirstOrDefault(c => string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase));

    public string AllocateCaseId(string workspaceRoot)
    {
        var path = CounterPath(workspaceRoot);
        lock (_idLocks.GetOrAdd(workspaceRoot, static _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var next = 1;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var current))
                next = current + 1;
            File.WriteAllText(path, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"WCH-{next:D6}";
        }
    }

    public WatcherCase Save(string workspaceRoot, WatcherCase watcherCase)
    {
        var index = Index(workspaceRoot);
        try
        {
            Directory.CreateDirectory(CasesDir(workspaceRoot));
            var path = CasePath(workspaceRoot, watcherCase.Fingerprint);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(watcherCase, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist watcher case {Fingerprint}", watcherCase.Fingerprint);
        }
        index[watcherCase.Fingerprint] = watcherCase;
        return watcherCase;
    }

    private ConcurrentDictionary<string, WatcherCase> Index(string workspaceRoot) =>
        _byWorkspace.GetOrAdd(workspaceRoot, LoadFromDisk);

    private ConcurrentDictionary<string, WatcherCase> LoadFromDisk(string workspaceRoot)
    {
        var loaded = new ConcurrentDictionary<string, WatcherCase>(StringComparer.OrdinalIgnoreCase);
        var dir = CasesDir(workspaceRoot);
        if (!Directory.Exists(dir)) return loaded;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var parsed = JsonSerializer.Deserialize<WatcherCase>(text, JsonOpts);
                if (parsed != null) loaded[parsed.Fingerprint] = parsed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipped unreadable watcher case file {File}", file);
            }
        }
        return loaded;
    }
}
