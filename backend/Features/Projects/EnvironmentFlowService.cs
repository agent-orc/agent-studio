using AgentStudio.Shared;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;

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
    AutoReviewStatusSnapshot autoReview, PickupHoldSweep pickupHolds,
    AgentStudio.Deploy.InstanceDeployService? instanceDeploy = null,
    DeliveryIntegrationQueueBoard? integrationQueue = null, ProjectSettingsService? projectSettings = null)
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
        var holds = pickupHolds.Sweep().ToDictionary(
            card => TaskIdentity.CreateKey(card.WatchPath, card.JobId), card => card.Hold,
            StringComparer.Ordinal);
        tasks = tasks.Select(task => holds.TryGetValue(task.TaskKey, out var hold)
            ? task with { PickupHold = hold } : task).ToList();
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
        var reviewActivities = autoReview.Read().ActiveJobs;
        var localProjects = projectSettings is null ? null : tasks.Select(task => task.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(project => ProjectExecutionPolicy.ResolveExecutionLocation(projectSettings.Get(project))
                == ExecutionLocations.Local).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Aggregate(tasks, task => events.GetValueOrDefault(task.FolderPath) ?? [], now,
            batchGates.ListPending().Select(item => item.EnqueuedAtUtc).ToList(),
            reviewActivities.Where(item => item.Step == AutoReviewActivitySteps.GateQueued)
                .Select(item => item.StartedAt).ToList(), batchReleased,
            instanceDeploy?.CardsWentLive(now.Date, now), integrationQueue?.Read(), reviewActivities, localProjects);
    }

    internal static EnvironmentFlowSnapshot Aggregate(IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, IReadOnlyList<TimelineEvent>> readTimeline, DateTime nowUtc,
        IReadOnlyList<DateTimeOffset>? batchPending = null, IReadOnlyList<DateTime>? gateQueued = null,
        int? batchReleasedLast24h = null, int? deployedToday = null,
        IReadOnlyDictionary<string, IReadOnlyList<DeliveryIntegrationQueueRow>>? integrationQueues = null,
        IReadOnlyList<AutoReviewActivityView>? reviewActivities = null,
        IReadOnlySet<string>? localIntegrationProjects = null)
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
        var integrationsLast24h = 0;
        var localIntegrationsLast24h = 0;
        var acceptancesLast24h = 0;
        var readyToIntegration = new List<double>();

        foreach (var task in tasks)
        {
            var taskEvents = readTimeline(task);
            foreach (var success in taskEvents.Where(e => e.Kind == TimelineEventKinds.IntegrationSucceeded
                         && e.Ts >= since && e.Ts <= now))
            {
                // Placement controls current admission only. Historical output belongs to the
                // path recorded by the event, even after a project changes execution location.
                if (success.Details?.GetValueOrDefault("stage") == RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage)
                    integrationsLast24h++;
                else
                    localIntegrationsLast24h++;
            }
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
                    if (from == TaskStates.HumanReview && to == TaskStates.Completed) acceptancesLast24h++;
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
        var integrationRows = integrationQueues?.Values.SelectMany(rows => rows).ToList();
        var oldestIntegration = integrationRows?.Where(row => row.SinceUtc is { } at && at.UtcDateTime <= now)
            .Select(row => row.SinceUtc!.Value.UtcDateTime).DefaultIfEmpty().Min();
        var queues = new List<EnvironmentQueueFlow>
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
            // An empty published queue is authoritative. Never replace it with Human Review work.
            integrationRows is not null
                ? new EnvironmentQueueFlow("Integration", integrationRows.Count,
                    oldestIntegration is null or { Ticks: 0 } ? null : (now - oldestIntegration.Value).TotalMinutes,
                    integrationsLast24h / 24d)
                : LocalIntegrationFallback(live),
            Queue("Acceptance", TaskStates.HumanReview, TaskStates.Completed)
                with { ThroughputPerHour = acceptancesLast24h / 24d },
        };
        if (integrationRows is not null && (localIntegrationProjects is { Count: > 0 } || localIntegrationsLast24h > 0))
            queues.Add(LocalIntegrationFallback(live.Where(task => localIntegrationProjects?.Contains(task.ProjectName) == true)));
        var reasons = live.Where(IsWaiting).GroupBy(task => WaitReason(task, integrationQueues, reviewActivities), StringComparer.Ordinal)
            .Select(g => new EnvironmentWaitReason(g.Key, g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Reason, StringComparer.Ordinal)
            .Take(6).ToList();
        return new EnvironmentFlowSnapshot(now,
            new EnvironmentDailyFlow(tasks.Count(t => t.CreatedAt >= dayStart && t.CreatedAt <= now), delivered, reviewed,
                integrated, deployedToday, completed, Median(readyToIntegration)),
            lanes.Select(lane => new EnvironmentLaneFlow(lane, entered[lane], left[lane],
                Median(dwell[lane]))).ToList(), queues, reasons);

        EnvironmentQueueFlow LocalIntegrationFallback(IEnumerable<TaskInfo> candidates)
        {
            // Local delivery has no published integration queue. Keep its legacy lane proxy explicit;
            // acceptance is never evidence that integration succeeded. Events without the remote
            // pre-review stage retain the legacy fallback attribution, independent of placement.
            var waiting = candidates.Where(task => task.State == TaskStates.HumanReview).ToList();
            var oldest = waiting.Select(task => task.EnteredLaneAt)
                .Where(at => at != default && at <= now).DefaultIfEmpty().Min();
            return new EnvironmentQueueFlow("Integration (local lane fallback)", waiting.Count,
                oldest == default ? null : (now - oldest).TotalMinutes,
                (integrationRows is null ? integrationsLast24h + localIntegrationsLast24h : localIntegrationsLast24h) / 24d);
        }

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

    private static string WaitReason(TaskInfo task,
        IReadOnlyDictionary<string, IReadOnlyList<DeliveryIntegrationQueueRow>>? integrationQueues,
        IReadOnlyList<AutoReviewActivityView>? reviewActivities)
    {
        if (task.PickupHold is { } hold) return HoldReason(hold.Mechanism);
        if (task.CauseWait is not null) return "Cause hold";
        if (task.QuotaWait is not null) return "Provider quota";
        if (task.RemoteClaimFailure is not null) return "Remote claim failure";
        if (task.State == TaskStates.AutoReview)
        {
            var row = integrationQueues?.GetValueOrDefault(task.ProjectName)?
                .FirstOrDefault(row => string.Equals(row.JobId, task.Id, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
                return row.Activity == DeliveryIntegrationQueueBoard.Waiting
                    ? "Waiting for integration" : $"Integration: {row.Activity}";
            var activity = reviewActivities?.FirstOrDefault(item =>
                string.Equals(item.Project, task.ProjectName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.JobId, task.Id, StringComparison.OrdinalIgnoreCase));
            if (activity is not null)
                return activity.Step.Split(':')[0] switch
                {
                    AutoReviewActivitySteps.GateQueued => "Waiting for build/test gate",
                    AutoReviewActivitySteps.Gate => "Build/test gate",
                    AutoReviewActivitySteps.Aspects => "Review aspects",
                    AutoReviewActivitySteps.Grade => "Review grading",
                    AutoReviewActivitySteps.Decision => "Review decision",
                    _ => "Post-processing",
                };
        }
        return task.State switch
        {
            TaskStates.Ready => "Waiting for coding slot",
            TaskStates.AutoReview => "Waiting for review",
            TaskStates.HumanReview => "Waiting for acceptance",
            TaskStates.Escalated => "Escalated",
            _ => "In progress",
        };
    }

    private static string HoldReason(string mechanism) => mechanism switch
    {
        PickupHoldMechanisms.ExecutionPlacement => "Execution placement",
        PickupHoldMechanisms.DependencyGate => "Dependency gate",
        PickupHoldMechanisms.CrashBackoff => "Crash backoff",
        PickupHoldMechanisms.HostCapabilityMissing => "Host capability missing",
        PickupHoldMechanisms.DispatchRejection => "Dispatch rejection",
        PickupHoldMechanisms.PickupPolicy => "Pickup policy",
        _ => "Pickup hold",
    };

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
