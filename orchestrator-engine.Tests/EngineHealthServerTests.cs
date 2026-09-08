using System.Net;
using System.Net.Sockets;
using AgentStudio.OrchestratorEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class EngineHealthServerTests
{
    [Fact]
    public async Task Listening_server_is_reachable_and_stop_makes_it_unreachable()
    {
        var port = FreePort();
        var options = new EngineOptions
        {
            ServerUrl = "http://127.0.0.1:1",
            ClientId = "engine-a",
            HealthPort = port,
        };
        var server = new EngineHealthServer(options, NullLogger<EngineHealthServer>.Instance);
        using var cts = new CancellationTokenSource();

        Assert.False(await EngineHealthProbe.IsReachableAsync(port, TimeSpan.FromMilliseconds(200)));

        // BackgroundService.StartAsync does not wait for ExecuteAsync to reach
        // its first await (the listener bind), so poll like a real HEALTHCHECK
        // (--start-period) would rather than assuming synchronous startup.
        await server.StartAsync(cts.Token);
        try
        {
            var reachable = false;
            for (var attempt = 0; attempt < 50 && !reachable; attempt++)
            {
                reachable = await EngineHealthProbe.IsReachableAsync(port, TimeSpan.FromMilliseconds(200));
                if (!reachable) await Task.Delay(50);
            }
            Assert.True(reachable);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }

        Assert.False(await EngineHealthProbe.IsReachableAsync(port, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Resolve_port_prefers_health_port_env_over_default()
    {
        var values = new Dictionary<string, string?> { ["HEALTH_PORT"] = "6001" };

        Assert.Equal(6001, EngineHealthProbe.ResolvePort(key => values.GetValueOrDefault(key)));
        Assert.Equal(EngineHealthProbe.DefaultPort, EngineHealthProbe.ResolvePort(_ => null));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("")]
    public void Resolve_port_falls_back_to_default_on_an_invalid_value(string configured)
    {
        var values = new Dictionary<string, string?> { ["HEALTH_PORT"] = configured };

        Assert.Equal(EngineHealthProbe.DefaultPort, EngineHealthProbe.ResolvePort(key => values.GetValueOrDefault(key)));
    }

    [Fact]
    public async Task Bind_conflict_is_caught_and_reported_unhealthy_without_hanging()
    {
        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var options = new EngineOptions
            {
                ServerUrl = "http://127.0.0.1:1",
                ClientId = "engine-a",
                HealthPort = port,
            };
            var server = new EngineHealthServer(options, NullLogger<EngineHealthServer>.Instance);
            using var cts = new CancellationTokenSource();

            await server.StartAsync(cts.Token);
            await Task.Delay(200);
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.StopAsync(stopTimeout.Token);
        }
        finally
        {
            blocker.Stop();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
