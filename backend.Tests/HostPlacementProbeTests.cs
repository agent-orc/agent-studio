using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3954: the kind of place a host runs in, detected from markers on the host.</summary>
public sealed class HostPlacementProbeTests
{
    private static Func<string, string?> Files(params (string Path, string Content)[] files)
        => path => files.FirstOrDefault(file => file.Path == path).Content;

    [Fact]
    public void Container_markers_win_and_the_hypervisor_of_the_machine_is_still_reported()
    {
        var placement = HostPlacementProbe.Detect(path => path == "/.dockerenv",
            Files(("/sys/class/dmi/id/sys_vendor", "QEMU\n"), ("/proc/sys/kernel/random/boot_id", "boot-one\n")),
            containerName: " review-host-1 ", parentMachine: "home-lab/linux-vm", machineName: "abc123");

        Assert.Equal(HostPlacementKinds.Container, placement.Kind);
        Assert.Equal(("docker", "kvm", "review-host-1", "abc123"),
            (placement.Virtualization, placement.MachineVirtualization, placement.ContainerName, placement.MachineName));
        Assert.Equal("linux-vm", placement.ParentMachineId);
        Assert.Equal([new("home-lab", HostPlacementKinds.Physical), new HostPlacementAncestorDto("linux-vm", HostPlacementKinds.Vm)],
            placement.Ancestors);
        Assert.Equal(12, placement.KernelId!.Length);
        Assert.DoesNotContain("boot-one", placement.KernelId);
    }

    [Theory]
    [InlineData("kvm", null, HostPlacementKinds.Vm, "kvm")]
    [InlineData("none", "podman", HostPlacementKinds.Container, "podman")]
    [InlineData("none", "none", HostPlacementKinds.Physical, null)]
    public void The_host_tool_decides_when_it_answers(string vm, string? container, string kind, string? technology)
    {
        var placement = HostPlacementProbe.Detect(_ => false, Files(("/sys/class/dmi/id/sys_vendor", "Board maker")), vm, container);
        Assert.Equal((kind, technology), (placement.Kind, placement.Virtualization));
        Assert.Null(placement.ContainerName);
    }

    [Fact]
    public void Markers_alone_tell_container_vm_physical_and_unknown_apart()
    {
        Assert.Equal(HostPlacementKinds.Container, HostPlacementProbe.Detect(_ => false,
            Files(("/proc/1/cgroup", "0::/system.slice/docker-abc.scope"))).Kind);
        Assert.Equal(HostPlacementKinds.Vm, HostPlacementProbe.Detect(_ => false,
            Files(("/sys/class/dmi/id/product_name", "VMware Virtual Platform"))).Kind);
        Assert.Equal(HostPlacementKinds.Vm, HostPlacementProbe.Detect(_ => false,
            Files(("/proc/cpuinfo", "flags\t\t: fpu vme hypervisor lm\n"))).Kind);
        Assert.Equal(HostPlacementKinds.Physical, HostPlacementProbe.Detect(_ => false,
            Files(("/sys/class/dmi/id/sys_vendor", "Board maker"), ("/proc/cpuinfo", "flags\t\t: fpu vme lm\n"))).Kind);
        var unknown = HostPlacementProbe.Detect(_ => false, _ => null);
        Assert.Equal((HostPlacementKinds.Unknown, (string?)null, (string?)null), (unknown.Kind, unknown.ParentMachineId, unknown.KernelId));
    }

    [Fact]
    public void Enclosing_machines_take_an_explicit_kind_or_the_position_in_the_path()
    {
        Assert.Equal([new HostPlacementAncestorDto("linux-vm", HostPlacementKinds.Vm)],
            HostPlacementProbe.ParseAncestors("linux-vm", inContainer: true, virtualized: true));
        Assert.Equal([new HostPlacementAncestorDto("rack", HostPlacementKinds.Physical)],
            HostPlacementProbe.ParseAncestors("rack", inContainer: true, virtualized: false));
        Assert.Equal([new HostPlacementAncestorDto("rack", HostPlacementKinds.Physical)],
            HostPlacementProbe.ParseAncestors(" rack ", inContainer: false, virtualized: true));
        Assert.Equal([new("rack", HostPlacementKinds.Physical), new("outer", HostPlacementKinds.Vm), new HostPlacementAncestorDto("inner", HostPlacementKinds.Vm)],
            HostPlacementProbe.ParseAncestors("rack/outer/inner", inContainer: true, virtualized: true));
        Assert.Equal([new("cloud", HostPlacementKinds.Vm), new HostPlacementAncestorDto("guest", HostPlacementKinds.Vm)],
            HostPlacementProbe.ParseAncestors("cloud=vm/guest/cloud", inContainer: true, virtualized: true));
        Assert.Empty(HostPlacementProbe.ParseAncestors(null, inContainer: false, virtualized: false));
    }

    [Fact]
    public void Network_counters_skip_loopback_and_give_a_rate_only_for_growing_counters()
    {
        var first = HostNetworkCounters.Parse(
        [
            "Inter-|   Receive                                                |  Transmit",
            " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed",
            "    lo: 900 9 0 0 0 0 0 0 900 9 0 0 0 0 0 0",
            "  eth0: 1000 10 0 0 0 0 0 0 4000 40 0 0 0 0 0 0",
            "  eth1: 500 5 0 0 0 0 0 0 100 1 0 0 0 0 0 0",
        ])!.Value;
        Assert.Equal(new HostNetworkCounters(1500, 4100), first);
        // clock-independent: two fixed readings ten seconds apart; the rate never meets now.
        var at = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal((150L, 590L), HostNetworkCounters.Rate(first, at, new(3000, 10000), at.AddSeconds(10)));
        Assert.Null(HostNetworkCounters.Rate(first, at, new(10, 10), at.AddSeconds(10)));
        Assert.Null(HostNetworkCounters.Rate(first, at, new(3000, 10000), at));
        Assert.Null(HostNetworkCounters.Parse(["    lo: 900 9 0 0 0 0 0 0 900 9 0 0 0 0 0 0"]));
    }
}
