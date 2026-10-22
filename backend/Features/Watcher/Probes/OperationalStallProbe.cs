using AgentStudio.Clients;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Watcher;

/// <summary>Read-only projection of existing host, attempt, gate, card and release facts.</summary>
public sealed class OperationalStallProbe : IWatcherSignalProbe
{
    private readonly ClientIdentityStore _clients;
    private readonly HostTelemetryStore _telemetry;
    private readonly AttemptAuthorityService _attempts;
    private readonly BatchGateStore _gates;
    private readonly TaskScannerService _tasks;
    private readonly HostReleaseDriftWatchdog _releaseDrift;
    private readonly V1ReviewExecutorRegistry _reviewHosts;
    private readonly ReviewClaimUnclaimableLog _unclaimable;
    private readonly GateSlotUsageTracker _gateSlots;
    private readonly IConfiguration _configuration;

    public string Name => "operational-stalls";

    public OperationalStallProbe(ClientIdentityStore clients, HostTelemetryStore telemetry,
        AttemptAuthorityService attempts, BatchGateStore gates, TaskScannerService tasks,
        HostReleaseDriftWatchdog releaseDrift, V1ReviewExecutorRegistry reviewHosts,
        ReviewClaimUnclaimableLog unclaimable, GateSlotUsageTracker gateSlots,
        IConfiguration configuration)
    {
        _clients = clients;
        _telemetry = telemetry;
        _attempts = attempts;
        _gates = gates;
        _tasks = tasks;
        _releaseDrift = releaseDrift;
        _reviewHosts = reviewHosts;
        _unclaimable = unclaimable;
        _gateSlots = gateSlots;
        _configuration = configuration;
    }

    public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc)
    {
        var thresholds = WatcherOptions.FromConfiguration(_configuration).OperationalThresholds;
        var clients = _clients.ListAll().Where(x => x.RunnerDaemonState is not null).ToArray();
        var allReviewHosts = _reviewHosts.ListCapabilitySnapshots();
        var reviewHosts = allReviewHosts
            .Where(x => nowUtc - x.LastSeenAt <= TimeSpan.FromMinutes(10)
                && x.HostAdmission.AdmissionState == "open").ToArray();
        var hostFacts = new List<HostStallFact>();
        var hostIds = clients.Select(x => (Id: x.Id, Name: x.DisplayName))
            .Concat(reviewHosts.Select(x => (Id: x.RunnerId, Name: x.HostId)))
            .DistinctBy(x => x.Id).ToArray();
        foreach (var client in hostIds)
        {
            var target = clients.FirstOrDefault(x => x.Id == client.Id)?.RunnerTargetLoadPercent
                ?? thresholds.HostTargetPercent;
            var points = _telemetry.Query(client.Id, "1h", nowUtc).Points
                .Where(x => x.Timestamp <= nowUtc).OrderBy(x => x.Timestamp).ToArray();
            if (points.Length == 0 || nowUtc - points[^1].Timestamp > TimeSpan.FromMinutes(10)) continue;
            var last = points[^1];
            DateTime? Since(Func<HostTelemetrySample, bool> predicate)
            {
                DateTime? oldest = null;
                DateTime? previous = null;
                foreach (var point in points.Reverse())
                {
                    if (!predicate(point) || previous - point.Timestamp > TimeSpan.FromMinutes(5)) break;
                    oldest = point.Timestamp;
                    previous = point.Timestamp;
                }
                return oldest;
            }
            var loadSince = Since(x => x.CpuPercent > target
                || x.CpuCores > 0 && x.Load1 / x.CpuCores * 100 > target);
            if (loadSince is { } overloadedAt)
                hostFacts.Add(new HostStallFact(client.Name, overloadedAt,
                    last.CpuCores > 0 ? last.Load1 / last.CpuCores : null, last.CpuPercent,
                    null, null, BlockedCards: last.ActiveSlots,
                    Evidence: $"load1={last.Load1:0.##}, cores={last.CpuCores}, activeSlots={last.ActiveSlots}",
                    TargetPercent: target));
            var memorySince = Since(x => MemoryPercent(x) > thresholds.MemoryUsedPercent
                || (x.SwapInBytesPerSecond ?? 0) + (x.SwapOutBytesPerSecond ?? 0) > thresholds.SwapBytesPerSecond);
            if (memorySince is { } pressuredAt)
                hostFacts.Add(new HostStallFact(client.Name, pressuredAt, null, null,
                    MemoryPercent(last), (last.SwapInBytesPerSecond ?? 0) + (last.SwapOutBytesPerSecond ?? 0),
                    Evidence: $"memoryUsed={last.MemoryUsedBytes}, memoryTotal={last.MemoryTotalBytes}"));
        }

        var jobs = _tasks.ScanAllJobs().Where(x => !x.Fixture).ToArray();
        var pendingReviews = _attempts.ListPendingReviewAttempts();
        var pendingGate = _gates.ListPending();
        var heartbeats = clients.Where(x => x.Kind != ClientIdentityKind.Retired)
            .Select(x => new HostHeartbeatFact(x.DisplayName, "coding", x.LastSeenAt ?? nowUtc, [],
                Stopped: x.RunnerDaemonState != "running"))
            .Concat(allReviewHosts.Select(x => new HostHeartbeatFact(x.HostId, "review", x.LastSeenAt,
                pendingReviews.Select(p => p.TaskKey).Distinct().ToArray())))
            .ToArray();
        bool CanClaim(Contract.RunnerCapabilitySnapshotDto host, ReviewAttemptDto attempt)
        {
            var capabilities = _reviewHosts.AdmittedReviewCapabilities(host.RunnerId, host.InstanceId);
            if (!capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor)) return false;
            var plan = attempt.Subject.Plan;
            return plan is null || Contract.ReviewLibraryStepPolicy.MissingCapabilities(plan, capabilities).Count == 0
                && Contract.ReviewRepositoryAccessPolicy.MissingCapabilities(
                    plan, attempt.Subject.RepositoryUrl, capabilities).Count == 0;
        }
        var capacity = reviewHosts.Where(x => x.RoleMaxParallelism > x.Telemetry?.ActiveSlots)
            .Select(x => (Host: x, Eligible: pendingReviews.Where(attempt => CanClaim(x, attempt)).ToArray()))
            .Select(x => new CapacityStallFact(QuotaProbeSignalProbe.WorkspaceProject, x.Host.HostId,
                "review", x.Host.RoleMaxParallelism!.Value - x.Host.Telemetry!.ActiveSlots,
                x.Eligible.Length, x.Eligible.Length == 0 ? nowUtc : x.Eligible.Min(y => y.CreatedAt),
                x.Eligible.Select(y => y.TaskKey).Distinct().ToArray())).ToList();
        var slots = _gateSlots.Snapshot();
        var gateHost = _configuration["RemoteGate:SshHost"]?.Trim();
        if (pendingGate.Count > 0 && !string.IsNullOrWhiteSpace(gateHost)
            && _gateSlots.Reachability == "reachable" && slots.Total > slots.Busy)
            capacity.Add(new CapacityStallFact(QuotaProbeSignalProbe.WorkspaceProject, gateHost,
                "gate", slots.Total - slots.Busy, pendingGate.Count,
                pendingGate.Min(y => y.EnqueuedAtUtc.UtcDateTime),
                pendingGate.Select(y => y.Subject.TaskKey).Distinct().ToArray()));

        var cardClaims = jobs.Where(x => (x.State is TaskStates.Ready or TaskStates.Progress or TaskStates.AutoReview)
                && x.RemoteClaimFailure is not null)
            .GroupBy(x => (x.ProjectName, x.RemoteClaimFailure!.Reason))
            .Select(group => new ClaimStallFact(group.Key.ProjectName, group.Key.Reason,
                group.Min(x => x.RemoteClaimFailure!.LastFailureAtUtc), group.Select(x => x.Id).ToArray(),
                group.First().RemoteClaimFailure!.Cause)).ToArray();
        var reviewClaims = pendingReviews.Select(attempt => (Attempt: attempt,
                Refusal: _unclaimable.Latest(attempt.AttemptId)))
            .Where(x => x.Refusal is not null && !reviewHosts.Any(host => CanClaim(host, x.Attempt)))
            .SelectMany(x => x.Refusal!.MissingCapabilities.Select(reason => (x.Attempt, x.Refusal, Reason: reason)))
            .GroupBy(x => (Project: jobs.FirstOrDefault(job => job.Id == x.Attempt.TaskKey)?.ProjectName
                ?? QuotaProbeSignalProbe.WorkspaceProject, x.Reason))
            .Select(group => new ClaimStallFact(group.Key.Project, group.Key.Reason,
                group.Min(x => x.Attempt.CreatedAt), group.Select(x => x.Attempt.TaskKey).Distinct().ToArray(),
                $"unclaimable-plan-requirements: {group.Key.Reason}"));
        var claims = cardClaims.Concat(reviewClaims).ToArray();
        var legacyModelHosts = allReviewHosts
            .Where(host => host.Status == "active"
                && nowUtc - host.LastSeenAt <= TimeSpan.FromMinutes(thresholds.HostHeartbeatMinutes)
                && host.Capabilities.Any(capability => capability.Key is
                    Contract.CapabilityProtocol.CodingExecutor or Contract.CapabilityProtocol.ReviewExecutor)
                && !host.Capabilities.Any(capability => capability.Key.StartsWith(
                    "model-acceptance:", StringComparison.Ordinal)))
            .Select(host => new LegacyModelHostFact(host.HostId,
                host.Capabilities.Any(capability => capability.Key == Contract.CapabilityProtocol.CodingExecutor)
                    ? "coding" : "review", host.RunnerId, host.RegisteredAt))
            .ToArray();
        var ready = jobs.Where(job => job.State == TaskStates.Ready).ToArray();
        var pendingCards = ready.Select(job => job.Id)
            .Concat(pendingReviews.Select(review => review.TaskKey))
            .Distinct(StringComparer.Ordinal).ToArray();
        var claimRefusals = ready.Where(job => job.RemoteDispatchRejection is not null)
            .Select(job => new FleetClaimRefusalFact(
                job.RemoteDispatchRejection!.Code,
                job.RemoteDispatchRejection.RejectedAtUtc, job.Id))
            .Concat(pendingReviews.Select(attempt => (Attempt: attempt,
                Refusal: _unclaimable.Latest(attempt.AttemptId)))
                .Where(item => item.Refusal is not null)
                .Select(item => new FleetClaimRefusalFact(
                    item.Refusal!.MissingCapabilities.FirstOrDefault() ?? "review-claim-refused",
                    item.Refusal.ObservedAt, item.Attempt.TaskKey)))
            .ToArray();
        var lastCodingClaim = clients.Where(client => client.Kind != ClientIdentityKind.Retired)
            .Select(client => client.RunnerLastClaimAt?.ToUniversalTime()).Max();
        var lastReviewClaim = _attempts.ReadReviewClaimActivity().LastClaimAt;
        var lastClaim = new[] { lastCodingClaim, lastReviewClaim }.OfType<DateTime>()
            .DefaultIfEmpty().Max();
        var fleetClaims = pendingCards.Length == 0 ? null : new FleetClaimStallFact(
            pendingCards,
            ready.Select(job => job.EnteredLaneAt)
                .Concat(pendingReviews.Select(review => review.CreatedAt))
                .DefaultIfEmpty(nowUtc).Min(),
            lastClaim == default ? null : lastClaim,
            claimRefusals,
            clients.Any(client => client.LastSeenAt is { } seen
                && nowUtc - seen <= TimeSpan.FromMinutes(thresholds.HostHeartbeatMinutes))
                || allReviewHosts.Any(host => nowUtc - host.LastSeenAt
                    <= TimeSpan.FromMinutes(thresholds.HostHeartbeatMinutes)));
        var cardProviders = jobs.Where(x => (x.State is TaskStates.Ready or TaskStates.Progress or TaskStates.AutoReview)
                && x.RemoteClaimFailure is { } failure
                && (failure.Reason.Contains("CredentialInvalid", StringComparison.OrdinalIgnoreCase)
                    || failure.Reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(x => (Host: x.RemoteClaimFailure!.Host ?? "unknown",
                Provider: x.CliType ?? "unknown",
                Failure: x.RemoteClaimFailure!.Reason.Contains("CredentialInvalid", StringComparison.OrdinalIgnoreCase)
                    ? "auth-invalid" : "rate-limited"))
            .Select(group => new ProviderStallFact(group.Key.Host, group.Key.Provider, group.Key.Failure,
                group.Min(x => x.RemoteClaimFailure!.LastFailureAtUtc),
                group.Select(x => x.Id).ToArray(), group.First().RemoteClaimFailure!.Reason)).ToArray();
        var capabilityProviders = allReviewHosts
            .Where(host => nowUtc - host.LastSeenAt <= TimeSpan.FromMinutes(thresholds.HostHeartbeatMinutes))
            .SelectMany(host => host.Capabilities
            .Where(capability => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)
                && capability.IsFresh
                && (capability.CredentialHealth == "invalid"
                    || capability.Signal is "signed-out" or "rate-limited"))
            .Select(capability => new ProviderStallFact(host.HostId,
                capability.Key["provider-auth:".Length..],
                capability.Signal == "rate-limited" ? "rate-limited" : "auth-invalid",
                capability.FirstFailureAt ?? capability.AdvertisedAt,
                pendingReviews.Select(x => x.TaskKey).Distinct().ToArray(),
                capability.EvidenceExcerpt ?? capability.Reason)));
        var providers = cardProviders.Concat(capabilityProviders)
            .GroupBy(x => (x.Host, x.Provider, x.Failure))
            .Select(group => group.OrderBy(x => x.SinceUtc).First() with
            {
                Cards = group.SelectMany(x => x.Cards).Distinct().ToArray(),
            }).ToArray();

        var activeProjects = _gates.ListManifests()
            .Where(x => HasState(x.BatchId)
                && _gates.LatestState(x.BatchId).Phase is BatchPhase.Formed or BatchPhase.Assembling
                or BatchPhase.Assembled or BatchPhase.Running or BatchPhase.Publishing)
            .Select(x => x.Scope.Project).ToHashSet(StringComparer.Ordinal);
        var gateFacts = pendingGate.GroupBy(x => x.Subject.Project)
            .Select(group => new GateStallFact(group.Key, "pending", PlanIdle: !activeProjects.Contains(group.Key),
                group.Count(), group.Min(x => x.EnqueuedAtUtc.UtcDateTime),
                group.Select(x => x.Subject.TaskKey).Distinct().ToArray(),
                group.Select(x => x.Subject.TaskKey).Where(key =>
                    jobs.Any(job => job.Id == key && job.State == TaskStates.Completed)).Distinct().ToArray()))
            .ToArray();
        var lanes = jobs.Where(x => x.EnteredLaneAt != default
                && x.State is TaskStates.Progress or TaskStates.AutoReview or TaskStates.HumanReview)
            .Select(x => new LaneStallFact(x.ProjectName, x.State, x.Id, x.EnteredLaneAt)).ToArray();
        var drift = _releaseDrift.Current;
        var deployments = drift.Hosts.Where(x => x.State == HostReleaseDriftStates.Behind && !x.HeartbeatStale)
            .Select(x => new DeployStallFact(x.HostId, x.Role, drift.Stable.Version,
                x.Release?.Version, x.LastSeenAt, x.Reason)).ToArray();

        return OperationalStallDetector.Detect(new OperationalStallSnapshot(hostFacts, capacity,
            claims, providers, gateFacts, lanes, deployments, heartbeats,
            legacyModelHosts, fleetClaims), thresholds, nowUtc);
    }

    private static double? MemoryPercent(HostTelemetrySample sample) => sample.MemoryTotalBytes is > 0
        ? 100.0 * sample.MemoryUsedBytes.GetValueOrDefault() / sample.MemoryTotalBytes.Value : null;

    private bool HasState(string batchId)
    {
        var path = Path.Combine(_gates.BatchDirectory(batchId), "state");
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.json").Any();
    }
}
