using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// File-backed snapshots for the two workspace token rollups that
/// feed the status-bar usage modal: the per-(project,time-bucket)
/// timeline and the top-N expensive-jobs list.
///
/// <para>
/// Both surfaces fold the workspace agent bus. The fold is cheap once
/// the bus is warm but the first call after a backend restart pays a
/// disk scan, and the modal is the user's primary "is anything
/// burning right now?" entry point. Persisting the last successful
/// result mirrors <see cref="TokenSummaryCacheStore"/> and
/// <see cref="AgentStudio.Cli.QuotaCacheStore"/> so
/// the modal can render last-known numbers immediately on hover.
/// </para>
///
/// <para>
/// Timeline snapshots are keyed by (windowHours, bucketMinutes); a
/// separate file per combination keeps each cache file small and
/// avoids stomping the 24 h view while writing the 168 h one. The
/// expensive-jobs cache is a single file - the list is small and the
/// hover panel only requests one limit (8).
/// </para>
///
/// <para>
/// Lives under <c>&lt;TaskRepository&gt;/.runtime/</c> (or
/// <c>AppContext.BaseDirectory/runtime/</c> when no TaskRepository is
/// configured). Atomic via temp + rename. Tolerant to corruption:
/// parse errors log and return null so the live aggregator is the
/// fallback.
/// </para>
/// </summary>
public sealed class WorkspaceTokensCacheStore
{
    private readonly ILogger<WorkspaceTokensCacheStore> _logger;
    private readonly string _baseDir;
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WorkspaceTokensCacheStore(IConfiguration config, ILogger<WorkspaceTokensCacheStore> logger)
    {
        _logger = logger;
        var taskRepo = config["TaskRepository"];
        _baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        try { Directory.CreateDirectory(_baseDir); } catch (Exception __ex) { SilentCatch.Note(__ex, "WorkspaceTokensCacheStore: best-effort"); /* best-effort */ }
    }

    // ---- Timeline ----

    public TokenTimeline? ReadTimeline(int windowHours, int bucketMinutes)
        => ReadJson<TokenTimeline>(TimelinePath(windowHours, bucketMinutes));

    public void WriteTimeline(int windowHours, int bucketMinutes, TokenTimeline timeline)
        => WriteJson(TimelinePath(windowHours, bucketMinutes), timeline);

    private string TimelinePath(int windowHours, int bucketMinutes)
        => Path.Combine(_baseDir, $"tokens-timeline-{windowHours}h-{bucketMinutes}m.json");

    // ---- Expensive jobs ----

    public WorkspaceExpensiveJobsResponse? ReadExpensiveJobs()
        => ReadJson<WorkspaceExpensiveJobsResponse>(ExpensiveJobsPath());

    public void WriteExpensiveJobs(WorkspaceExpensiveJobsResponse response)
        => WriteJson(ExpensiveJobsPath(), response);

    private string ExpensiveJobsPath()
        => Path.Combine(_baseDir, "tokens-expensive-jobs.json");

    public void Invalidate()
    {
        try
        {
            lock (_writeLock)
            {
                if (!Directory.Exists(_baseDir)) return;
                foreach (var path in Directory.EnumerateFiles(_baseDir, "tokens-*.json"))
                    File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate workspace token caches under {Path}", _baseDir);
        }
    }

    // ---- Shared I/O ----

    private T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(raw, ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read workspace-tokens cache at {Path}", path);
            return null;
        }
    }

    private void WriteJson<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, WriteOpts);
            lock (_writeLock)
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist workspace-tokens cache to {Path}", path);
        }
    }
}
