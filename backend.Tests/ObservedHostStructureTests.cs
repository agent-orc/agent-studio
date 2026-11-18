using AgentStudio.Management;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3954: the structure shows every host that reports, nested from the
/// hosts' own machine reports, joined by id with what a placement file
/// declares, and changes reach views as small deltas. Role-named fixtures only.
/// </summary>
public sealed class ObservedHostStructureTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private const string StudioKernel = "kernel-studio-01";
    private const string LabKernel = "kernel-lab-0001";

    private static StructureResponse Declared(StructurePlacement? placement = null)
        => StructureProjection.Build(placement ?? StructurePlacement.Empty, null, [],
            _ => Array.Empty<EnvironmentDeclarationFile>(), null, null);

    private static RunnerCapabilitySnapshotDto Report(string runnerId, string hostId, string executor,
        HostMachineDto? machine = null, DateTime? seen = null, double? cpu = 10,
        IReadOnlyList<AdvertisedCapabilityStub>? capabilities = null, IReadOnlyList<HeldCapabilityDto>? held = null,
        string connection = "reachable", HostCliUpdateDto? update = null, RunnerReleaseIdentityDto? release = null)
    {
        var at = seen ?? Now;
        var telemetry = new HostTelemetrySnapshotDto(at, cpu, 1.5, 1.2, 1.0, 8L << 30, 32L << 30, null, null, null,
            null, 24, 1, TaskServerConnectionStatus: connection, TaskServerConnectionObservedAt: at,
            Filesystems: [new("runner work", "/", 16L << 30, 100L << 30)],
            Machine: machine, Network: new(at, 2048, 1024, 10, 20));
        return new(runnerId, runnerId, hostId, "instance", "1.0", 1, "active", Now.AddHours(-1), at,
            new(hostId, "open", null, null, null, null),
            [Capability(executor), .. (capabilities ?? []).Select(item => Capability(item.Key, item.Status, item.Version))],
            telemetry, RoleMaxParallelism: 3, Release: release ?? new("release-1", "1.0"),
            HeldCapabilities: held, CliUpdate: update);
    }

    private sealed record AdvertisedCapabilityStub(string Key, string Status = "ready", string? Version = null);

    private static CapabilityHealthDto Capability(string key, string status = "ready", string? version = null)
        => new(key, key.Split(':')[0], status, CapabilityHealthStates.Healthy, null, Now, Now.AddMinutes(3), true,
            null, null, null, null, 0, version, null, null, [], []);

    private static HostMachineDto Container(string name, string? parent = null, string kernel = LabKernel)
        => new(HostMachineKinds.Container, "kvm", "docker", name, parent, kernel);

    private static StudioSelfFacts Self(string kernel = StudioKernel, string? configured = null) => new(
        new(HostMachineKinds.Container, "kvm", "docker", null, null, kernel), "0123456789ab", configured, "2.0",
        Now.AddHours(-2), null,
        [new("workspace-repository", "Workspace repository", 1L << 30, true, true, Now, null,
             new("workspace-repository", "/data", 84L << 30, 16L << 30, 100L << 30)),
         new("secrets-key", "Secrets key", 32, false, null, Now, "Excluded from backup archives by design; keep its escrow copy.", null)],
        new(Now.AddMinutes(-1), Now.AddMinutes(-1), Now.AddMinutes(-1)),
        new(OrchestrationEngineReadiness.Ready, true, Now.AddSeconds(-5), "engine-1"));

    /// <summary>The live installation the owner described: Studio server, a home-lab VM with four containers.</summary>
    private static StructureHostFacts LiveInstallation(StudioSelfFacts? self = null) => new(
        [
            Report("coding-studio", "studio-host", CapabilityProtocol.CodingExecutor,
                new(HostMachineKinds.Vm, "kvm", null, null, null, StudioKernel),
                capabilities: [new("toolchain:dotnet", Version: "9.0.100"), new("cli-execution:claude", Version: "2.1.0")]),
            Report("review-studio", "studio-host", CapabilityProtocol.ReviewExecutor,
                new(HostMachineKinds.Vm, "kvm", null, null, null, StudioKernel)),
            .. Enumerable.Range(1, 3).Select(n => Report($"review-{n}", $"review-host-{n}", CapabilityProtocol.ReviewExecutor,
                Container($"review-host-{n}", "lab-vm"))),
            Report("gate-1", "gate-host-1", GateCapabilities.Executor, Container("gate-host-1", "lab-vm")),
        ],
        [], [], null, self ?? Self());

    private static StructureResponse Project(StructureResponse declared, StructureHostFacts facts, DateTime? now = null)
        => ObservedStructureProjection.Apply(declared, facts, [], [], OrchestrationEngineReadiness.Ready, now ?? Now);

    [Fact]
    public void Every_reporting_host_becomes_an_observed_node_with_a_stable_id()
    {
        var result = Project(Declared(), LiveInstallation());
        var byId = result.Nodes.ToDictionary(node => node.Id);

        var studio = byId["machine:studio-host"];
        Assert.Equal("vm", studio.Kind);
        Assert.Equal("observed", studio.Provenance);
        Assert.Equal("undeclared", studio.Observation!.Difference);
        Assert.Equal("up", studio.Observation.Health);
        Assert.Equal("installation", studio.ParentId);
        Assert.Equal("machine:studio-host", byId["runner:coding-studio"].ParentId);
        Assert.Equal("machine:studio-host", byId["runner:review-studio"].ParentId);
        Assert.Equal("Review runner", byId["runner:review-studio"].Label);
        // The API shares the kernel of the machine the runner daemons report, so its components join that machine.
        foreach (var component in new[] { "api", "web", "edge", "engine" })
            Assert.Equal("machine:studio-host", byId[$"component:studio-host:{component}"].ParentId);
        Assert.Equal("up", byId["component:studio-host:engine"].Observation!.Health);
        Assert.Equal("container", byId["component:studio-host:api"].Kind);
        Assert.Equal("external", byId["external:cdn"].Kind);

        for (var n = 1; n <= 3; n++)
        {
            var review = byId[$"machine:review-host-{n}"];
            Assert.Equal("container", review.Kind);
            Assert.Equal("machine:lab-vm", review.ParentId);
            Assert.Equal($"review-host-{n}", review.Facts!.Machine!.ContainerName);
            Assert.Equal($"machine:review-host-{n}", byId[$"runner:review-{n}"].ParentId);
        }
        Assert.Equal("machine:lab-vm", byId["machine:gate-host-1"].ParentId);
        Assert.Equal("Gate runner", byId["runner:gate-1"].Label);
        var vm = byId["machine:lab-vm"];
        Assert.Equal("vm", vm.Kind);
        Assert.Equal("undeclared", vm.Observation!.Difference);

        // Stable across rebuilds and restarts: ids depend on the reports only.
        var again = Project(Declared(), LiveInstallation());
        Assert.Equal(result.Nodes.Select(node => node.Id), again.Nodes.Select(node => node.Id));
        Assert.Equal(result.Links.Select(link => link.Id), again.Links.Select(link => link.Id));
        Assert.All(result.Links, link => Assert.StartsWith("link:", link.Id));
    }

    [Fact]
    public void Hosts_without_a_parent_id_nest_by_shared_kernel_and_older_hosts_stay_flat()
    {
        var facts = new StructureHostFacts(
        [
            Report("coding-lab", "lab-host", CapabilityProtocol.CodingExecutor,
                new(HostMachineKinds.Vm, "kvm", null, null, null, LabKernel)),
            Report("review-a", "review-a", CapabilityProtocol.ReviewExecutor, Container("review-a")),
            Report("review-b", "review-b", CapabilityProtocol.ReviewExecutor, Container("review-b", kernel: "kernel-other")),
            Report("review-c", "review-c", CapabilityProtocol.ReviewExecutor, Container("review-c", kernel: "kernel-other")),
            Report("legacy", "legacy-host", CapabilityProtocol.CodingExecutor) with { Telemetry = null },
        ], [], [], null);
        var byId = Project(Declared(), facts).Nodes.ToDictionary(node => node.Id);

        Assert.Equal("machine:lab-host", byId["machine:review-a"].ParentId);
        Assert.Equal("machine:kernel-kernel-other", byId["machine:review-b"].ParentId);
        Assert.Equal("machine:kernel-kernel-other", byId["machine:review-c"].ParentId);
        Assert.Equal("Machine (not declared)", byId["machine:kernel-kernel-other"].Label);
        var legacy = byId["machine:legacy-host"];
        Assert.Equal("host", legacy.Kind);
        Assert.Equal("installation", legacy.ParentId);
        Assert.Null(legacy.Facts!.Machine);
        Assert.Equal("runner:legacy", byId["runner:legacy"].Id);
    }

    [Fact]
    public void Declared_placement_joins_observed_hosts_by_host_id_and_unreported_machines_stay_not_observed()
    {
        var file = StructurePlacementFile.Parse("""
            {
              "machines": [
                { "id": "lab", "name": "Home-lab machine", "kind": "physical", "components": [] },
                { "id": "lab-vm", "name": "Home-lab VM", "kind": "vm", "parentId": "lab", "components": [] },
                { "id": "review-host-1", "name": "Review host 1", "kind": "container", "parentId": "lab-vm", "components": [] },
                { "id": "spare", "name": "Spare machine", "kind": "physical", "components": [] }
              ],
              "externals": [ { "id": "github", "name": "GitHub" } ],
              "structureLinks": [ { "from": "studio-host", "to": "lab-vm", "kind": "tunnel", "label": "Gate link" } ]
            }
            """);
        Assert.Empty(StructurePlacementLoader.Validate(file));
        var result = Project(Declared(StructurePlacementLoader.Merge(file, null)), LiveInstallation());
        var byId = result.Nodes.ToDictionary(node => node.Id);

        Assert.Single(result.Nodes, node => node.Id == "machine:review-host-1");
        var review = byId["machine:review-host-1"];
        Assert.Equal("declared", review.Provenance);
        Assert.Equal("matched", review.Observation!.Difference);
        Assert.Equal("Structure:PlacementPath#machines/review-host-1", review.SourceRef);
        Assert.Equal("review-host-1", review.Facts!.HostId);
        Assert.Equal("machine:lab-vm", byId["machine:review-host-2"].ParentId);
        Assert.Equal("declared", byId["machine:lab-vm"].Provenance);
        Assert.Equal("machine:lab", byId["machine:lab-vm"].ParentId);
        Assert.Equal("declared-only", byId["machine:spare"].Observation!.Difference);
        Assert.Equal("not observed", byId["machine:spare"].Observation!.Health);
        Assert.Contains(result.Links, link => link.From == "machine:studio-host" && link.To == "machine:lab-vm"
                                              && link.SourceRef.StartsWith("Structure:PlacementPath", StringComparison.Ordinal));
    }

    [Fact]
    public void The_placement_file_never_feeds_connectivity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atp-structure-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var placementPath = Path.Combine(directory, "placement.json");
            // Connectivity-shaped fields in a placement file are ignored, never resolved.
            File.WriteAllText(placementPath, """
                { "machines": [ { "id": "lab-vm", "name": "Home-lab VM", "kind": "vm", "components": [] } ],
                  "enabledListeners": ["legacy-api"], "runners": [ { "runnerId": "r", "route": "reverse-ssh" } ] }
                """);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StructurePlacementLoader.PathKey] = placementPath,
                ["TaskRepository"] = directory,
            }).Build();

            Assert.Same(configuration, ConnectivityManifestLoader.RunnerLinkSource(configuration));
            Assert.Empty(configuration.GetSection("RunnerLinks").GetChildren());
            var read = StructurePlacementLoader.Read(configuration, null);
            Assert.Empty(read.Findings);
            Assert.Equal("lab-vm", Assert.Single(read.Placement.Machines).Id);

            // With a manifest, runner links come from the manifest alone, whether a placement file is set or not.
            var manifestPath = Path.Combine(directory, "connectivity.json");
            File.WriteAllText(manifestPath, $$"""
                { "schemaVersion": 1, "installationId": "installation", "revision": 1,
                  "mode": "reverse-ssh-transition", "package": "legacy-backend",
                  "serverOrigin": "http://127.0.0.1:5031", "browserOrigin": "http://127.0.0.1:4011",
                  "linkOwner": "link-supervisor", "enabledListeners": ["legacy-api", "runner-tunnel", "browser"],
                  "runners": [ { "runnerId": "review-runner", "route": "reverse-ssh", "sshTarget": "review-host",
                                 "knownHostsFile": "{{Path.Combine(directory, "known_hosts").Replace("\\", "/")}}" } ],
                  "machines": [ { "id": "manifest-machine", "name": "Manifest machine", "components": [] } ] }
                """);
            IReadOnlyDictionary<string, string?> Links(bool withPlacement) => ConnectivityManifestLoader.RunnerLinkSource(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ConnectivityManifestLoader.ManifestPathKey] = manifestPath,
                        [StructurePlacementLoader.PathKey] = withPlacement ? placementPath : null,
                        ["TaskRepository"] = directory,
                    }).Build())
                .AsEnumerable().Where(item => item.Key.StartsWith("RunnerLinks", StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value);
            Assert.Equal(Links(withPlacement: false), Links(withPlacement: true));
            Assert.Equal("review-runner", Links(withPlacement: true)["RunnerLinks:0:RunnerId"]);
            var manifest = InstallationConnectivityManifest.Parse(File.ReadAllText(manifestPath));
            var merged = StructurePlacementLoader.Read(configuration, manifest);
            Assert.Equal(["lab-vm", "manifest-machine"], merged.Placement.Machines.Select(machine => machine.Id));

            File.WriteAllText(placementPath, "{ not json");
            var broken = StructurePlacementLoader.Read(configuration, null);
            Assert.Equal("placement-unreadable", Assert.Single(broken.Findings).Code);
            Assert.Empty(broken.Placement.Machines);

            File.WriteAllText(placementPath, """{ "machines": [ { "id": "a", "name": "A", "parentId": "a", "components": [] } ] }""");
            Assert.Contains(StructurePlacementLoader.Read(configuration, null).Findings,
                finding => finding.Code == "machine-parent");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void A_fact_update_is_a_small_delta_and_a_lost_host_removes_its_nodes_and_links()
    {
        var first = Project(Declared(), LiveInstallation()) with { Revision = 10, GeneratedAt = Now };
        var hosts = LiveInstallation().Hosts.Select(report => report.HostId == "review-host-2"
            ? report with { Telemetry = report.Telemetry! with { CpuPercent = 93 } } : report).ToArray();
        var second = Project(Declared(), LiveInstallation() with { Hosts = hosts }) with { Revision = 11, GeneratedAt = Now };

        var delta = StructureIdentity.Diff(first, second);
        Assert.Equal(10, delta.FromRevision);
        Assert.Equal(11, delta.Revision);
        Assert.Equal(["machine:review-host-2", "runner:review-2"], delta.UpsertNodes.Select(node => node.Id).Order());
        Assert.Empty(delta.RemoveNodes);
        Assert.Empty(delta.UpsertLinks);
        Assert.Equal(93, delta.UpsertNodes.Single(node => node.Id == "machine:review-host-2").Facts!.CpuPercent);

        var lost = Project(Declared(), LiveInstallation() with
            { Hosts = hosts.Where(report => report.HostId != "review-host-3").ToArray() }) with { Revision = 12 };
        var removal = StructureIdentity.Diff(second, lost);
        Assert.Equal(["machine:review-host-3", "runner:review-3"], removal.RemoveNodes.Order());
        Assert.Contains(removal.RemoveLinks, id => id.StartsWith("link:machine:review-host-3|", StringComparison.Ordinal));
        Assert.True(StructureIdentity.Diff(lost, lost with { Revision = 13 }).IsEmpty);
    }

    [Fact]
    public void Host_nodes_carry_performance_capability_and_data_facts()
    {
        var update = new HostCliUpdateDto("review-host-1", CliUpdateStates.Upgrading, "1.0", "2.2", Now, Now, 0, true);
        var facts = LiveInstallation() with
        {
            Hosts = LiveInstallation().Hosts.Select(report => report.RunnerId switch
            {
                "review-1" => report with
                {
                    Capabilities = [.. report.Capabilities, Capability("toolchain:playwright", "unavailable")],
                    HeldCapabilities = [new("toolchain:node", HeldCapabilityStates.Held, HeldCapabilityCauses.NotRegistered,
                        "Not registered", Now, ["AGT-7"])],
                    CliUpdate = update,
                    Release = new("release-0", "0.9"),
                    Telemetry = report.Telemetry! with { TaskServerConnectionStatus = "unreachable" },
                },
                _ => report,
            }).ToArray(),
            RunningCards = new Dictionary<string, IReadOnlyList<string>> { ["review-1"] = ["AGT-5"] },
            RefusedCards = new Dictionary<string, IReadOnlyList<string>> { ["review-1"] = ["AGT-8"] },
            Releases = new HostReleaseDriftSnapshot(Now, new("1.0", null, Now), 0, []),
        };
        var result = Project(Declared(), facts);
        var review = result.Nodes.Single(node => node.Id == "machine:review-host-1").Facts!;

        Assert.Equal(24, review.CpuCores);
        Assert.Equal(10, review.CpuPercent);
        Assert.Equal(1.5, review.Load1);
        Assert.Equal(8L << 30, review.MemoryUsedBytes);
        Assert.Equal(84L << 30, Assert.Single(review.Mounts!).UsedBytes);
        Assert.Equal(2048, review.NetworkReceiveBytesPerSecond);
        Assert.Equal(1, review.BusySlots);
        Assert.Equal(2, review.FreeSlots);
        Assert.Equal(["AGT-5"], review.RunningCards);
        Assert.Equal("0.9", review.Version);
        Assert.Equal("1.0", review.StableVersion);
        Assert.Equal(CliUpdateStates.Upgrading, review.PendingHostUpdate);
        Assert.Equal(Now, review.LastSeenAt);
        Assert.Contains(review.Requirements!, item => item.Key == "toolchain:node" && item.State == "missing"
                                                     && item.WaitingCards!.SequenceEqual(["AGT-7"]));
        Assert.Contains(review.Requirements!, item => item.Key == "toolchain:playwright" && item.State == "not-ready");
        Assert.Equal(["AGT-7", "AGT-8"], review.HeldCards);
        var route = Assert.Single(review.LinksDown!);
        Assert.Equal("down", result.Links.Single(link => link.Id == route).Observation!.Health);

        var coding = result.Nodes.Single(node => node.Id == "machine:studio-host").Facts!;
        Assert.Contains(coding.Tools!, tool => tool.Key == "toolchain:dotnet" && tool.Version == "9.0.100");
        Assert.Contains(coding.Tools!, tool => tool.Key == "cli-execution:claude" && tool.Version == "2.1.0");
        Assert.Equal(2, coding.Roles!.Count);
        var workspace = coding.DataLocations!.Single(location => location.Role == "workspace-repository");
        Assert.Equal("data:studio-host:workspace-repository", workspace.Id);
        Assert.True(workspace.BackupCovered);
        Assert.True(workspace.GitRemoteCovered);
        Assert.False(coding.DataLocations!.Single(location => location.Role == "secrets-key").BackupCovered);
    }

    [Fact]
    public void Without_a_reporting_host_the_studio_server_is_named_by_role_and_gate_heartbeats_still_show()
    {
        var facts = new StructureHostFacts([], [new PullGateHost("gate-host", "Gate host", 2, 8,
            new DateTimeOffset(Now), "AGT-9") { Busy = 1 }], [], null, Self());
        var byId = Project(Declared(), facts).Nodes.ToDictionary(node => node.Id);

        Assert.Equal("Studio server", byId["machine:studio-server"].Label);
        Assert.Equal("vm", byId["machine:studio-server"].Kind);
        Assert.Equal("machine:studio-server", byId["component:studio-server:api"].ParentId);
        Assert.Equal("host", byId["machine:gate-host"].Kind);
        Assert.Equal(1, byId["machine:gate-host"].Facts!.BusySlots);
        Assert.Equal(1, byId["machine:gate-host"].Facts!.FreeSlots);
        Assert.Equal(["AGT-9"], byId["gate:gate-host"].Facts!.RunningCards);

        var configured = Project(Declared(), facts with { Self = Self(configured: "control-plane") })
            .Nodes.ToDictionary(node => node.Id);
        Assert.Equal("machine:control-plane", configured["component:control-plane:api"].ParentId);
    }

    [Fact]
    public void The_machine_classifier_reads_container_vm_and_physical_markers()
    {
        var container = HostMachineClassifier.Classify(new(DockerEnvFile: true, DmiVendor: "QEMU",
            BootId: "boot", Hostname: "0123456789ab", ConfiguredParentMachineId: "lab-vm"));
        Assert.Equal(HostMachineKinds.Container, container.Kind);
        Assert.Equal("docker", container.ContainerRuntime);
        Assert.Equal("qemu", container.Virtualization);
        Assert.Equal("0123456789ab", container.ContainerName);
        Assert.Equal("lab-vm", container.ParentMachineId);
        Assert.Equal(16, container.KernelId!.Length);
        Assert.Equal(container.KernelId, HostMachineClassifier.KernelId("boot"));

        Assert.Equal("review-1", HostMachineClassifier.Classify(new(ContainerEnvFile: "engine=\"podman\"\nname=\"review-1\"\n"))
            .ContainerName);
        Assert.Equal(HostMachineKinds.Vm, HostMachineClassifier.Classify(new(DetectedVirtualization: "kvm", DetectedContainer: "none")).Kind);
        Assert.Equal(HostMachineKinds.Physical, HostMachineClassifier.Classify(new(DetectedVirtualization: "none")).Kind);
        Assert.Equal(HostMachineKinds.Physical, HostMachineClassifier.Classify(new(DmiVendor: "Example Systems", DmiProduct: "Board")).Kind);
        Assert.Equal(HostMachineKinds.Vm, HostMachineClassifier.Classify(new(DmiVendor: "Example", CpuHypervisorFlag: true)).Kind);
        Assert.Equal(HostMachineKinds.Unknown, HostMachineClassifier.Classify(new()).Kind);
        Assert.Equal(HostMachineKinds.Container, HostMachineClassifier.Classify(new(InitCgroup: "0::/kubepods/burstable/pod1")).Kind);
        Assert.Equal(HostMachineKinds.Physical, HostMachineClassifier.Classify(new(DockerEnvFile: true, ConfiguredKind: "physical")).Kind);

        var counters = HostNetworkCounters.Parse([
            "Inter-|   Receive", " face |bytes packets",
            "    lo: 100 1 0 0 0 0 0 0 100 1 0 0 0 0 0 0",
            "  eth0: 4000 10 0 0 0 0 0 0 2000 10 0 0 0 0 0 0",
            "veth12: 900 1 0 0 0 0 0 0 900 1 0 0 0 0 0 0"]);
        Assert.Equal((4000L, 2000L), counters);
        var rate = HostNetworkCounters.Rate((6000, 2500), Now, (4000, 2000, Now.AddSeconds(-10)))!;
        Assert.Equal(200, rate.ReceiveBytesPerSecond);
        Assert.Equal(50, rate.TransmitBytesPerSecond);
        Assert.Null(HostNetworkCounters.Rate((1, 1), Now, (4000, 2000, Now.AddSeconds(-10)))!.ReceiveBytesPerSecond);
    }
}
