using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Clients;
using AgentStudio.Host;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

internal sealed class OperatorPressureReader(IConfiguration configuration, IHttpClientFactory clients,
    V1ReviewExecutorRegistry runners, ClientIdentityStore identities, HostTelemetryStore telemetry,
    AttemptAuthorityService reviews, PullGateStore pullGates, SshGateHostProbe sshGate, GateSlotUsageTracker gateSlots,
    ShardedRemoteGateTransport shardedGates,
    CliQuotaCapsService caps, QuotaHistoryStore history, ModelSelectionLedger ledger, ILogger<OperatorPressureReader> logger)
{
    internal async Task<CapacityPressureSnapshot> ReadAsync(DateTime now, bool scoped, QuotaReport report,
        ModelSelectionPolicy policy, IReadOnlyList<TaskInfo> tasks, IReadOnlyDictionary<string, ProjectSettings> settings,
        Func<TaskInfo, CapacityForecast> forecastOf, IReadOnlyList<ModelSelectionEvent> active, CancellationToken ct)
    {
        var ready = tasks.Where(task => task.State == TaskStates.Ready && !task.Fixture)
            .DistinctBy(CodingCapacityProjection.Key, StringComparer.OrdinalIgnoreCase)
            .Select(task => (Task: task, Forecast: forecastOf(task))).ToArray();
        var codingQueue = CodingQueue(ready, now);
        if (scoped)
            return new(now, "restricted", [], [], codingQueue,
                ready.Select(row => CodingAdmission(row.Task, row.Forecast, [], now, scoped: true)).ToArray());

        var remote = TaskServerPlaneProxy.IsConfigured(configuration);
        var hostRead = remote ? ReadRemoteAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", ct)
            : Task.FromResult<RunnerCapabilitySnapshotDto[]?>(runners.ListCapabilitySnapshots().ToArray());
        var reviewRead = remote ? ReadRemoteAsync<AutoReviewQueueResponse>("/api/v1/studio/runner/auto-review-queue", ct)
            : Task.FromResult<AutoReviewQueueResponse?>(null);
        var receipts = ledger.LatestQuotaAdmissions(now);
        var quota = OperatorPressureProjection.Quota(report, caps, policy, history.Read, receipts, active, now);
        var snapshots = await hostRead;
        var remoteReview = await reviewRead;
        var reviewQueue = OperatorPressureProjection.Queue("review", remote ? "task-server-review-queue" : "review-attempts",
            remote ? remoteReview?.Entries.Where(entry => entry.Status == "queued").Select(entry => entry.CreatedAt)
                : reviews.ListPendingReviewAttempts().Select(attempt => attempt.CreatedAt), now);
        var pullQueue = pullGates.QueueObservation();
        var gateQueue = OperatorPressureProjection.Queue("gate", "pull-gate-queue", pullQueue.Count, pullQueue.OldestAt, now);
        var sshQueue = OperatorPressureProjection.Queue("gate", "gate-slot-waiters",
            gateSlots.Board().Waiting.Select(waiter => waiter.Since.UtcDateTime), now);
        var claimedGateQueue = OperatorPressureProjection.Queue("gate", "claimed-gate-queue", null, now);

        var legacy = identities.ListPublished().Where(client => client.RunnerDaemonState is not null && client.Kind != ClientIdentityKind.Retired)
            .ToDictionary(client => client.Id, StringComparer.OrdinalIgnoreCase);
        var byExecutor = new List<CapacityHostPressure>();
        foreach (var host in snapshots ?? [])
        {
            legacy.Remove(host.RunnerId, out var identity);
            var keys = host.Capabilities.Select(cap => cap.Key).ToHashSet(StringComparer.Ordinal);
            var roles = new List<string>(3);
            if (keys.Contains(CapabilityProtocol.CodingExecutor) || identity is not null) roles.Add("coding");
            if (keys.Contains(CapabilityProtocol.ReviewExecutor)) roles.Add("review");
            if (keys.Contains(GateCapabilities.Executor)) roles.Add("gate");
            foreach (var role in roles)
            {
                var assigned = role == "coding" ? ready.Where(row => Assigned(settings[row.Task.ProjectName],
                    host.RunnerId, host.Name, keys)).ToArray() : [];
                var work = role switch
                {
                    "coding" => CodingQueue(assigned, now),
                    "review" => [reviewQueue],
                    _ => [claimedGateQueue],
                };
                var slotAt = role == "gate" && remote ? host.LastSeenAt : host.Telemetry?.ObservedAt;
                var effective = host.EffectiveMaxParallelism ?? (role == "coding" ? identity?.RunnerEffectiveMaxParallelism : null);
                if (host.RoleMaxParallelism is { } roleMax)
                    effective = effective is { } adopted ? Math.Min(adopted, roleMax) : role == "review" ? roleMax : null;
                var occupied = role == "gate" ? remote ? host.ActiveGateCount : (int?)null : host.Telemetry?.ActiveSlots;
                var hold = role == "coding" ? DiskRefusal(assigned.Select(row => row.Task), host.RunnerId, now) : null;
                if (hold is null && !string.IsNullOrWhiteSpace(host.HostAdmission.AdmissionState)
                    && host.HostAdmission.AdmissionState != "open"
                    && OperatorHostDiskFactSource.IsFresh(host.LastSeenAt, now))
                    hold = new(host.HostAdmission.AdmissionState,
                        host.HostAdmission.OperatorDrainReason ?? host.HostAdmission.AutomaticDrainReason ?? host.HostAdmission.AdmissionState,
                        host.HostAdmission.OperatorDrainAt ?? host.HostAdmission.AutomaticDrainAt ?? host.LastSeenAt, "host-admission");
                byExecutor.Add(Host(host.HostId, host.RunnerId, role, remote ? "task-server-heartbeat" : "runner-heartbeat",
                    host.LastSeenAt, host.Telemetry, slotAt, effective, occupied, work, hold));
            }
        }
        if (!remote)
            foreach (var identity in legacy.Values)
            {
                var assigned = ready.Where(row => Assigned(settings[row.Task.ProjectName], identity.Id, identity.DisplayName, [])).ToArray();
                byExecutor.Add(Host(identity.Id, identity.Id, "coding", "legacy-runner-heartbeat", identity.LastSeenAt,
                    Latest(identity.Id), identity.LastSeenAt, identity.RunnerEffectiveMaxParallelism, identity.RunnerActiveSlots,
                    CodingQueue(assigned, now), DiskRefusal(assigned.Select(row => row.Task), identity.Id, now)));
            }

        var localQueue = CodingQueue(ready.Where(row => ProjectExecutionPolicy.IsLocalExecution(settings[row.Task.ProjectName])), now);
        byExecutor.Add(Host("api-host", "api-host", "coding", "api-host-disk-sample", null, Latest("api-host"),
            null, null, null, localQueue));
        foreach (var pull in pullGates.Hosts())
            byExecutor.Add(Host(pull.Id, pull.Id, "gate", "pull-gate-heartbeat", pull.LastHeartbeat.UtcDateTime, Latest(pull.Id),
                pull.LastHeartbeat.UtcDateTime, pull.Slots, pull.Busy, [gateQueue]));
        if (sshGate.Host is { } ssh)
        {
            var id = "gate-ssh:" + ssh;
            var disk = sshGate.LatestDisk;
            var reading = disk?.Id == id ? DiskSample(disk.ObservedAt, disk.Filesystems) : Latest(id);
            byExecutor.Add(SshHost(ssh, gateSlots, reading, sshQueue));
        }
        foreach (var shard in shardedGates.AdditionalGateHosts.DistinctBy(host => host.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (OperatorPressureProjection.Same(shard.Name, sshGate.Host)) continue;
            byExecutor.Add(SshHost(shard.Name, shard.Slots, Latest("gate-ssh:" + shard.Name),
                OperatorPressureProjection.Queue("gate", "gate-slot-waiters",
                    shard.Slots.Board().Waiting.Select(waiter => waiter.Since.UtcDateTime), now)));
        }

        CapacityReadyWork[] gateQueues = remote ? [gateQueue, sshQueue, claimedGateQueue] : [gateQueue, sshQueue];
        return new(now, snapshots is null ? "partial" : "observed", byExecutor.ToArray(), quota,
            [.. codingQueue, reviewQueue, .. gateQueues],
            ready.Select(row => CodingAdmission(row.Task, row.Forecast, quota, now, scoped: false)).ToArray());

        CapacityHostPressure Host(string hostId, string executorId, string role, string source, DateTime? heartbeat,
            HostTelemetrySnapshotDto? reading, DateTime? slotAt, int? effective, int? occupied,
            IReadOnlyList<CapacityReadyWork> work, CapacityClaimHold? hold = null)
            => OperatorPressureProjection.Host(hostId, executorId, role, source, heartbeat, reading, slotAt, effective,
                occupied, work, now, filesystems => telemetry.ReadDiskPressure(executorId, filesystems), hold);

        CapacityHostPressure SshHost(string host, GateSlotUsageTracker tracker, HostTelemetrySnapshotDto? reading,
            CapacityReadyWork queue)
        {
            var slots = tracker.RemoteObservation();
            return Host(host, "gate-ssh:" + host, "gate", "ssh-gate", tracker.LastReachedAt?.UtcDateTime,
                reading, slots?.At.UtcDateTime, slots?.Usage.Total, slots?.Usage.Busy, [queue]);
        }
    }

    private async Task<T?> ReadRemoteAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await clients.CreateClient(TaskServerPlaneProxy.ClientName).GetFromJsonAsync<T>(path, deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
                                      || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(error, "Operator pressure read unavailable for {Path}", path);
            return null;
        }
    }

    private HostTelemetrySnapshotDto? Latest(string id)
        => telemetry.Latest(id) is { } sample ? new(sample.Timestamp, sample.CpuPercent, sample.Load1, sample.Load5,
            sample.Load15, sample.MemoryUsedBytes, sample.MemoryTotalBytes, sample.SwapInBytesPerSecond,
            sample.SwapOutBytesPerSecond, sample.CpuStealPercent, sample.IoWaitPercent, sample.CpuCores,
            sample.ActiveSlots, Filesystems: sample.Filesystems, SwapUsedBytes: sample.SwapUsedBytes,
            SwapTotalBytes: sample.SwapTotalBytes) : null;

    private static HostTelemetrySnapshotDto DiskSample(DateTime at, IReadOnlyList<HostFilesystemUsageDto> filesystems)
        => new(at, null, null, null, null, null, null, null, null, null, null, 0, 0, Filesystems: filesystems);

    private static bool Assigned(ProjectSettings settings, string runnerId, string name, IReadOnlyCollection<string> keys)
        => ProjectExecutionPolicy.IsAssignedRemote(settings, runnerId, name)
           && (ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(settings)) is not { } required
               || keys.Contains(required));

    private static CapacityClaimHold? DiskRefusal(IEnumerable<TaskInfo> assigned, string runnerId, DateTime now)
    {
        var refusal = assigned.Where(task => task.RemoteDispatchRejection is { Code: "host-disk-pressure" } row
                && OperatorPressureProjection.Same(row.RunnerId, runnerId) && row.RejectedAtUtc >= task.EnteredLaneAt
                && OperatorHostDiskFactSource.IsFresh(row.RejectedAtUtc, now))
            .Select(task => task.RemoteDispatchRejection!).MaxBy(row => row.RejectedAtUtc);
        return refusal is null ? null : new(refusal.Code, refusal.Reason, refusal.RejectedAtUtc, "last-claim-refusal");
    }

    private static CapacityReadyWork[] CodingQueue(IEnumerable<(TaskInfo Task, CapacityForecast Forecast)> ready, DateTime now)
    {
        var byClass = ready.ToLookup(row => CodingClass(row.Task, row.Forecast), row => row.Task.EnteredLaneAt);
        return new[] { "first-launch", "recovery", "unclassified" }.Select(workClass =>
            OperatorPressureProjection.Queue(workClass, "ready-lane", byClass[workClass], now)).ToArray();
    }

    private static string CodingClass(TaskInfo task, CapacityForecast forecast)
    {
        if (forecast.HasContinuation || task.PendingIntent is not null
            || !string.IsNullOrWhiteSpace(task.SessionName)
            || task.RunActivity?.Attempt > 0
            || task.Provenance?.Transitions.Any(entry => entry.Lane == TaskStates.Progress) == true)
            return "recovery";
        return task.Provenance?.Transitions.Count > 0 || task.CreatedAt != default && task.CreatedAt == task.EnteredLaneAt
            ? "first-launch" : "unclassified";
    }

    internal static CapacityCodingAdmission CodingAdmission(TaskInfo task, CapacityForecast forecast,
        IReadOnlyList<CapacityQuotaPressure> quota, DateTime now, bool scoped)
    {
        var plan = forecast.Plan;
        var projected = plan.Projection;
        var retry = plan.NextResetAt is { } next && next > now && quota.Any(window =>
            OperatorPressureProjection.Same(window.Provider, forecast.Configured.CliType)
            && window.SubscriptionKey == forecast.QuotaSubscriptionKey && window.UsedPercent is not null
            && (window.ResetAt == next || (projected?.FreeAt == next && window.PaceReleaseAt == next
                && OperatorPressureProjection.Same(window.Window, projected.WindowLabel)))) ? next : (DateTime?)null;
        var outcome = plan.Outcome switch
        {
            QuotaAdmissionOutcome.LaunchPrimary => "primary",
            QuotaAdmissionOutcome.LaunchFallback => "fallback",
            QuotaAdmissionOutcome.Wait => "wait",
            QuotaAdmissionOutcome.Throttle => "throttle",
            _ => "refused",
        };
        return new(task.ProjectName, CodingCapacityProjection.Key(task), CodingClass(task, forecast),
            forecast.PinMode == SelectionPinMode.Hard, forecast.Configured.CliType, outcome,
            scoped ? null : plan.Reason, scoped ? null : retry,
            plan.Outcome == QuotaAdmissionOutcome.LaunchFallback ? new(plan.CliType, plan.Model, plan.ThinkingLevel) : null);
    }
}
