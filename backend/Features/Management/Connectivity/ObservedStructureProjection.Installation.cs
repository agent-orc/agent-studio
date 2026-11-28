using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public static partial class ObservedStructureProjection
{
    private static void AddInstallation(List<StructureNode> nodes, List<StructureLink> links,
        IReadOnlyList<RunnerCapabilitySnapshotDto> hosts, StructureObservedContext context, DateTime now)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        void Put(StructureNode node) => byId[node.Id] = node;
        var studio = context.Studio is { } self ? $"machine:{self.MachineId}" : null;
        var api = nodes.FirstOrDefault(n => n.ParentId == studio && n.Role == "api");
        foreach (var host in hosts.OrderBy(h => h.Telemetry?.Structure?.ObservedAt))
        {
            var id = $"machine:{host.HostId}";
            if (!byId.TryGetValue(id, out var reporter)) continue;
            if (api is not null && host.Telemetry?.Peer is { } peer)
            {
                var sample = new StructureStamp(peer.ObservedAt, "heartbeat receiver", peer.ObservedAt + Fresh);
                var kind = "runner-to-server";
                // The v2 control link already represents this connection. Keep its identity and measured RTT.
                var position = links.FindIndex(link => link.From == id && link.To == api.Id && link.Kind == "control");
                var facts = new StructureLinkFacts(now > sample.FreshUntil ? "stale" : "up", host.Telemetry.TaskServerRoundTripMs,
                    peer.ObservedAt, peer.Scheme, peer.AddressClass, sample);
                var observed = new StructureObservation(now > sample.FreshUntil ? "stale" : "matched", facts.State, peer.ObservedAt);
                if (position >= 0) links[position] = links[position] with { Kind = kind, Facts = facts, Observation = observed };
                else if (!links.Any(l => l.From == id && l.To == api.Id && l.Kind == kind))
                    links.Add(new(id, api.Id, kind, "Runner connection", sample.ObservedBy, observed,
                        StructureLink.IdFor(id, api.Id, kind), facts));
            }
            // A container's /proc counters can describe the enclosing kernel. Its cgroup
            // counters below describe only the container. Preserve the measurement's clock.
            if (reporter.Kind == "container" && reporter.ParentId is { } enclosing &&
                byId.TryGetValue(enclosing, out var guest) && guest.Kind is "vm" or "physical" &&
                Performance([host]) is { } performance && !(guest.Facts?.Performance?.ObservedAt >= performance.ObservedAt))
                Put(guest with { Facts = (guest.Facts ?? new()) with { Performance = performance,
                    Sample = new(performance.ObservedAt, host.RunnerId, performance.ObservedAt + Fresh) } });
            if (host.Telemetry?.Structure is not { } reading) continue;
            var stamp = new StructureStamp(reading.ObservedAt, reading.ObservedBy, reading.FreshUntil);
            var obs = new StructureObservation(now > stamp.FreshUntil ? "stale" : "undeclared",
                now > stamp.FreshUntil ? "stale" : "up", stamp.ObservedAt);
            var component = byId.Values.FirstOrDefault(n => n.UnitId == host.RunnerId && n.Id.StartsWith("component:", StringComparison.Ordinal));
            var machine = reporter.Kind == "host" && reporter.ParentId == studio ? studio! : id;
            if (host.Telemetry.Placement is { Kind: HostPlacementKinds.Container, ParentMachineId: { } parent } &&
                byId.ContainsKey($"machine:{parent}")) machine = $"machine:{parent}";
            var housing = byId[machine];
            var machineFacts = housing.Facts ?? new();
            Put(housing with { Facts = machineFacts with { DmiProduct = reading.DmiProduct, Sample = stamp,
                DetectedVm = host.Telemetry.Placement?.DetectedVm, DetectedContainer = host.Telemetry.Placement?.DetectedContainer,
                Performance = Performance([host]) ?? machineFacts.Performance } });
            if (host.Telemetry.Placement?.Kind == HostPlacementKinds.Container)
                Put(byId[id] with { Facts = (byId[id].Facts ?? new()) with { Resources = reading.Service,
                    Sample = stamp, Performance = null } });
            foreach (var disk in reading.Devices)
                Put(new($"disk:{machine}:{disk.Device}", "disk", disk.Device, machine, null, null, null,
                    "observed", "host block devices", Observation: obs, Facts: new(Sample: stamp, Device: disk)));
            if (component is not null)
            {
                var inside = host.Telemetry.Placement?.Kind == HostPlacementKinds.Container;
                var facts = component.Facts ?? new();
                var capacity = facts.Capacity;
                if (capacity is not null)
                {
                    var limit = host.Telemetry.ReviewPlane?.CurrentCeiling ?? reading.EffectiveSlotLimit ?? host.EffectiveMaxParallelism;
                    if (reading.SlotLimit is { } slots)
                        capacity = capacity with { TotalSlots = slots, FreeSlots = Math.Max(0, slots - capacity.BusySlots) };
                    var waiting = facts.Warnings?.BlockedCards.Select(c => c.Key).Distinct().Order().ToArray();
                    int? held = host.Telemetry.ClaimLoadGate?.State == "closed" || host.HostAdmission.AdmissionState != "open"
                        ? capacity.FreeSlots : limit is { } cap && capacity.TotalSlots is { } total
                            ? Math.Max(0, total - Math.Max(capacity.BusySlots, cap)) : null;
                    capacity = capacity with { RunningCards = capacity.RunningCards.Concat(reading.Workers.Select(w => w.CardKey)
                        .OfType<string>()).Distinct().Order().ToArray(), HeldSlots = held, WaitingCards = waiting,
                        FreeSlots = capacity.FreeSlots - (held ?? 0),
                        QueueLength = reading.Role switch
                        {
                            "review" => context.Installation?.Api.Queues.Review,
                            "coding" => context.Installation?.Api.Queues.Coding,
                            "gate" => context.Installation?.Api.Queues.Gate,
                            _ => null,
                        } };
                }
                Put(component with { Kind = inside ? "service" : "host-service", Label = reading.DisplayName, Facts = facts with
                    { Sample = stamp, Capacity = capacity, Workers = reading.Workers, Resources = reading.Service } });
            }
            foreach (var unit in reading.PostMergeUnits)
                Put(new($"service:{machine}:{unit.Unit}", "host-service", "Post-merge tests", machine, null, null, null,
                    "observed", "systemd cgroup", "post-merge", Observation: obs,
                    Facts: new(Sample: stamp, Resources: unit.Resources)));
            if (reading.StudioContainers is { Count: > 0 } containers)
            {
                var stack = $"stack:{machine}:studio";
                Put(new(stack, "container-stack", "Studio stack", machine, null, null, null, "observed", "studio.slice",
                    Observation: obs, Facts: new(Sample: stamp)));
                foreach (var c in containers)
                {
                    var known = c.Role is null || containers.Count(item => item.Role == c.Role) != 1 ? null : byId.Values.FirstOrDefault(n => n.Role == c.Role && (n.ParentId == machine || n.ParentId == stack));
                    var container = known ?? new StructureNode($"container:{machine}:{c.Id}", "container", c.Role ?? "Studio container",
                        stack, null, null, null, "observed", "studio.slice", c.Role);
                    Put(container with { ParentId = stack, Kind = "container", Observation = obs with { Version = container.Observation?.Version },
                        Facts = (container.Facts ?? new()) with { Sample = stamp, Container = c, Resources = c.Resources } });
                }
            }
        }
        if (context.Installation is { } installation && api is not null)
            AddRoutesAndProviders(byId, links, byId[api.Id], installation, now);
        nodes.Clear();
        nodes.AddRange(byId.Values);
    }

    private static void AddRoutesAndProviders(Dictionary<string, StructureNode> nodes, List<StructureLink> links,
        StructureNode api, StructureInstallationFacts installation, DateTime now)
    {
        var stamp = installation.Api.Sample;
        nodes[api.Id] = api with { Facts = (api.Facts ?? new()) with { Api = installation.Api } };
        string External(string role, string label, string source)
        {
            var id = $"external:{role}";
            nodes.TryAdd(id, new(id, "external", label, null, null, null, null, "declared", source, role,
                Observation: new("unreachable-by-design", "not observed"), Facts: new(Sample: stamp with { ObservedBy = source })));
            return id;
        }
        void Route(string from, string to, string kind, string source, string? transport)
        {
            var id = StructureLink.IdFor(from, to, kind);
            if (!links.Any(l => l.Id == id))
                links.Add(new(from, to, kind, kind.Replace('-', ' '), source,
                    new("declared-only", "not observed", stamp.ObservedAt), id,
                    new("not observed", Transport: transport, Sample: stamp with { ObservedBy = source })));
        }
        if (installation.PublicCdn is { } cdn)
        {
            var edge = nodes.Values.FirstOrDefault(n => n.ParentId == api.ParentId && n.Role == "edge");
            var web = nodes.Values.FirstOrDefault(n => n.ParentId == api.ParentId && n.Role == "web");
            const string source = "Studio edge configuration";
            var traffic = External("traffic", cdn ? "Public traffic" : "Browser", source);
            if (cdn)
            {
                var next = External("cdn", "CDN edge", source);
                Route(traffic, next, "public-route", source, "https");
                traffic = next;
            }
            if (edge is not null && web is not null)
            {
                Route(traffic, edge.Id, "public-route", source, "https");
                Route(edge.Id, web.Id, "public-route", source, "http");
                Route(web.Id, api.Id, "public-route", source, "http");
            }
        }
        if (installation.GateSshHost?.Trim() is { Length: > 0 } ssh)
        {
            const string source = "RemoteGate:SshHost";
            var gate = nodes.GetValueOrDefault($"machine:{ssh}")?.Id ?? nodes.GetValueOrDefault($"machine:ssh:{ssh}")?.Id
                ?? External("ssh-gate", "Gate host", source);
            Route(api.Id, gate, "gate-ssh", source, "ssh");
            if (installation.GateDisk is { } disk && disk.Host == ssh)
            {
                var read = new StructureStamp(disk.ObservedAt, "gate SSH disk probe", disk.ObservedAt + Fresh);
                var status = now > read.FreshUntil ? "stale" : "up";
                var link = links.FindIndex(l => l.From == api.Id && l.To == gate && l.Kind == "gate-ssh");
                links[link] = links[link] with { Observation = new(status == "stale" ? "stale" : "matched", status, disk.ObservedAt),
                    Facts = new(status, ProbedAt: disk.ObservedAt, Transport: "ssh", Sample: read) };
                var target = nodes[gate];
                nodes[gate] = target with { Facts = (target.Facts ?? new()) with { Sample = read,
                    Performance = (target.Facts?.Performance ?? new(disk.ObservedAt)) with
                    { Disks = disk.Filesystems.Select(m => new StructureDiskFact(m.Path, m.Purpose,
                        m.TotalBytes - m.FreeBytes, m.FreeBytes, m.TotalBytes)).ToArray() } } };
            }
            if (installation.GateNetwork == "overlay")
            {
                var network = External("overlay", "Overlay network", "Structure:GateNetwork");
                Route(api.Id, network, "overlay", "Structure:GateNetwork", null);
                Route(network, gate, "overlay", "Structure:GateNetwork", null);
            }
        }
        if (installation.SourceHosting) External("source-hosting", "Source hosting", "project repository configuration");
        foreach (var provider in installation.Providers ?? [])
            External(provider, provider == "codex" ? "Codex subscription" : "Claude subscription", "runner provider registration");
        foreach (var windows in installation.Windows.GroupBy(w => w.Provider))
        {
            var id = External(windows.Key, windows.Key == "codex" ? "Codex subscription" : "Claude subscription", "subscription probe");
            var latest = windows.MaxBy(w => w.Sample.ObservedAt)!.Sample;
            nodes[id] = nodes[id] with { Facts = new(Sample: latest, WeeklyWindows: windows.ToArray()),
                Observation = new(now > latest.FreshUntil ? "stale" : "matched", "not observed", latest.ObservedAt) };
        }
    }
}
