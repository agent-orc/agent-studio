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
    private readonly ConcurrentDictionary<(string Project, string Id), BusRevision> _busRevisions = new();

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
        _index.CoreRescanCompleted += PruneAbsentEntries;
    }

    public TaskTokenSummary? Get(string projectName, string watchPath, string jobId)
        => GetAsync(projectName, watchPath, jobId).GetAwaiter().GetResult();

    public async Task<TaskTokenSummary?> GetAsync(string projectName, string watchPath, string jobId)
    {
        var key = (projectName, watchPath, jobId);
        var entry = _entries.GetOrAdd(key, _ => new Entry(_index.CoreScanSequence));
        var receipt = ReceiptStamp(_index.GetDetailFacts(jobId, watchPath).Info);
        var bus = _busRevisions.GetValueOrDefault((projectName, jobId)).Version;
        var last = Volatile.Read(ref entry.Last);
        if (last is not null && last.Receipt == receipt && last.Bus == bus
            && last.ExpiresAt > _clock.GetUtcNow()) return last.Summary;

        var project = _projectsCache.GetOrAdd((projectName, watchPath), _ => new ProjectEntry());
        Task<ProjectSnapshot> load;
        lock (project)
        {
            var current = project.Current;
            if (current is not null && current.ExpiresAt > _clock.GetUtcNow()
                && (current.ReceiptStamps.GetValueOrDefault(jobId) ?? "none") == receipt
                && current.BusRevisions.GetValueOrDefault(jobId) == bus)
                load = Task.FromResult(current);
            else
                load = project.Loading ??= Task.Run(() => LoadProject(projectName, watchPath, project));
        }
        var snapshot = await load.ConfigureAwait(false);
        // A change during the walk starts another load on the next request.
        // Publish the summary that was actually captured by this walk.
        var summary = snapshot.Summaries.GetValueOrDefault(jobId);
        Volatile.Write(ref entry.Last, new CardSnapshot(
            snapshot.ReceiptStamps.GetValueOrDefault(jobId) ?? "none",
            snapshot.BusRevisions.GetValueOrDefault(jobId), snapshot.ExpiresAt, summary));
        return summary;
    }

    private ProjectSnapshot LoadProject(string projectName, string watchPath, ProjectEntry project)
    {
        try
        {
            // Capture versions before the ledger walk. An append or receipt
            // publication during the walk must cause the next read to reload.
            var resident = _index.GetAllResidentTasks();
            var receiptStamps = resident
                .Where(task => task.ProjectName == projectName && task.WatchPath == watchPath)
                .ToDictionary(task => task.Id, ReceiptStamp, StringComparer.Ordinal);
            var busRevisions = _busRevisions
                .Where(pair => pair.Key.Project == projectName)
                .ToDictionary(pair => pair.Key.Id, pair => pair.Value.Version, StringComparer.Ordinal);
            var ledger = _ledger.LoadFreshSnapshot(projectName, watchPath);
            var result = new ProjectSnapshot(TaskTokenSummaryFold.PerJob(_pricing.Price(ledger.Entries)),
                receiptStamps, busRevisions, _clock.GetUtcNow() + Ttl);
            lock (project)
            {
                project.Current = result;
                project.Loading = null;
                project.LoadCount++;
            }
            return result;
        }
        catch
        {
            lock (project) project.Loading = null;
            throw;
        }
    }

    /// <summary>Never reads the ledger on the detail request. An absent value warms in the background.</summary>
    public TaskTokenSummary? GetCached(TaskInfo info, out bool ready)
    {
        var entry = _entries.GetOrAdd((info.ProjectName, info.WatchPath, info.Id),
            _ => new Entry(_index.CoreScanSequence));
        var stamp = ReceiptStamp(info);
        var bus = _busRevisions.GetValueOrDefault((info.ProjectName, info.Id)).Version;
        var last = Volatile.Read(ref entry.Last);
        ready = last is not null && last.Receipt == stamp && last.Bus == bus
            && last.ExpiresAt > _clock.GetUtcNow();
        if (!ready && Interlocked.CompareExchange(ref entry.Warming, 1, 0) == 0)
        {
            void StartWarm() => _ = Task.Run(async () =>
            {
                try { await GetAsync(info.ProjectName, info.WatchPath, info.Id).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "task-token-cache-warm-failed"); }
                finally { Volatile.Write(ref entry.Warming, 0); }
            });
            if (ExecutionContext.IsFlowSuppressed()) StartWarm();
            else
            {
                using (ExecutionContext.SuppressFlow()) StartWarm();
            }
        }
        return last?.Summary;
    }

    public void OnTokenUsageAppended(AgentMessage message)
    {
        if (message.Kind != "token-usage" || string.IsNullOrWhiteSpace(message.Project)
            || string.IsNullOrWhiteSpace(message.JobId)) return;
        var projectName = _projects?.FindByIdOrDisplayName(message.Project)?.DisplayName ?? message.Project;
        var ids = _index.GetAllResidentTasks()
            .Where(task => task.ProjectName == projectName
                && (task.Id == message.JobId || task.TaskKey == message.JobId
                    || task.Key == message.JobId))
            .Select(task => task.Id)
            .Append(message.JobId)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var generation = _index.CoreScanSequence;
            _busRevisions.AddOrUpdate((projectName, id),
                new BusRevision(1, generation), (_, old) => new BusRevision(old.Version + 1, generation));
        }
    }

    private void PruneAbsentEntries(long completedScanSequence, IReadOnlyList<TaskInfo> resident)
    {
        // A scan may have started before an entry was written. Only a later
        // scan has proven that a missing card is still absent.
        var present = resident.Select(info => (info.ProjectName, info.WatchPath, info.Id)).ToHashSet();
        foreach (var (key, entry) in _entries)
            if (completedScanSequence > entry.BornScanSequence && !present.Contains(key))
                ((ICollection<KeyValuePair<(string Project, string Path, string Id), Entry>>)_entries)
                    .Remove(new KeyValuePair<(string Project, string Path, string Id), Entry>(key, entry));
        var presentIds = resident.Select(info => (info.ProjectName, info.Id)).ToHashSet();
        foreach (var (key, revision) in _busRevisions)
            if (completedScanSequence > revision.Generation && !presentIds.Contains(key))
                ((ICollection<KeyValuePair<(string Project, string Id), BusRevision>>)_busRevisions)
                    .Remove(new KeyValuePair<(string Project, string Id), BusRevision>(key, revision));
    }

    internal long ProjectLoadCount(string projectName, string watchPath) =>
        _projectsCache.TryGetValue((projectName, watchPath), out var project) ? project.LoadCount : 0;

    internal int EntryCount => _entries.Count;
    internal int BusRevisionCount => _busRevisions.Count;

    private static string ReceiptStamp(TaskInfo? info) => info?.TokenReceiptStamp ?? "none";

    private sealed class ProjectEntry
    {
        public ProjectSnapshot? Current;
        public Task<ProjectSnapshot>? Loading;
        public long LoadCount;
    }

    private sealed record ProjectSnapshot(
        IReadOnlyDictionary<string, TaskTokenSummary> Summaries,
        IReadOnlyDictionary<string, string> ReceiptStamps,
        IReadOnlyDictionary<string, long> BusRevisions,
        DateTimeOffset ExpiresAt);

    private sealed record CardSnapshot(string Receipt, long Bus, DateTimeOffset ExpiresAt,
        TaskTokenSummary? Summary);

    private readonly record struct BusRevision(long Version, long Generation);

    private sealed class Entry(long bornScanSequence)
    {
        public long BornScanSequence { get; } = bornScanSequence;
        public CardSnapshot? Last;
        public int Warming;
    }
}
