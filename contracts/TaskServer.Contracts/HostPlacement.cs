using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Where one reporting process runs, detected on the host itself (AGT-3954).
/// The structure view nests container, virtual machine and physical machine
/// from these reports, so no manifest has to declare the hosts that report.
/// </summary>
/// <param name="Kind">One of <see cref="HostPlacementKinds"/>.</param>
/// <param name="ContainerName">Configured or detected name of the container this process runs in.</param>
/// <param name="ParentMachineId">Configured id of the machine that directly contains this place.</param>
/// <param name="Ancestors">Configured enclosing machines, outermost first; the last one is the parent.</param>
/// <param name="Virtualization">Detected technology of this place, for example <c>kvm</c> or <c>docker</c>.</param>
/// <param name="MachineVirtualization">
/// Hypervisor of the machine whose kernel this process runs on, also when the
/// process itself runs in a container. Null on bare metal or when unknown.
/// </param>
/// <param name="KernelId">
/// Short digest of the kernel boot id. Every process on one kernel reports the
/// same value, so it joins reports of one machine. It changes on reboot and is
/// therefore never used as a node id.
/// </param>
/// <param name="MachineName">Host name as the reporting process sees it.</param>
public sealed record HostPlacementDto(
    string Kind,
    string? ContainerName = null,
    string? ParentMachineId = null,
    IReadOnlyList<HostPlacementAncestorDto>? Ancestors = null,
    string? Virtualization = null,
    string? KernelId = null,
    string? MachineName = null,
    string? MachineVirtualization = null);

/// <summary>One enclosing machine of a reporting host. <c>Kind</c> is <c>vm</c> or <c>physical</c>.</summary>
public sealed record HostPlacementAncestorDto(string Id, string Kind);

public static class HostPlacementKinds
{
    public const string Container = "container";
    public const string Vm = "vm";
    public const string Physical = "physical";
    public const string Unknown = "unknown";
}

/// <summary>
/// Pure placement detection over injected file reads. The runner adds the
/// answer of <c>systemd-detect-virt</c> when the host has it; without it the
/// container markers and the DMI identity decide.
/// </summary>
public static class HostPlacementProbe
{
    private static readonly string[] ContainerCgroupMarkers =
        ["docker", "kubepods", "containerd", "libpod", "lxc", "podman", "crio"];
    private static readonly string[] VirtualVendors =
        ["qemu", "kvm", "vmware", "virtualbox", "innotek", "xen", "microsoft corporation", "bochs",
         "parallels", "bhyve", "amazon ec2", "google", "digitalocean", "hetzner", "openstack", "virtual machine"];

    /// <param name="exists">Whether a marker file exists.</param>
    /// <param name="read">Reads a small text file, or returns null when it is absent or unreadable.</param>
    /// <param name="detectVirtVm">Answer of <c>systemd-detect-virt --vm</c>, when the host has the tool.</param>
    /// <param name="detectVirtContainer">Answer of <c>systemd-detect-virt --container</c>, when the host has the tool.</param>
    /// <param name="parentMachine">
    /// Configured enclosing machines as a slash-separated path, outermost first,
    /// for example <c>home-lab/linux-vm</c>. A segment may name its kind as
    /// <c>id=vm</c> or <c>id=physical</c>.
    /// </param>
    public static HostPlacementDto Detect(Func<string, bool> exists, Func<string, string?> read,
        string? detectVirtVm = null, string? detectVirtContainer = null, string? containerName = null,
        string? parentMachine = null, string? machineName = null)
    {
        var containerTool = Normalize(detectVirtContainer);
        var vmTool = Normalize(detectVirtVm);
        string? containerTechnology = containerTool;
        if (containerTechnology is null)
        {
            if (exists("/.dockerenv")) containerTechnology = "docker";
            else if (exists("/run/.containerenv")) containerTechnology = "podman";
            else if (read("/proc/1/cgroup") is { } cgroup)
                containerTechnology = ContainerCgroupMarkers.FirstOrDefault(marker =>
                    cgroup.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (containerTechnology is null && read("/proc/1/environ") is { } environ
                && environ.Split('\0').FirstOrDefault(item => item.StartsWith("container=", StringComparison.Ordinal))
                    is { Length: > 10 } declared)
                containerTechnology = declared[10..];
        }
        var vmTechnology = vmTool ?? DetectVm(read);
        var inContainer = containerTechnology is not null;
        var kind = inContainer ? HostPlacementKinds.Container
            : vmTechnology is not null ? HostPlacementKinds.Vm
            : read("/sys/class/dmi/id/sys_vendor") is not null || read("/sys/class/dmi/id/product_name") is not null
                ? HostPlacementKinds.Physical
                : HostPlacementKinds.Unknown;
        var ancestors = ParseAncestors(parentMachine, inContainer, vmTechnology is not null);
        var name = string.IsNullOrWhiteSpace(containerName) ? null : containerName.Trim();
        return new HostPlacementDto(kind, inContainer ? name : null,
            ancestors.Count > 0 ? ancestors[^1].Id : null, ancestors.Count > 0 ? ancestors : null,
            inContainer ? containerTechnology : vmTechnology, KernelId(read("/proc/sys/kernel/random/boot_id")),
            string.IsNullOrWhiteSpace(machineName) ? null : machineName.Trim(), vmTechnology);
    }

    /// <summary>The real host: marker files under <c>/</c>, <c>/proc</c> and <c>/sys</c>.</summary>
    public static HostPlacementDto DetectLocal(string? detectVirtVm = null, string? detectVirtContainer = null,
        string? containerName = null, string? parentMachine = null, string? machineName = null)
        => Detect(File.Exists, ReadSmall, detectVirtVm, detectVirtContainer, containerName, parentMachine,
            machineName ?? Environment.MachineName);

    /// <summary>
    /// Enclosing machines from the configured path. Without an explicit kind the
    /// outermost machine is physical and every inner one a virtual machine; a
    /// single parent of a place that is not virtualized is physical.
    /// </summary>
    public static IReadOnlyList<HostPlacementAncestorDto> ParseAncestors(string? parentMachine,
        bool inContainer, bool virtualized)
    {
        var segments = (parentMachine ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<HostPlacementAncestorDto>(segments.Length);
        for (var index = 0; index < segments.Length; index++)
        {
            var parts = segments[index].Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts[0].Length == 0 || result.Any(item => string.Equals(item.Id, parts[0], StringComparison.OrdinalIgnoreCase)))
                continue;
            var explicitKind = parts.Length == 2 && parts[1] is HostPlacementKinds.Vm or HostPlacementKinds.Physical
                ? parts[1] : null;
            var last = index == segments.Length - 1;
            // A lone parent of a container on a virtualized kernel is that virtual machine.
            var inferred = segments.Length == 1
                ? inContainer && virtualized ? HostPlacementKinds.Vm : HostPlacementKinds.Physical
                : index == 0 ? HostPlacementKinds.Physical
                : last && !inContainer ? HostPlacementKinds.Physical : HostPlacementKinds.Vm;
            result.Add(new HostPlacementAncestorDto(parts[0], explicitKind ?? inferred));
        }
        return result;
    }

    private static string? DetectVm(Func<string, string?> read)
    {
        if (read("/sys/hypervisor/type")?.Trim() is { Length: > 0 } hypervisor) return hypervisor.ToLowerInvariant();
        var identity = string.Join(' ', new[] { "/sys/class/dmi/id/sys_vendor", "/sys/class/dmi/id/product_name" }
            .Select(read).Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        if (VirtualVendors.FirstOrDefault(identity.Contains) is { } vendor)
            return vendor is "qemu" or "bochs" or "hetzner" or "digitalocean" or "openstack" or "google" ? "kvm"
                : vendor is "microsoft corporation" or "virtual machine" ? "microsoft"
                : vendor is "innotek" or "virtualbox" ? "oracle"
                : vendor is "amazon ec2" ? "amazon" : vendor;
        return read("/proc/cpuinfo") is { } cpu && cpu.Split('\n').Any(line =>
            line.StartsWith("flags", StringComparison.Ordinal) && line.Contains(" hypervisor", StringComparison.Ordinal))
            ? "vm" : null;
    }

    private static string? Normalize(string? tool)
        => tool?.Trim().ToLowerInvariant() is { Length: > 0 } value && value != "none" ? value : null;

    /// <summary>Twelve hex characters of the SHA-256 of the kernel boot id.</summary>
    public static string? KernelId(string? bootId)
        => string.IsNullOrWhiteSpace(bootId) ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bootId.Trim())))[..12];

    private static string? ReadSmall(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[16 * 1024];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }
}

/// <summary>Counters of every network interface except loopback, read from <c>/proc/net/dev</c>.</summary>
public readonly record struct HostNetworkCounters(long ReceiveBytes, long TransmitBytes)
{
    public static HostNetworkCounters? Parse(IEnumerable<string> lines)
    {
        long receive = 0, transmit = 0;
        var found = false;
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var name = line[..separator].Trim();
            if (name is "lo" or "") continue;
            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9
                || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx)
                || !long.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx)) continue;
            receive += rx;
            transmit += tx;
            found = true;
        }
        return found ? new HostNetworkCounters(receive, transmit) : null;
    }

    /// <summary>Bytes per second between two readings; null when the counters went backwards or no time passed.</summary>
    public static (long Receive, long Transmit)? Rate(HostNetworkCounters previous, DateTime previousAt,
        HostNetworkCounters current, DateTime currentAt)
    {
        var seconds = (currentAt - previousAt).TotalSeconds;
        if (seconds <= 0 || current.ReceiveBytes < previous.ReceiveBytes
            || current.TransmitBytes < previous.TransmitBytes) return null;
        return ((long)((current.ReceiveBytes - previous.ReceiveBytes) / seconds),
            (long)((current.TransmitBytes - previous.TransmitBytes) / seconds));
    }
}
