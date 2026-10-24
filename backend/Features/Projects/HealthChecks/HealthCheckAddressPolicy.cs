using System.Net;
using System.Net.Sockets;

namespace AgentStudio.Projects;

public enum HealthAddressClass
{
    Public,
    /// <summary>RFC 1918, shared address space and IPv6 unique-local: only with the environment's opt-in.</summary>
    Private,
    /// <summary>Loopback, link-local, cloud metadata, unspecified, multicast and reserved: never probed.</summary>
    Blocked,
}

/// <summary>
/// Pure address rule for health check targets (IP-14). Targets come from repository files, so
/// without it a declaration could point the control plane at its own loopback services or a
/// cloud metadata endpoint and read the answer back through the probe result.
/// </summary>
public static class HealthCheckAddressPolicy
{
    private static readonly byte[] Nat64Prefix = [0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly IPAddress[] Metadata =
    [
        IPAddress.Parse("169.254.169.254"), // AWS, GCP, Azure, OCI (also link-local)
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud
        IPAddress.Parse("168.63.129.16"),   // Azure WireServer
        IPAddress.Parse("fd00:ec2::254"),   // AWS IPv6
    ];

    public static HealthAddressClass Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var encoded = address.GetAddressBytes();
            var nat64 = encoded.AsSpan(0, 12).SequenceEqual(Nat64Prefix);
            var sixToFour = encoded[0] == 0x20 && encoded[1] == 0x02;
            if (nat64) address = new IPAddress(encoded.AsSpan(12, 4));
            else if (sixToFour) address = new IPAddress(encoded.AsSpan(2, 4));
        }
        if (Metadata.Contains(address)) return HealthAddressClass.Blocked;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0], bytes[1]) switch
            {
                (0, _) or (127, _) or (169, 254) or (>= 224, _) => HealthAddressClass.Blocked,
                (10, _) or (172, >= 16 and <= 31) or (192, 168) or (100, >= 64 and <= 127) => HealthAddressClass.Private,
                _ => HealthAddressClass.Public,
            };
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return HealthAddressClass.Blocked;
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
            return HealthAddressClass.Blocked;
        var first = address.GetAddressBytes()[0];
        return (first & 0xfe) == 0xfc || address.IsIPv6SiteLocal ? HealthAddressClass.Private : HealthAddressClass.Public;
    }

    public static bool Allows(IPAddress address, bool allowPrivateNetwork)
        => Classify(address) switch
        {
            HealthAddressClass.Public => true,
            HealthAddressClass.Private => allowPrivateNetwork,
            _ => false,
        };
}

/// <summary>The target resolved only to addresses the policy refuses; no connection was attempted.</summary>
public sealed class HealthCheckAddressBlockedException(string host)
    : Exception($"Health check target {host} resolves only to addresses the control plane does not probe.");

/// <summary>
/// Opens every health check connection: resolves the host, keeps the addresses
/// <see cref="HealthCheckAddressPolicy"/> allows and connects to exactly those. HTTP uses it
/// as the <see cref="SocketsHttpHandler.ConnectCallback"/>, so the address that is checked is
/// the address that is connected to, and a DNS answer that changes between checks (rebinding)
/// is checked again on every connection.
/// </summary>
public sealed class HealthCheckConnector
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateNetworkOption = new("environment-health-allow-private-network");
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, Task<Socket>> _connect;

    public HealthCheckConnector() : this(Dns.GetHostAddressesAsync, ConnectSocketAsync)
    {
    }

    public HealthCheckConnector(Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect)
    {
        _resolve = resolve;
        _connect = connect;
    }

    public async Task<Socket> ConnectAsync(string host, int port, bool allowPrivateNetwork, CancellationToken cancellationToken)
    {
        var name = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(name, out var literal) ? [literal] : await _resolve(name, cancellationToken);
        var allowed = addresses.Where(address => HealthCheckAddressPolicy.Allows(address, allowPrivateNetwork)).ToArray();
        if (allowed.Length == 0) throw new HealthCheckAddressBlockedException(name);
        SocketException? last = null;
        foreach (var address in allowed)
        {
            try
            {
                return await _connect(new IPEndPoint(address, port), cancellationToken);
            }
            catch (SocketException ex)
            {
                // Try the next allowed address; the last failure is the probe's result.
                last = ex;
            }
        }
        throw last!;
    }

    /// <summary>The primary handler of the probe's HTTP client: no proxy, redirects or cookies.</summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // A proxy would make the policy check the proxy's address instead of the target's.
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            context.InitialRequestMessage.Options.TryGetValue(AllowPrivateNetworkOption, out var allowPrivate);
            var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowPrivate,
                cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        },
    };

    private static async Task<Socket> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
