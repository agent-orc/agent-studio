using AgentStudio.Shared;
using AgentStudio.Pipeline;
using AgentStudio.Runner;

namespace AgentStudio.Projects;

public sealed record EnvironmentLaneFlow(string Lane, int Entered, int Left, double? MedianMinutes);
public sealed record EnvironmentQueueFlow(string Queue, int? Waiting, double? OldestWaitMinutes,
    double? ThroughputPerHour);
public sealed record EnvironmentWaitReason(string Reason, int Count);
public sealed record EnvironmentDailyFlow(int Created, int Delivered, int Reviewed, int Integrated,
    int? Deployed, int Completed, double? ReadyToIntegrationMedianMinutes);
public sealed record EnvironmentFlowSnapshot(DateTime CapturedAt, EnvironmentDailyFlow Today,
    IReadOnlyList<EnvironmentLaneFlow> Lanes, IReadOnlyList<EnvironmentQueueFlow> Queues,
    IReadOnlyList<EnvironmentWaitReason> WaitingReasons);

/// <summary>Fleet flow from the archive-inclusive task index and append-only lane ledger.</summary>
public sealed class EnvironmentFlowService(TaskScannerService scanner, TimelineLog timeline,
    TimeProvider clock, ILogger<EnvironmentFlowService> logger, BatchGateStore batchGates,
    AutoReviewStatusSnapshot autoReview)
{
    private readonly object _cacheGate = new();
    private EnvironmentFlowSnapshot? _cached;
    private DateTimeOffset _cachedAt;

    public EnvironmentFlowSnapshot Build()
    {
        var observedAt = clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (_cached is not null && observedAt >= _cachedAt
                && observedAt - _cachedAt < TimeSpan.FromSeconds(10))
                return _cached;
            var snapshot = BuildCore(observedAt.UtcDateTime);
            _cached = snapshot;
            _cachedAt = observedAt;
            return snapshot;
        }
    }

    private EnvironmentFlowSnapshot BuildCore(DateTime now)
    {
        var tasks = scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => !RetiredContainerTask.IsRetired(task)).ToList();
        var events = new Dictionary<string, IReadOnlyList<TimelineEvent>>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(task => task.LastActivity >= now.AddHours(-24)
                     || task.EnteredLaneAt >= now.AddHours(-24) || task.CreatedAt >= now.Date))
        {
            try { events[task.FolderPath] = timeline.ReadAll(task.FolderPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "environment-flow-ledger-read-failed task={TaskId}", task.Id);
            }
        }
        var batchReleased = batchGates.ListManifests().Sum(manifest =>
            batchGates.ReadPublication(manifest.BatchId) is { } publication
                && publication.VerifiedAtUtc.UtcDateTime >= now.AddHours(-24)
                && publication.VerifiedAtUtc.UtcDateTime <= now
                ? manifest.Members.Count : 0);
        return Aggregate(tasks, task => events.GetValueOrDefault(task.FolderPath) ?? [], now,
            batchGates.ListPending().Select(item => item.EnqueuedAtUtc).ToList(),
            autoReview.Read().ActiveJobs.Where(item => item.Step == AutoReviewActivitySteps.GateQueued)
                .Select(item => item.StartedAt).ToList(), batchReleased);
    }

    internal static EnvironmentFlowSnapshot Aggregate(IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, IReadOnlyList<TimelineEvent>> readTimeline, DateTime nowUtc,
        IReadOnlyList<DateTimeOffset>? batchPending = null, IReadOnlyList<DateTime>? gateQueued = null,
        int? batchReleasedLast24h = null)
    {
        tasks = tasks.Where(task => !RetiredContainerTask.IsRetired(task)).ToList();
        var now = nowUtc.ToUniversalTime();
        var since = now.AddHours(-24);
        var dayStart = now.Date;
        var lanes = TaskStates.All;
        var entered = lanes.ToDictionary(lane => lane, _ => 0);
        var left = lanes.ToDictionary(lane => lane, _ => 0);
        var dwell = lanes.ToDictionary(lane => lane, _ => new List<double>());
        var delivered = 0;
        var reviewed = 0;
        var integrated = 0;
        var completed = 0;
        var gateCompleted = 0;
        var readyToIntegration = new List<double>();

        foreach (var task in tasks)
        {
            var taskEvents = readTimeline(task);
            if (taskEvents.Any(e => e.Kind == TimelineEventKinds.PostStepFinished
                    && e.Ts >= since && e.Ts <= now
                    && e.Details?.GetValueOrDefault("pipelineStepId") == PipelineCatalogue.BuildTestGateStepId))
                gateCompleted++;
            var transitions = taskEvents
                .Where(e => e.Kind == TimelineEventKinds.LaneChanged && e.Details is not null)
                .OrderBy(e => e.Ts).ToList();
            if (task.CreatedAt >= since && task.CreatedAt <= now)
            {
                var initialLane = transitions.FirstOrDefault()?.Details?.GetValueOrDefault("from") ?? task.State;
                if (entered.ContainsKey(initialLane)) entered[initialLane]++;
            }
            var deliveredToday = false;
            var reviewedToday = false;
            var completedToday = false;
            foreach (var e in transitions)
            {
                var from = e.Details!.GetValueOrDefault("from") ?? "";
                var to = e.Details!.GetValueOrDefault("to") ?? "";
                if (e.Ts >= since && e.Ts <= now)
                {
                    if (entered.ContainsKey(to)) entered[to]++;
                    if (left.ContainsKey(from)) left[from]++;
                }
                if (e.Ts >= since && e.Ts <= now && from.Length > 0 && dwell.ContainsKey(from))
                {
                    var previous = transitions.LastOrDefault(p => p.Ts < e.Ts
                        && p.Details?.GetValueOrDefault("to") == from);
                    if (previous is not null) dwell[from].Add((e.Ts - previous.Ts).TotalMinutes);
                }
                if (e.Ts < dayStart || e.Ts > now) continue;
                if (to == TaskStates.AutoReview) deliveredToday = true;
                if (to == TaskStates.HumanReview) reviewedToday = true;
                if (to == TaskStates.Completed)
                    completedToday = true;
            }
            if (deliveredToday) delivered++;
            if (reviewedToday) reviewed++;
            if (completedToday) completed++;
            var integrationAt = taskEvents.Where(e => e.Kind == TimelineEventKinds.IntegrationSucceeded
                    && e.Ts >= dayStart && e.Ts <= now)
                .Select(e => e.Ts).DefaultIfEmpty().Min();
            if (integrationAt != default)
            {
                integrated++;
                var ready = transitions.LastOrDefault(e => e.Ts <= integrationAt
                    && e.Details?.GetValueOrDefault("to") == TaskStates.Ready)?.Ts;
                if (ready is { } start && start <= integrationAt)
                    readyToIntegration.Add((integrationAt - start).TotalMinutes);
            }
            // Old cards without a ledger have an honest current-lane anchor only.
            if (transitions.Count == 0 && task.State == TaskStates.Completed
                && task.EnteredLaneAt >= dayStart && task.EnteredLaneAt <= now)
            {
                completed++;
                if (task.CreatedAt < since || task.EnteredLaneAt != task.CreatedAt)
                    entered[TaskStates.Completed]++;
            }
        }

        var live = tasks.Where(task => task.State != TaskStates.Archive
            && task.State != TaskStates.Completed).ToList();
        var queues = new[]
        {
            Queue("Coding", TaskStates.Ready, TaskStates.Progress),
            Queue("Review", TaskStates.AutoReview, TaskStates.HumanReview),
            new EnvironmentQueueFlow("Gate", gateQueued?.Count,
                gateQueued is { Count: > 0 }
                    ? (now - gateQueued.Min().ToUniversalTime()).TotalMinutes : null,
                gateCompleted / 24d),
            new EnvironmentQueueFlow("Batch gate", batchPending?.Count,
                batchPending is { Count: > 0 }
                    ? (now - batchPending.Min().UtcDateTime).TotalMinutes : null,
                batchReleasedLast24h / 24d),
            Queue("Integration", TaskStates.HumanReview, TaskStates.Completed),
        };
        var reasons = live.Where(IsWaiting).GroupBy(WaitReason, StringComparer.Ordinal)
            .Select(g => new EnvironmentWaitReason(g.Key, g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Reason, StringComparer.Ordinal)
            .Take(6).ToList();
        return new EnvironmentFlowSnapshot(now,
            new EnvironmentDailyFlow(tasks.Count(t => t.CreatedAt >= dayStart && t.CreatedAt <= now), delivered, reviewed,
                integrated, null, completed, Median(readyToIntegration)),
            lanes.Select(lane => new EnvironmentLaneFlow(lane, entered[lane], left[lane],
                Median(dwell[lane]))).ToList(), queues, reasons);

        EnvironmentQueueFlow Queue(string name, string waitingLane, string exitLane)
        {
            var waiting = live.Where(t => t.State == waitingLane).ToList();
            var oldest = waiting.Select(t => t.EnteredLaneAt)
                .Where(at => at != default && at <= now).DefaultIfEmpty().Min();
            return new EnvironmentQueueFlow(name, waiting.Count,
                oldest == default ? null : (now - oldest).TotalMinutes,
                entered.GetValueOrDefault(exitLane) / 24d);
        }
    }

    private static string WaitReason(TaskInfo task)
    {
        if (task.CauseWait is not null) return "Cause hold";
        if (task.QuotaWait is not null) return "Provider quota";
        if (task.RemoteClaimFailure is not null) return "Remote claim failure";
        if (task.PickupHold is not null) return "Pickup hold";
        return task.State switch
        {
            TaskStates.Ready => "Waiting for coding slot",
            TaskStates.AutoReview => "Waiting for review",
            TaskStates.HumanReview => "Waiting for acceptance or integration",
            TaskStates.Escalated => "Escalated",
            _ => "In progress",
        };
    }

    private static bool IsWaiting(TaskInfo task) => task.CauseWait is not null
        || task.QuotaWait is not null || task.RemoteClaimFailure is not null
        || task.PickupHold is not null || task.State is TaskStates.Ready
            or TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated;

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var middle = values.Count / 2;
        return Math.Round(values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2 : values[middle], 1);
    }
}
