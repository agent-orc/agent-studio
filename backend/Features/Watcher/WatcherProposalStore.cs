using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Durable, restart-safe store for <see cref="WatcherProposal"/> records. One
/// JSON file per proposal under <c>&lt;workspace&gt;/logs/watcher/proposals/&lt;id&gt;.json</c>,
/// same atomic-write shape as <see cref="WatcherCaseStore"/>.
/// </summary>
public sealed class WatcherProposalStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<WatcherProposalStore> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WatcherProposal>> _byWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _idLocks = new(StringComparer.OrdinalIgnoreCase);

    public WatcherProposalStore(ILogger<WatcherProposalStore> logger)
    {
        _logger = logger;
    }

    private static string ProposalsDir(string workspaceRoot) => Path.Combine(workspaceRoot, "logs", "watcher", "proposals");
    private static string CounterPath(string workspaceRoot) => Path.Combine(workspaceRoot, "logs", "watcher", "next-proposal-id.txt");
    private static string ProposalPath(string workspaceRoot, string id) => Path.Combine(ProposalsDir(workspaceRoot), $"{Sanitize(id)}.json");
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    public IReadOnlyCollection<WatcherProposal> All(string workspaceRoot) => Index(workspaceRoot).Values.ToList();

    public WatcherProposal? FindById(string workspaceRoot, string id) =>
        Index(workspaceRoot).TryGetValue(id, out var found) ? found : null;

    public WatcherProposal? FindByCaseId(string workspaceRoot, string caseId) =>
        Index(workspaceRoot).Values.FirstOrDefault(p => string.Equals(p.CaseId, caseId, StringComparison.OrdinalIgnoreCase));

    public string AllocateProposalId(string workspaceRoot)
    {
        var path = CounterPath(workspaceRoot);
        lock (_idLocks.GetOrAdd(workspaceRoot, static _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var next = 1;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var current))
                next = current + 1;
            File.WriteAllText(path, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"WPR-{next:D6}";
        }
    }

    public WatcherProposal Save(string workspaceRoot, WatcherProposal proposal)
    {
        var index = Index(workspaceRoot);
        try
        {
            Directory.CreateDirectory(ProposalsDir(workspaceRoot));
            var path = ProposalPath(workspaceRoot, proposal.Id);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(proposal, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist watcher proposal {Id}", proposal.Id);
        }
        index[proposal.Id] = proposal;
        return proposal;
    }

    private ConcurrentDictionary<string, WatcherProposal> Index(string workspaceRoot) =>
        _byWorkspace.GetOrAdd(workspaceRoot, LoadFromDisk);

    private ConcurrentDictionary<string, WatcherProposal> LoadFromDisk(string workspaceRoot)
    {
        var loaded = new ConcurrentDictionary<string, WatcherProposal>(StringComparer.OrdinalIgnoreCase);
        var dir = ProposalsDir(workspaceRoot);
        if (!Directory.Exists(dir)) return loaded;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<WatcherProposal>(File.ReadAllText(file), JsonOpts);
                if (parsed != null) loaded[parsed.Id] = parsed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipped unreadable watcher proposal file {File}", file);
            }
        }
        return loaded;
    }
}
