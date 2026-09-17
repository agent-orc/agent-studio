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
/// </summary>
public sealed class ClosedLoopbackPort : IDisposable
{
    private readonly Socket _socket;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";

    public ClosedLoopbackPort()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public void Dispose() => _socket.Dispose();
}
