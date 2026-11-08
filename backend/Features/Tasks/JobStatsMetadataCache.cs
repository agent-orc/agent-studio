using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// Persisted compact metadata index for cross-job statistics. The hot
/// <see cref="TaskIndexCache"/> deliberately excludes archived cards; stats
/// surfaces still need all-job title/state metadata to label token rows and
/// keep the Job/Supporting/Orchestrator split stable. This cache owns that
/// all-job lookup without hydrating full archived <see cref="TaskInfo"/> rows.
/// </summary>
public sealed class JobStatsMetadataCache
{
    private const int CacheVersion = 1;

    private readonly TaskScannerService _scanner;
    private readonly ILogger<JobStatsMetadataCache> _logger;
    private readonly TimeSpan _safetyTtl;
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, JobStatsMetadataEntry> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<TaskInfo> _snapshot = [];
    private DateTime _snapshotAtUtc = DateTime.MinValue;
    private bool _loaded;
    private bool _dirty = true;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public long Hits;
    public long Refreshes;
    public long IncrementalParses;

    public JobStatsMetadataCache(TaskScannerService scanner, IConfiguration config, ILogger<JobStatsMetadataCache> logger)
    {
        _scanner = scanner;
        _logger = logger;
        var ttlSec = int.TryParse(config["JobStatsMetadataCache:SafetyTtlSeconds"], out var v) ? v : 30;
        _safetyTtl = TimeSpan.FromSeconds(Math.Max(1, ttlSec));

        var taskRepo = config["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        try { Directory.CreateDirectory(baseDir); } catch (Exception __ex) { SilentCatch.Note(__ex, "JobStatsMetadataCache: best-effort"); /* best-effort */ }
        _path = Path.Combine(baseDir, "job-stats-metadata-cache.json");
    }

    /// <summary>
    /// Returns compact metadata for every known job across all lanes, including
    /// <c>7-archive</c>. Refresh is incremental: unchanged task.json files are
    /// reused from memory or the persisted cache, while changed/new folders are
    /// reparsed individually.
    /// </summary>
    public IReadOnlyList<TaskInfo> AllJobs()
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_dirty && DateTime.UtcNow - _snapshotAtUtc < _safetyTtl)
            {
                Interlocked.Increment(ref Hits);
                return _snapshot;
            }

            RefreshUnderLock();
            return _snapshot;
        }
    }

    public IReadOnlyDictionary<string, TaskInfo> JobsById(string watchPath)
    {
        var map = new Dictionary<string, TaskInfo>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(watchPath)) return map;

        foreach (var job in AllJobs())
        {
            if (!string.Equals(job.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
            map[job.Id] = job;
        }
        return map;
    }

    public void Invalidate()
    {
        lock (_lock) { _dirty = true; }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(_path)) return;

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var persisted = JsonSerializer.Deserialize<PersistedJobStatsMetadataCache>(stream, ReadOpts);
            if (persisted?.Version != CacheVersion) return;

            _byFolder = persisted.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.FolderPath))
                .GroupBy(e => e.FolderPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            RebuildSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read job stats metadata cache at {Path}; rebuilding on demand", _path);
            _byFolder.Clear();
            _snapshot = [];
        }
    }

    private void RefreshUnderLock()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var parsed = 0;

        foreach (var candidate in EnumerateCandidates())
        {
            var taskJsonPath = Path.Combine(candidate.JobDir, "task.json");
            if (!File.Exists(taskJsonPath)) continue;

            seen.Add(candidate.JobDir);
            var mtimeTicks = File.GetLastWriteTimeUtc(taskJsonPath).Ticks;
            if (_byFolder.TryGetValue(candidate.JobDir, out var existing)
                && existing.TaskJsonLastWriteUtcTicks == mtimeTicks
                && string.Equals(existing.WatchPath, candidate.Entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsedEntry = TryReadMetadata(candidate.JobDir, candidate.Entry, candidate.State, mtimeTicks);
            if (parsedEntry == null) continue;
            _byFolder[candidate.JobDir] = parsedEntry;
            changed = true;
            parsed++;
        }

        foreach (var folder in _byFolder.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _byFolder.Remove(folder);
            changed = true;
        }

        _dirty = false;
        _snapshotAtUtc = DateTime.UtcNow;
        Interlocked.Increment(ref Refreshes);
        if (parsed > 0) Interlocked.Add(ref IncrementalParses, parsed);
        if (changed)
        {
            RebuildSnapshot();
            PersistUnderLock();
        }
        else if (_snapshot.IsDefaultOrEmpty)
        {
            RebuildSnapshot();
        }
    }

    private IEnumerable<(string JobDir, WatchPathEntry Entry, string State)> EnumerateCandidates()
    {
        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            var flatJobs = TaskStorageLayout.EnumerateJobDirs(entry.Path).ToList();
            if (flatJobs.Count > 0)
            {
                foreach (var jobDir in flatJobs)
                    yield return (jobDir, entry, "");
                continue;
            }

            foreach (var state in TaskStates.All)
            {
                var stateDir = Path.Combine(entry.Path, state);
                if (!Directory.Exists(stateDir)) continue;
                foreach (var jobDir in Directory.EnumerateDirectories(stateDir))
                {
                    if (Path.GetFileName(jobDir).StartsWith('_')) continue;
                    yield return (jobDir, entry, state);
                }
            }
        }
    }

    private JobStatsMetadataEntry? TryReadMetadata(string jobDir, WatchPathEntry entry, string state, long mtimeTicks)
    {
        try
        {
            var taskJsonPath = Path.Combine(jobDir, "task.json");
            var raw = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(taskJsonPath), TaskJsonFile.ReadOpts);
            var folderId = Path.GetFileName(jobDir);
            var isFlatLayout = IsFlatLayoutJobDir(jobDir);
            var id = isFlatLayout
                ? ReadString(raw, "id") ?? folderId
                : folderId;
            var resolvedState = ReadString(raw, "state") ?? state;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(resolvedState)) return null;

            var lastActivity = raw.TryGetProperty("enteredLaneAt", out var entered) && entered.TryGetDateTime(out var enteredAt)
                ? enteredAt
                : File.GetLastWriteTimeUtc(taskJsonPath);
            var createdAt = raw.TryGetProperty("createdAt", out var created) && created.TryGetDateTime(out var createdDt)
                ? createdDt
                : File.GetCreationTimeUtc(taskJsonPath);

            return new JobStatsMetadataEntry
            {
                Id = id,
                TaskKey = TaskIdentity.CreateKey(entry.Path, id),
                Title = ReadString(raw, "title") ?? "",
                State = resolvedState,
                Order = raw.TryGetProperty("order", out var order) && order.TryGetInt32(out var orderVal) ? orderVal : 999,
                WatchPath = entry.Path,
                ProjectName = entry.Name,
                FolderPath = jobDir,
                CreatedAt = createdAt,
                LastActivity = lastActivity,
                TaskJsonLastWriteUtcTicks = mtimeTicks,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse stats metadata from {Dir}", jobDir);
            return null;
        }
    }

    private void RebuildSnapshot()
    {
        _snapshot = _byFolder.Values
            .Select(e => new TaskInfo
            {
                Id = e.Id,
                TaskKey = e.TaskKey,
                Title = e.Title,
                State = e.State,
                Order = e.Order,
                WatchPath = e.WatchPath,
                ProjectName = e.ProjectName,
                FolderPath = e.FolderPath,
                CreatedAt = e.CreatedAt,
                LastActivity = e.LastActivity,
                EnteredLaneAt = e.LastActivity,
            })
            .ToImmutableArray();
    }

    private void PersistUnderLock()
    {
        try
        {
            var payload = new PersistedJobStatsMetadataCache
            {
                Version = CacheVersion,
                WrittenAtUtc = DateTime.UtcNow,
                Entries = _byFolder.Values
                    .OrderBy(e => e.ProjectName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.State, StringComparer.Ordinal)
                    .ThenBy(e => e.Id, StringComparer.Ordinal)
                    .ToList()
            };
            var json = JsonSerializer.Serialize(payload, WriteOpts);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist job stats metadata cache to {Path}", _path);
        }
    }

    private static bool IsFlatLayoutJobDir(string jobDir)
    {
        var bucketDir = Path.GetDirectoryName(jobDir);
        var jobsDir = bucketDir == null ? null : Path.GetDirectoryName(bucketDir);
        return string.Equals(Path.GetFileName(jobsDir), TaskStorageLayout.JobsDirName, StringComparison.Ordinal);
    }

    private static string? ReadString(JsonElement raw, string name)
        => raw.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    private sealed record PersistedJobStatsMetadataCache
    {
        public int Version { get; init; }
        public DateTime WrittenAtUtc { get; init; }
        public List<JobStatsMetadataEntry> Entries { get; init; } = [];
    }

    private sealed record JobStatsMetadataEntry
    {
        public string Id { get; init; } = "";
        public string TaskKey { get; init; } = "";
        public string Title { get; init; } = "";
        public string State { get; init; } = "";
        public int Order { get; init; }
        public string WatchPath { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public string FolderPath { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime LastActivity { get; init; }
        public long TaskJsonLastWriteUtcTicks { get; init; }
    }
}
