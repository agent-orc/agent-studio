using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.OrchestratorEngine;

/// <summary>
/// Liveness signal for a process with no HTTP surface and no filesystem access
/// (orchestrator-engine is a pure Task Server API client - see
/// <c>EngineContractTests.Engine_project_is_a_pure_contract_api_client</c>).
/// Accepting a loopback TCP connection is the entire contract: <c>--health-check</c>
/// (<see cref="EngineHealthProbe"/>) treats a successful connect as live.
///
/// Every failure is caught and logged rather than rethrown: the health probe
/// is diagnostic, not load-bearing, and the default
/// <see cref="Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior"/>
/// stops the whole host - including the real orchestration stage loops in
/// <see cref="OrchestratorEngineService"/> - on the first unhandled exception
/// from any hosted service.
/// </summary>
public sealed class EngineHealthServer : BackgroundService
{
    private readonly int _port;
    private readonly ILogger<EngineHealthServer> _logger;

    public EngineHealthServer(EngineOptions options, ILogger<EngineHealthServer> logger)
    {
        _port = options.HealthPort;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
        }
        catch (SocketException exception)
        {
            _logger.LogWarning(
                exception,
                "orchestrator-engine health listener could not bind 127.0.0.1:{Port}; "
                + "health checks will report unhealthy, but orchestration continues",
                _port);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (SocketException exception)
                {
                    _logger.LogWarning(exception, "orchestrator-engine health listener accept failed; retrying");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}

public static class EngineHealthProbe
{
    public const int DefaultPort = 5073;

    public static int ResolvePort(Func<string, string?> value)
        => value("HEALTH_PORT") is { Length: > 0 } configured && int.TryParse(configured, out var port) && port > 0
            ? port
            : DefaultPort;

    public static async Task<bool> IsReachableAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
