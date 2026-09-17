using System.Net;
using System.Net.Sockets;

namespace AgentStudio.Tests;

/// <summary>
/// A loopback port that is guaranteed to refuse connections for as long as
/// this object lives. The Update Service's frontend-down cases need a URL
/// nothing answers on, and the obvious way to get one - bind port 0, read the
/// port, close the socket - only reports a port that was free a moment ago.
/// Between the close and the orchestrator's connect attempt the port is back
/// in the ephemeral range, and on a loaded host another test in the same
/// assembly (or anything else on the machine) can take it; the "frontend"
/// check then succeeds and the case under test never reaches the failure it
/// was written to observe.
///
/// So the socket is bound and kept bound, and <c>Listen</c> is never called:
/// the port stays reserved against other binders, and a connect attempt is
/// refused because there is no accept queue behind it.
///
/// AGT-2862: the same port is reserved on <em>both</em> loopback families.
/// The frontend probe now tries <c>localhost</c>, <c>127.0.0.1</c> and
/// <c>[::1]</c> before it calls the frontend down, so reserving only the IPv4
/// address would leave the IPv6 one free for an unrelated process to answer
/// on and turn the frontend-down cases flaky in exactly the way the IPv4-only
/// reservation was written to prevent.
/// </summary>
public sealed class ClosedLoopbackPort : IDisposable
{
    /// <summary>
    /// Attempts at finding a port that is free in both families. A collision
    /// needs the ephemeral IPv4 port this picked to be already taken on IPv6,
    /// which is rare; the retry exists so a rare collision is a retry rather
    /// than a failed test.
    /// </summary>
    private const int BindAttempts = 20;

    private readonly Socket _ipv4;
    private readonly Socket? _ipv6;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";

    public ClosedLoopbackPort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ipv4.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)ipv4.LocalEndPoint!).Port;

            Socket? ipv6 = null;
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    ipv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                    ipv6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                }
            }
            catch (SocketException)
            {
                ipv6?.Dispose();
                ipv6 = null;
                if (attempt < BindAttempts)
                {
                    // The IPv4 port this picked is taken on IPv6 by something
                    // else; pick another one rather than leaving half a
                    // reservation behind.
                    ipv4.Dispose();
                    continue;
                }
                // IPv6 loopback is not bindable on this host at all. An IPv4
                // reservation is still a usable closed port, so the cases
                // that need one run instead of failing on the environment.
            }

            _ipv4 = ipv4;
            _ipv6 = ipv6;
            Port = port;
            return;
        }
    }

    public void Dispose()
    {
        _ipv4.Dispose();
        _ipv6?.Dispose();
    }
}
