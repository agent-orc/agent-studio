using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public sealed record StructureStamp(DateTime ObservedAt, string ObservedBy, DateTime FreshUntil);
public sealed record StructureQueueLengths(int? Integration, int? Review, int? PostMerge, int? Coding = null, int? Gate = null)
{
    public int? QueuedTasks => Coding + Review + Gate;
}
public sealed record StructureApiFacts(StructureStamp Sample, double? RequestsPerSecond, int? RequestsInFlight,
    StructureQueueLengths Queues);
public sealed record StructureTrendPoint(StructureStamp Sample, int? BusySlots, int? QueuedTasks);
public sealed record StructureActivity(DateTime ObservedAt, string ObservedBy, string Kind, string Subject);
public sealed record StructureWeeklyWindow(StructureStamp Sample, string Provider, string Label, double? UsedPercent,
    DateTime? ResetAt);
public sealed record StructureHistory(IReadOnlyList<StructureTrendPoint> Trends, IReadOnlyList<StructureActivity> Events,
    int StepSeconds = 5, int WindowSeconds = 900);

/// <summary>Shared by workspace readers. Historical events keep the time of their source receipt.</summary>
public sealed class StructureHistoryBuffer
{
    private readonly object _gate = new();
    private readonly Queue<StructureTrendPoint> _points = new();
    private readonly List<StructureActivity> _events = [];

    public void Add(StructureActivity activity)
    {
        lock (_gate)
        {
            if (_events.Contains(activity)) return;
            _events.Add(activity);
            _events.Sort((a, b) => b.ObservedAt.CompareTo(a.ObservedAt));
            if (_events.Count > 20) _events.RemoveAt(20);
        }
    }

    internal void Sample(DateTime at, int? busy, int? queued)
    {
        lock (_gate)
        {
            while (_points.TryPeek(out var point) && point.Sample.ObservedAt <= at.AddMinutes(-15)) _points.Dequeue();
            _points.Enqueue(new(new(at, "structure sampler", at.AddSeconds(5)), busy, queued));
            while (_points.Count > 180) _points.Dequeue();
        }
    }

    public StructureHistory Snapshot(DateTime at)
    {
        lock (_gate) return new(_points.Where(p => p.Sample.ObservedAt > at.AddMinutes(-15)).ToArray(), _events.ToArray());
    }
}

public sealed record StructureInstallationFacts(StructureApiFacts Api, bool? PublicCdn, string? GateSshHost,
    bool SourceHosting, IReadOnlyList<StructureWeeklyWindow> Windows, StructureHistory History,
    string? GateNetwork = null, IReadOnlyList<string>? Providers = null, AgentStudio.Pipeline.GateHostDiskReading? GateDisk = null);
