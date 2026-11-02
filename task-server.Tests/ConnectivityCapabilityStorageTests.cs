using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ConnectivityCapabilityStorageTests
{
    [Fact]
    public async Task Failure_fields_survive_capability_storage_and_recovery()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest(
            "runner", "host-a", "instance-a", "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "test", default);
        var since = clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        async Task Advertise(long generation, AdvertisedCapabilityDto capability)
            => await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                "runner-a", "instance-a", CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime, 300, generation, [capability]), "test", default);

        await Advertise(1, new AdvertisedCapabilityDto(CapabilityProtocol.TaskServerConnectivity,
            "foundation", "unavailable", Detail: "3 consecutive request failures",
            Since: since, HttpStatus: 524, Request: "POST /api/runner/claim"));
        var failed = Assert.Single(Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Capabilities, item => item.Key == CapabilityProtocol.TaskServerConnectivity);
        Assert.Equal((since, 524, "POST /api/runner/claim"),
            (failed.Since, failed.HttpStatus, failed.Request));

        await Advertise(2, new AdvertisedCapabilityDto(CapabilityProtocol.TaskServerConnectivity, "foundation"));
        var recovered = Assert.Single(Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Capabilities, item => item.Key == CapabilityProtocol.TaskServerConnectivity);
        Assert.Null(recovered.Since);
        Assert.Null(recovered.HttpStatus);
        Assert.Null(recovered.Request);
    }
}
