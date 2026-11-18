using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// AGT-3954: where an agent host runs, as the host itself detected it. The
/// operations structure nests container, VM and physical machine from these
/// facts when no placement file declares them. Every field is optional on the
/// wire: an older host sends none and stays a flat node.
/// </summary>
/// <param name="Kind">One of <see cref="HostMachineKinds"/>.</param>
/// <param name="Virtualization">
/// The hypervisor under the machine (for example <c>kvm</c>), from
/// <c>systemd-detect-virt --vm</c> or the DMI vendor; <c>none</c> on bare metal.
/// A container reports the hypervisor of the machine that runs it.
/// </param>
/// <param name="ContainerRuntime">Container runtime marker (<c>docker</c>, <c>podman</c>, ...), container hosts only.</param>
/// <param name="ContainerName">The configured or runtime-reported container name, container hosts only.</param>
/// <param name="ParentMachineId">Operator-configured id of the machine this host runs in.</param>
/// <param name="KernelId">
/// Hash of the running kernel's boot id. Containers share it with the machine
/// that runs them, so hosts on one machine group without configuration. It
/// changes on reboot and is never a node id.
/// </param>
public sealed record HostMachineDto(
    string Kind,
    string? Virtualization = null,
    string? ContainerRuntime = null,
    string? ContainerName = null,
    string? ParentMachineId = null,
    string? KernelId = null);

public static class HostMachineKinds
{
    public const string Container = "container";
    public const string Vm = "vm";
    public const string Physical = "physical";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Container, Vm, Physical, Unknown];
}

/// <summary>Network throughput of the host's own interfaces between two samples.</summary>
public sealed record HostNetworkUsageDto(
    DateTime ObservedAt,
    long? ReceiveBytesPerSecond,
    long? TransmitBytesPerSecond,
    long ReceiveBytesTotal,
    long TransmitBytesTotal);

/// <summary>The raw signals a host collects to classify where it runs. Pure input to <see cref="HostMachineClassifier"/>.</summary>
public sealed record HostMachineEvidence(
    bool DockerEnvFile = false,
    string? ContainerEnvFile = null,
    string? ContainerVariable = null,
    string? InitCgroup = null,
    string? DetectedVirtualization = null,
    string? DetectedContainer = null,
    string? DmiVendor = null,
    string? DmiProduct = null,
    bool CpuHypervisorFlag = false,
    string? BootId = null,
    string? ConfiguredKind = null,
    string? ConfiguredContainerName = null,
    string? ConfiguredParentMachineId = null,
    string? Hostname = null);

/// <summary>Turns host signals into a <see cref="HostMachineDto"/>. No I/O; each host reads its own files.</summary>
public static class HostMachineClassifier
{
    private static readonly (string Marker, string Name)[] DmiHypervisors =
    [
        ("qemu", "qemu"), ("kvm", "kvm"), ("vmware", "vmware"), ("virtualbox", "oracle"),
        ("innotek", "oracle"), ("microsoft corporation", "microsoft"), ("hyper-v", "microsoft"),
        ("xen", "xen"), ("amazon ec2", "amazon"), ("google compute", "google"), ("bochs", "bochs"),
        ("parallels", "parallels"), ("bhyve", "bhyve"), ("hetzner", "kvm"), ("openstack", "kvm"),
    ];

    public static HostMachineDto Classify(HostMachineEvidence evidence)
    {
        var runtime = ContainerRuntime(evidence);
        var virtualization = Virtualization(evidence);
        var configured = Clean(evidence.ConfiguredKind)?.ToLowerInvariant();
        var kind = configured is not null && HostMachineKinds.All.Contains(configured)
            ? configured
            : runtime is not null ? HostMachineKinds.Container
            : virtualization is null ? HostMachineKinds.Unknown
            : virtualization == "none" ? HostMachineKinds.Physical
            : HostMachineKinds.Vm;
        var container = kind == HostMachineKinds.Container
            ? Clean(evidence.ConfiguredContainerName) ?? ContainerEnvName(evidence.ContainerEnvFile)
              ?? Clean(evidence.Hostname)
            : null;
        return new HostMachineDto(kind, virtualization,
            kind == HostMachineKinds.Container ? runtime ?? "container" : null,
            container, Clean(evidence.ConfiguredParentMachineId), KernelId(evidence.BootId));
    }

    /// <summary>A short, stable digest of the boot id. The raw id is not sent.</summary>
    public static string? KernelId(string? bootId)
        => Clean(bootId) is { } value
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16]
            : null;

    private static string? ContainerRuntime(HostMachineEvidence evidence)
    {
        if (Clean(evidence.DetectedContainer) is { } detected && detected != "none") return detected;
        if (Clean(evidence.ContainerVariable) is { } variable) return variable.ToLowerInvariant();
        if (evidence.ContainerEnvFile is not null) return "podman";
        if (evidence.DockerEnvFile) return "docker";
        var cgroup = evidence.InitCgroup ?? "";
        if (cgroup.Contains("kubepods", StringComparison.Ordinal)) return "kubernetes";
        if (cgroup.Contains("docker", StringComparison.Ordinal)) return "docker";
        if (cgroup.Contains("libpod", StringComparison.Ordinal)) return "podman";
        if (cgroup.Contains("containerd", StringComparison.Ordinal)) return "containerd";
        if (cgroup.Contains("/lxc", StringComparison.Ordinal)) return "lxc";
        return null;
    }

    private static string? Virtualization(HostMachineEvidence evidence)
    {
        if (Clean(evidence.DetectedVirtualization) is { } detected) return detected.ToLowerInvariant();
        var dmi = $"{evidence.DmiVendor} {evidence.DmiProduct}".ToLowerInvariant();
        foreach (var (marker, name) in DmiHypervisors)
            if (dmi.Contains(marker, StringComparison.Ordinal)) return name;
        if (evidence.CpuHypervisorFlag) return "vm-other";
        return string.IsNullOrWhiteSpace(evidence.DmiVendor) && string.IsNullOrWhiteSpace(evidence.DmiProduct)
            ? null : "none";
    }

    private static string? ContainerEnvName(string? containerEnv)
    {
        foreach (var line in (containerEnv ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("name=", StringComparison.Ordinal)) continue;
            return Clean(trimmed[5..].Trim('"'));
        }
        return null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Parses <c>/proc/net/dev</c>. Loopback and bridge or veth interfaces that mirror container traffic are left out.</summary>
public static class HostNetworkCounters
{
    private static readonly string[] VirtualPrefixes =
        ["lo", "veth", "docker", "br-", "virbr", "cni", "flannel", "cali", "vnet", "tap", "podman", "kube-"];

    public static (long Receive, long Transmit)? Parse(IEnumerable<string> lines)
    {
        long receive = 0, transmit = 0;
        var any = false;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (VirtualPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9
                || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx)
                || !long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx)) continue;
            receive += rx;
            transmit += tx;
            any = true;
        }
        return any ? (receive, transmit) : null;
    }

    /// <summary>Rate between two counter readings; a counter reset yields no rate.</summary>
    public static HostNetworkUsageDto? Rate((long Receive, long Transmit)? current, DateTime now,
        (long Receive, long Transmit, DateTime At)? previous)
    {
        if (current is not { } value) return null;
        long? Per(long now0, long before)
            => previous is { } p && now > p.At && now0 >= before
                ? (long)((now0 - before) / (now - p.At).TotalSeconds) : null;
        return new HostNetworkUsageDto(now, Per(value.Receive, previous?.Receive ?? 0),
            Per(value.Transmit, previous?.Transmit ?? 0), value.Receive, value.Transmit);
    }
}
