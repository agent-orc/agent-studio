using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>Everything the Watcher keeps across restarts, as one document per workspace.</summary>
public sealed record WatcherState
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Highest case number handed out so far, so ids never repeat after a
    /// restart. Settable because the store advances it in place while holding
    /// the write lock.
    /// </summary>
    public int LastCaseNumber { get; set; }

    public List<WatcherCase> Cases { get; init; } = [];
    public List<WatcherProposal> Proposals { get; init; } = [];
    public List<WatcherSuppression> Suppressions { get; init; } = [];
    public List<WatcherSpendEntry> Spend { get; init; } = [];
}

/// <summary>
/// One dated row of Watcher spend. Daily and weekly windows are derived from
/// these rows rather than stored as counters, so a restart cannot lose or
/// double-count a window boundary.
/// </summary>
public sealed record WatcherSpendEntry
{
    public DateTime AtUtc { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public int ModelCalls { get; init; }
    public int Proposals { get; init; }
    public int Comments { get; init; }

    /// <summary>Null when the price catalog had no entry for the model. Never summed as zero.</summary>
    public double? Dollars { get; init; }
}

/// <summary>
/// File-backed durable state for the Watcher, at
/// <c>&lt;TaskRepository&gt;/.metadata/watcher-state.json</c>. Cases are keyed by
/// fingerprint, so replaying the same sweep after a restart appends evidence to
/// an existing case instead of creating a second one.
/// </summary>
/// <remarks>
/// Reads are lazy and every mutation persists the whole document. The volume is
/// bounded by the number of distinct open faults in a workspace, which is small
/// by construction: a fault that keeps recurring is one row, not one row per
/// occurrence.
/// </remarks>
public sealed class WatcherStore
{
    public const string FileName = "watcher-state.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IConfiguration _config;
    private readonly ILogger<WatcherStore> _logger;
    private readonly object _gate = new();
    private WatcherState _state = new();
    private bool _loaded;

    /// <summary>Overrides the configured location. Tests point this at a temp folder.</summary>
    public string? StorePathOverride { get; init; }

    public WatcherStore(IConfiguration config, ILogger<WatcherStore> logger)
    {
        _config = config;
        _logger = logger;
    }

    public WatcherState Snapshot()
    {
        EnsureLoaded();
        lock (_gate) return Clone(_state);
    }

    public IReadOnlyList<WatcherCase> Cases()
    {
        EnsureLoaded();
        lock (_gate) return [.. _state.Cases];
    }

    public IReadOnlyList<WatcherProposal> Proposals()
    {
        EnsureLoaded();
        lock (_gate) return [.. _state.Proposals];
    }

    public IReadOnlyList<WatcherSuppression> Suppressions()
    {
        EnsureLoaded();
        lock (_gate) return [.. _state.Suppressions];
    }

    public WatcherCase? FindCase(string caseId)
        => Cases().FirstOrDefault(row => string.Equals(row.CaseId, caseId, StringComparison.OrdinalIgnoreCase));

    public WatcherProposal? FindProposal(string proposalId)
        => Proposals().FirstOrDefault(row => string.Equals(row.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Apply one sweep's worth of changes under a single lock, so a concurrent
    /// reader never observes a case without its proposal.
    /// </summary>
    public T Mutate<T>(Func<WatcherState, T> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        EnsureLoaded();
        lock (_gate)
        {
            var result = mutation(_state);
            Persist();
            return result;
        }
    }

    /// <summary>Reserve the next case id. Must be called inside <see cref="Mutate{T}"/>.</summary>
    public static string NextCaseId(WatcherState state)
    {
        state.LastCaseNumber++;
        return $"WCH-{state.LastCaseNumber:D4}";
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolveStorePath();
            if (path is null || !File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<WatcherState>(json, JsonOpts);
                if (parsed is not null) _state = parsed;
            }
            catch (Exception ex)
            {
                // A corrupt document must not take the sweep down. The Watcher
                // restarts from an empty case store and rediscovers every live
                // fault on the next sweep, which is exactly what it is for.
                _logger.LogError(ex, "watcher-state-unreadable path={Path}", path);
            }
        }
    }

    private void Persist()
    {
        var path = ResolveStorePath();
        if (path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "watcher-state-write-failed path={Path}", path);
        }
    }

    private string? ResolveStorePath()
    {
        if (!string.IsNullOrWhiteSpace(StorePathOverride)) return StorePathOverride;

        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(RegistryPaths.MetadataDir(taskRepo), FileName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", FileName);
    }

    /// <summary>Defensive copy so a caller cannot mutate the live lists outside the lock.</summary>
    private static WatcherState Clone(WatcherState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        LastCaseNumber = state.LastCaseNumber,
        Cases = [.. state.Cases],
        Proposals = [.. state.Proposals],
        Suppressions = [.. state.Suppressions],
        Spend = [.. state.Spend],
    };
}
