using System.Net;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>All members share the sampler, timestamp and expiry of this reading. Missing readings are null.</summary>
public sealed record HostStructureReading(DateTime ObservedAt, string ObservedBy, DateTime FreshUntil,
    string DisplayName, string Role, string? DmiProduct, IReadOnlyList<HostBlockDevice> Devices,
    CgroupReading? Service, IReadOnlyList<HostWorkerReading> Workers, IReadOnlyList<StudioContainerReading>? StudioContainers,
    IReadOnlyList<HostUnitReading> PostMergeUnits, int? SlotLimit = null, int? EffectiveSlotLimit = null);

public sealed record HostBlockDevice(string Device, long? SizeBytes, string? RaidLevel, IReadOnlyList<string> Members,
    IReadOnlyList<HostFilesystemUsageDto> Filesystems);
public sealed record CgroupReading(long? CpuUsageMicroseconds, long? MemoryBytes, double? CpuCoresUsed = null);
public sealed record HostWorkerReading(string? CardKey, CgroupReading? Resources);
public sealed record HostUnitReading(string Unit, CgroupReading Resources);
public sealed record StudioContainerReading(string Id, string? Role, CgroupReading Resources, double? UptimeSeconds,
    string? ImageTag, int? Restarts);

/// <summary>Class of the connection the receiving server saw. Stores no network address.</summary>
public sealed record HeartbeatPeer(string AddressClass, string Scheme, DateTime ObservedAt)
{
    public static CapabilityAdvertisementRequest Stamp(CapabilityAdvertisementRequest request, IPAddress? address,
        string scheme, DateTime now)
    {
        if (request.Telemetry is not { } telemetry) return request;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        var kind = address?.GetAddressBytes() switch
        {
            null => null,
            _ when IPAddress.IsLoopback(address!) => "loopback",
            [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _] => "private",
            [100, >= 64 and <= 127, _, _] => "shared",
            [169, 254, _, _] => "link-local",
            _ when address!.IsIPv6LinkLocal => "link-local",
            _ when address!.IsIPv6UniqueLocal => "private",
            _ => "public",
        };
        return request with { Telemetry = telemetry with { Peer = kind is null ? null : new(kind, scheme, now) } };
    }
}
