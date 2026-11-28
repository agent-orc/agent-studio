using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Per-job append-mostly writer + reader for <c>logs/timeline.jsonl</c>,
/// the unified event ledger described in ADR-0049.
///
/// <para>
/// One row per event, JSONL so a torn write costs at most one event
/// rather than a corrupted document - same shape contract as
/// <see cref="TaskSessionLog"/>. Writes are best-effort: persistence is
/// observability, not a state-machine input, so a write failure logs and
/// returns false rather than throwing.
/// </para>
///
/// <para>
/// Callers pass the job folder path directly. The class deliberately does
/// not route through <see cref="TaskScannerService.FindJob"/> - many of the
/// producers (the runner, the review-decision orchestrator, the pipeline
/// step recorder) already hold the <see cref="TaskInfo"/> for the row they
/// are about to emit, and threading a scanner lookup through every call
/// site would add cost and a stale-folder failure mode that the existing
/// callers do not have today.
/// </para>
/// </summary>
public sealed class TimelineLog : IDisposable
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Keep only the events used by weekly hold reporting, bounded across all cards.
    private readonly MemoryCache _integrationWaitHistory = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private sealed record WaitHistoryStamp(long Length, DateTime Written, DateTime Created);
    private sealed record WaitHistorySnapshot(WaitHistoryStamp Stamp, IReadOnlyList<TimelineEvent> Events);

    /// <summary>
    /// Reuse wait receipts while the ledger is unchanged. File metadata also detects
    /// writes from other readers or processes; a moved card has a different path.
    /// A read racing an append is returned but never cached under the newer stamp.
    /// </summary>
    internal IReadOnlyList<TimelineEvent> ReadIntegrationWaitHistory(string jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return [];
        var path = Path.GetFullPath(TaskPaths.TimelineLog(jobFolderPath));
        var stamp = Stamp(path);
        if (stamp is null)
        {
            _integrationWaitHistory.Remove(path);
            return [];
        }
        if (_integrationWaitHistory.TryGetValue<WaitHistorySnapshot>(path, out var cached) && cached!.Stamp == stamp)
            return cached.Events;
        var events = Array.AsReadOnly(ReadAll(jobFolderPath).Where(e =>
            e.Kind is TimelineEventKinds.IntegrationQuarantineWait or TimelineEventKinds.IntegrationSucceeded
            || e.Kind == TimelineEventKinds.LaneChanged && e.Details?.GetValueOrDefault("to") == TaskStates.Archive).ToArray());
        if (stamp == Stamp(path))
            _integrationWaitHistory.Set(path, new WaitHistorySnapshot(stamp, events),
                new MemoryCacheEntryOptions { Size = Math.Max(1, events.Count), SlidingExpiration = TimeSpan.FromMinutes(10) });
        return events;
    }

    private static WaitHistoryStamp? Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : null;
    }

    public void Dispose() => _integrationWaitHistory.Dispose();

    private readonly ILogger<TimelineLog> _logger;

    private readonly AgentStudio.Management.StructureHistoryBuffer? _structure;

    public TimelineLog(ILogger<TimelineLog> logger, AgentStudio.Management.StructureHistoryBuffer? structure = null)
    {
        _logger = logger;
        _structure = structure;
    }

    /// <summary>
    /// Convenience overload used by most producers. Stamps <see cref="DateTime.UtcNow"/>
    /// at the call site and forwards to <see cref="Append(string, TimelineEvent)"/>.
    /// </summary>
    public bool Append(string jobFolderPath, string kind, string actor, string summary, string? runId = null, string? payloadRef = null, Dictionary<string, string>? details = null)
        => Append(jobFolderPath, new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = kind,
            Actor = actor,
            Summary = summary ?? string.Empty,
            RunId = runId,
            PayloadRef = payloadRef,
            Details = details,
        });

    /// <summary>
    /// Append one event to <c>logs/timeline.jsonl</c>. The job folder is
    /// created if missing; we do **not** refuse-on-missing-folder the way
    /// <see cref="Runner.OrchestratorChatLog"/> does, because the timeline
    /// is the canonical record (ADR-0049) and silently dropping an
    /// escalation event because the folder was racing a move would defeat
    /// the point. The chat log can drop a duplicate line; the timeline
    /// must not drop the underlying event. During an operator agent session
    /// the event is attributed to that session (<see cref="OperatorActorScope"/>).
    /// </summary>
    public bool Append(string jobFolderPath, TimelineEvent evt)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath) || evt == null) return false;
        evt = OperatorActorScope.Attribute(evt);
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(jobFolderPath));
            var path = TaskPaths.TimelineLog(jobFolderPath);
            var line = JsonSerializer.Serialize(evt, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
            if (evt.Kind == TimelineEventKinds.IntegrationSucceeded)
                _structure?.Add(new(evt.Ts, "task timeline", "delivery-integrated", Path.GetFileName(jobFolderPath)));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TimelineLog: failed to append {Kind} for {Folder}", evt.Kind, jobFolderPath);
            return false;
        }
    }

    /// <summary>
    /// Read the timeline for one job. Tolerant to torn / malformed
    /// trailing lines (skipped silently) - same contract as
    /// <see cref="TaskSessionLog.ReadSessionEvents"/>.
    /// <para>
    /// The ledger has no size cap and many callers read it per card per
    /// tick, so the read is bounded to the newest
    /// <see cref="BoundedFileRead.LedgerBytes"/> (AGT-2991). A normal
    /// timeline is a small fraction of that and is returned whole; a larger
    /// one yields its newest complete events and a warning.
    /// </para>
    /// </summary>
    public List<TimelineEvent> ReadAll(string jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return [];
        var path = TaskPaths.TimelineLog(jobFolderPath);
        if (!File.Exists(path)) return [];
        var result = new List<TimelineEvent>();
        var lines = BoundedFileRead.ReadTailLines(path, BoundedFileRead.LedgerBytes, out var truncated);
        if (truncated)
            _logger.LogWarning(
                "TimelineLog: {Path} exceeds {MaxBytes} bytes; reading the newest events only",
                path, BoundedFileRead.LedgerBytes);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<TimelineEvent>(line, ReadOpts);
                if (evt != null) result.Add(evt);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "TimelineLog: Best-effort: skip torn / malformed lines.");
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }
}
