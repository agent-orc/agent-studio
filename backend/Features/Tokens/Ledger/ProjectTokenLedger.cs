using System.Globalization;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

/// <summary>
/// The merged usage ledger of one project: historical <c>kind=token-usage</c>
/// bus rows, durable per-task receipts, and chat fallback receipts, read as
/// transient <see cref="OrchestratorLogEntry"/> records with overlap counted
/// once. Every project, card, timeline, and cockpit surface reads this one
/// snapshot; the read health travels with it as
/// <see cref="ProjectTokenDataFreshness"/>.
/// </summary>
public sealed class ProjectTokenLedger
{
    // The snapshot is a pure projection of on-disk facts (bus day-files,
    // task.json receipts including archive, and chat fallback receipts). A board
    // poll re-derives it per project on every request, and the receipt walk
    // alone is heavier than the board scan. Memoize it against the task index
    // snapshot generation: token numbers then lag by at most one generation
    // (mutation, watcher event, or the index safety TTL), which is acceptable
    // because receipts already lag the live bus.
    public const int SnapshotTtlSeconds = 60;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(SnapshotTtlSeconds);

    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly ProjectTokenReceiptReader _receipts;
    private readonly TaskIndexCache? _indexCache;
    private readonly GenerationSingleFlightCache<ProjectTokenLedgerSnapshot> _snapshotCache = new();

    public ProjectTokenLedger(
        AgentMessageBusStore store,
        IConfiguration config,
        ProjectTokenReceiptReader receipts,
        TaskIndexCache? indexCache = null)
    {
        _store = store;
        _config = config;
        _receipts = receipts;
        _indexCache = indexCache;
    }

    public ProjectTokenLedgerSnapshot LoadSnapshot(string projectName, string watchPath)
    {
        // Without an index generation to key on, or without any key material,
        // there is nothing to memoize against.
        if (_indexCache is null
            || (string.IsNullOrWhiteSpace(projectName) && string.IsNullOrWhiteSpace(watchPath)))
        {
            return LoadSnapshotUncached(projectName, watchPath);
        }

        var key = $"{projectName}\n{watchPath}";
        var version = _indexCache.Generation.ToString(CultureInfo.InvariantCulture);
        return _snapshotCache.GetOrCreateVersioned(
            key, version, SnapshotTtl, () => LoadSnapshotUncached(projectName, watchPath));
    }

    private ProjectTokenLedgerSnapshot LoadSnapshotUncached(string projectName, string watchPath)
    {
        var warnings = new List<string>();
        var sources = new List<string>();
        IReadOnlyList<OrchestratorLogEntry> historical = [];
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            warnings.Add("The historical token bus is unavailable because TaskRepository is not configured.");
        }
        else
        {
            try
            {
                historical = BusTokenEntryConverter.LoadTokenUsageEntries(_store, workspace!, projectName);
                sources.Add("historical-token-bus");
            }
            catch (Exception)
            {
                warnings.Add("The historical token bus could not be read. Lifetime values may be incomplete.");
            }
        }

        var receiptRead = _receipts.Read(watchPath);
        if (receiptRead.SourceAvailable) sources.Add("task-token-receipts");
        if (!string.IsNullOrWhiteSpace(receiptRead.Warning)) warnings.Add(receiptRead.Warning!);

        var entries = TokenLedgerMerge.WithoutDuplicates(historical, receiptRead.Entries);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var fallback = ChatUsageFallbackReceipts.Read(workspace!, projectName);
            entries = TokenLedgerMerge.WithoutDuplicates(entries, fallback.Entries);
            sources.Add("chat-usage-fallback");
            if (!string.IsNullOrWhiteSpace(fallback.Warning)) warnings.Add(fallback.Warning!);
        }
        var asOf = entries
            .Where(entry => entry.TokenUsage is not null)
            .Select(entry => (DateTime?)entry.Ts.ToUniversalTime())
            .Max();
        var status = warnings.Count == 0
            ? "complete"
            : entries.Count > 0 ? "partial" : "unavailable";
        var freshness = new ProjectTokenDataFreshness
        {
            Status = status,
            AsOf = asOf?.ToString("o"),
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            Sources = sources,
        };
        return new ProjectTokenLedgerSnapshot(entries, receiptRead.Summaries, freshness);
    }
}

public sealed record ProjectTokenLedgerSnapshot(
    IReadOnlyList<OrchestratorLogEntry> Entries,
    IReadOnlyDictionary<string, TaskTokenSummary> ReceiptSummaries,
    ProjectTokenDataFreshness Freshness);
