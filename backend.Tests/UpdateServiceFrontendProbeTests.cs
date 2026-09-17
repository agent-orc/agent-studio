extern alias UpdSvc;
using System.Net;
using System.Net.Sockets;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2862. The Stable v0.6.0 upgrade restarted cleanly, passed the runtime
/// identity check, and was then reported as "frontend dev server did not come
/// up" while the frontend was serving: <c>ng serve</c> without an explicit
/// <c>--host</c> binds "localhost", which resolves to <c>::1</c> on that
/// Windows host, and the Update Service polled <c>http://127.0.0.1:4011</c>
/// only (run 1027d2e7, 17.09.2026).
///
/// These cases pin the two halves of the fix that can be proven without a
/// full pipeline run: which origins the probe tries, and that a server
/// listening on either loopback family alone is found. The bind half lives in
/// <c>frontend/angular.json</c> (<c>host: 127.0.0.1</c>); the probe half is
/// here so a future change that narrows it back to one spelling fails a test
/// instead of an upgrade.
///
/// The probe budgets are deliberately short: every case either connects on
/// the first attempt or is expected to run out, and the loopback connect
/// either succeeds or is refused immediately.
/// </summary>
public class UpdateServiceFrontendProbeTests
{
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Targets_ForALoopbackOrigin_CoverEverySpellingWithTheConfiguredOneFirst()
    {
        var targets = LoopbackProbeTargets.For("http://127.0.0.1:4011");

        Assert.Equal(
            new[] { "http://127.0.0.1:4011", "http://localhost:4011", "http://[::1]:4011" },
            targets);
    }

    [Theory]
    [InlineData("http://localhost:4011", "http://localhost:4011")]
    [InlineData("http://[::1]:4011", "http://[::1]:4011")]
    public void Targets_PutTheConfiguredSpellingFirstAndStillCoverTheOthers(string configured, string expectedFirst)
    {
        var targets = LoopbackProbeTargets.For(configured);

        Assert.Equal(expectedFirst, targets[0]);
        Assert.Equal(3, targets.Count);
        Assert.Contains("http://127.0.0.1:4011", targets);
        Assert.Contains("http://[::1]:4011", targets);
        Assert.Contains("http://localhost:4011", targets);
    }

    /// <summary>
    /// Widening a remote origin to loopback would probe this machine's
    /// frontend and call a different machine's frontend healthy.
    /// </summary>
    [Fact]
    public void Targets_ForANonLoopbackOrigin_AreNotWidened()
    {
        Assert.Equal(new[] { "http://stable.example:4011" }, LoopbackProbeTargets.For("http://stable.example:4011"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("tcp://127.0.0.1:4011")]
    public void Targets_ForAnUnusableOrigin_AreEmpty(string? configured)
    {
        Assert.Empty(LoopbackProbeTargets.For(configured));
    }

    /// <summary>
    /// The exact incident shape: the dev server is only on the IPv6 loopback,
    /// and the service is configured with the IPv4 one.
    /// </summary>
    [SkippableFact]
    public async Task FrontendOnIPv6LoopbackOnly_IsFound_ThroughTheConfiguredIPv4Origin()
    {
        using var frontend = LoopbackServer.TryStart(IPAddress.IPv6Loopback);
        Skip.If(frontend is null, "IPv6 loopback is not bindable on this host.");

        var result = await ProbeAsync($"http://127.0.0.1:{frontend!.Port}");

        Assert.True(result.Up, $"probe called the frontend down; attempts: {Describe(result)}");
        // Which spelling wins depends on how this host resolves "localhost",
        // so the assertion is the one that matters: the configured origin was
        // not the one that answered, and the probe kept going anyway.
        Assert.NotEqual($"http://127.0.0.1:{frontend.Port}", result.ReachedUrl);
    }

    /// <summary>
    /// The mirror case, so the fix cannot be "probe IPv6 instead": a server on
    /// the IPv4 loopback is found through an IPv6-configured origin too.
    /// </summary>
    [Fact]
    public async Task FrontendOnIPv4LoopbackOnly_IsFound_ThroughAnIPv6ConfiguredOrigin()
    {
        using var frontend = LoopbackServer.TryStart(IPAddress.Loopback)!;

        var result = await ProbeAsync($"http://[::1]:{frontend.Port}");

        Assert.True(result.Up, $"probe called the frontend down; attempts: {Describe(result)}");
        Assert.NotEqual($"http://[::1]:{frontend.Port}", result.ReachedUrl);
    }

    /// <summary>
    /// A frontend that is genuinely down still fails, and the failure carries
    /// the evidence the run log has to show: one row per origin tried, plus
    /// the listener list for the port.
    /// </summary>
    [Fact]
    public async Task FrontendDown_FailsWithOneAttemptPerOriginAndListenerEvidence()
    {
        using var closed = new ClosedLoopbackPort();

        var result = await ProbeAsync(closed.Url, budget: TimeSpan.FromMilliseconds(1));

        Assert.False(result.Up);
        Assert.Null(result.ReachedUrl);
        Assert.Equal(closed.Port, result.Port);
        Assert.Equal(
            new[] { $"http://127.0.0.1:{closed.Port}", $"http://localhost:{closed.Port}", $"http://[::1]:{closed.Port}" },
            result.Attempts.Select(a => a.Url));
        Assert.All(result.Attempts, attempt => Assert.False(attempt.Reachable));
        Assert.All(result.Attempts, attempt => Assert.False(string.IsNullOrWhiteSpace(attempt.Detail)));
        Assert.NotEmpty(result.Listeners);

        var rendered = FrontendProbe.Render(result);
        Assert.Contains("verdict: DOWN", rendered);
        Assert.Contains($"http://[::1]:{closed.Port}", rendered);
        Assert.Contains($"listeners on port {closed.Port}", rendered);
    }

    /// <summary>
    /// Listener evidence is read from the OS, not from the probe's own
    /// attempts, so it can contradict them - which is the whole point when a
    /// frontend is listening on an address the probe could not reach.
    /// </summary>
    [Fact]
    public void Listeners_NameTheBoundAddressAndPort()
    {
        using var frontend = LoopbackServer.TryStart(IPAddress.Loopback)!;

        var listeners = LoopbackListeners.ForPort(frontend.Port);

        Assert.Contains($"TCP 127.0.0.1:{frontend.Port} LISTENING", listeners);
    }

    [Fact]
    public void Listeners_ForAnUnusedPort_SayNobodyIsListening()
    {
        using var closed = new ClosedLoopbackPort();

        Assert.Equal(new[] { $"(no process is listening on port {closed.Port})" }, LoopbackListeners.ForPort(closed.Port));
    }

    /// <summary>
    /// An installation without a frontend origin must not be blocked by a
    /// check it never opted into; the skip is still recorded as evidence.
    /// </summary>
    [Fact]
    public async Task NoConfiguredOrigin_IsTreatedAsUpAndSaysSo()
    {
        var result = await ProbeAsync("");

        Assert.True(result.Up);
        Assert.Contains(result.Attempts, a => a.Detail.Contains("skipped", StringComparison.Ordinal));
    }

    private static Task<FrontendProbeResult> ProbeAsync(string? url, TimeSpan? budget = null)
        => FrontendProbe.WaitForAsync(
            url,
            budget ?? TimeSpan.FromSeconds(5),
            CancellationToken.None,
            connectTimeout: ConnectBudget,
            pollInterval: PollInterval);

    private static string Describe(FrontendProbeResult result)
        => string.Join("; ", result.Attempts.Select(a => $"{a.Url}: {a.Detail}"));

    /// <summary>
    /// A TCP listener on exactly one loopback address family, standing in for
    /// the dev server. Nothing is served on it: the probe only asks whether
    /// the port accepts a connection.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;

        public int Port { get; }

        private LoopbackServer(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public static LoopbackServer? TryStart(IPAddress address)
        {
            try
            {
                var listener = new TcpListener(address, 0);
                listener.Start();
                return new LoopbackServer(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
            }
            catch (SocketException)
            {
                return null;
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
