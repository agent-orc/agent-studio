using System.Collections.Concurrent;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3219: the batch gate's view of the machine gate queue. Gate requests
/// report while they wait for a slot; a queue overrun opens a stall episode
/// that raises one <see cref="GateQueueStallEvent"/>, raises it again every
/// <see cref="GateQueueStallPolicy.StallReminderInterval"/> while the episode
/// stays open, and closes once the queue has drained. Waiters are process
/// memory; episodes and events are durable in the <see cref="BatchGateStore"/>.
/// </summary>
public sealed class GateQueueStallMonitor(BatchGateStore store, TimeProvider time)
{
    private static readonly TimeSpan DefaultGateDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DurationCacheLifetime = TimeSpan.FromMinutes(5);
    private const int DurationSamples = 20;

    private sealed record Waiter(string Project, DateTimeOffset SinceUtc, int Busy, int Total);
    private sealed record CachedDuration(TimeSpan Value, DateTimeOffset ReadAtUtc);

    private readonly ConcurrentDictionary<string, Waiter> _waiting = new(StringComparer.Ordinal);
    private readonly object _episodeLock = new();
    private readonly ConcurrentDictionary<string, CachedDuration> _durations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Busy, int Total)> _lastSlots = new(StringComparer.Ordinal);

    /// <summary>A request waits for a slot; the first report fixes its wait start.</summary>
    public void Waiting(string project, string requestKey, int busy, int total)
    {
        _lastSlots[project] = (busy, total);
        _waiting.AddOrUpdate(requestKey,
            _ => new Waiter(project, time.GetUtcNow(), busy, total),
            (_, prior) => prior with { Busy = busy, Total = total });
    }

    /// <summary>The request holds a slot or ended; it no longer waits.</summary>
    public void Left(string requestKey) => _waiting.TryRemove(requestKey, out _);

    public int WaitingCount(string project)
        => _waiting.Values.Count(waiter => string.Equals(waiter.Project, project, StringComparison.Ordinal));

    /// <summary>The latest event of the project's open episode, or null.</summary>
    public GateQueueStallEvent? OpenEpisode(string project) => store.ReadQueueStallEpisode(project)?.Latest;

    /// <summary>
    /// Facts for <see cref="GateQueueStallPolicy"/>. <paramref name="queuedMembers"/>
    /// counts batch members that wait for their batch; requests that wait for
    /// a slot are added here.
    /// </summary>
    public GateQueueFacts Facts(string project, int queuedMembers, TimeSpan queueBudget,
        int configuredSlots, TimeSpan? oldestWaitAtLeast = null)
    {
        var now = time.GetUtcNow();
        var waiters = _waiting.Values
            .Where(waiter => string.Equals(waiter.Project, project, StringComparison.Ordinal)).ToArray();
        var (busy, total) = _lastSlots.TryGetValue(project, out var slots) ? slots : (0, 0);
        var oldest = waiters.Length == 0 ? TimeSpan.Zero : now - waiters.Min(waiter => waiter.SinceUtc);
        if (oldestWaitAtLeast is { } floor && floor > oldest) oldest = floor;
        var inUse = Math.Max(1, total > 0 ? total : configuredSlots);
        return new GateQueueFacts(waiters.Length + Math.Max(0, queuedMembers), busy, inUse,
            Math.Max(inUse, configuredSlots), oldest, TypicalGateDuration(project), queueBudget);
    }

    /// <summary>
    /// Records a queue overrun. Returns the event when this overrun opened a
    /// new episode or the open episode's reminder is due, otherwise null.
    /// </summary>
    public GateQueueStallEvent? QueueOverrun(string project, string taskKey, GateQueueFacts facts)
    {
        lock (_episodeLock)
        {
            var open = store.ReadQueueStallEpisode(project);
            if (!AgentStudio.Runner.MonolithTransitionAdapter.GateQueueStallRaisesEvent(open is not null))
                return RemindLocked(open!, taskKey, facts);
            var opened = Event(Guid.NewGuid().ToString("N"), project, taskKey, facts, 1, time.GetUtcNow());
            store.WriteQueueStallEpisode(new GateQueueStallEpisode(project, opened.EpisodeId,
                opened.EpisodeOpenedAtUtc!.Value, opened));
            store.RecordQueueStall(opened);
            return opened;
        }
    }

    /// <summary>
    /// Raises the open episode's event again when its reminder is due, even
    /// when no further request overran; null when none is open or due.
    /// </summary>
    public GateQueueStallEvent? Remind(string project, GateQueueFacts facts)
    {
        lock (_episodeLock)
        {
            return store.ReadQueueStallEpisode(project) is { } open
                ? RemindLocked(open, open.Latest.TriggerTaskKey, facts)
                : null;
        }
    }

    private GateQueueStallEvent? RemindLocked(GateQueueStallEpisode open, string taskKey, GateQueueFacts facts)
    {
        if (!AgentStudio.Runner.MonolithTransitionAdapter.GateQueueStallReminds(open.Latest.RaisedAtUtc,
                time.GetUtcNow()))
            return null;
        var reminder = Event(open.EpisodeId, open.Project, taskKey, facts, open.Latest.Raise + 1, open.OpenedAtUtc);
        store.WriteQueueStallEpisode(open with { Latest = reminder });
        store.RecordQueueStall(reminder);
        return reminder;
    }

    private GateQueueStallEvent Event(string episodeId, string project, string taskKey, GateQueueFacts facts,
        int raise, DateTimeOffset? openedAtUtc)
    {
        var assessment = AgentStudio.Runner.MonolithTransitionAdapter.GateQueue(facts);
        return new GateQueueStallEvent(episodeId, project, time.GetUtcNow(),
            facts.QueueDepth, (long)facts.OldestWait.TotalMilliseconds, facts.SlotsBusy, facts.SlotsTotal,
            (long)assessment.EstimatedDrain.TotalMilliseconds, (long)facts.QueueBudget.TotalMilliseconds,
            assessment.IdleConfiguredSlots, assessment.Advisory, taskKey, raise, openedAtUtc);
    }

    /// <summary>Closes the project's episode once its queue has drained.</summary>
    public bool Observe(string project, int queuedMembers)
    {
        var depth = WaitingCount(project) + Math.Max(0, queuedMembers);
        if (!AgentStudio.Runner.MonolithTransitionAdapter.GateQueueEpisodeDrained(depth)) return false;
        lock (_episodeLock)
        {
            if (store.ReadQueueStallEpisode(project) is null) return false;
            store.CloseQueueStallEpisode(project);
            return true;
        }
    }

    /// <summary>
    /// Median execution time of the project's recent gate runs that reached a
    /// slot, without their queue wait; 30 minutes before any run was recorded.
    /// </summary>
    public TimeSpan TypicalGateDuration(string project)
    {
        var now = time.GetUtcNow();
        if (_durations.TryGetValue(project, out var cached) && now - cached.ReadAtUtc < DurationCacheLifetime)
            return cached.Value;
        var samples = store.ListExecutions(project)
            .OrderByDescending(fact => fact.CompletedAtUtc)
            .Select(fact => fact.DurationMs is long ms && ms > 0
                ? ms
                : (long)(fact.CompletedAtUtc - fact.StartedAtUtc).TotalMilliseconds - fact.QueueWaitMs)
            .Where(ms => ms > 0)
            .Take(DurationSamples)
            .Order()
            .ToArray();
        var value = samples.Length == 0 ? DefaultGateDuration : TimeSpan.FromMilliseconds(samples[samples.Length / 2]);
        _durations[project] = new CachedDuration(value, now);
        return value;
    }
}
