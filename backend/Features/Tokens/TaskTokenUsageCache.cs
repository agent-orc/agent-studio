using System.Collections.Concurrent;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

/// <summary>One priced project fold shared by every card, with card-specific receipt and bus versions.</summary>
public sealed class TaskTokenUsageCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private readonly ProjectTokenLedger _ledger;
    private readonly ITokenPricing _pricing;
    private readonly TaskIndexCache _index;
    private readonly TimeProvider _clock;
    private readonly ProjectRegistry? _projects;
    private readonly ILogger<TaskTokenUsageCache>? _logger;
    private readonly ConcurrentDictionary<(string Project, string Path), ProjectEntry> _projectsCache = new();
    private readonly ConcurrentDictionary<(string Project, string Path, string Id), Entry> _entries = new();
    private readonly ConcurrentDictionary<(string Project, string Id), long> _busRevisions = new();

    public TaskTokenUsageCache(ProjectTokenLedger ledger, ITokenPricing pricing,
        TaskIndexCache index, TimeProvider clock, ProjectRegistry? projects = null,
        ILogger<TaskTokenUsageCache>? logger = null)
    {
        _ledger = ledger;
        _pricing = pricing;
        _index = index;
        _clock = clock;
        _projects = projects;
        _logger = logger;
    }

    public TaskTokenSummary? Get(string projectName, string watchPath, string jobId)
    {
        var key = (projectName, watchPath, jobId);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            var receipt = ReceiptStamp(_index.GetDetailFacts(jobId, watchPath).Info);
            var bus = _busRevisions.GetValueOrDefault((projectName, jobId));
            if (entry.Loaded && entry.Receipt == receipt && entry.Bus == bus
                && entry.ExpiresAt > _clock.GetUtcNow()) return entry.Summary;

            var project = _projectsCache.GetOrAdd((projectName, watchPath), _ => new ProjectEntry());
            lock (project)
            {
                if (!project.Loaded || project.ExpiresAt <= _clock.GetUtcNow()
                    || project.ReceiptStamps.GetValueOrDefault(jobId) != receipt
                    || entry.Bus != bus)
                {
                    var snapshot = _ledger.LoadFreshSnapshot(projectName, watchPath);
                    project.Summaries = TaskTokenSummaryFold.PerJob(_pricing.Price(snapshot.Entries));
                    project.ReceiptStamps = _index.GetAllResidentTasks()
                        .Where(task => task.ProjectName == projectName
                            && task.WatchPath == watchPath)
                        .ToDictionary(task => task.Id, ReceiptStamp, StringComparer.Ordinal);
                    project.ReceiptStamps[jobId] = receipt;
                    project.ExpiresAt = _clock.GetUtcNow() + Ttl;
                    project.Loaded = true;
                    project.LoadCount++;
                }
                entry.Summary = project.Summaries.GetValueOrDefault(jobId);
            }
            entry.Receipt = receipt;
            entry.Bus = bus;
            entry.ExpiresAt = _clock.GetUtcNow() + Ttl;
            entry.Loaded = true;
            return entry.Summary;
        }
    }

    /// <summary>Never reads the ledger on the detail request. An absent value warms in the background.</summary>
    public TaskTokenSummary? GetCached(TaskInfo info, out bool ready)
    {
        var entry = _entries.GetOrAdd((info.ProjectName, info.WatchPath, info.Id), _ => new Entry());
        var stamp = ReceiptStamp(info);
        var bus = _busRevisions.GetValueOrDefault((info.ProjectName, info.Id));
        if (!Monitor.TryEnter(entry))
        {
            ready = false;
            return null;
        }
        try
        {
            ready = entry.Loaded && entry.Receipt == stamp && entry.Bus == bus
                && entry.ExpiresAt > _clock.GetUtcNow();
            if (!ready && !entry.Warming)
            {
                entry.Warming = true;
                void StartWarm() => _ = Task.Run(() =>
                {
                    try { Get(info.ProjectName, info.WatchPath, info.Id); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "task-token-cache-warm-failed"); }
                    finally { lock (entry) entry.Warming = false; }
                });
                if (ExecutionContext.IsFlowSuppressed()) StartWarm();
                else
                {
                    using (ExecutionContext.SuppressFlow()) StartWarm();
                }
            }
            return entry.Summary;
        }
        finally { Monitor.Exit(entry); }
    }

    public void OnTokenUsageAppended(AgentMessage message)
    {
        if (message.Kind != "token-usage" || string.IsNullOrWhiteSpace(message.Project)
            || string.IsNullOrWhiteSpace(message.JobId)) return;
        var projectName = _projects?.FindByIdOrDisplayName(message.Project)?.DisplayName ?? message.Project;
        _busRevisions.AddOrUpdate((projectName, message.JobId), 1, (_, old) => old + 1);
    }

    internal long ProjectLoadCount(string projectName, string watchPath) =>
        _projectsCache.TryGetValue((projectName, watchPath), out var project) ? project.LoadCount : 0;

    private static string ReceiptStamp(TaskInfo? info) => info?.TokenReceiptStamp ?? "none";

    private sealed class ProjectEntry
    {
        public bool Loaded;
        public DateTimeOffset ExpiresAt;
        public Dictionary<string, TaskTokenSummary> Summaries = new(StringComparer.Ordinal);
        public Dictionary<string, string> ReceiptStamps = new(StringComparer.Ordinal);
        public long LoadCount;
    }

    private sealed class Entry
    {
        public bool Loaded;
        public bool Warming;
        public string Receipt = "";
        public long Bus;
        public DateTimeOffset ExpiresAt;
        public TaskTokenSummary? Summary;
    }
}
