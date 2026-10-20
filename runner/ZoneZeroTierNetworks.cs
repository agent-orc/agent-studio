using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentRunner;

public static class ZoneZeroTierNetworks
{
    private const string RoutesPath = "/proc/net/route";

    /// <summary>Read ZeroTier interface prefixes and kernel routes on every render and apply.</summary>
    public static IReadOnlyList<string> Detect()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.Name.StartsWith("zt", StringComparison.OrdinalIgnoreCase)
                              || network.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var routes = File.Exists(RoutesPath) ? File.ReadAllText(RoutesPath) : string.Empty;
        var ranges = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var network in interfaces)
        {
            foreach (var range in RangesFor(network.GetIPProperties().UnicastAddresses
                         .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                         .Select(address => (address.Address, address.PrefixLength)), routes, network.Name))
                ranges.Add(range);
        }
        return ranges.ToList();
    }

    public static IReadOnlyList<string> RangesFor(
        IEnumerable<(IPAddress Address, int Prefix)> addresses, string routeTable, string interfaceName)
        => addresses.Select(item => ZonePolicyParser.NetworkOf(item.Address, item.Prefix))
            .Concat(RoutesFor(routeTable, interfaceName))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>Decode IPv4 routes for one interface from Linux's little-endian route table.</summary>
    public static IReadOnlyList<string> RoutesFor(string table, string interfaceName)
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 || fields[0] != interfaceName
                || !uint.TryParse(fields[3], System.Globalization.NumberStyles.HexNumber, null, out var flags)
                || (flags & 1) == 0
                || !TryReadAddress(fields[1], out var address)
                || !TryReadAddress(fields[7], out var mask)) continue;
            var prefix = mask.GetAddressBytes().Sum(value => System.Numerics.BitOperations.PopCount((uint)value));
            var maskBytes = mask.GetAddressBytes();
            var actualMask = (uint)maskBytes[0] << 24 | (uint)maskBytes[1] << 16
                             | (uint)maskBytes[2] << 8 | maskBytes[3];
            var expectedMask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            if (actualMask != expectedMask) continue;
            var route = ZonePolicyParser.NetworkOf(address, prefix);
            if (ZonePolicyParser.TryNormalizeCidr($"{address}/{prefix}", out var canonical)
                && canonical == route)
                routes.Add(route);
        }
        return routes.ToList();
    }

    private static bool TryReadAddress(string hex, out IPAddress address)
    {
        address = IPAddress.None;
        if (hex.Length != 8) return false;
        try
        {
            address = new IPAddress(Convert.FromHexString(hex).Reverse().ToArray());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
