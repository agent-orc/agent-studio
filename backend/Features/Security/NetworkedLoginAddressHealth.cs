using System.Net;

namespace AgentStudio.Security;

/// <summary>Temporary warning when forwarded login traffic resolves to an internal peer.</summary>
public sealed class NetworkedLoginAddressHealth(TimeProvider clock)
{
    private static readonly TimeSpan WarningDuration = TimeSpan.FromMinutes(5);
    private long _warningUntilUtcTicks;

    public bool Healthy => clock.GetUtcNow().UtcDateTime.Ticks >= Volatile.Read(ref _warningUntilUtcTicks);

    public void ReportInvalid()
    {
        var until = clock.GetUtcNow().Add(WarningDuration).UtcDateTime.Ticks;
        var current = Volatile.Read(ref _warningUntilUtcTicks);
        while (until > current)
        {
            var previous = Interlocked.CompareExchange(ref _warningUntilUtcTicks, until, current);
            if (previous == current) return;
            current = previous;
        }
    }

    public static bool IsInvalid(IPAddress? address, IConfiguration configuration)
    {
        if (address is null) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var trusted = (configuration["Security:TrustedProxyAddresses"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (trusted.Any(raw => IPAddress.TryParse(raw, out var proxy)
            && (proxy.IsIPv4MappedToIPv6 ? proxy.MapToIPv4() : proxy).Equals(address))) return true;
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc;
    }
}
