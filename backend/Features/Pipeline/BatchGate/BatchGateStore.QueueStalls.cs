namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3219: one machine gate queue episode in which a gate request spent its
/// wait budget before it reached a slot. Raised once per episode, not per card.
/// </summary>
public sealed record GateQueueStallEvent(
    string EpisodeId, string Project, DateTimeOffset RaisedAtUtc,
    int QueueDepth, long OldestWaitMs, int SlotsBusy, int SlotsTotal,
    long EstimatedDrainMs, long QueueBudgetMs, int IdleConfiguredSlots,
    string? Advisory, string TriggerTaskKey);

public sealed partial class BatchGateStore
{
    public void RecordQueueStall(GateQueueStallEvent stall)
    {
        var folder = Path.Combine(_root, "queue-stalls");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(stall.EpisodeId) + ".json");
        if (!File.Exists(path)) WriteOnce(path, stall);
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
                .ToArray()
            : [];
    }
}
