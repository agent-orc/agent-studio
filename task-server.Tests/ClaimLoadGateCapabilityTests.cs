using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ClaimLoadGateCapabilityTests
{
    [Fact]
    public async Task Snapshot_and_stream_record_each_gate_transition_once()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest(
            "Coding host", "host-a", "instance-a", "1.0.0", TaskServerProtocol.Current,
            [CapabilityProtocol.CodingExecutor]), "test", default);
        var since = clock.GetUtcNow().UtcDateTime;

        async Task Advertise(long generation, string state, long sustained)
        {
            var gate = new ClaimLoadGateDto(state, state == "closed" ? 5.51 : 2.1,
                4, sustained, state == "closed" ? since : null, 7);
            var telemetry = new HostTelemetrySnapshotDto(clock.GetUtcNow().UtcDateTime,
                92, 44.08, null, null, null, null, null, null, null, null, 8, 7,
                ClaimLoadGate: gate);
            await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                "runner-a", "instance-a", CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime, 300, generation,
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
                Telemetry: telemetry), "test", default);
        }

        await Advertise(1, "open", 0);
        await Advertise(2, "closed", 702);
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(new ClaimLoadGateDto("closed", 5.51, 4, 702, since, 7),
            snapshot.Telemetry?.ClaimLoadGate);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Advertise(3, "closed", 762);
        await Advertise(4, "open", 0);
        var events = (await store.ListStudioStreamEventsSinceAsync(0, default))
            .Where(item => item.Kind.StartsWith("runner.claim-load-gate.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["runner.claim-load-gate.closed", "runner.claim-load-gate.opened"],
            events.Select(item => item.Kind));
        Assert.Contains("closedSeconds", events[1].PayloadJson);
        var audit = (await store.ListAuditAsync(0, default))
            .Where(item => item.Action.StartsWith("runner.claim-load-gate.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, audit.Length);

        var history = await store.ListClaimGateEventsAsync(14, default);
        Assert.Equal(["claim-resumed", "claim-stopped"], history.Select(item => item.Event));
        Assert.Equal(60, history[0].DurationSeconds);
        Assert.Equal(2.1, history[0].LoadPerCore);
        Assert.Equal(702, history[1].DurationSeconds);
        Assert.Equal(5.51, history[1].LoadPerCore);
        Assert.Equal(4, history[1].Threshold);
        Assert.Equal(7, history[1].ActiveSlots);
        Assert.All(history, item =>
        {
            Assert.Equal("runner-a", item.RunnerId);
            Assert.Equal("host-a", item.HostId);
        });
        clock.Advance(TimeSpan.FromDays(15));
        Assert.Empty(await store.ListClaimGateEventsAsync(14, default));
    }
}
