using AgentStudio.Management;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3954: hosts that report show without a declaration, nested and with their facts.</summary>
public sealed class ObservedStructureHostsTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly StructureResponse Undeclared = new(
        [new("installation", "boundary", "Installation", null, null, null, null, "declared", "role fixture")], []);
    private static readonly HostReleaseDriftSnapshot Stable = new(Now, new("2.0", "stable-commit", Now), 0, []);

    private static CapabilityHealthDto Capability(string key, string? version = null, string status = "ready")
        => new(key, "host", status, CapabilityHealthStates.Healthy, null, Now, Now.AddMinutes(3), true,
            null, null, null, null, 0, version, null, null, [], []);

    private static HostTelemetrySnapshotDto Telemetry(HostPlacementDto? placement = null, int activeSlots = 0,
        string connection = "reachable", double? roundTripMs = null) => new(
        Now, 37.5, 2.5, 2.0, 1.5, 8L << 30, 32L << 30, null, null, null, null, 24, activeSlots,
        TaskServerConnectionStatus: connection, TaskServerConnectionObservedAt: Now,
        Filesystems: [new("runner work", "/srv/work", 40L << 30, 100L << 30)],
        Placement: placement, NetworkReceiveBytesPerSecond: 1200, NetworkTransmitBytesPerSecond: 3400,
        TaskServerRoundTripMs: roundTripMs);

    private static RunnerCapabilitySnapshotDto Report(string runnerId, string hostId, string role = "coding",
        HostTelemetrySnapshotDto? telemetry = null, DateTime? seen = null, string version = "2.0",
        string? commit = "stable-commit", int? slots = null, IReadOnlyList<InstalledCliDto>? clis = null,
        IReadOnlyList<HeldCapabilityDto>? held = null, IReadOnlyList<HostContainerSummaryDto>? containers = null,
        IReadOnlyList<CapabilityHealthDto>? capabilities = null) => new(
        runnerId, $"{role} daemon", hostId, "instance", version, 1, "active", Now.AddHours(-1), seen ?? Now,
        new(hostId, "open", null, null, null, null),
        [.. capabilities ?? [], .. role == "review" ? [Capability(CapabilityProtocol.ReviewExecutor)]
            : role == "gate" ? [Capability(GateCapabilities.Executor)] : Array.Empty<CapabilityHealthDto>()],
        telemetry, RoleMaxParallelism: slots, InstalledClis: clis, Release: new("release", version, commit),
        Containers: containers, HeldCapabilities: held);

    private static StructureResponse Apply(StructureResponse declared, IReadOnlyList<RunnerCapabilitySnapshotDto> hosts,
        StructureObservedContext? context = null, IReadOnlyList<RunnerLinkResource>? links = null,
        IReadOnlyList<PullGateHost>? gates = null, HostReleaseDriftSnapshot? releases = null, string engine = "not-required")
        => ObservedStructureProjection.Apply(declared, hosts, links ?? [], [], [], releases ?? Stable, engine, Now, gates,
            context ?? new StructureObservedContext());

    [Fact]
    public void Every_host_that_reports_becomes_a_node_without_a_declaration()
    {
        var gate = new PullGateHost("gate-host", "Gate host", 2, 4, new DateTimeOffset(Now), null) { Busy = 1 };
        var projected = Apply(Undeclared,
            [Report("coding-runner", "coding-host", telemetry: Telemetry(activeSlots: 1), slots: 3),
             Report("review-runner", "review-host", "review")],
            gates: [gate]);

        var coding = projected.Nodes.Single(node => node.Id == "machine:coding-host");
        Assert.Equal("observed", coding.Provenance);
        Assert.Equal("installation", coding.ParentId);
        Assert.Equal("undeclared", coding.Observation!.Difference);
        Assert.Equal("up", coding.Observation.Health);
        Assert.Equal(["coding"], coding.Facts!.Roles);
        // An older host reports no placement: it still shows, flat, as a host.
        Assert.Equal("host", projected.Nodes.Single(node => node.Id == "machine:review-host").Kind);
        Assert.Null(projected.Nodes.Single(node => node.Id == "machine:review-host").Facts!.MachineKind);

        var daemon = projected.Nodes.Single(node => node.Id == "component:coding-host:coding-runner");
        Assert.Equal("machine:coding-host", daemon.ParentId);
        Assert.Equal("coding", daemon.Role);
        Assert.Equal("coding-runner", daemon.UnitId);
        Assert.Equal("review", projected.Nodes.Single(node => node.Id == "component:review-host:review-runner").Role);

        var gateNode = projected.Nodes.Single(node => node.Id == "machine:gate-host");
        Assert.Equal("Gate host", gateNode.Label);
        Assert.Equal(["gate"], gateNode.Facts!.Roles);
        Assert.Equal(new StructureCapacityFacts(1, 1, 2, []), gateNode.Facts.Capacity, CapacityComparer.Instance);

        // Without the context the projection stays declaration driven, as before.
        var declaredOnly = ObservedStructureProjection.Apply(Undeclared,
            [Report("coding-runner", "coding-host")], [], [], [], Stable, "not-required", Now);
        Assert.Equal(["installation"], declaredOnly.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Container_vm_and_physical_nesting_comes_from_the_host_reports()
    {
        HostPlacementDto InVm(string name) => HostPlacementProbe.Detect(path => path == "/.dockerenv",
            path => path == "/sys/class/dmi/id/sys_vendor" ? "QEMU" : null,
            containerName: name, parentMachine: "home-lab/linux-vm");
        var projected = Apply(Undeclared,
            [Report("review-1-runner", "review-1", "review", Telemetry(InVm("review-host-1"))),
             Report("review-2-runner", "review-2", "review", Telemetry(InVm("review-host-2"))),
             Report("gate-runner", "gate-1", "gate", Telemetry(InVm("gate-host"))),
             Report("old-runner", "old-host")]);

        var physical = projected.Nodes.Single(node => node.Id == "machine:home-lab");
        Assert.Equal(("physical", "installation", "observed"), (physical.Kind, physical.ParentId, physical.Provenance));
        var vm = projected.Nodes.Single(node => node.Id == "machine:linux-vm");
        Assert.Equal(("vm", "machine:home-lab"), (vm.Kind, vm.ParentId));
        foreach (var id in new[] { "machine:review-1", "machine:review-2", "machine:gate-1" })
        {
            var container = projected.Nodes.Single(node => node.Id == id);
            Assert.Equal(("container", "machine:linux-vm"), (container.Kind, container.ParentId));
            Assert.Equal("container", container.Facts!.MachineKind);
        }
        var first = projected.Nodes.Single(node => node.Id == "machine:review-1");
        Assert.Equal(("review-1", "review-host-1", "review-host-1"), (first.Label, first.Detail, first.Facts!.ContainerName));
        Assert.Equal("gate", projected.Nodes.Single(node => node.Id == "component:gate-1:gate-runner").Role);
        Assert.Equal("installation", projected.Nodes.Single(node => node.Id == "machine:old-host").ParentId);
        // Ids come from the reports, so a second projection of the same reports is identical.
        Assert.Equal(projected.Nodes.Select(node => node.Id), Apply(Undeclared,
            [Report("old-runner", "old-host"),
             Report("gate-runner", "gate-1", "gate", Telemetry(InVm("gate-host"))),
             Report("review-2-runner", "review-2", "review", Telemetry(InVm("review-host-2"))),
             Report("review-1-runner", "review-1", "review", Telemetry(InVm("review-host-1")))]).Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Studio_server_shows_its_components_and_the_daemons_that_report_from_it()
    {
        var apiPlacement = new HostPlacementDto(HostPlacementKinds.Container, KernelId: "kernel-a",
            MachineName: "0123456789ab", Virtualization: "docker", MachineVirtualization: "kvm");
        var studio = new StudioSelfObservation("studio-server", "Studio server", null, apiPlacement,
            new(Now, 12, 12, 1, 1, 1, 20L << 30, 62L << 30,
                [new("/", "instance data", 84, 16, 100)], 10, 20), "2.0", Now, Now.AddSeconds(-30));
        var daemon = new HostPlacementDto(HostPlacementKinds.Vm, KernelId: "kernel-a", MachineName: "studio",
            Virtualization: "kvm", MachineVirtualization: "kvm");
        var location = new StructureDataLocation("data:studio-server:workspace-repository", "workspace-repository",
            "Workspace repository", "TaskRepository", true, 1024, true, Now, true, true, "both");
        var projected = Apply(Undeclared,
            [Report("coding-runner", "runner-01", telemetry: Telemetry(daemon, roundTripMs: 4.5), containers:
                [new("stack-api-1", "sha256:api", "running", "0123456789ab"),
                 new("stack-frontend-1", "sha256:web", "running", "aaaaaaaaaaaa"),
                 new("stack-edge-1", "sha256:edge", "running", "bbbbbbbbbbbb"),
                 new("stack-orchestrator-engine-1", "sha256:engine", "running", "cccccccccccc"),
                 new("deploy-stage-api-1", "sha256:stage", "exited", "dddddddddddd")]),
             Report("review-runner", "runner-01", "review", Telemetry(daemon))],
            new(studio, [location]), engine: OrchestrationEngineReadiness.Ready);

        var machine = projected.Nodes.Single(node => node.Id == "machine:studio-server");
        Assert.Equal(("server", "installation", "observed"), (machine.Kind, machine.ParentId, machine.Provenance));
        Assert.Equal((HostPlacementKinds.Vm, "kvm"), (machine.Facts!.MachineKind, machine.Facts.Virtualization));
        Assert.Equal(12, machine.Facts!.Performance!.CpuCores);
        Assert.Equal(84, machine.Facts.Performance.Disks!.Single().UsedBytes);
        Assert.Equal(location, machine.Facts.DataLocations!.Single());

        var api = projected.Nodes.Single(node => node.Id == "component:studio-server:api");
        Assert.Equal(("container", "up", "2.0"), (api.Kind, api.Observation!.Health, api.Observation.Version));
        Assert.Contains("stack-api-1", api.Observation.Detail);
        var web = projected.Nodes.Single(node => node.Id == "component:studio-server:web");
        Assert.Equal(("up", "sha256:web", "stack-frontend-1"), (web.Observation!.Health, web.Observation.Version, web.Detail));
        Assert.Equal("up", projected.Nodes.Single(node => node.Id == "component:studio-server:edge").Observation!.Health);
        var engine = projected.Nodes.Single(node => node.Id == "component:studio-server:engine");
        Assert.Equal(("up", "sha256:engine"), (engine.Observation!.Health, engine.Observation.Version));

        // The leftover stage container is nobody's component: it shows as it is, on the machine.
        var stage = projected.Nodes.Single(node => node.Id == "observed-container:runner-01:deploy-stage-api-1");
        Assert.Equal(("machine:studio-server", "down"), (stage.ParentId, stage.Observation!.Health));
        Assert.DoesNotContain(projected.Nodes, node => node.Id == "observed-container:runner-01:stack-api-1");

        // Daemons on the Studio server's kernel are an agent host on that machine with both roles.
        var host = projected.Nodes.Single(node => node.Id == "machine:runner-01");
        Assert.Equal(("host", "machine:studio-server"), (host.Kind, host.ParentId));
        Assert.Equal(["coding", "review"], host.Facts!.Roles);
        Assert.Equal(["component:runner-01:coding-runner", "component:runner-01:review-runner"],
            projected.Nodes.Where(node => node.ParentId == "machine:runner-01").Select(node => node.Id).Order());

        var control = projected.Links.Single(link => link.From == "machine:runner-01");
        Assert.Equal("link:machine:runner-01>component:studio-server:api:control", control.Id);
        Assert.Equal(("control", "up", 4.5), (control.Kind, control.Facts!.State, control.Facts.LatencyMs));
    }

    [Fact]
    public void Studio_components_without_a_container_report_say_what_the_api_knows()
    {
        var studio = new StudioSelfObservation("studio-server", "Studio server", "cloud-server", null, null, "2.0", Now);
        var projected = Apply(Undeclared, [], new(studio), engine: OrchestrationEngineReadiness.NotLive);

        Assert.Equal("cloud-server", projected.Nodes.Single(node => node.Id == "machine:studio-server").Kind);
        Assert.Equal("up", projected.Nodes.Single(node => node.Id == "component:studio-server:api").Observation!.Health);
        Assert.Equal("down", projected.Nodes.Single(node => node.Id == "component:studio-server:engine").Observation!.Health);
        foreach (var role in new[] { "web", "edge" })
            Assert.Equal("not observed", projected.Nodes.Single(node => node.Id == $"component:studio-server:{role}").Observation!.Health);
    }

    [Fact]
    public void A_declared_studio_server_joins_what_the_api_and_the_container_report_know()
    {
        var placement = StructurePlacement.Parse("""
            {
              "machines": [{ "id": "studio", "name": "Studio machine", "kind": "cloud-server", "components": [
                { "id": "api", "name": "API", "role": "api", "kind": "container" },
                { "id": "web", "name": "Web frontend", "role": "web", "kind": "container" } ] }]
            }
            """);
        var declared = StructureProjection.Build(null, [], _ => null, null, null, placement: placement);
        var kernel = new HostPlacementDto(HostPlacementKinds.Vm, KernelId: "kernel-a", MachineVirtualization: "kvm");
        var studio = new StudioSelfObservation("studio", "Studio server", null,
            new(HostPlacementKinds.Container, KernelId: "kernel-a", MachineName: "0123456789ab"), null, "2.0", Now);
        var projected = Apply(declared,
            [Report("coding-runner", "runner-01", telemetry: Telemetry(kernel), containers:
                [new("stack-api-1", "sha256:api", "running", "0123456789ab"), new("stack-frontend-1", "sha256:web", "running", "aaaaaaaaaaaa")])],
            new(studio));

        var machine = projected.Nodes.Single(node => node.Id == "machine:studio");
        Assert.Equal(("declared", "cloud-server", "Studio machine", "matched", "up"),
            (machine.Provenance, machine.Kind, machine.Label, machine.Observation!.Difference, machine.Observation.Health));
        var web = projected.Nodes.Single(node => node.Id == "component:studio:web");
        Assert.Equal(("declared", "matched", "up", "sha256:web"),
            (web.Provenance, web.Observation!.Difference, web.Observation.Health, web.Observation.Version));
        Assert.Equal(("declared", "up"), (projected.Nodes.Single(node => node.Id == "component:studio:api").Provenance,
            projected.Nodes.Single(node => node.Id == "component:studio:api").Observation!.Health));
        // Edge and engine are not declared: they show beside the declared components, once.
        Assert.Equal(["component:studio:api", "component:studio:edge", "component:studio:engine", "component:studio:web", "machine:runner-01"],
            projected.Nodes.Where(node => node.ParentId == "machine:studio").Select(node => node.Id).Order());
        Assert.DoesNotContain(projected.Nodes, node => node.Id.StartsWith("observed-container:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_container_that_reports_as_a_host_is_placed_by_the_machine_that_lists_it()
    {
        var onMachine = new HostPlacementDto(HostPlacementKinds.Vm);
        var inContainer = new HostPlacementDto(HostPlacementKinds.Container, "review-host-1", MachineName: "aaaaaaaaaaaa");
        var projected = Apply(Undeclared,
            [Report("lister-runner", "lister", telemetry: Telemetry(onMachine), containers:
                [new("review-host-1", "sha256:review", "running", "aaaaaaaaaaaa"), new("sidecar", null, "running", "bbbbbbbbbbbb")]),
             Report("review-runner", "review-1", "review", Telemetry(inContainer), containers:
                [new("review-host-1", "sha256:review", "running", "aaaaaaaaaaaa")])]);

        Assert.Equal("machine:lister", projected.Nodes.Single(node => node.Id == "machine:review-1").ParentId);
        Assert.Equal(["observed-container:lister:sidecar"],
            projected.Nodes.Where(node => node.Id.StartsWith("observed-container:", StringComparison.Ordinal)).Select(node => node.Id));
    }

    [Fact]
    public void Declared_and_observed_nodes_join_by_host_id()
    {
        var placement = StructurePlacement.Parse("""
            {
              "machines": [
                { "id": "home-lab", "name": "Home lab machine", "kind": "physical", "location": "Home lab", "components": [] },
                { "id": "review-1", "name": "Review host", "kind": "vm", "parentId": "home-lab",
                  "components": [{ "id": "review-1-runner", "name": "Review daemon", "role": "review", "kind": "process" }] },
                { "id": "spare", "name": "Spare host", "components": [] }
              ],
              "externals": [{ "id": "source-host", "name": "Source host" }],
              "structureLinks": [
                { "from": "review-1", "to": "external:source-host", "kind": "call", "label": "Clone" },
                { "from": "extra", "to": "external:source-host", "kind": "call", "label": "Clone" }
              ]
            }
            """);
        var declared = StructureProjection.Build(null, [], _ => null, null, null, placement: placement);
        var projected = Apply(declared,
            [Report("review-1-runner", "review-1", "review", Telemetry(activeSlots: 2), slots: 4),
             Report("extra-runner", "extra")]);

        var joined = projected.Nodes.Single(node => node.Id == "machine:review-1");
        Assert.Equal(("declared", "vm", "machine:home-lab", "Review host"),
            (joined.Provenance, joined.Kind, joined.ParentId, joined.Label));
        Assert.Equal("matched", joined.Observation!.Difference);
        Assert.Equal(new StructureCapacityFacts(2, 2, 4, []), joined.Facts!.Capacity, CapacityComparer.Instance);
        // The declared component carries the runner; no second node appears for it.
        Assert.Equal("matched", projected.Nodes.Single(node => node.Id == "component:review-1:review-1-runner").Observation!.Difference);
        Assert.Single(projected.Nodes, node => node.UnitId == "review-1-runner");

        var spare = projected.Nodes.Single(node => node.Id == "machine:spare");
        Assert.Equal(("declared-only", "not observed"), (spare.Observation!.Difference, spare.Observation.Health));
        Assert.Null(spare.Facts);
        Assert.Equal(("declared-only", "not observed"),
            (projected.Nodes.Single(node => node.Id == "machine:home-lab").Observation!.Difference,
             projected.Nodes.Single(node => node.Id == "machine:home-lab").Observation!.Health));

        var extra = projected.Nodes.Single(node => node.Id == "machine:extra");
        Assert.Equal(("observed", "undeclared"), (extra.Provenance, extra.Observation!.Difference));
        // A declared link that ends at a host which only reports resolves against the observed node.
        Assert.Equal(["link:machine:extra>external:source-host:call", "link:machine:review-1>external:source-host:call"],
            projected.Links.Select(link => link.Id).Order());
    }

    [Fact]
    public void Facts_carry_performance_network_slots_release_and_what_a_warnings_layer_needs()
    {
        var down = new RunnerLinkResource("review-runner", "reverse-ssh", RunnerLinkStates.Down, Now.AddHours(-1), Now,
            new(Now, "heartbeat", false, 255, "refused"), "refused", 3, null, null, null, null, null, null, null, "no-route");
        var studio = new StudioSelfObservation("studio-server", "Studio server", null, null, null, "2.0", Now);
        var projected = Apply(Undeclared,
            [Report("review-runner", "review-host", "review", Telemetry(activeSlots: 1), version: "1.0", commit: "old",
                slots: 2, clis: [new("claude", "1.2.0", "/usr/bin/claude", Now, "1.3.0", true), new("codex", "0.9.0", "/usr/bin/codex", Now)],
                held: [new("toolchain:playwright", HeldCapabilityStates.Held, HeldCapabilityCauses.NotRegistered, null, Now, ["AGT-10"]),
                       new("provider-auth:claude", HeldCapabilityStates.Indeterminate, HeldCapabilityCauses.ProbeIndeterminate, null, Now, ["AGT-11"])],
                capabilities: [Capability("toolchain:dotnet", "10.0.100"), Capability("toolchain:node", "24.1.0", "degraded")])],
            new(studio, RunningCards: new Dictionary<string, IReadOnlyList<string>> { ["review-runner"] = ["AGT-7"] }),
            links: [down]);

        var facts = projected.Nodes.Single(node => node.Id == "machine:review-host").Facts!;
        Assert.Equal((37.5, 24, 2.5, 8L << 30, 32L << 30), (facts.Performance!.CpuPercent, facts.Performance.CpuCores,
            facts.Performance.Load1, facts.Performance.MemoryUsedBytes, facts.Performance.MemoryTotalBytes));
        Assert.Equal(new StructureDiskFact("/srv/work", "runner work", 60L << 30, 40L << 30, 100L << 30), facts.Performance.Disks!.Single());
        Assert.Equal((1200L, 3400L), (facts.Performance.NetworkReceiveBytesPerSecond, facts.Performance.NetworkTransmitBytesPerSecond));
        Assert.Equal(new StructureCapacityFacts(1, 1, 2, ["AGT-7"]), facts.Capacity, CapacityComparer.Instance);
        Assert.Equal(("1.0", "2.0", HostReleaseDriftStates.Behind, true), (facts.Release!.Version, facts.Release.StableVersion,
            facts.Release.State, facts.Release.Behind));
        Assert.Equal(Now, facts.LastSeenAt);

        var warnings = facts.Warnings!;
        Assert.Equal(
            [new("claude", "cli", "1.2.0", "1.3.0", "behind"), new("codex", "cli", "0.9.0", null, "ok"),
             new("dotnet", "toolchain", "10.0.100", null, "ok"), new("node", "toolchain", "24.1.0", null, "unhealthy"),
             new StructureToolFact("playwright", "toolchain", null, null, "missing")], warnings.Tools);
        Assert.Equal(["toolchain:playwright"], warnings.MissingRequired);
        Assert.Equal(["claude"], warnings.BehindRequired);
        Assert.True(warnings.ReleaseBehind);
        Assert.Equal([new("AGT-10", "refused", "toolchain:playwright"), new StructureBlockedCard("AGT-11", "parked", "provider-auth:claude")],
            warnings.BlockedCards);
        var link = projected.Links.Single(item => item.Kind == "reverse-ssh");
        Assert.Equal(("link:machine:studio-server>machine:review-host:reverse-ssh", "down", "no-route"),
            (link.Id, link.Facts!.State, link.Facts.Transport));
        Assert.Equal([link.Id!], warnings.LinksDown);
        // The role node carries its own facts, without the machine's performance.
        var role = projected.Nodes.Single(node => node.Id == "component:review-host:review-runner").Facts!;
        Assert.Null(role.Performance);
        Assert.Equal(["AGT-7"], role.Capacity!.RunningCards);
    }

    [Fact]
    public void A_host_that_stopped_reporting_stays_visible_as_stale()
    {
        var projected = Apply(Undeclared, [Report("coding-runner", "coding-host", seen: Now.AddMinutes(-10))]);
        var host = projected.Nodes.Single(node => node.Id == "machine:coding-host");
        Assert.Equal(("observed", "stale", "stale"), (host.Provenance, host.Observation!.Difference, host.Observation.Health));
        Assert.Equal(Now.AddMinutes(-10), host.Facts!.LastSeenAt);
    }

    [Fact]
    public void Delta_names_only_what_changed()
    {
        var before = Apply(Undeclared, [Report("a-runner", "a", telemetry: Telemetry()), Report("b-runner", "b")]) with { Revision = 7 };
        var after = Apply(Undeclared, [Report("a-runner", "a", telemetry: Telemetry(activeSlots: 2)), Report("c-runner", "c")]) with { Revision = 8 };

        var delta = StructureDelta.Between("workspace", null, before, after);
        Assert.Equal((7L, 8L), (delta.BaseRevision, delta.Revision));
        Assert.Equal(["component:a:a-runner", "component:c:c-runner", "machine:a", "machine:c"], delta.Nodes.Select(node => node.Id).Order());
        Assert.Equal(["component:b:b-runner", "machine:b"], delta.RemovedNodes.Order());
        Assert.Empty(delta.Links);
        Assert.True(StructureDelta.Between("workspace", null, before, before with { Revision = 8 }).IsEmpty);
    }

    private sealed class CapacityComparer : IEqualityComparer<StructureCapacityFacts?>
    {
        public static readonly CapacityComparer Instance = new();
        public bool Equals(StructureCapacityFacts? left, StructureCapacityFacts? right)
            => left is not null && right is not null && left.BusySlots == right.BusySlots && left.FreeSlots == right.FreeSlots
               && left.TotalSlots == right.TotalSlots && left.RunningCards.SequenceEqual(right.RunningCards);
        public int GetHashCode(StructureCapacityFacts? value) => value?.BusySlots ?? 0;
    }
}
