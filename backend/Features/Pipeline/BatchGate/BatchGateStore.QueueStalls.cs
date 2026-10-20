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
    private readonly object _queueWaitLock = new();

    public void RecordQueueStall(GateQueueStallEvent stall)
    {
        var folder = Path.Combine(_root, "queue-stalls");
        Directory.CreateDirectory(folder);
        var name = stall.Raise <= 1 ? stall.EpisodeId : $"{stall.EpisodeId}-{stall.Raise}";
        var path = Path.Combine(folder, SafeName(name) + ".json");
        if (!File.Exists(path)) WriteOnce(path, stall);
    }

    public GateQueueStallEpisode? ReadQueueStallEpisode(string project)
    {
        var path = QueueStallEpisodePath(project);
        return File.Exists(path) ? Read<GateQueueStallEpisode>(path) : null;
    }

    public void WriteQueueStallEpisode(GateQueueStallEpisode episode)
    {
        var path = QueueStallEpisodePath(episode.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Replace(path, episode);
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
            Replace(path, ledger);
            return ledger;
        }
    }

    /// <summary>An operator release grants the review generation a fresh queue wait cap.</summary>
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

    private static void Replace<T>(string path, T value)
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

    public IReadOnlyList<GateQueueStallEvent> ListQueueStalls(string project)
    {
        var folder = Path.Combine(_root, "queue-stalls");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<GateQueueStallEvent>)
                .Where(stall => string.Equals(stall.Project, project, StringComparison.Ordinal))
                .OrderBy(stall => stall.RaisedAtUtc)
                .ThenBy(stall => stall.Raise)
                .ToArray()
            : [];
    }
}
