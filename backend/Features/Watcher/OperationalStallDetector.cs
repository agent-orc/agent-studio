namespace AgentStudio.Watcher;

/// <summary>Facts from the existing host, attempt, gate, lane and deployment projections.</summary>
public sealed record OperationalStallSnapshot(
    IReadOnlyList<HostStallFact> Hosts,
    IReadOnlyList<CapacityStallFact> Capacity,
    IReadOnlyList<ClaimStallFact> Claims,
    IReadOnlyList<ProviderStallFact> Providers,
    IReadOnlyList<GateStallFact> Gates,
    IReadOnlyList<LaneStallFact> Lanes,
    IReadOnlyList<DeployStallFact> Deployments,
    IReadOnlyList<HostHeartbeatFact>? Heartbeats = null);

public sealed record HostHeartbeatFact(string Host, string Role, DateTime LastSeenUtc,
    IReadOnlyList<string> WaitingCards, bool Stopped = false);

public sealed record HostStallFact(string Host, DateTime SinceUtc, double? LoadPerCore,
    double? CpuPercent, double? MemoryUsedPercent, double? SwapBytesPerSecond,
    int BlockedCards = 0, string? Evidence = null, double? TargetPercent = null);
public sealed record CapacityStallFact(string Project, string Host, string Role, int FreeSlots,
    int WaitingCount, DateTime OldestWaitUtc, IReadOnlyList<string> Cards);
public sealed record ClaimStallFact(string Project, string Reason, DateTime SinceUtc,
    IReadOnlyList<string> Cards, string? LogExcerpt = null);
public sealed record ProviderStallFact(string Host, string Provider, string Failure,
    DateTime SinceUtc, IReadOnlyList<string> Cards, string? LogExcerpt = null);
public sealed record GateStallFact(string Project, string Plan, bool PlanIdle,
    int PendingCount, DateTime? OldestPendingUtc, IReadOnlyList<string> Cards,
    IReadOnlyList<string> AlreadyCompletedCards);
public sealed record LaneStallFact(string Project, string Lane, string Card,
    DateTime EnteredUtc, string? Evidence = null);
public sealed record DeployStallFact(string Host, string Service, string? ExpectedVersion,
    string? RunningVersion, DateTime SinceUtc, string? StartError = null);

public sealed record OperationalStallThresholds(
    double HostTargetPercent = 85, int HostOverloadMinutes = 10,
    double MemoryUsedPercent = 90, double SwapBytesPerSecond = 65536,
    int IdleWaitMinutes = 15, int GatePendingHours = 2,
    int LaneBudgetHours = 24, int HostHeartbeatMinutes = 10);

/// <summary>Pure policy. The collector supplies facts and the sweep supplies the clock.</summary>
public static class OperationalStallDetector
{
    public static IReadOnlyList<WatcherSignalObservation> Detect(
        OperationalStallSnapshot facts, OperationalStallThresholds thresholds, DateTime nowUtc)
    {
        var result = new List<WatcherSignalObservation>();
        foreach (var heartbeat in facts.Heartbeats ?? [])
        {
            if (!heartbeat.Stopped
                && nowUtc - heartbeat.LastSeenUtc < TimeSpan.FromMinutes(thresholds.HostHeartbeatMinutes)) continue;
            AddScoped("host-offline", $"host:{heartbeat.Host}:{heartbeat.Role}", "critical",
                heartbeat.LastSeenUtc, $"{heartbeat.Role} on {heartbeat.Host} "
                    + (heartbeat.Stopped ? "is stopped." : "stopped reporting."),
                heartbeat.WaitingCards.Count, "Check the host or container and restore its heartbeat.",
                $"lastSeenUtc={heartbeat.LastSeenUtc:O}", [("stopped", heartbeat.Stopped ? "true" : "false")],
                cards: heartbeat.WaitingCards);
        }
        foreach (var host in facts.Hosts)
        {
            var sustained = nowUtc - host.SinceUtc >= TimeSpan.FromMinutes(thresholds.HostOverloadMinutes);
            var target = host.TargetPercent ?? thresholds.HostTargetPercent;
            var overload = host.LoadPerCore is { } load && load * 100 > target
                || host.CpuPercent > target;
            var memory = host.MemoryUsedPercent > thresholds.MemoryUsedPercent
                || host.SwapBytesPerSecond > thresholds.SwapBytesPerSecond;
            if (sustained && overload)
                Add("host-overload", $"host:{host.Host}", "critical", host.SinceUtc,
                    $"Host {host.Host} exceeds its {target:0}% CPU target.",
                    host.BlockedCards, "Reduce admission or move work to a host with headroom.", host.Evidence,
                    ("loadPerCore", host.LoadPerCore), ("cpuPercent", host.CpuPercent), ("targetPercent", target));
            if (sustained && memory)
                Add("memory-pressure", $"host:{host.Host}", "critical", host.SinceUtc,
                    $"Host {host.Host} is under memory pressure.", host.BlockedCards,
                    "Reduce admission and inspect memory or swap usage.", host.Evidence,
                    ("memoryUsedPercent", host.MemoryUsedPercent), ("swapBytesPerSecond", host.SwapBytesPerSecond));
        }
        foreach (var capacity in facts.Capacity.Where(x => x.FreeSlots > 0 && x.WaitingCount > 0
            && nowUtc - x.OldestWaitUtc >= TimeSpan.FromMinutes(thresholds.IdleWaitMinutes)))
            AddScoped("idle-capacity", $"host:{capacity.Host}:{capacity.Role}", "warning", capacity.OldestWaitUtc,
                $"{capacity.Host} has {capacity.FreeSlots} idle {capacity.Role} slots while {capacity.WaitingCount} items wait.",
                capacity.WaitingCount, "Check claim eligibility and resume eligible work.", null,
                [("freeSlots", capacity.FreeSlots), ("waitingCount", capacity.WaitingCount)],
                project: capacity.Project, cards: capacity.Cards);
        foreach (var claim in facts.Claims.Where(x => x.Cards.Count > 0))
            AddScoped("claim-refusal", $"queue:{claim.Reason}", "warning", claim.SinceUtc,
                $"{claim.Cards.Count} attempts cannot be claimed: {claim.Reason}.", claim.Cards.Count,
                "Repair the reported host capability or credential, then retry the claim.", claim.LogExcerpt,
                project: claim.Project, cards: claim.Cards);
        foreach (var provider in facts.Providers.Where(x => x.Failure is "auth-invalid" or "rate-limited"))
            AddScoped($"provider-{provider.Failure}", $"host:{provider.Host}:{provider.Provider}", "critical",
                provider.SinceUtc, $"{provider.Provider} is {provider.Failure} on {provider.Host}.",
                provider.Cards.Count, provider.Failure == "auth-invalid"
                    ? "Renew the provider credential and verify a claim." : "Wait for the provider reset and retry affected attempts.",
                provider.LogExcerpt, cards: provider.Cards);
        foreach (var gate in facts.Gates.Where(x => x.PendingCount > 0))
        {
            var aged = gate.OldestPendingUtc is { } since
                && nowUtc - since >= TimeSpan.FromHours(thresholds.GatePendingHours);
            if (!gate.PlanIdle && !aged && gate.AlreadyCompletedCards.Count == 0) continue;
            var sinceUtc = gate.OldestPendingUtc ?? nowUtc;
            AddScoped("batch-gate-stuck", $"gate:{gate.Project}:{gate.Plan}", "critical", sinceUtc,
                $"Batch gate {gate.Plan} has {gate.PendingCount} pending members"
                + (gate.PlanIdle ? " while its plan is idle" : "")
                + (gate.AlreadyCompletedCards.Count > 0 ? $"; {gate.AlreadyCompletedCards.Count} are already completed" : "") + ".",
                gate.PendingCount, "Reconcile completed members and restart the eligible gate plan.", null,
                [("pendingCount", gate.PendingCount), ("completedCount", gate.AlreadyCompletedCards.Count)],
                project: gate.Project, cards: gate.Cards);
        }
        foreach (var lane in facts.Lanes.Where(x => nowUtc - x.EnteredUtc >= TimeSpan.FromHours(thresholds.LaneBudgetHours)))
            AddScoped("lane-stall", $"card:{lane.Card}:{lane.Lane}", "warning", lane.EnteredUtc,
                $"{lane.Card} has stayed in {lane.Lane} beyond its {thresholds.LaneBudgetHours}h budget.", 1,
                "Inspect the lane's pending action and resume or escalate it.", lane.Evidence,
                project: lane.Project, cards: [lane.Card]);
        foreach (var deploy in facts.Deployments.Where(x => x.StartError is not null
            || x.ExpectedVersion is not null && x.RunningVersion != x.ExpectedVersion))
            Add("deploy-drift", $"container:{deploy.Host}:{deploy.Service}", "critical", deploy.SinceUtc,
                $"{deploy.Service} on {deploy.Host} does not match the deployed release.", 0,
                "Inspect the deployment failure and reconcile the running image.", deploy.StartError,
                ("expectedVersion", deploy.ExpectedVersion), ("runningVersion", deploy.RunningVersion));
        return result;

        void Add(string kind, string subject, string severity, DateTime sinceUtc, string summary,
            int blocked, string action, string? excerpt, params (string Key, object? Value)[] numbers)
            => AddScoped(kind, subject, severity, sinceUtc, summary, blocked, action, excerpt,
                numbers, QuotaProbeSignalProbe.WorkspaceProject, []);

        void AddScoped(string kind, string subject, string severity, DateTime sinceUtc, string summary,
            int blocked, string action, string? excerpt, (string Key, object? Value)[]? numbers = null,
            string project = QuotaProbeSignalProbe.WorkspaceProject, IReadOnlyList<string>? cards = null)
        {
            var details = (numbers ?? []).Where(x => x.Value is not null)
                .ToDictionary(x => x.Key, x => Convert.ToString(x.Value, System.Globalization.CultureInfo.InvariantCulture)!, StringComparer.Ordinal);
            if (excerpt is not null) details["logExcerpt"] = excerpt.Length <= 500 ? excerpt : excerpt[..500];
            // AGT-3207: the AI Operator reads the stall kind from the case.
            details["stallKind"] = kind;
            result.Add(new WatcherSignalObservation
            {
                DetectorClass = kind is "host-offline" or "host-overload" or "memory-pressure" or "idle-capacity" or "lane-stall"
                    ? WatcherDetectorClasses.Silence : WatcherDetectorClasses.Hygiene,
                Project = project,
                FingerprintKey = $"operational:{kind}:{subject}",
                Summary = summary,
                ObservedAtUtc = nowUtc,
                AffectedCards = cards?.ToList() ?? [],
                Details = details,
                Subject = subject,
                Severity = severity,
                SinceUtc = sinceUtc,
                BlockedCount = blocked,
                SuggestedAction = action,
            });
        }
    }
}
