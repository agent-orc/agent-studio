using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteGateTransportReachabilityTests
{
    [Theory]
    [InlineData("ssh")]
    [InlineData("scp")]
    public async Task Later_transport_failure_immediately_replaces_successful_reachability(string failingCall)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var slots = new GateSlotUsageTracker(clock: clock);
        var calls = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            if (++calls == 2) throw new IOException(fileName + " failed");
            return Task.CompletedTask;
        }
        var transport = new RemoteGateTransport(new ConfigurationBuilder().Build(),
            NullLogger<RemoteGateTransport>.Instance, clock, "/tmp/gate-test", _ => { }, slots, Run);
        var options = new RemoteGateOptions("gate.example", "/opt/gate/worker.dll", "/var/tmp/gate", 2, 1);

        await transport.Ssh(options, "/tmp", "true", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("reachable", slots.Reachability);

        if (failingCall == "ssh")
            await Assert.ThrowsAsync<IOException>(() => transport.Ssh(options, "/tmp", "true",
                TimeSpan.FromSeconds(1), CancellationToken.None));
        else
            await Assert.ThrowsAsync<IOException>(() => transport.Scp(options, "/tmp", "source", "target",
                TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal("unreachable", slots.Reachability);
        Assert.Equal(clock.GetUtcNow(), slots.LastFailureAt);

        await transport.Scp(options, "/tmp", "source", "target", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("reachable", slots.Reachability);
        Assert.Equal(clock.GetUtcNow(), slots.LastReachedAt);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("unverified", slots.Reachability);
    }
}
