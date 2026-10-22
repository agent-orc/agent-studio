using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3219: one machine gate queue episode in which a gate request spent its
/// wait budget before it reached a slot. Raised when the episode opens, not per
/// card, and raised again as reminder <see cref="Raise"/> 2, 3, ... while the
/// episode stays open.
/// </summary>
public sealed record GateQueueStallEvent(
    string EpisodeId, string Project, DateTimeOffset RaisedAtUtc,
    int QueueDepth, long OldestWaitMs, int SlotsBusy, int SlotsTotal,
    long EstimatedDrainMs, long QueueBudgetMs, int IdleConfiguredSlots,
    string? Advisory, string TriggerTaskKey, int Raise = 1, DateTimeOffset? EpisodeOpenedAtUtc = null);

/// <summary>The open stall episode of a project and its latest event; durable across restarts.</summary>
public sealed record GateQueueStallEpisode(
    string Project, string EpisodeId, DateTimeOffset OpenedAtUtc, GateQueueStallEvent Latest);

/// <summary>
/// Queue wait one review generation spent in machine gate queue overruns,
/// summed across its requeues. The cap on it turns an endless requeue into a
/// gate capacity escalation.
/// </summary>
public sealed record GateQueueWaitLedger(
    string ReviewAttemptId, string TaskKey, long TotalQueueWaitMs, int Overruns,
    DateTimeOffset FirstOverrunAtUtc, DateTimeOffset LastOverrunAtUtc);

public sealed partial class BatchGateStore
{
    /// <summary>Stall events kept per project; older ones are pruned when a new one is recorded.</summary>
    internal const int RetainedQueueStalls = 50;

    private readonly object _queueWaitLock = new();
    private readonly object _queueStallLock = new();

    /// <summary>
    /// Records a stall event in the project's folder. File names start with the
    /// raise time, so the history sorts by name and reads only the newest files.
    /// </summary>
    public void RecordQueueStall(GateQueueStallEvent stall)
    {
        lock (_queueStallLock)
        {
            var folder = QueueStallFolder(stall.Project);
            Directory.CreateDirectory(folder);
            var name = $"{stall.RaisedAtUtc.UtcTicks:D19}-{SafeName(stall.EpisodeId)}-{stall.Raise}";
            var path = Path.Combine(folder, name + ".json");
            if (!File.Exists(path)) WriteOnce(path, stall);
            PruneQueueStalls(folder);
        }
    }

    private void PruneQueueStalls(string folder)
    {
        foreach (var stale in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                     .SkipLast(RetainedQueueStalls).ToArray())
            File.Delete(stale);
        // The first AGT-3219 builds kept every project's events in one flat
        // folder that was read whole for each report; that layout is retired.
        foreach (var legacy in Directory.EnumerateFiles(Path.Combine(_root, "queue-stalls"), "*.json").ToArray())
            File.Delete(legacy);
    }

    private string QueueStallFolder(string project) => Path.Combine(_root, "queue-stalls", SafeName(project));

    public GateQueueStallEpisode? ReadQueueStallEpisode(string project)
    {
        var path = QueueStallEpisodePath(project);
        return File.Exists(path) ? Read<GateQueueStallEpisode>(path) : null;
    }

    public void WriteQueueStallEpisode(GateQueueStallEpisode episode)
    {
        var path = QueueStallEpisodePath(episode.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ReplaceQueueStallRecord(path, episode);
    }

    public void CloseQueueStallEpisode(string project)
    {
        var path = QueueStallEpisodePath(project);
        if (File.Exists(path)) File.Delete(path);
    }

    private string QueueStallEpisodePath(string project)
        => Path.Combine(_root, "queue-stall-episodes", SafeName(project) + ".json");

    public GateQueueWaitLedger? ReadQueueWait(string reviewAttemptId)
    {
        var path = QueueWaitPath(reviewAttemptId);
        return File.Exists(path) ? Read<GateQueueWaitLedger>(path) : null;
    }

    /// <summary>Adds one queue overrun of the review generation and returns the new total.</summary>
    public GateQueueWaitLedger AddQueueWait(string reviewAttemptId, string taskKey, long waitedMs,
        DateTimeOffset nowUtc)
    {
        lock (_queueWaitLock)
        {
            var prior = ReadQueueWait(reviewAttemptId);
            var ledger = prior is null
                ? new GateQueueWaitLedger(reviewAttemptId, taskKey, Math.Max(0, waitedMs), 1, nowUtc, nowUtc)
                : prior with
                {
                    TotalQueueWaitMs = prior.TotalQueueWaitMs + Math.Max(0, waitedMs),
                    Overruns = prior.Overruns + 1,
                    LastOverrunAtUtc = nowUtc,
                };
            var path = QueueWaitPath(reviewAttemptId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ReplaceQueueStallRecord(path, ledger);
            return ledger;
        }
    }

    /// <summary>
    /// Ends the review generation's queue wait ledger: the review passed or was
    /// superseded, or an operator release grants it a fresh queue wait cap.
    /// </summary>
    public void ClearQueueWait(string reviewAttemptId)
    {
        lock (_queueWaitLock)
        {
            var path = QueueWaitPath(reviewAttemptId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string QueueWaitPath(string reviewAttemptId)
        => Path.Combine(_root, "queue-waits", SafeName(reviewAttemptId) + ".json");

    private static void ReplaceQueueStallRecord<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Returns a resolved pending record to the queue with its original
    /// sequence. Used only when an operator releases a card that a queue
    /// overrun escalated; a missing record is not an error.
    /// </summary>
    public void ReopenPending(string reviewAttemptId)
    {
        var resolved = Path.Combine(_root, "pending", SafeName(reviewAttemptId) + ".json.resolved");
        if (File.Exists(resolved)) File.Delete(resolved);
    }

    /// <summary>True while an unresolved pending record exists for the review.</summary>
    public bool IsQueued(string reviewAttemptId)
    {
        var path = Path.Combine(_root, "pending", SafeName(reviewAttemptId) + ".json");
        return File.Exists(path) && !File.Exists(path + ".resolved");
    }

    /// <summary>The project's newest <paramref name="take"/> stall events, oldest first.</summary>
    public IReadOnlyList<GateQueueStallEvent> ListQueueStalls(string project, int take = RetainedQueueStalls)
    {
        var folder = QueueStallFolder(project);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Order(StringComparer.Ordinal)
                .TakeLast(Math.Max(0, take))
                .Select(Read<GateQueueStallEvent>)
                .ToArray()
            : [];
    }
}
