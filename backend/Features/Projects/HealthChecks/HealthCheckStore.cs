using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Projects;

public sealed record HealthIncident(string Id, string EnvironmentId, string CheckId, DateTimeOffset StartedAt,
    string FirstError, int FailedProbes, DateTimeOffset? EndedAt = null, string? CardId = null,
    bool OpenedEventRaised = false, bool RecoveryReported = false);

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
    /// <summary>The card of the latest incident; a reopened incident updates it while it is open.</summary>
    public string? IncidentCardId { get; set; }
    public DateTimeOffset? CertificateNotAfter { get; set; }
    public List<int> CertificateWarnedDays { get; set; } = [];
    public string? CertificateCardId { get; set; }
}

public sealed class ProjectHealthState
{
    public Dictionary<string, HealthCheckState> Checks { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastRetentionAt { get; set; }
}

/// <summary>
/// File store under <c>&lt;TaskRepository&gt;/health-checks/&lt;project&gt;/</c>: <c>state.json</c>, raw
/// samples as one JSONL file per UTC day per check (kept 30 days) and <c>hourly.jsonl</c>
/// aggregates (kept 400 days).
/// </summary>
public sealed class HealthCheckStore(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    // Completed day files never change; the cache key includes the length so today's file rereads after appends.
    private readonly Dictionary<string, (long Length, HealthSample[] Samples)> _dayCache = new(StringComparer.Ordinal);

    public static string Key(string environmentId, string checkId) => environmentId + "/" + checkId;

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
            File.AppendAllText(Path.Combine(directory, DayFile(DateOnly.FromDateTime(sample.At.UtcDateTime))),
                JsonSerializer.Serialize(sample, Json) + "\n");
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
                result.AddRange(ReadDay(file).Where(sample => sample.At > since));
            return result;
        }
    }

    public IReadOnlyList<HealthHourlyAggregate> Hourly(string project, string environmentId, string checkId)
    {
        lock (_gate)
        {
            var path = HourlyPath(project, environmentId, checkId);
            return File.Exists(path) ? ReadLines<HealthHourlyAggregate>(path) : [];
        }
    }

    /// <summary>
    /// Folds raw days that left the raw window into hourly aggregates, then deletes them, and
    /// drops aggregates past their retention. An hour that is already aggregated is never
    /// written twice, so a crash between the append and the delete is safe to repeat.
    /// </summary>
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
                var existing = File.Exists(hourlyPath) ? ReadLines<HealthHourlyAggregate>(hourlyPath).ToList() : [];
                var known = existing.Select(item => item.Hour).ToHashSet();
                var days = Directory.Exists(raw) ? RawDays(raw).ToDictionary(item => item.Day, item => item.File) : [];
                foreach (var day in HealthSeries.DaysToAggregate(days.Keys, now))
                {
                    var file = days[day];
                    existing.AddRange(HealthSeries.Hourly(ReadDay(file)).Where(item => known.Add(item.Hour)));
                    WriteLines(hourlyPath, existing.OrderBy(item => item.Hour));
                    File.Delete(file);
                    _dayCache.Remove(file);
                    folded++;
                }
                var kept = existing.Where(item => item.Hour > now - HealthSeries.HourlyRetention).ToList();
                if (kept.Count != existing.Count) WriteLines(hourlyPath, kept);
            }
            return folded;
        }
    }

    private HealthSample[] ReadDay(string file)
    {
        var length = new FileInfo(file).Length;
        if (_dayCache.TryGetValue(file, out var cached) && cached.Length == length) return cached.Samples;
        var samples = ReadLines<HealthSample>(file).ToArray();
        _dayCache[file] = (length, samples);
        return samples;
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

    private static void WriteLines<T>(string path, IEnumerable<T> values)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllLines(temporary, values.Select(value => JsonSerializer.Serialize(value, Json)));
        File.Move(temporary, path, true);
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
