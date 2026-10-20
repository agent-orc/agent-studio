using System.Net;
using System.Net.Sockets;

namespace AgentStudio.Security;

/// <summary>
/// Per-client part of the login throttle key (AGT-3230). With real client
/// addresses behind the edge, one IPv6 host can rotate through its whole /64,
/// so IPv6 clients share their /64; IPv4 (also IPv4-mapped) keeps the address.
/// </summary>
public static class LoginThrottleKeys
{
    public static string Client(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
