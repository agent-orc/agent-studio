using System.Text;
using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Durable home for Watcher cases, proposals, suppressions, and the contingent
/// ledger. Four append-only JSONL files under
/// <c>{workspace}/logs/watcher/</c>, next to the bus files that feed the
/// detectors.
/// </summary>
/// <remarks>
/// <para>
/// Cases, proposals, and suppressions are last-write-wins by key: a restart
/// replays the file and keeps the newest record for each id, so an interrupted
/// sweep never resurrects a stale state. The contingent ledger is the one file
/// that is never folded, because a budget that could be rewritten is not a
/// budget.
/// </para>
/// <para>
/// Writes take a process-wide lock and land through a full read of the current
/// in-memory view, so two sweeps cannot interleave a case update. Reads return
/// snapshots, never the live collections.
/// </para>
/// </remarks>
public sealed class WatcherStore
{
    public const string FolderName = "watcher";
    public const string CasesFileName = "cases.jsonl";
    public const string ProposalsFileName = "proposals.jsonl";
    public const string SuppressionsFileName = "suppressions.jsonl";
    public const string ContingentFileName = "contingent.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherStore> _logger;
    private readonly object _gate = new();

    /// <summary>
    /// Lines a keyed file may hold before it is folded back to one record per
    /// key. The sweep advances a case counter every five minutes forever, so
    /// without this the case file grows without holding one additional fact.
    /// The ledger is never compacted: a budget that can be rewritten is not a
    /// budget.
    /// </summary>
    public const int CompactionThreshold = 500;

    private string? _loadedRoot;
    private Dictionary<string, WatcherCase> _cases = new(StringComparer.Ordinal);
    private Dictionary<string, WatcherProposal> _proposals = new(StringComparer.Ordinal);
    private Dictionary<string, WatcherSuppression> _suppressions = new(StringComparer.Ordinal);
    private List<WatcherContingentEntry> _ledger = [];
    private readonly Dictionary<string, int> _lineCounts = new(StringComparer.Ordinal);

    public WatcherStore(IConfiguration configuration, ILogger<WatcherStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Workspace root the Watcher writes under, or null when none is configured.</summary>
    public string? WorkspaceRoot => Normalize(_configuration["TaskRepository"]);

    public static string Folder(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "logs", FolderName);

    /// <summary>True when the store has a workspace to persist into.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(WorkspaceRoot);

    public IReadOnlyList<WatcherCase> Cases()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cases.Values
                .OrderByDescending(item => item.LastSeenAtUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public WatcherCase? Case(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cases.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<WatcherProposal> Proposals()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _proposals.Values
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public WatcherProposal? Proposal(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _proposals.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<WatcherSuppression> Suppressions()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _suppressions.Values
                .OrderBy(item => item.ExpiresAtUtc)
                .ToList();
        }
    }

    /// <summary>
    /// Active suppressions at the given instant. Expired entries stay on disk
    /// so the operator can see that a fingerprint was once rejected and why.
    /// </summary>
    public IReadOnlyList<WatcherSuppression> ActiveSuppressions(DateTime nowUtc)
        => Suppressions().Where(item => item.IsActiveAt(nowUtc)).ToList();

    public IReadOnlyList<WatcherContingentEntry> Ledger()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _ledger.ToList();
        }
    }

    public WatcherCase Upsert(WatcherCase item)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _cases[item.Id] = item;
            Append(CasesFileName, item);
            Compact(CasesFileName, _cases.Values);
            return item;
        }
    }

    public WatcherProposal Upsert(WatcherProposal item)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _proposals[item.Id] = item;
            Append(ProposalsFileName, item);
            Compact(ProposalsFileName, _proposals.Values);
            return item;
        }
    }

    public WatcherSuppression Upsert(WatcherSuppression item)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _suppressions[item.Fingerprint] = item;
            Append(SuppressionsFileName, item);
            Compact(SuppressionsFileName, _suppressions.Values);
            return item;
        }
    }

    public void Record(WatcherContingentEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _ledger.Add(entry);
            Append(ContingentFileName, entry);
        }
    }

    /// <summary>
    /// Drop the in-memory view so the next read replays from disk. Used by the
    /// restart test and by any caller that changed the workspace root.
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _loadedRoot = null;
            _cases = new Dictionary<string, WatcherCase>(StringComparer.Ordinal);
            _proposals = new Dictionary<string, WatcherProposal>(StringComparer.Ordinal);
            _suppressions = new Dictionary<string, WatcherSuppression>(StringComparer.Ordinal);
            _ledger = [];
        }
    }

    private void EnsureLoaded()
    {
        var root = WorkspaceRoot;
        if (root is null) return;
        if (string.Equals(_loadedRoot, root, StringComparison.Ordinal)) return;

        _cases = Read<WatcherCase>(root, CasesFileName)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _proposals = Read<WatcherProposal>(root, ProposalsFileName)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _suppressions = Read<WatcherSuppression>(root, SuppressionsFileName)
            .GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _ledger = Read<WatcherContingentEntry>(root, ContingentFileName).ToList();
        _loadedRoot = root;
        // Seed the compaction counters from what is already on disk, so a
        // restart does not reset a long file back to zero.
        foreach (var fileName in new[] { CasesFileName, ProposalsFileName, SuppressionsFileName })
            _lineCounts[fileName] = CountLines(root, fileName);
    }

    private static int CountLines(string root, string fileName)
    {
        var path = Path.Combine(Folder(root), fileName);
        if (!File.Exists(path)) return 0;
        try
        {
            return File.ReadLines(path, Encoding.UTF8).Count(line => !string.IsNullOrWhiteSpace(line));
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "WatcherStore: an uncountable file only defers compaction");
            return 0;
        }
    }

    private List<T> Read<T>(string root, string fileName)
    {
        var path = Path.Combine(Folder(root), fileName);
        var result = new List<T>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, Json);
                if (item is not null) result.Add(item);
            }
            catch (Exception ex)
            {
                // A torn trailing line is one lost record, not a corrupt file.
                SilentCatch.Note(ex, $"WatcherStore: skipping a malformed line in {fileName}");
            }
        }
        return result;
    }

    /// <summary>
    /// Fold a keyed file back to one line per key once it has accumulated more
    /// history than it holds distinct records. The in-memory view is already the
    /// authority for those files, so the rewrite loses nothing.
    /// </summary>
    private void Compact<T>(string fileName, IReadOnlyCollection<T> current)
    {
        var root = WorkspaceRoot;
        if (root is null) return;
        if (_lineCounts.GetValueOrDefault(fileName) < CompactionThreshold) return;
        if (_lineCounts[fileName] <= current.Count) return;

        var path = Path.Combine(Folder(root), fileName);
        var temporary = path + ".compacting";
        try
        {
            var builder = new StringBuilder();
            foreach (var item in current)
            {
                builder.Append(JsonSerializer.Serialize(item, Json).Replace("\r", "").Replace("\n", " "))
                       .Append(Environment.NewLine);
            }
            File.WriteAllText(temporary, builder.ToString(), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            _lineCounts[fileName] = current.Count;
            _logger.LogInformation(
                "watcher-store-compacted fileName={FileName} records={Records}", fileName, current.Count);
        }
        catch (Exception ex)
        {
            // The append already succeeded, so the record is durable either way.
            // A failed compaction only means the file stays long.
            _logger.LogWarning(ex, "watcher-store-compact-failed fileName={FileName}", fileName);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanupEx) { SilentCatch.Note(cleanupEx, "WatcherStore: temp cleanup is best-effort"); }
        }
    }

    private void Append<T>(string fileName, T item)
    {
        var root = WorkspaceRoot;
        if (root is null)
        {
            _logger.LogDebug("watcher-store-skipped fileName={FileName} reason=no-workspace", fileName);
            return;
        }
        try
        {
            var folder = Folder(root);
            Directory.CreateDirectory(folder);
            var line = JsonSerializer.Serialize(item, Json).Replace("\r", "").Replace("\n", " ");
            File.AppendAllText(Path.Combine(folder, fileName), line + Environment.NewLine, Encoding.UTF8);
            _lineCounts[fileName] = _lineCounts.GetValueOrDefault(fileName) + 1;
        }
        catch (Exception ex)
        {
            // Persistence is observability for the sweep, not a gate on it. The
            // in-memory view already holds the record; the next sweep re-derives
            // the same case from the same signals.
            _logger.LogWarning(ex, "watcher-store-append-failed fileName={FileName}", fileName);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
}
