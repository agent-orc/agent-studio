using System.Text;
using System.Text.Json;

namespace AgentStudio.AdHoc;

/// <summary>
/// Workspace-wide append-only log of ad-hoc CLI invocations.
/// One JSONL line per <see cref="AdHocUsageRecord"/>, written under
/// <c>&lt;LocalAppData&gt;/agent-taskboard/adhoc-usage.jsonl</c> (or under
/// <c>TaskRepository</c> when configured, mirroring <c>TagRegistryService</c>).
///
/// <para>
/// The recorder is best-effort: any IO failure is logged and swallowed
/// so a transient disk hiccup never breaks the title-generate / summary
/// flow that triggered the call. Callers always invoke
/// <see cref="Record(AdHocUsageRecord)"/> after the subprocess returns
/// so the latency is unaffected by the write.
/// </para>
///
/// <para>
/// Why a separate log instead of folding into the per-project
/// <c>orchestrator.jsonl</c>? Most ad-hoc calls are not tied to a
/// single watched project (the title-generate dialog runs before a job
/// is even created; prompt-enhance runs against the current edit
/// buffer). A workspace-wide log lets the status-bar usage modal show
/// "ambient orchestrator spend" in one place regardless of project.
/// </para>
/// </summary>
public sealed class AdHocUsageRecorder
{
    public const string LogFileName = "adhoc-usage.jsonl";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<AdHocUsageRecorder> _logger;
    private readonly IConfiguration _configuration;
    private readonly AgentMessageBusBridge? _bus;
    private readonly object _writeLock = new();

    public AdHocUsageRecorder(
        ILogger<AdHocUsageRecorder> logger,
        IConfiguration configuration,
        AgentMessageBusBridge? bus = null)
    {
        _logger = logger;
        _configuration = configuration;
        _bus = bus;
    }

    /// <summary>
    /// Resolved log path. Public so the API can echo it to the UI for
    /// the "open log" affordance and tests can read it back.
    /// </summary>
    public string LogPath
    {
        get
        {
            var taskRepo = _configuration["TaskRepository"];
            if (!string.IsNullOrWhiteSpace(taskRepo))
                return Path.Combine(taskRepo, LogFileName);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
                local = Path.GetTempPath();
            return Path.Combine(local, "agent-taskboard", LogFileName);
        }
    }

    /// <summary>
    /// Append one record. Emits a structured info log with the stable
    /// event name <c>adhoc-usage-recorded</c> so the same data is also
    /// visible in the backend's diagnostics stream.
    /// </summary>
    public bool Record(AdHocUsageRecord record)
    {
        try
        {
            var path = LogPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(record, WriteOpts) + Environment.NewLine;
            lock (_writeLock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            _logger.LogInformation(
                "adhoc-usage-recorded source={Source} model={Model} input={Input} output={Output} durationMs={Duration}",
                record.Source, record.Model, record.InputTokens, record.OutputTokens, record.DurationMs);

            // Mirror onto the bus so token aggregation has a single source of
            // truth. Fire-and-forget by design (the bus is observability;
            // failures must not block the canonical write path). When tokens
            // are zero (the plain-text fallback case) we still emit so the
            // per-source call counts on the bus stay accurate.
            if (_bus is not null)
            {
                var usage = new OrchestratorTokenUsage
                {
                    Model = record.Model,
                    InputTokens = (int)record.InputTokens,
                    OutputTokens = (int)record.OutputTokens,
                    CacheReadTokens = (int)record.CacheReadTokens,
                    CacheCreationTokens = (int)record.CacheCreationTokens,
                };
                // Ad-hoc records are workspace-wide by design (the legacy JSONL is
                // workspace-wide too), so route every message to the _workspace
                // projection regardless of the record.Project metadata. The
                // optional project / jobId stay on the message body for
                // drill-down without affecting workspace-wide aggregation.
                _ = _bus.EmitTokenUsageAsync(
                    project: null,
                    jobId: string.IsNullOrWhiteSpace(record.JobId) ? null : record.JobId,
                    participantId: "support:adhoc",
                    topic: string.IsNullOrWhiteSpace(record.Source) ? AdHocUsageSources.Unknown : record.Source,
                    usage: usage,
                    createdAt: record.Ts == default ? null : DateTime.SpecifyKind(record.Ts, DateTimeKind.Utc));
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append ad-hoc usage record (source={Source})", record.Source);
            return false;
        }
    }

    /// <summary>
    /// Read all records, oldest first. Tolerant to torn / malformed
    /// trailing lines: a parse failure on one line skips that line, the
    /// rest are returned. Returns an empty list if the log file does not
    /// exist yet.
    /// </summary>
    public List<AdHocUsageRecord> ReadAll()
    {
        var result = new List<AdHocUsageRecord>();
        var path = LogPath;
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AdHocUsageRecord>(line, ReadOpts);
                    if (entry != null) result.Add(entry);
                }
                catch (Exception __ex)
                {
                    SilentCatch.Note(__ex, "AdHocUsageRecorder: Best-effort: skip torn / malformed lines.");
                    // Best-effort: skip torn / malformed lines.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ad-hoc usage log at {Path}", path);
        }
        return result;
    }

    /// <summary>
    /// Returns (size in bytes, last-modified UTC) for the log file, or
    /// (0, null) when the file does not exist. Used by the API so the UI
    /// can render "log: 12 KB, last write 2 min ago" without round-tripping
    /// the entire file.
    /// </summary>
    public (long SizeBytes, DateTime? ModifiedAt) Stat()
    {
        var path = LogPath;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (0, null);
            return (fi.Length, fi.LastWriteTimeUtc);
        }
        catch
        {
            return (0, null);
        }
    }
}
