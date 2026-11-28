using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>What the API knows about the machine it runs on and about itself (AGT-3954).</summary>
/// <param name="MachineId">Stable id of the Studio server node; hosts and the placement file join on it.</param>
/// <param name="MachineKind">Configured kind, or null to derive it from <paramref name="Placement"/>.</param>
/// <param name="LastProxiedRequestAt">Last request that arrived through a forwarding proxy.</param>
public sealed record StudioSelfObservation(string MachineId, string MachineLabel, string? MachineKind,
    HostPlacementDto? Placement, StructurePerformanceFacts? Performance, string? Version, DateTime ObservedAt,
    DateTime? LastProxiedRequestAt = null);

/// <summary>Inputs of the observed projection beyond the declared joins.</summary>
/// <param name="RunningCards">Card keys that run right now, by runner id.</param>
/// <param name="IncludeObservedHosts">False for a project or environment excerpt, which stays declaration driven.</param>
public sealed record StructureObservedContext(StudioSelfObservation? Studio = null,
    IReadOnlyList<StructureDataLocation>? DataLocations = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RunningCards = null,
    bool IncludeObservedHosts = true, StructureInstallationFacts? Installation = null);

public static partial class ObservedStructureProjection
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);
    private const string ReportRef = "host report";
    private const string SelfRef = "studio self observation";

    /// <summary>Components of the Studio server and the container name tokens that identify them.</summary>
    public static readonly IReadOnlyList<(string Role, string Label, string[] Tokens)> StudioComponents =
    [
        ("api", "API", ["api", "backend", "orchestratorapi"]),
        ("web", "Web frontend", ["web", "frontend", "studio"]),
        ("edge", "Edge proxy", ["edge", "caddy", "proxy"]),
        ("engine", "Orchestrator engine", ["engine"]),
    ];

    public static string RoleOf(RunnerCapabilitySnapshotDto report)
        => report.Telemetry?.Structure?.Role is "coding" or "review" or "post" or "gate" ? report.Telemetry.Structure.Role
            : report.Capabilities.Any(capability => capability.Key is CapabilityProtocol.ReviewExecutor
            or ReviewCapabilities.ReviewExecutor) ? "review"
            : report.Capabilities.Any(capability => capability.Key == GateCapabilities.Executor) ? "gate" : "coding";

    private static string KindOf(HostPlacementDto? placement) => placement?.Kind switch
    {
        HostPlacementKinds.Container => "container",
        HostPlacementKinds.Vm => "vm",
        HostPlacementKinds.Physical => "physical",
        _ => "host",
    };

    /// <summary>
    /// Adds what reports and is not declared: every host, its runner roles, the
    /// Studio server with its components, the links the hosts hold, and the
    /// facts of all of them. Declared nodes keep their ids; an observed node
    /// takes its id from the report (<c>machine:{hostId}</c>), so both join.
    /// </summary>
    private static void AddObserved(List<StructureNode> nodes, List<StructureLink> links,
        IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> hostReports,
        IReadOnlyList<RunnerLinkResource> runnerLinks, IReadOnlyDictionary<string, PullGateHost> gates,
        HostReleaseDriftSnapshot? releases, string engineState, DateTime now, StructureObservedContext context)
    {
        var index = nodes.Select((node, position) => (node.Id, position))
            .ToDictionary(item => item.Id, item => item.position, StringComparer.Ordinal);
        bool Has(string id) => index.ContainsKey(id);
        void Put(StructureNode node)
        {
            if (index.TryGetValue(node.Id, out var position)) nodes[position] = node;
            else { index[node.Id] = nodes.Count; nodes.Add(node); }
        }
        StructureNode Get(string id) => nodes[index[id]];

        var studio = context.Studio;
        var studioId = studio is null ? null : $"machine:{studio.MachineId}";
        var placements = hostReports.ToDictionary(item => item.Key,
            item => item.Value.Select(report => report.Telemetry?.Placement).FirstOrDefault(value => value is not null),
            StringComparer.Ordinal);

        // The Studio server is a server whatever it runs on; the detected virtualization is a fact of it.
        if (studio is not null && !Has(studioId!))
            Put(new(studioId!, studio.MachineKind ?? "server",
                studio.MachineLabel, "installation", null, null, null, "observed", SelfRef,
                Observation: new("undeclared", "up", studio.ObservedAt, Detail: "The API reports from this machine")));

        // Machines a host names as its enclosing place, outermost first.
        foreach (var (hostId, placement) in placements.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string parent = "installation";
            foreach (var ancestor in placement?.Ancestors ?? [])
            {
                var id = $"machine:{ancestor.Id}";
                if (id == $"machine:{hostId}") break;
                if (!Has(id))
                    Put(new(id, ancestor.Kind == HostPlacementKinds.Vm ? "vm" : "physical",
                        ancestor.Kind == HostPlacementKinds.Vm ? "Virtual machine" : "Physical machine", parent,
                        null, null, null, "observed", ReportRef, Observation: new("undeclared", "not observed",
                            Detail: "Named by a reporting host")));
                parent = id;
            }
        }

        var gateOnly = gates.Keys.Where(id => !hostReports.ContainsKey(id));
        foreach (var hostId in hostReports.Keys.Concat(gateOnly).OrderBy(id => id, StringComparer.Ordinal))
        {
            var id = $"machine:{hostId}";
            var reports = hostReports.GetValueOrDefault(hostId) ?? [];
            var placement = placements.GetValueOrDefault(hostId);
            gates.TryGetValue(hostId, out var gate);
            if (!Has(id))
            {
                var sameKernel = studio?.Placement?.KernelId is { } kernel && placement?.KernelId == kernel;
                var parent = placement?.ParentMachineId is { Length: > 0 } configured && Has($"machine:{configured}")
                    && configured != hostId ? $"machine:{configured}"
                    : sameKernel && studioId != id ? studioId! : "installation";
                // A daemon on the Studio server's own kernel is an agent host on that machine,
                // not a second machine: the machine carries the kind.
                var kind = parent == studioId && placement?.Kind != HostPlacementKinds.Container ? "host" : KindOf(placement);
                // Use the enrolled display name. The host id remains the join key.
                var label = reports.FirstOrDefault()?.Telemetry?.Structure?.DisplayName ?? reports.FirstOrDefault()?.Name
                    ?? gate?.Name ?? "Execution host";
                Put(new(id, kind, label, parent, null, null, null, "observed", ReportRef,
                    Detail: placement?.ContainerName is { Length: > 0 } container && container != label ? container : null,
                    Observation: HostObservation(hostId, reports, gate, releases, now, declared: false)));
            }
            Put(Get(id) with { Facts = HostFacts(hostId, reports, gate, releases, now, context, performance: true) });

            foreach (var report in reports)
            {
                // A declared component with this runner id already carries the observation.
                var declared = nodes.FirstOrDefault(node => node.Id.StartsWith("component:", StringComparison.Ordinal)
                    && string.Equals(node.UnitId, report.RunnerId, StringComparison.OrdinalIgnoreCase));
                var component = declared?.Id ?? $"component:{hostId}:{report.RunnerId}";
                if (declared is null)
                    Put(new(component, "process", report.Name is { Length: > 0 } runnerName ? runnerName : report.RunnerId,
                        id, report.RunnerId, null, null, "observed", ReportRef, RoleOf(report),
                        Observation: HostObservation(hostId, [report], null, releases, now, declared: false)));
                Put(Get(component) with { Facts = HostFacts(hostId, [report], null, releases, now, context, performance: false) });
            }
        }

        if (studio is not null) AddStudio(nodes, Put, Get, Has, studio, studioId!, hostReports, placements, engineState, now, context);

        // Links the hosts hold themselves: the control connection each one reports, and supervised runner links.
        var api = studio is null ? null : nodes.FirstOrDefault(node => node.ParentId == studioId && node.Role == "api")?.Id;
        if (api is not null)
            foreach (var (hostId, reports) in hostReports.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var telemetry = reports.Select(report => report.Telemetry)
                    .Where(value => value is not null && value.TaskServerConnectionStatus != "unknown")
                    .OrderByDescending(value => value!.ObservedAt).FirstOrDefault();
                if (telemetry is null) continue;
                var from = $"machine:{hostId}";
                var id = StructureLink.IdFor(from, api, "control");
                if (links.Any(link => link.Id == id)) continue;
                var stale = now - telemetry.ObservedAt > Fresh;
                var state = stale ? "stale" : telemetry.TaskServerConnectionStatus is "reachable" or "connected" ? "up" : "down";
                links.Add(new(from, api, "control", "Task Server connection", ReportRef,
                    new(stale ? "stale" : "undeclared", state, telemetry.TaskServerConnectionObservedAt ?? telemetry.ObservedAt,
                        Detail: telemetry.TaskServerConnectionLastError ?? telemetry.TaskServerConnectionStatus),
                    id, new(state, telemetry.TaskServerRoundTripMs,
                        telemetry.TaskServerConnectionObservedAt ?? telemetry.ObservedAt, "https")));
            }
        if (studioId is not null)
            foreach (var runnerLink in runnerLinks)
            {
                var host = hostReports.FirstOrDefault(item => item.Value.Any(report =>
                    string.Equals(report.RunnerId, runnerLink.RunnerId, StringComparison.OrdinalIgnoreCase))).Key;
                if (host is null || !Has($"machine:{host}")) continue;
                var to = $"machine:{host}";
                // A declared link of this machine already shows the supervised link.
                if (links.Any(link => link.SourceRef is not (ReportRef or "RunnerLinks") && (link.From == to || link.To == to)
                        && link.Observation is { Difference: not "declared-only" })) continue;
                var id = StructureLink.IdFor(studioId, to, runnerLink.Kind);
                if (links.Any(link => link.Id == id)) continue;
                var stale = runnerLink.LastHeartbeatAt is { } at && now - at > Fresh;
                var state = stale ? "stale" : runnerLink.State == RunnerLinkStates.Up ? "up" : "down";
                links.Add(new(studioId, to, runnerLink.Kind, "Runner link", "RunnerLinks",
                    new(stale ? "stale" : "undeclared", state, runnerLink.LastHeartbeatAt, Detail: runnerLink.LastError ?? runnerLink.State),
                    id, new(state, null, runnerLink.LastProbe?.At, runnerLink.Transport ?? runnerLink.Kind, Sample: LinkStamp(runnerLink))));
            }
    }

    private static void AddStudio(List<StructureNode> nodes, Action<StructureNode> put, Func<string, StructureNode> get,
        Func<string, bool> has, StudioSelfObservation studio, string studioId,
        IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> hostReports,
        IReadOnlyDictionary<string, HostPlacementDto?> placements, string engineState, DateTime now,
        StructureObservedContext context)
    {
        var machine = get(studioId);
        // Performance: the API's own sample, else a daemon that reports from the same kernel.
        var performance = studio.Performance ?? hostReports
            .Where(item => placements.GetValueOrDefault(item.Key)?.KernelId is { } kernel
                && kernel == studio.Placement?.KernelId)
            .Select(item => Performance(item.Value)).FirstOrDefault(value => value is not null);
        put(machine with
        {
            // A declared Studio server that no host reports for is still observed: the API runs on it.
            Observation = machine.Observation is null or { Difference: "declared-only" }
                ? new("matched", "up", studio.ObservedAt, Detail: "The API reports from this machine") : machine.Observation,
            Facts = (machine.Facts ?? new StructureNodeFacts()) with
            {
                MachineKind = studio.Placement?.MachineVirtualization is not null ? HostPlacementKinds.Vm
                    : studio.Placement?.Kind == HostPlacementKinds.Container ? null : studio.Placement?.Kind,
                Virtualization = studio.Placement?.MachineVirtualization,
                LastSeenAt = machine.Facts?.LastSeenAt ?? studio.ObservedAt,
                Performance = machine.Facts?.Performance ?? performance,
                DataLocations = context.DataLocations,
                Sample = new(studio.ObservedAt, SelfRef, studio.ObservedAt + Fresh),
            },
        });
        var containerized = studio.Placement?.Kind == HostPlacementKinds.Container;
        foreach (var (role, label, _) in StudioComponents)
        {
            var declared = nodes.FirstOrDefault(node => node.ParentId == studioId
                && node.Id.StartsWith("component:", StringComparison.Ordinal)
                && (string.Equals(node.Role, role, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(node.UnitId, role, StringComparison.OrdinalIgnoreCase)));
            var observation = role switch
            {
                "api" => new StructureObservation(declared is null ? "undeclared" : "matched", "up", studio.ObservedAt,
                    studio.Version, studio.Version, Detail: "The API answers this request"),
                "engine" => engineState == OrchestrationEngineReadiness.NotRequired
                    ? new(declared is null ? "undeclared" : "declared-only", "not observed", Detail: "Engine contact is not required")
                    : new(declared is null ? "undeclared" : "matched",
                        engineState == OrchestrationEngineReadiness.Ready ? "up" : "down", studio.ObservedAt, Detail: engineState),
                "edge" when studio.LastProxiedRequestAt is { } proxied => new(
                    now - proxied > TimeSpan.FromMinutes(5) ? "stale" : declared is null ? "undeclared" : "matched",
                    now - proxied > TimeSpan.FromMinutes(5) ? "stale" : "up", proxied, Detail: "Last request forwarded by a proxy"),
                _ => new(declared is null ? "undeclared" : "declared-only", "not observed",
                    Detail: "Not observable from the API"),
            };
            if (declared is null)
                put(new($"component:{studio.MachineId}:{role}", containerized ? "container" : "process", label, studioId,
                    role, null, null, "observed", SelfRef, role, Observation: observation));
            // A declared component keeps a host-reported observation; self knowledge fills the gap.
            else if (declared.Observation is null or { Difference: "declared-only" })
                put(declared with { Observation = observation });
        }
    }

    /// <summary>
    /// Assigns reported containers of the Studio server's docker daemon to its
    /// components. The API is the container whose id is the API's host name; the
    /// others match by the last word of their name within the API's name prefix.
    /// </summary>
    internal static IReadOnlyDictionary<string, HostContainerSummaryDto> MatchStudioContainers(
        IReadOnlyList<HostContainerSummaryDto> containers, string? apiHostName)
    {
        static string[] Words(string name) => name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.All(char.IsAsciiDigit)).Select(word => word.ToLowerInvariant()).ToArray();
        static string? RoleFor(string name) => Words(name).LastOrDefault() is { } last
            ? StudioComponents.FirstOrDefault(item => item.Tokens.Contains(last)).Role : null;
        var matched = new Dictionary<string, HostContainerSummaryDto>(StringComparer.Ordinal);
        var apiByName = containers.Where(container => RoleFor(container.Name) == "api").ToArray();
        var api = containers.FirstOrDefault(container => container.Id is { Length: > 0 } id && apiHostName is { Length: > 0 }
                && (apiHostName.StartsWith(id, StringComparison.OrdinalIgnoreCase) || id.StartsWith(apiHostName, StringComparison.OrdinalIgnoreCase)))
            ?? (apiByName.Length == 1 ? apiByName[0] : null);
        if (api is not null) matched["api"] = api;
        var apiWords = api is null ? null : Words(api.Name);
        var prefix = apiWords is { Length: > 1 } && RoleFor(api!.Name) == "api"
            ? api.Name[..api.Name.LastIndexOf(apiWords[^1], StringComparison.OrdinalIgnoreCase)] : null;
        foreach (var (role, _, _) in StudioComponents.Where(item => item.Role != "api"))
        {
            var candidates = containers.Where(container => container != api && RoleFor(container.Name) == role).ToArray();
            if (candidates.Length > 1 && prefix is { Length: > 0 })
                candidates = candidates.Where(container => container.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1) matched[role] = candidates[0];
        }
        return matched;
    }

    private static StructureObservation HostObservation(string hostId, IReadOnlyList<RunnerCapabilitySnapshotDto> reports,
        PullGateHost? gate, HostReleaseDriftSnapshot? releases, DateTime now, bool declared)
    {
        if (reports.Count == 0 && gate is not null)
        {
            var gateStale = now - gate.LastHeartbeat.UtcDateTime > TimeSpan.FromMinutes(2);
            return new(gateStale ? "stale" : declared ? "matched" : "undeclared", gateStale ? "stale" : "up",
                gate.LastHeartbeat.UtcDateTime, ActiveGates: gate.Busy, Detail: $"Gate slots: {gate.Busy}/{gate.Slots} busy");
        }
        var host = reports.OrderByDescending(report => report.LastSeenAt).First();
        var stale = now - host.LastSeenAt > Fresh;
        var drifting = reports.FirstOrDefault(report => HostReleaseDriftPolicy.Evaluate(report.Release, releases?.Stable, now).State
            is HostReleaseDriftStates.Behind or HostReleaseDriftStates.Differs) ?? host;
        var verdict = HostReleaseDriftPolicy.Evaluate(drifting.Release, releases?.Stable, now);
        var behind = verdict.State == HostReleaseDriftStates.Behind;
        var differs = behind || verdict.State == HostReleaseDriftStates.Differs;
        var telemetry = host.Telemetry is { } value && now - value.ObservedAt <= Fresh ? value : null;
        return new(stale ? "stale" : differs ? "differs" : declared ? "matched" : "undeclared",
            stale ? "stale" : host.Status == "active" ? "up" : "down", host.LastSeenAt,
            drifting.Release?.Version, releases?.Stable.Version, behind, telemetry?.CpuPercent,
            telemetry?.MemoryUsedBytes, telemetry?.MemoryTotalBytes,
            reports.Sum(report => report.ActiveGateCount) + (gate?.Busy ?? 0), verdict.Reason);
    }

    private static StructurePerformanceFacts? Performance(IReadOnlyList<RunnerCapabilitySnapshotDto> reports)
    {
        // A re-adopted slot record carries no machine reading; skip it.
        var telemetry = reports.Select(report => report.Telemetry)
            .Where(value => value is not null && (value.CpuCores > 0 || value.MemoryTotalBytes is not null
                || value.Filesystems is { Count: > 0 }))
            .OrderByDescending(value => value!.ObservedAt).FirstOrDefault();
        if (telemetry is null) return null;
        var disks = telemetry.Filesystems is { Count: > 0 } filesystems
            ? filesystems.Where(item => item.TotalBytes > 0).Select(item => new StructureDiskFact(item.Path, item.Purpose,
                Math.Max(0, item.TotalBytes - item.FreeBytes), item.FreeBytes, item.TotalBytes)).ToArray()
            : telemetry is { DiskTotalBytes: > 0 and var total, DiskFreeBytes: { } free }
                ? [new StructureDiskFact("runner work", "runner work", Math.Max(0, total - free), free, total)]
                : null;
        return new(telemetry.ObservedAt, telemetry.CpuPercent, telemetry.CpuCores > 0 ? telemetry.CpuCores : null,
            telemetry.Load1, telemetry.Load5, telemetry.Load15, telemetry.MemoryUsedBytes, telemetry.MemoryTotalBytes,
            disks, telemetry.NetworkReceiveBytesPerSecond, telemetry.NetworkTransmitBytesPerSecond);
    }

    private static StructureNodeFacts HostFacts(string hostId, IReadOnlyList<RunnerCapabilitySnapshotDto> reports,
        PullGateHost? gate, HostReleaseDriftSnapshot? releases, DateTime now, StructureObservedContext context,
        bool performance)
    {
        var placement = reports.Select(report => report.Telemetry?.Placement).FirstOrDefault(value => value is not null);
        var roles = reports.Select(RoleOf).Concat(gate is null ? [] : ["gate"]).Distinct(StringComparer.Ordinal)
            .OrderBy(role => role, StringComparer.Ordinal).ToArray();
        DateTime? lastSeen = reports.Count > 0 ? reports.Max(report => report.LastSeenAt) : null;
        if (gate is not null && (lastSeen is null || gate.LastHeartbeat.UtcDateTime > lastSeen))
            lastSeen = gate.LastHeartbeat.UtcDateTime;

        var totals = reports.Select(report => report.RoleMaxParallelism ?? report.EffectiveMaxParallelism
            ?? report.RuntimeCapacity?.MaxParallelism).Where(value => value is not null).Select(value => value!.Value).ToArray();
        int? total = totals.Length == 0 && gate is null ? null : totals.Sum() + (gate?.Slots ?? 0);
        var busy = reports.Sum(report => report.Telemetry?.ActiveSlots ?? 0) + (gate?.Busy ?? 0);
        var running = reports.SelectMany(report => context.RunningCards?.GetValueOrDefault(report.RunnerId) ?? [])
            .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var capacity = new StructureCapacityFacts(busy, total is null ? null : Math.Max(0, total.Value - busy), total, running);

        StructureReleaseFacts? release = null;
        if (reports.Count > 0)
        {
            static int Rank(string state) => state switch
            {
                HostReleaseDriftStates.Behind => 3, HostReleaseDriftStates.Differs => 2,
                HostReleaseDriftStates.Current => 0, _ => 1,
            };
            var worst = reports.Select(report => (Report: report,
                    Verdict: HostReleaseDriftPolicy.Evaluate(report.Release, releases?.Stable, now)))
                .OrderByDescending(item => Rank(item.Verdict.State)).First();
            var pending = reports.Any(report => report.CliUpdate?.State is CliUpdateStates.Draining
                    or CliUpdateStates.Ready or CliUpdateStates.Upgrading or CliUpdateStates.Probing)
                || reports.Any(report => releases?.Hosts.FirstOrDefault(entry => entry.RunnerId == report.RunnerId)
                    is { ExpectedImageRef: { Length: > 0 } expected }
                    && !string.Equals(expected, report.Release?.ImageRef, StringComparison.Ordinal));
            release = new(worst.Report.Release?.Version ?? (worst.Report.RunnerVersion is { Length: > 0 } label ? label : null),
                releases?.Stable.Version, worst.Verdict.State, worst.Verdict.State == HostReleaseDriftStates.Behind,
                pending, worst.Verdict.Reason);
        }

        var tools = new Dictionary<(string Kind, string Name), StructureToolFact>();
        void Tool(StructureToolFact tool)
        {
            // The worse state of two roles on one host wins.
            if (!tools.TryGetValue((tool.Kind, tool.Name), out var known) || known.State == "ok") tools[(tool.Kind, tool.Name)] = tool;
        }
        foreach (var report in reports)
        {
            foreach (var cli in report.InstalledClis ?? [])
                Tool(new(cli.Name, "cli", cli.Version, cli.TargetVersion, cli.IsBelowTarget ? "behind" : "ok"));
            foreach (var capability in report.Capabilities.Where(item => item.Key.StartsWith("toolchain:", StringComparison.Ordinal)))
                Tool(new(capability.Key["toolchain:".Length..], "toolchain", capability.Version, null,
                    string.Equals(capability.AdvertisedStatus, "ready", StringComparison.OrdinalIgnoreCase)
                    && capability.HealthState == CapabilityHealthStates.Healthy ? "ok" : "unhealthy"));
        }
        var held = reports.SelectMany(report => report.HeldCapabilities ?? []).ToArray();
        var missing = held.Where(item => item.Cause == HeldCapabilityCauses.NotRegistered).Select(item => item.Key)
            .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        foreach (var key in missing.Where(key => key.StartsWith("toolchain:", StringComparison.Ordinal)))
            Tool(new(key["toolchain:".Length..], "toolchain", null, null, "missing"));
        var blocked = held.SelectMany(item => (item.WaitingTaskKeys ?? []).Select(key => new StructureBlockedCard(key,
                // A key the role never offered refuses the claim; a key held back for now parks the work.
                item.Cause == HeldCapabilityCauses.NotRegistered ? "refused" : "parked", item.Key, item.ObservedAt)))
            .Distinct().OrderBy(card => card.Key, StringComparer.Ordinal).ThenBy(card => card.Reason, StringComparer.Ordinal).ToArray();
        var ordered = tools.Values.OrderBy(tool => tool.Kind, StringComparer.Ordinal).ThenBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        var warnings = new StructureWarningFacts(ordered, missing,
            ordered.Where(tool => tool.State == "behind").Select(tool => tool.Name).ToArray(),
            release?.Behind == true, release?.PendingInstall == true, [], blocked);

        var observers = reports.Select(r => r.RunnerId).Concat(gate is null ? [] : ["gate heartbeat"]).Order(StringComparer.Ordinal);
        return new(hostId, placement?.Kind, placement?.Virtualization, placement?.ContainerName, roles, lastSeen,
            performance ? Performance(reports) : null, capacity, release, null, warnings,
            Sample: lastSeen is { } seen ? new(seen, string.Join(", ", observers), seen + Fresh) : null);
    }

    /// <summary>Names the links that are down on the nodes they touch.</summary>
    private static void MarkLinksDown(List<StructureNode> nodes, IReadOnlyList<StructureLink> links)
    {
        var down = links.Where(link => link.Id is not null && link.Observation?.Health == "down").ToArray();
        if (down.Length == 0) return;
        for (var position = 0; position < nodes.Count; position++)
        {
            var node = nodes[position];
            if (node.Facts?.Warnings is not { } warnings) continue;
            var ids = down.Where(link => link.From == node.Id || link.To == node.Id).Select(link => link.Id!)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ids.Length > 0) nodes[position] = node with { Facts = node.Facts with { Warnings = warnings with { LinksDown = ids } } };
        }
    }
}
