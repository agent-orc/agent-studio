using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Projects;

/// <summary>
/// One check's failure streak past its threshold. <see cref="CardId"/> is the card of the
/// environment incident it belongs to; <see cref="Reported"/> says that card shows it.
/// <see cref="EndReason"/> is <c>recovered</c> or <c>check-removed</c>.
/// </summary>
public sealed record HealthIncident(string Id, string EnvironmentId, string CheckId, DateTimeOffset StartedAt,
    string FirstError, int FailedProbes, DateTimeOffset? EndedAt = null, string? CardId = null,
    bool Reported = false, bool RecoveryReported = false, string? EndReason = null);

/// <summary>
/// The incident of a whole environment: open while any of its checks has an open incident. It
/// owns the one card and the Activity entries, so a site whose HTTP and TLS checks fail together
/// raises one alert and one card.
/// </summary>
public sealed record EnvironmentIncident(string Id, string EnvironmentId, string EnvironmentName,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt = null, string? CardId = null, bool OpenedEventRaised = false,
    bool RecoveryReported = false);

public sealed class EnvironmentHealthState
{
    public EnvironmentIncident? OpenIncident { get; set; }
    public EnvironmentIncident? LastIncident { get; set; }
    /// <summary>The card of the latest incident; a later incident updates it while it is open.</summary>
    public string? IncidentCardId { get; set; }
}

/// <summary>Durable per-check state: counters, open and last incident, certificate warnings.</summary>
public sealed class HealthCheckState
{
    public DateTimeOffset? LastRunAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    /// <summary>The first failure of the current failure streak; the incident start.</summary>
    public DateTimeOffset? FailingSince { get; set; }
    public HealthSample? LastSample { get; set; }
    public HealthIncident? OpenIncident { get; set; }
    public HealthIncident? LastIncident { get; set; }
    public DateTimeOffset? CertificateNotAfter { get; set; }
    public List<int> CertificateWarnedDays { get; set; } = [];
    public string? CertificateCardId { get; set; }
}

public sealed class ProjectHealthState
{
    public Dictionary<string, HealthCheckState> Checks { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, EnvironmentHealthState> Environments { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastRetentionAt { get; set; }
}

/// <summary>
/// File store under <c>&lt;TaskRepository&gt;/health-checks/&lt;project&gt;/</c>: <c>state.json</c>, raw
/// samples as one JSONL file per UTC day per check (kept 30 days) and <c>hourly.jsonl</c>
/// aggregates of every completed day (kept 400 days).
/// </summary>
public sealed class HealthCheckStore(IConfiguration configuration)
{
    /// <summary>Parsed files kept in memory; about two raw days and one hourly file per check are hot.</summary>
    public const int CacheCapacity = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    // Parsed JSONL files by path, least recently used first out. Every write through the store drops
    // its entry; the length check also catches a file changed behind the store's back.
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.Ordinal);
    private long _cacheUse;

    private sealed class CachedFile(long length, Array values)
    {
        public long Length { get; } = length;
        public Array Values { get; } = values;
        public long LastUse { get; set; }
    }

    public static string Key(string environmentId, string checkId) => environmentId + "/" + checkId;

    /// <summary>Entries in the file cache, for the eviction test.</summary>
    public int CachedFiles
    {
        get
        {
            lock (_gate) return _cache.Count;
        }
    }

    public ProjectHealthState Load(string project)
    {
        lock (_gate)
        {
            var path = StatePath(project);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProjectHealthState>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
    }

    public bool HasState(string project)
    {
        lock (_gate) return File.Exists(StatePath(project));
    }

    public void Save(string project, ProjectHealthState state)
    {
        lock (_gate)
        {
            var path = StatePath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, path, true);
        }
    }

    public void Append(string project, string environmentId, string checkId, HealthSample sample)
    {
        lock (_gate)
        {
            var directory = RawDirectory(project, environmentId, checkId);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, DayFile(DateOnly.FromDateTime(sample.At.UtcDateTime)));
            File.AppendAllText(file, JsonSerializer.Serialize(sample, Json) + "\n");
            _cache.Remove(file);
        }
    }

    /// <summary>Raw samples with <c>At</c> after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<HealthSample> Samples(string project, string environmentId, string checkId,
        DateTimeOffset since)
    {
        lock (_gate)
        {
            var directory = RawDirectory(project, environmentId, checkId);
            if (!Directory.Exists(directory)) return [];
            var first = DateOnly.FromDateTime(since.UtcDateTime);
            var result = new List<HealthSample>();
            foreach (var (day, file) in RawDays(directory).Where(item => item.Day >= first).OrderBy(item => item.Day))
                result.AddRange(ReadCached<HealthSample>(file).Where(sample => sample.At > since));
            return result;
        }
    }

    public IReadOnlyList<HealthHourlyAggregate> Hourly(string project, string environmentId, string checkId)
    {
        lock (_gate)
        {
            var path = HourlyPath(project, environmentId, checkId);
            return File.Exists(path) ? ReadCached<HealthHourlyAggregate>(path) : [];
        }
    }

    /// <summary>
    /// Folds every completed raw day into hourly aggregates, which serve the 7- and 30-day
    /// uptime, deletes raw days past the raw window and drops aggregates past their retention.
    /// A day with an aggregated hour is folded already and never folded twice, so a crash
    /// between the write and the delete is safe to repeat.
    /// </summary>
    /// <returns>The number of days folded.</returns>
    public int ApplyRetention(string project, DateTimeOffset now)
    {
        lock (_gate)
        {
            var root = ProjectDirectory(project);
            if (!Directory.Exists(root)) return 0;
            var folded = 0;
            foreach (var environment in Directory.EnumerateDirectories(root))
            foreach (var check in Directory.EnumerateDirectories(environment))
            {
                var raw = Path.Combine(check, "raw");
                var hourlyPath = Path.Combine(check, "hourly.jsonl");
                var existing = File.Exists(hourlyPath) ? ReadCached<HealthHourlyAggregate>(hourlyPath).ToList() : [];
                var foldedDays = existing.Select(item => DateOnly.FromDateTime(item.Hour.UtcDateTime)).ToHashSet();
                var days = Directory.Exists(raw) ? RawDays(raw).ToDictionary(item => item.Day, item => item.File) : [];
                var toFold = HealthSeries.DaysToAggregate(days.Keys.Where(day => !foldedDays.Contains(day)), now);
                foreach (var day in toFold)
                    existing.AddRange(HealthSeries.Hourly(ReadCached<HealthSample>(days[day])));
                folded += toFold.Count;
                var kept = existing.Where(item => item.Hour > now - HealthSeries.HourlyRetention)
                    .OrderBy(item => item.Hour).ToList();
                if (toFold.Count > 0 || kept.Count != existing.Count) WriteLines(hourlyPath, kept);
                foreach (var day in HealthSeries.DaysPastRawRetention(days.Keys, now))
                {
                    File.Delete(days[day]);
                    _cache.Remove(days[day]);
                }
            }
            return folded;
        }
    }

    private T[] ReadCached<T>(string path)
    {
        var length = new FileInfo(path).Length;
        if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Values is T[] values)
        {
            cached.LastUse = ++_cacheUse;
            return values;
        }
        var read = ReadLines<T>(path).ToArray();
        if (_cache.Count >= CacheCapacity && !_cache.ContainsKey(path))
            _cache.Remove(_cache.MinBy(item => item.Value.LastUse).Key);
        _cache[path] = new CachedFile(length, read) { LastUse = ++_cacheUse };
        return read;
    }

    private static IReadOnlyList<T> ReadLines<T>(string path)
    {
        var result = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(line, Json) is { } value) result.Add(value);
            }
            catch (JsonException)
            {
                // A torn trailing line from an interrupted append carries no sample; skip it.
                continue;
            }
        }
        return result;
    }

    private void WriteLines<T>(string path, IEnumerable<T> values)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllLines(temporary, values.Select(value => JsonSerializer.Serialize(value, Json)));
        File.Move(temporary, path, true);
        _cache.Remove(path);
    }

    private static IEnumerable<(DateOnly Day, string File)> RawDays(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return (day, file);
    }

    private static string DayFile(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";

    private string Root() => Path.Combine(configuration["TaskRepository"] ??
        throw new InvalidOperationException("TaskRepository is required."), "health-checks");

    private string ProjectDirectory(string project) => Path.Combine(Root(), SafeSegment(project.ToLowerInvariant()));
    private string StatePath(string project) => Path.Combine(ProjectDirectory(project), "state.json");
    private string CheckDirectory(string project, string environmentId, string checkId)
        => Path.Combine(ProjectDirectory(project), SafeSegment(environmentId), SafeSegment(checkId));
    private string RawDirectory(string project, string environmentId, string checkId)
        => Path.Combine(CheckDirectory(project, environmentId, checkId), "raw");
    private string HourlyPath(string project, string environmentId, string checkId)
        => Path.Combine(CheckDirectory(project, environmentId, checkId), "hourly.jsonl");

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Invalid path segment.", nameof(value));
        return Uri.EscapeDataString(value);
    }
}
