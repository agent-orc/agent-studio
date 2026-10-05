using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// One recorded reading of one quota window (AGT-3001). <see cref="At"/> is the
/// probe time of the snapshot the reading came from, not the time it was stored.
/// </summary>
public sealed record QuotaHistoryPoint(DateTime At, string Window, double UsedPct, DateTime? ResetAt);

/// <summary>
/// Durable per-CLI series of quota readings behind
/// <c>GET /api/cli/quota/history</c> (AGT-3001). <see cref="QuotaService"/>
/// keeps only the latest snapshot; the operator needs the curve to see the
/// burn rate and when a weekly window will run out.
///
/// <para>
/// Storage is one append-only JSON-lines file per CLI under
/// <c>&lt;TaskRepository&gt;/.runtime/quota-history/</c>, one line per window per
/// trusted snapshot. Probes run at most every few minutes, so 14 days of
/// retention stays in the low thousands of lines per CLI. Readings older than
/// <see cref="Retention"/> are dropped from memory immediately and from disk by
/// an atomic rewrite once the file carries more than a day of expired lines.
/// A malformed line is skipped, never fatal.
/// </para>
/// </summary>
public sealed class QuotaHistoryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly TimeSpan CompactionSlack = TimeSpan.FromDays(1);
    private static readonly Regex SafeCliName = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<QuotaHistoryStore> _logger;
    private readonly TimeProvider _time;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);

    public QuotaHistoryStore(IConfiguration config, ILogger<QuotaHistoryStore> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        var taskRepo = config["TaskRepository"];
        var runtimeDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        _dir = Path.Combine(runtimeDir, "quota-history");
    }

    /// <summary>
    /// Append one reading per window of a trusted snapshot. Failed, suspicious,
    /// and already-recorded snapshots (same or older probe time) are skipped so a
    /// held last-good value is never counted twice. Returns the number of
    /// readings stored.
    /// </summary>
    public int Record(QuotaSnapshot snapshot)
    {
        if (snapshot is null || !string.IsNullOrWhiteSpace(snapshot.Error) || snapshot.Suspicious) return 0;
        var cli = snapshot.CliType?.Trim().ToLowerInvariant() ?? "";
        if (!SafeCliName.IsMatch(cli)) return 0;

        var at = ToUtc(snapshot.FetchedAt);
        var points = snapshot.Windows
            .Where(window => window.UsedPct is not null && !string.IsNullOrWhiteSpace(window.Label))
            .Select(window => new QuotaHistoryPoint(
                at, window.Label.Trim(), window.UsedPct!.Value, window.ResetAt is { } reset ? ToUtc(reset) : null))
            .ToList();
        if (points.Count == 0) return 0;

        lock (_lock)
        {
            var series = Load(cli);
            if (series.Points.Count > 0 && series.Points[^1].At >= at) return 0;
            try
            {
                Directory.CreateDirectory(_dir);
                var lines = new StringBuilder();
                foreach (var point in points) lines.Append(JsonSerializer.Serialize(point, JsonOpts)).Append('\n');
                File.AppendAllText(PathFor(cli), lines.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "quota_history_append_failed cli={Cli}", cli);
                return 0;
            }
            series.Points.AddRange(points);
            series.OldestOnDisk ??= at;
            Prune(cli, series);
            return points.Count;
        }
    }

    /// <summary>Retained readings for one CLI at or after <paramref name="fromUtc"/>, oldest first.</summary>
    public IReadOnlyList<QuotaHistoryPoint> Read(string cliType, DateTime fromUtc)
    {
        var cli = cliType?.Trim().ToLowerInvariant() ?? "";
        if (!SafeCliName.IsMatch(cli)) return [];
        var from = ToUtc(fromUtc);
        lock (_lock)
        {
            var series = Load(cli);
            Prune(cli, series);
            return series.Points.Where(point => point.At >= from).ToList();
        }
    }

    private Series Load(string cli)
    {
        if (_series.TryGetValue(cli, out var cached)) return cached;
        var series = new Series();
        var path = PathFor(cli);
        if (File.Exists(path))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var point = JsonSerializer.Deserialize<QuotaHistoryPoint>(line, JsonOpts);
                        if (point is null || string.IsNullOrWhiteSpace(point.Window)) continue;
                        series.Points.Add(point with
                        {
                            At = ToUtc(point.At),
                            ResetAt = point.ResetAt is { } reset ? ToUtc(reset) : null,
                        });
                    }
                    catch (JsonException) { series.SkippedLines++; }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "quota_history_read_failed cli={Cli} path={Path}", cli, path);
            }
            series.Points.Sort((a, b) => a.At.CompareTo(b.At));
            series.OldestOnDisk = series.Points.Count > 0 ? series.Points[0].At : null;
            if (series.SkippedLines > 0)
                _logger.LogWarning("quota_history_lines_skipped cli={Cli} count={Count}", cli, series.SkippedLines);
        }
        _series[cli] = series;
        return series;
    }

    private void Prune(string cli, Series series)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - Retention;
        var firstKept = series.Points.FindIndex(point => point.At >= cutoff);
        series.Points.RemoveRange(0, firstKept == -1 ? series.Points.Count : firstKept);
        var diskHasExpiredDay = series.OldestOnDisk is { } oldest && oldest < cutoff - CompactionSlack;
        if (diskHasExpiredDay || series.SkippedLines > 0) Compact(cli, series);
    }

    private void Compact(string cli, Series series)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(cli);
            var tmp = path + ".tmp";
            var lines = new StringBuilder();
            foreach (var point in series.Points) lines.Append(JsonSerializer.Serialize(point, JsonOpts)).Append('\n');
            File.WriteAllText(tmp, lines.ToString(), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
            series.SkippedLines = 0;
            series.OldestOnDisk = series.Points.Count > 0 ? series.Points[0].At : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "quota_history_compaction_failed cli={Cli}", cli);
        }
    }

    private string PathFor(string cli) => Path.Combine(_dir, cli + ".jsonl");

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed class Series
    {
        public List<QuotaHistoryPoint> Points { get; } = [];
        public int SkippedLines { get; set; }
        public DateTime? OldestOnDisk { get; set; }
    }
}
