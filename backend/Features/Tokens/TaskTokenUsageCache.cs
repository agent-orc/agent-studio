using System.Collections.Concurrent;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

/// <summary>
/// Per-card usage for the independent detail resource. A durable card publish
/// or token bus append invalidates only that card; ordinary reads return the
/// priced summary without reopening the project ledger.
/// </summary>
public sealed class TaskTokenUsageCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private readonly ProjectTokenLedger _ledger;
    private readonly ITokenPricing _pricing;
    private readonly TaskIndexCache _index;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string Project, string Path, string Id), Entry> _entries = new();
    private readonly ConcurrentDictionary<(string Project, string Id), long> _busRevisions = new();

    public TaskTokenUsageCache(ProjectTokenLedger ledger, ITokenPricing pricing,
        TaskIndexCache index, TimeProvider clock)
    {
        _ledger = ledger;
        _pricing = pricing;
        _index = index;
        _clock = clock;
    }

    public TaskTokenSummary? Get(string projectName, string watchPath, string jobId)
    {
        var key = (projectName, watchPath, jobId);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            var coreRevision = _index.GetCoreRevision(watchPath, jobId);
            var busRevision = _busRevisions.GetValueOrDefault((projectName, jobId));
            if (entry.Loaded && entry.CoreRevision == coreRevision && entry.BusRevision == busRevision
                && entry.ExpiresAt > _clock.GetUtcNow())
                return entry.Summary;

            var snapshot = _ledger.LoadFreshSnapshot(projectName, watchPath);
            var summary = TaskTokenSummaryFold.PerJob(_pricing.Price(snapshot.Entries))
                .GetValueOrDefault(jobId);
            // If an append arrived while the load ran, this value is still a
            // consistent read, but the next request must compute it again.
            if (coreRevision == _index.GetCoreRevision(watchPath, jobId)
                && busRevision == _busRevisions.GetValueOrDefault((projectName, jobId)))
            {
                entry.Summary = summary;
                entry.CoreRevision = coreRevision;
                entry.BusRevision = busRevision;
                entry.ExpiresAt = _clock.GetUtcNow() + Ttl;
                entry.Loaded = true;
            }
            return summary;
        }
    }

    public void OnTokenUsageAppended(AgentMessage message)
    {
        if (message.Kind != "token-usage" || string.IsNullOrWhiteSpace(message.Project)
            || string.IsNullOrWhiteSpace(message.JobId)) return;
        _busRevisions.AddOrUpdate((message.Project!, message.JobId!), 1, (_, old) => old + 1);
    }

    private sealed class Entry
    {
        public bool Loaded;
        public long CoreRevision;
        public long BusRevision;
        public DateTimeOffset ExpiresAt;
        public TaskTokenSummary? Summary;
    }
}
