using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-2999: the salvage store a coding host measures travels in its telemetry
/// and is served unchanged by <c>GET /api/v1/management/remote-hosts</c>.
/// </summary>
public sealed class SalvageStoreHostReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Remote_host_report_carries_salvage_store_size_count_oldest_entry_and_last_sweep()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest(
            "runner-a", "host-a", "instance-a", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "runner-a", default);
        var now = Start.UtcDateTime;
        var sweep = new SalvageSweepDto(
            now.AddMinutes(-3), now.AddMinutes(-2), "apply", "completed",
            2050, 1990, 91_000_000_000, 1990, 91_000_000_000, 12, 4, 4, 1, 0);
        var salvage = new SalvageStoreDto(
            now, "/home/agent/salvage", true, 3_000_000_000, 64, 60, 4,
            "AGT-2139-2009.tgz", new DateTime(2026, 7, 11, 20, 9, 0, DateTimeKind.Utc),
            "apply", 14, 3, sweep);

        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-a", "instance-a", CapabilityProtocol.CurrentSchemaVersion, now, 300, 1,
            [new(CapabilityProtocol.CodingExecutor, "executor")],
            new HostTelemetrySnapshotDto(now, 10, 0, 0, 0, 1, 2, 0, 0, 0, 0, 4, 0, SalvageStore: salvage)),
            "runner-a", default);

        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(salvage, snapshot.Telemetry!.SalvageStore);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"salvageStore\":{", json);
        Assert.Contains("\"sizeBytes\":3000000000", json);
        Assert.Contains("\"entryCount\":64", json);
        Assert.Contains("\"oldestEntry\":\"AGT-2139-2009.tgz\"", json);
        Assert.Contains("\"lastSweep\":{", json);
        Assert.Contains("\"tarballBytesDeleted\":91000000000", json);
    }
}
