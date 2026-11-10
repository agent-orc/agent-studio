using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ClaimGateHistoryRegistryTests
{
    [Fact]
    public void Registry_records_each_claim_gate_transition_once_with_load_and_duration()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        var registry = new V1ReviewExecutorRegistry(clock);
        registry.Register("coding", new RegisterRunnerRequest("coding", "host-a", "instance",
            "1", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]));
        var since = clock.GetUtcNow().UtcDateTime;

        void Advertise(long generation, string state, double load, long sustained)
        {
            var gate = new ClaimLoadGateDto(state, load, 4, sustained,
                state == "closed" ? since : null, 7);
            registry.AdvertiseCapabilities("coding", new CapabilityAdvertisementRequest(
                "coding", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, generation,
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
                Telemetry: new HostTelemetrySnapshotDto(clock.GetUtcNow().UtcDateTime,
                    92, 44.08, null, null, null, null, null, null, null, null, 8, 7,
                    ClaimLoadGate: gate)));
        }

        Advertise(1, "open", 2.1, 0);
        Advertise(2, "closed", 5.51, 702);
        clock.Advance(TimeSpan.FromMinutes(23));
        Advertise(3, "closed", 5.2, 702);
        Advertise(4, "open", 3.2, 0);

        var events = registry.ListClaimGateEvents(14);
        Assert.Equal(["claim-resumed", "claim-stopped"], events.Select(item => item.Event));
        Assert.Equal(23 * 60, events[0].DurationSeconds);
        Assert.Equal(3.2, events[0].LoadPerCore);
        Assert.Equal(702, events[1].DurationSeconds);
        Assert.Equal(5.51, events[1].LoadPerCore);
        Assert.All(events, item => Assert.Equal("host-a", item.HostId));

        clock.Advance(TimeSpan.FromDays(15));
        Assert.Empty(registry.ListClaimGateEvents(14));
    }
}
