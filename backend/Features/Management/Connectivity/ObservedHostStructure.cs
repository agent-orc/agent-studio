using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>Everything the hosts and the API itself report, collected once per snapshot refresh.</summary>
/// <param name="RunningCards">Task keys under an unexpired lease, by runner id.</param>
/// <param name="RefusedCards">Task keys a host's claims refused or held, by runner id.</param>
public sealed record StructureHostFacts(
    IReadOnlyList<RunnerCapabilitySnapshotDto> Hosts,
    IReadOnlyList<PullGateHost> Gates,
    IReadOnlyList<RunnerLinkResource> Links,
    HostReleaseDriftSnapshot? Releases,
    StudioSelfFacts? Self = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RunningCards = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RefusedCards = null);

/// <summary>
/// AGT-3954: every host that reports becomes a node, declared or not. Ids come
/// from the reports (<c>machine:{hostId}</c>, <c>runner:{runnerId}</c>), so a
/// placement file that declares the same id joins the observation, and the
/// ids survive restarts. Nesting comes from the host's own machine report:
/// a configured parent machine id first, then the machine whose kernel it
/// shares. Pure; the caller supplies the clock.
/// </summary>
public static class ObservedHostStructure
{
    public const string ServerFallbackId = "studio-server";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan IngressFresh = TimeSpan.FromMinutes(10);
    private static readonly string[] FinishedUpdates =
        [CliUpdateStates.Succeeded, CliUpdateStates.Failed, CliUpdateStates.Cancelled];

    public static StructureResponse Extend(StructureResponse current, StructureHostFacts facts, DateTime now)
    {
        var nodes = current.Nodes.ToList();
        var links = current.Links.ToList();
        var index = nodes.Select((node, position) => (node.Id, position))
            .ToDictionary(item => item.Id, item => item.position, StringComparer.Ordinal);
        StructureNode? Find(string id) => index.TryGetValue(id, out var at) ? nodes[at] : null;
        void Put(StructureNode node)
        {
            if (index.TryGetValue(node.Id, out var at)) nodes[at] = node;
            else { index[node.Id] = nodes.Count; nodes.Add(node); }
        }

        var reports = facts.Hosts.GroupBy(host => host.HostId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(host => host.LastSeenAt).ToArray(),
                StringComparer.Ordinal);
        HostMachineDto? MachineOf(string hostId) => reports.TryGetValue(hostId, out var list)
            ? list.Select(report => report.Telemetry?.Machine).FirstOrDefault(machine => machine is not null) : null;
        var declaredRunners = current.Nodes.Where(node => node.Id.StartsWith("component:", StringComparison.Ordinal)
                                                          && node.UnitId is not null)
            .Select(node => node.UnitId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var serverId = ServerId(facts, reports, MachineOf);
        var server = $"machine:{serverId}";

        string Placeholder(string id, string label, string kind, string detail)
        {
            var nodeId = $"machine:{id}";
            if (Find(nodeId) is null)
                Put(new(nodeId, kind, label, "installation", null, null, null, "observed", "host machine report",
                    Observation: new("undeclared", "not observed", Detail: detail),
                    Facts: new(Machine: new(kind == "host" ? HostMachineKinds.Unknown : kind, null, null, null, null,
                        "named by host reports"))));
            return nodeId;
        }
        string ParentFor(string hostId, HostMachineDto? machine)
        {
            var under = machine?.Kind == HostMachineKinds.Container ? KindUnder(machine.Virtualization) : "host";
            if (machine?.ParentMachineId is { } parent)
                return Find($"machine:{parent}") is not null ? $"machine:{parent}"
                    : Placeholder(parent, parent, under, $"Named as parent machine by {hostId}");
            if (machine is not { Kind: HostMachineKinds.Container, KernelId: { } kernel }) return "installation";
            var sibling = reports.Keys.FirstOrDefault(other => other != hostId && MachineOf(other) is
                { KernelId: var otherKernel, Kind: not HostMachineKinds.Container } && otherKernel == kernel);
            if (sibling is not null) return $"machine:{sibling}";
            if (facts.Self?.Machine.KernelId == kernel) return server;
            return Placeholder($"kernel-{kernel}", "Machine (not declared)", under,
                "Containers on one kernel; set RUNNER_PARENT_MACHINE_ID to give it a stable id");
        }

        // Hosts first, so containers can nest under hosts that also report.
        var hostOrder = reports.Keys.OrderBy(id => MachineOf(id)?.Kind == HostMachineKinds.Container ? 1 : 0)
            .ThenBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (var hostId in hostOrder)
        {
            var list = reports[hostId];
            var machine = MachineOf(hostId);
            var nodeId = $"machine:{hostId}";
            var hostFacts = HostFacts(hostId, list, facts, machine, now);
            if (Find(nodeId) is { } declared)
                Put(declared with { Facts = Merge(declared.Facts, hostFacts) });
            else if (list.All(report => declaredRunners.Contains(report.RunnerId)))
            {
                foreach (var node in nodes.Where(node => node.Id.StartsWith("component:", StringComparison.Ordinal)
                             && list.Any(report => string.Equals(report.RunnerId, node.UnitId, StringComparison.OrdinalIgnoreCase))).ToArray())
                    Put(node with { Facts = Merge(node.Facts, hostFacts) });
                continue;
            }
            else
                Put(new(nodeId, KindOf(machine), hostId, ParentFor(hostId, machine), null, null, null, "observed",
                    "host capability report", Observation: HostObservation(list, facts, now, undeclared: true),
                    Facts: hostFacts));
            foreach (var report in list.Where(report => !declaredRunners.Contains(report.RunnerId)))
            {
                var role = RoleOf(report);
                Put(new($"runner:{report.RunnerId}", "process", RoleLabel(role), nodeId, report.RunnerId, null, null,
                    "observed", "host capability report", FunctionOf(role),
                    Observation: HostObservation([report], facts, now, undeclared: true),
                    Facts: RoleFacts(report, facts, now)));
            }
        }

        foreach (var gate in facts.Gates.Where(gate => !reports.ContainsKey(gate.Id)))
        {
            var stale = now - gate.LastHeartbeat.UtcDateTime > TimeSpan.FromMinutes(2);
            var observation = new StructureObservation(stale ? "stale" : "undeclared", stale ? "stale" : "up",
                gate.LastHeartbeat.UtcDateTime, ActiveGates: gate.Busy, Detail: $"Gate slots: {gate.Busy}/{gate.Slots} busy");
            var gateFacts = new StructureNodeFacts(HostId: gate.Id, CpuCores: gate.ProcessorCount, BusySlots: gate.Busy,
                FreeSlots: Math.Max(0, gate.Slots - gate.Busy), LastSeenAt: gate.LastHeartbeat.UtcDateTime,
                RunningCards: gate.CurrentRun is { } run ? [run] : null);
            var nodeId = $"machine:{gate.Id}";
            if (Find(nodeId) is { } declared) Put(declared with { Facts = Merge(declared.Facts, gateFacts) });
            else Put(new(nodeId, "host", gate.Name, "installation", null, null, null, "observed", "gate heartbeat",
                Observation: observation, Facts: gateFacts));
            Put(new($"gate:{gate.Id}", "process", "Gate worker", nodeId, gate.Id, null, null, "observed",
                "gate heartbeat", "verification", Observation: observation, Facts: gateFacts with { CpuCores = null }));
        }

        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        if (facts.Self is { } self) AddServer(self, serverId, Find, Put, components, now);

        AddContainers(facts, reports, nodes, Find, Put, components, now);
        AddLinks(facts, reports, links, Find, Put, server, components, now);

        var linkList = StructureIdentity.AssignLinkIds(links.Where(link => index.ContainsKey(link.From) && index.ContainsKey(link.To)));
        var down = linkList.Where(link => link.Observation?.Health == "down").ToArray();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var touching = down.Where(link => link.From == node.Id || link.To == node.Id).Select(link => link.Id!).ToArray();
            if (touching.Length > 0) nodes[i] = node with { Facts = (node.Facts ?? new()) with { LinksDown = touching } };
        }
        return current with { Nodes = nodes, Links = linkList };
    }

    /// <summary>The configured id, else the reporting machine that shares the API's kernel, else a role name.</summary>
    private static string ServerId(StructureHostFacts facts, IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> reports,
        Func<string, HostMachineDto?> machineOf)
    {
        if (facts.Self is not { } self) return ServerFallbackId;
        if (self.ConfiguredServerMachineId is { } configured) return configured;
        var kernel = self.Machine.KernelId;
        return kernel is null ? ServerFallbackId
            : reports.Keys.Order(StringComparer.Ordinal).FirstOrDefault(host => machineOf(host) is
                { Kind: not HostMachineKinds.Container, KernelId: var hostKernel } && hostKernel == kernel)
              ?? ServerFallbackId;
    }

    private static void AddServer(StudioSelfFacts self, string serverId, Func<string, StructureNode?> find,
        Action<StructureNode> put, Dictionary<string, string> components, DateTime now)
    {
        var machineId = $"machine:{serverId}";
        var locations = self.DataLocations.Select(location => new StructureDataLocation(
            $"data:{serverId}:{location.Role}", location.Role, location.Label, location.SizeBytes,
            location.BackupCovered, location.GitRemoteCovered, location.MeasuredAt, location.Detail)).ToArray();
        // The API observes itself continuously; a timestamp of "now" would turn
        // every refresh into a change, so self facts carry the process start only.
        var serverFacts = TelemetryFacts(self.Telemetry, now) with
        {
            TelemetryObservedAt = null,
            Machine = self.Machine.Kind == HostMachineKinds.Container
                ? new(KindUnder(self.Machine.Virtualization), self.Machine.Virtualization, null, null, null, "studio api")
                : new(self.Machine.Kind, self.Machine.Virtualization, null, null, null, "studio api"),
            DataLocations = locations,
        };
        if (find(machineId) is { } existing)
        {
            var merged = existing.Facts is { } reported
                ? reported with
                {
                    DataLocations = locations,
                    CpuCores = reported.CpuCores ?? serverFacts.CpuCores,
                    CpuPercent = reported.CpuPercent ?? serverFacts.CpuPercent,
                    Load1 = reported.Load1 ?? serverFacts.Load1, Load5 = reported.Load5 ?? serverFacts.Load5,
                    Load15 = reported.Load15 ?? serverFacts.Load15,
                    MemoryUsedBytes = reported.MemoryUsedBytes ?? serverFacts.MemoryUsedBytes,
                    MemoryTotalBytes = reported.MemoryTotalBytes ?? serverFacts.MemoryTotalBytes,
                    Mounts = (reported.Mounts ?? []).Concat((serverFacts.Mounts ?? []).Where(mount =>
                        reported.Mounts?.Any(item => item.Mount == mount.Mount) != true)).ToArray(),
                }
                : serverFacts;
            put(existing with { Facts = merged });
        }
        else
        {
            put(new(machineId, serverFacts.Machine!.Kind is HostMachineKinds.Unknown ? "host" : serverFacts.Machine.Kind,
                serverId == ServerFallbackId ? "Studio server" : serverId, "installation", null, null, null,
                "observed", "studio self observation",
                Observation: new("undeclared", "up", self.StartedAt, self.Version, Detail: "Runs the Studio API"),
                Facts: serverFacts));
        }

        var kind = self.Machine.Kind == HostMachineKinds.Container ? "container" : "process";
        void Component(string key, string label, StructureObservation observation, StructureNodeFacts? facts = null)
        {
            var id = $"component:{serverId}:{key}";
            components[key] = id;
            if (find(id) is { } declared)
                put(declared with
                {
                    Observation = observation with { Difference = observation.Difference == "undeclared" ? "matched" : observation.Difference },
                    Facts = Merge(declared.Facts, facts),
                });
            else
                put(new(id, kind, label, machineId, key, null, null, "observed", "studio self observation", "control",
                    Observation: observation, Facts: facts));
        }

        Component("api", "Studio API", new("undeclared", "up", self.StartedAt, self.Version, Detail: "Serves this structure"),
            new(Version: self.Version, Machine: new(self.Machine.Kind, self.Machine.Virtualization,
                self.Machine.ContainerRuntime, self.Machine.ContainerName, null, "studio api")));
        if (self.Engine.Required || self.Engine.LastContact is not null)
            Component("engine", "Orchestrator Engine",
                new("undeclared",
                    self.Engine.State == OrchestrationEngineReadiness.Ready ? "up"
                        : self.Engine.State == OrchestrationEngineReadiness.NotRequired ? "not observed" : "down",
                    self.Engine.LastContact, Detail: $"Engine readiness: {self.Engine.State}"
                        + (self.Engine.EngineId is { } engine ? $" · {engine}" : "")),
                new(LastSeenAt: self.Engine.LastContact));
        if (self.Ingress.EdgeSeenAt is { } edge)
            Component("edge", "Edge proxy", Seen(edge, now, "Forwards browser requests to the API"),
                new(LastSeenAt: edge));
        if (self.Ingress.BrowserSeenAt is { } browser)
            Component("web", "Web frontend", Seen(browser, now, "Seen through browser requests"),
                new(LastSeenAt: browser));
        if (self.Ingress.CdnSeenAt is { } cdn && find("external:cdn") is null)
            put(new("external:cdn", "external", "CDN proxy", null, null, null, null, "observed", "studio ingress",
                Observation: Seen(cdn, now, "CDN request headers seen on forwarded requests")));
    }

    private static StructureObservation Seen(DateTime at, DateTime now, string detail)
        => now - at > IngressFresh
            ? new("stale", "stale", at, Detail: detail)
            : new("undeclared", "up", at, Detail: detail);

    /// <summary>
    /// Containers a host's runtime lists. The API's own container and its
    /// Compose siblings (web, edge, engine) become the Studio components;
    /// everything else that is not declared is an observed container.
    /// </summary>
    private static void AddContainers(StructureHostFacts facts, IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> reports,
        List<StructureNode> nodes, Func<string, StructureNode?> find, Action<StructureNode> put,
        Dictionary<string, string> components, DateTime now)
    {
        var self = facts.Self;
        foreach (var (hostId, list) in reports)
        {
            var parent = $"machine:{hostId}";
            if (find(parent) is null) continue;
            var report = list.FirstOrDefault(item => item.Containers is not null);
            if (report?.Containers is not { } containers) continue;
            var stale = now - report.LastSeenAt > Fresh;
            var apiContainer = self is null ? null : containers.FirstOrDefault(container =>
                container.Id is { } id && self.Hostname.Length >= 12 && id.StartsWith(self.Hostname[..12], StringComparison.Ordinal)
                || string.Equals(container.Name, self.Hostname, StringComparison.Ordinal));
            foreach (var container in containers)
            {
                var observation = new StructureObservation(stale ? "stale" : "undeclared",
                    stale ? "stale" : container.State == "running" ? "up" : "down",
                    report.LastSeenAt, container.ImageDigest, Detail: container.State);
                var containerFacts = new StructureNodeFacts(ImageDigest: container.ImageDigest,
                    ContainerState: container.State, LastSeenAt: report.LastSeenAt);
                var key = ReferenceEquals(container, apiContainer) ? "api"
                    : apiContainer?.ComposeProject is { } project && container.ComposeProject == project
                        ? StudioComponent(container.ComposeService) : null;
                if (key is not null && components.TryGetValue(key, out var componentId) && find(componentId) is { } component)
                {
                    put(component with { Facts = Merge(component.Facts, containerFacts) });
                    continue;
                }
                if (key is not null && self is not null)
                {
                    var serverMachine = find(parent)!.Id;
                    var id = $"component:{serverMachine[8..]}:{key}";
                    components[key] = id;
                    put(new(id, "container", ComponentLabel(key), parent, key, null, null, "observed",
                        "host capability report", "control", Observation: observation, Facts: containerFacts));
                    continue;
                }
                var declared = nodes.FirstOrDefault(node => node.Kind == "container" && node.Provenance == "declared"
                    && HostOf(node, find) == hostId
                    && (string.Equals(node.UnitId, container.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(node.Label, container.Name, StringComparison.OrdinalIgnoreCase)));
                if (declared is not null)
                {
                    put(declared with { Facts = Merge(declared.Facts, containerFacts) });
                    continue;
                }
                put(new($"observed-container:{hostId}:{container.Name}", "container", container.Name, parent, null, null,
                    null, "observed", "host capability report", Observation: observation, Facts: containerFacts));
            }
        }
    }

    private static string? HostOf(StructureNode node, Func<string, StructureNode?> find)
    {
        var current = node;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (current.Id.StartsWith("machine:", StringComparison.Ordinal)) return current.Id[8..];
            current = current.ParentId is { } parent ? find(parent) : null;
        }
        return null;
    }

    private static string? StudioComponent(string? service) => service?.ToLowerInvariant() switch
    {
        null => null,
        "api" or "backend" or "orchestrator-api" => "api",
        "frontend" or "web" => "web",
        "edge" or "caddy" => "edge",
        var name when name.Contains("engine", StringComparison.Ordinal) => "engine",
        _ => null,
    };

    private static string ComponentLabel(string key) => key switch
    {
        "api" => "Studio API", "web" => "Web frontend", "edge" => "Edge proxy", "engine" => "Orchestrator Engine",
        _ => key,
    };

    private static void AddLinks(StructureHostFacts facts, IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> reports,
        List<StructureLink> links, Func<string, StructureNode?> find, Action<StructureNode> put, string server,
        IReadOnlyDictionary<string, string> components, DateTime now)
    {
        var api = components.TryGetValue("api", out var apiId) ? apiId : find(server) is not null ? server : null;
        bool Has(string from, string to, string kind) => links.Any(link => link.From == from && link.To == to && link.Kind == kind);
        void Add(string from, string to, string kind, string label, string source, StructureObservation observation,
            StructureLinkFacts linkFacts)
        {
            if (from == to || find(from) is null || find(to) is null || Has(from, to, kind)) return;
            links.Add(new(from, to, kind, label, source, observation, Facts: linkFacts));
        }
        for (var i = 0; i < links.Count; i++)
            if (links[i].Observation is { } declared && links[i].Facts is null)
                links[i] = links[i] with { Facts = new(declared.Health) };

        foreach (var (hostId, list) in reports)
        {
            var from = find($"machine:{hostId}") is not null ? $"machine:{hostId}"
                : list.Select(report => find($"runner:{report.RunnerId}")?.Id).FirstOrDefault(id => id is not null);
            if (from is null) continue;
            var telemetry = list.Select(report => report.Telemetry).FirstOrDefault(item => item is not null);
            if (api is not null && telemetry is not null && find(api)?.ParentId != from)
            {
                var state = telemetry.TaskServerConnectionStatus;
                var health = state == "reachable" ? "up" : state == "unreachable" ? "down" : "not observed";
                Add(from, api, "call", "Task Server route", "host telemetry",
                    new("observed", health, telemetry.TaskServerConnectionObservedAt,
                        Detail: telemetry.TaskServerConnectionLastError ?? state),
                    new(state, ProbedAt: telemetry.TaskServerConnectionObservedAt));
            }
            foreach (var link in facts.Links.Where(link => list.Any(report => report.RunnerId == link.RunnerId)))
            {
                if (links.Any(existing => existing.Kind is "tunnel" or "ssh"
                        && (existing.From == from || existing.To == from))) continue;
                var stale = link.LastHeartbeatAt is { } at && now - at > Fresh;
                Add(server, from, "tunnel", "Runner link", "link supervisor",
                    new(stale ? "stale" : "observed", stale ? "stale" : link.State == RunnerLinkStates.Up ? "up" : "down",
                        link.LastHeartbeatAt, Detail: link.LastError ?? link.State),
                    new(link.State, link.LastProbe?.DurationMs, link.LastProbe?.At, link.Transport));
            }
            foreach (var report in list)
                foreach (var capability in report.Capabilities)
                {
                    var (external, label, linkLabel) = capability.Key switch
                    {
                        var key when key.StartsWith("provider-auth:", StringComparison.Ordinal) =>
                            ($"external:model-provider:{key[14..]}", $"Model provider {key[14..]}", "Model calls"),
                        CapabilityProtocol.GitFetch => ("external:git-remote", "Git remote", "Git"),
                        _ => (null, null, null),
                    };
                    if (external is null) continue;
                    if (find(external) is null)
                        put(new(external, "external", label!, null, null, null, null, "observed", "host capability report",
                            Observation: new("unreachable-by-design", "not observed", Detail: "Reached by hosts; no direct observation")));
                    var ready = capability.AdvertisedStatus == "ready" && capability.IsFresh;
                    Add(from, external, "call", linkLabel!, "host capability report",
                        new("observed", ready ? "up" : "down", capability.AdvertisedAt, Detail: capability.Detail ?? capability.AdvertisedStatus),
                        new(capability.AdvertisedStatus, ProbedAt: capability.AdvertisedAt));
                }
        }
        foreach (var gate in facts.Gates.Where(gate => !reports.ContainsKey(gate.Id)))
        {
            if (api is null) break;
            var stale = now - gate.LastHeartbeat.UtcDateTime > TimeSpan.FromMinutes(2);
            Add($"machine:{gate.Id}", api, "call", "Gate pull", "gate heartbeat",
                new(stale ? "stale" : "observed", stale ? "stale" : "up", gate.LastHeartbeat.UtcDateTime),
                new(stale ? "stale" : "up", ProbedAt: gate.LastHeartbeat.UtcDateTime));
        }
        if (find("external:cdn") is not null && components.TryGetValue("edge", out var edge))
            Add("external:cdn", edge, "call", "Browser traffic", "studio ingress",
                find("external:cdn")!.Observation! with { Difference = "observed" },
                new(find("external:cdn")!.Observation!.Health, ProbedAt: find("external:cdn")!.Observation!.ObservedAt));
    }

    /// <summary>The newest report decides health; version drift uses the role that differs.</summary>
    public static StructureObservation HostObservation(IReadOnlyList<RunnerCapabilitySnapshotDto> reports,
        StructureHostFacts facts, DateTime now, bool undeclared, PullGateHost? gate = null)
    {
        var host = reports[0];
        var stale = now - host.LastSeenAt > Fresh;
        var stable = facts.Releases?.Stable;
        var releaseHost = reports.FirstOrDefault(report =>
            HostReleaseDriftPolicy.Evaluate(report.Release, stable, now).State
                is HostReleaseDriftStates.Behind or HostReleaseDriftStates.Differs) ?? host;
        var verdict = HostReleaseDriftPolicy.Evaluate(releaseHost.Release, stable, now);
        var behind = verdict.State == HostReleaseDriftStates.Behind;
        var differs = behind || verdict.State == HostReleaseDriftStates.Differs;
        var telemetry = reports.Select(report => report.Telemetry)
            .FirstOrDefault(item => item is not null && now - item.ObservedAt <= Fresh);
        var difference = stale ? "stale" : undeclared ? "undeclared" : differs ? "differs" : "matched";
        return new(difference, stale ? "stale" : host.Status != "active" ? "down" : "up", host.LastSeenAt,
            releaseHost.Release?.Version, stable?.Version, behind, telemetry?.CpuPercent, telemetry?.MemoryUsedBytes,
            telemetry?.MemoryTotalBytes, reports.Sum(report => report.ActiveGateCount) + (gate?.Busy ?? 0), verdict.Reason);
    }

    private static StructureNodeFacts HostFacts(string hostId, IReadOnlyList<RunnerCapabilitySnapshotDto> reports,
        StructureHostFacts facts, HostMachineDto? machine, DateTime now)
    {
        var telemetry = reports.Select(report => report.Telemetry).FirstOrDefault(item => item is not null);
        var roles = reports.Select(report => RoleFact(report, facts)).ToArray();
        var gate = facts.Gates.FirstOrDefault(item => item.Id == hostId);
        var stable = facts.Releases?.Stable;
        var release = reports.Select(report => (report, verdict: HostReleaseDriftPolicy.Evaluate(report.Release, stable, now)))
            .OrderBy(item => item.verdict.State switch
            {
                HostReleaseDriftStates.Behind => 0, HostReleaseDriftStates.Differs => 1,
                HostReleaseDriftStates.Current => 2, _ => 3,
            }).First();
        var busy = roles.Sum(role => role.BusySlots ?? 0) + (gate?.Busy ?? 0);
        var free = roles.Any(role => role.FreeSlots is not null) || gate is not null
            ? roles.Sum(role => role.FreeSlots ?? 0) + (gate is null ? 0 : Math.Max(0, gate.Slots - gate.Busy)) : (int?)null;
        var (tools, requirements) = Capabilities(reports);
        var held = requirements.SelectMany(requirement => requirement.WaitingCards ?? [])
            .Concat(reports.SelectMany(report => facts.RefusedCards?.GetValueOrDefault(report.RunnerId) ?? []))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var update = reports.Select(report => report.CliUpdate)
            .FirstOrDefault(item => item is not null && !FinishedUpdates.Contains(item.State));
        return TelemetryFacts(telemetry, now) with
        {
            HostId = hostId,
            Machine = machine is null ? null : new(machine.Kind, machine.Virtualization, machine.ContainerRuntime,
                machine.ContainerName, machine.ParentMachineId, "host report"),
            Roles = roles,
            BusySlots = busy,
            FreeSlots = free,
            RunningCards = roles.SelectMany(role => role.RunningCards).Concat(gate?.CurrentRun is { } run ? [run] : [])
                .Distinct(StringComparer.Ordinal).ToArray(),
            Version = release.report.Release?.Version ?? release.report.RunnerVersion,
            StableVersion = stable?.Version,
            ReleaseState = release.verdict.State,
            ReleaseDetail = release.verdict.Reason,
            PendingHostUpdate = update is null ? null : $"{update.State}{(update.Detail is { } detail ? $": {detail}" : "")}",
            LastSeenAt = reports.Max(report => report.LastSeenAt),
            AdmissionState = reports[0].HostAdmission.AdmissionState,
            Tools = tools,
            Requirements = requirements,
            HeldCards = held.Length > 0 ? held : null,
        };
    }

    private static StructureNodeFacts RoleFacts(RunnerCapabilitySnapshotDto report, StructureHostFacts facts, DateTime now)
    {
        var role = RoleFact(report, facts);
        var (tools, requirements) = Capabilities([report]);
        var verdict = HostReleaseDriftPolicy.Evaluate(report.Release, facts.Releases?.Stable, now);
        var held = requirements.SelectMany(requirement => requirement.WaitingCards ?? [])
            .Concat(facts.RefusedCards?.GetValueOrDefault(report.RunnerId) ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(HostId: report.HostId, Roles: [role], BusySlots: role.BusySlots, FreeSlots: role.FreeSlots,
            RunningCards: role.RunningCards, Version: report.Release?.Version ?? report.RunnerVersion,
            StableVersion: facts.Releases?.Stable.Version, ReleaseState: verdict.State, ReleaseDetail: verdict.Reason,
            LastSeenAt: report.LastSeenAt, AdmissionState: report.HostAdmission.AdmissionState, Tools: tools,
            Requirements: requirements, HeldCards: held.Length > 0 ? held : null);
    }

    private static StructureRoleFact RoleFact(RunnerCapabilitySnapshotDto report, StructureHostFacts facts)
    {
        var active = (report.Telemetry?.ActiveSlots ?? 0) + report.ActiveGateCount;
        var capacity = report.RoleMaxParallelism ?? report.EffectiveMaxParallelism;
        return new(report.RunnerId, RoleOf(report), report.Status, report.Release?.Version ?? report.RunnerVersion,
            report.LastSeenAt, active, capacity is { } total ? Math.Max(0, total - active) : null,
            facts.RunningCards?.GetValueOrDefault(report.RunnerId) ?? []);
    }

    private static StructureNodeFacts TelemetryFacts(HostTelemetrySnapshotDto? telemetry, DateTime now)
    {
        if (telemetry is null) return new();
        var fresh = now - telemetry.ObservedAt <= Fresh;
        var mounts = telemetry.Filesystems is { Count: > 0 } filesystems
            ? filesystems.Select(item => new StructureMountFact(item.Purpose, item.Path,
                Math.Max(0, item.TotalBytes - item.FreeBytes), item.FreeBytes, item.TotalBytes)).ToArray()
            : telemetry.DiskTotalBytes is { } total && telemetry.DiskFreeBytes is { } free
                ? [new StructureMountFact("disk", "/", Math.Max(0, total - free), free, total)] : null;
        return new(CpuCores: telemetry.CpuCores > 0 ? telemetry.CpuCores : null,
            CpuPercent: fresh ? telemetry.CpuPercent : null, Load1: fresh ? telemetry.Load1 : null,
            Load5: fresh ? telemetry.Load5 : null, Load15: fresh ? telemetry.Load15 : null,
            MemoryUsedBytes: fresh ? telemetry.MemoryUsedBytes : null, MemoryTotalBytes: telemetry.MemoryTotalBytes,
            Mounts: mounts, NetworkReceiveBytesPerSecond: fresh ? telemetry.Network?.ReceiveBytesPerSecond : null,
            NetworkTransmitBytesPerSecond: fresh ? telemetry.Network?.TransmitBytesPerSecond : null,
            TelemetryObservedAt: telemetry.ObservedAt);
    }

    /// <summary>Toolchains and CLIs with versions, and required ones the capability and readiness checks find unmet.</summary>
    private static (IReadOnlyList<StructureToolFact> Tools, IReadOnlyList<StructureRequirementFact> Requirements)
        Capabilities(IReadOnlyList<RunnerCapabilitySnapshotDto> reports)
    {
        var tools = new Dictionary<string, StructureToolFact>(StringComparer.Ordinal);
        var requirements = new Dictionary<string, StructureRequirementFact>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            foreach (var capability in report.Capabilities.Where(item =>
                         item.Key.StartsWith("toolchain:", StringComparison.Ordinal)
                         || item.Key.StartsWith("cli-execution:", StringComparison.Ordinal)))
            {
                var cli = report.InstalledClis?.FirstOrDefault(item =>
                    capability.Key == CapabilityProtocol.CliExecution(item.Name));
                tools.TryAdd(capability.Key, new(capability.Key, capability.Key[(capability.Key.IndexOf(':') + 1)..],
                    capability.AdvertisedStatus, capability.Version ?? cli?.Version, cli?.TargetVersion,
                    cli?.IsBelowTarget == true));
                if (capability.AdvertisedStatus != "ready")
                    requirements.TryAdd(capability.Key, new(capability.Key, "not-ready", capability.Detail ?? capability.Reason));
                else if (cli?.IsBelowTarget == true)
                    requirements.TryAdd(capability.Key, new(capability.Key, "behind",
                        $"{cli.Version} is below the target {cli.TargetVersion}"));
            }
            foreach (var hold in report.HeldCapabilities ?? [])
            {
                var state = hold.State == HeldCapabilityStates.Indeterminate ? "indeterminate"
                    : hold.Cause == HeldCapabilityCauses.NotRegistered ? "missing"
                    : hold.Cause == HeldCapabilityCauses.Stale ? "stale" : "held";
                requirements[hold.Key] = new(hold.Key, state, hold.Detail,
                    hold.WaitingTaskKeys is { Count: > 0 } waiting ? waiting : null);
            }
        }
        return (tools.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            requirements.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }

    private static StructureNodeFacts? Merge(StructureNodeFacts? declared, StructureNodeFacts? observed)
        => declared is null ? observed : observed is null ? declared : observed with
        {
            DataLocations = observed.DataLocations ?? declared.DataLocations,
            ImageDigest = observed.ImageDigest ?? declared.ImageDigest,
            ContainerState = observed.ContainerState ?? declared.ContainerState,
            Version = observed.Version ?? declared.Version,
            LastSeenAt = observed.LastSeenAt ?? declared.LastSeenAt,
            Machine = observed.Machine ?? declared.Machine,
        };

    private static string KindOf(HostMachineDto? machine) => machine?.Kind switch
    {
        HostMachineKinds.Container => "container",
        HostMachineKinds.Vm => "vm",
        HostMachineKinds.Physical => "physical",
        _ => "host",
    };

    /// <summary>The machine under a container: a hypervisor means a VM, <c>none</c> bare metal.</summary>
    private static string KindUnder(string? virtualization) => virtualization switch
    {
        null => "host",
        "none" => "physical",
        _ => "vm",
    };

    public static string RoleOf(RunnerCapabilitySnapshotDto report)
    {
        bool Has(string key) => report.Capabilities.Any(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        if (Has(GateCapabilities.Executor)) return "gate";
        if (Has(CapabilityProtocol.ReviewExecutor)) return "review";
        if (Has(CapabilityProtocol.CodingExecutor)) return "coding";
        var name = $"{report.RunnerId} {report.Name}".ToLowerInvariant();
        return name.Contains("gate", StringComparison.Ordinal) ? "gate"
            : name.Contains("review", StringComparison.Ordinal) ? "review"
            : name.Contains("integration", StringComparison.Ordinal) ? "integration" : "coding";
    }

    private static string RoleLabel(string role) => role switch
    {
        "gate" => "Gate runner", "review" => "Review runner", "integration" => "Integration runner", _ => "Coding runner",
    };

    private static string FunctionOf(string role) => role switch
    {
        "gate" => "verification", "review" => "review", "integration" => "integration", _ => "execution",
    };
}
