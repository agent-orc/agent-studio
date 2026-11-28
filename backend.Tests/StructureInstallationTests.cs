using System.Net;
using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StructureInstallationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Fixture = Path.Combine(RepositoryRoot.Find(), "testdata", "structure");

    public sealed record Capture(DateTime At, StructureResponse Declared, RunnerCapabilitySnapshotDto[] Hosts,
        StructureObservedContext Context);

    // The renderer fixture is the JSON response of this same replay. It contains only role identities.
    public static StructureResponse Replay()
    {
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        return ObservedStructureProjection.Apply(capture.Declared, capture.Hosts, [], [], [], null,
            "not-required", capture.At, context: capture.Context) with { GeneratedAt = capture.At };
    }

    [Fact]
    public void Installation_recording_replays_the_shared_workspace_response()
    {
        var actual = Replay();
        var expected = JsonSerializer.Deserialize<StructureResponse>(File.ReadAllText(Path.Combine(Fixture, "workspace.json")), Json);
        Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(actual, Json));
        Assert.Equal(3, actual.SchemaVersion);
        Assert.Equal(2, actual.Nodes.Count(n => n.Kind is "server" or "physical"));
        Assert.Single(actual.Nodes, n => n.Kind == "vm");
        Assert.Equal(8, actual.Nodes.Count(n => n.Kind == "container"));
        Assert.Single(actual.Nodes, n => n.Kind == "container-stack" && n.Label == "Studio stack");
        var services = actual.Nodes.Where(n => n.Kind == "host-service" && n.Facts?.Capacity?.TotalSlots > 0).ToArray();
        Assert.True(services.Length >= 2);
        Assert.Contains(services, n => n.Facts!.Capacity!.RunningCards.Count > 0);
        Assert.Contains(actual.Nodes, n => n.Facts?.Device is { RaidLevel: "raid1", Members.Count: 2 });
        Assert.True(actual.Links.Count >= 5);
        foreach (var kind in new[] { "public-route", "runner-to-server", "gate-ssh", "overlay" })
            Assert.Contains(actual.Links, l => l.Kind == kind);
        Assert.NotEmpty(actual.History!.Trends);
        Assert.NotEmpty(actual.History.Events);
        var ids = actual.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(actual.Nodes, n => Assert.True(n.ParentId is null || ids.Contains(n.ParentId)));
        Assert.All(actual.Links, l => Assert.True(ids.Contains(l.From) && ids.Contains(l.To)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Machine_performance_keeps_its_newest_measurement_and_own_clock(bool reverse, bool selfReports)
    {
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        var hosts = reverse ? capture.Hosts.Reverse().ToArray() : capture.Hosts;
        var local = hosts.Single(h => h.RunnerId == "coding-runner").Telemetry!;
        var studio = capture.Context.Studio!;
        if (selfReports)
            studio = studio with { Performance = new(capture.At, CpuPercent: 42) };
        var snapshot = ObservedStructureProjection.Apply(capture.Declared, hosts, [], [], [], null,
            "not-required", capture.At, context: capture.Context with { Studio = studio });
        var performance = snapshot.Nodes.Single(n => n.Id == "machine:studio-server").Facts!.Performance!;
        var measured = selfReports ? capture.At : local.ObservedAt;
        Assert.Equal(measured, performance.ObservedAt);
        Assert.Equal(selfReports ? 42 : local.CpuPercent, performance.CpuPercent);
        Assert.Equal(new StructureStamp(measured, selfReports ? "studio self observation" : "coding-runner",
            measured.AddSeconds(90)), performance.Sample);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void Gate_disk_probe_uses_its_own_clock_when_the_gate_also_reports(int seconds)
    {
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        var host = capture.Hosts.Single(h => h.RunnerId == "coding-runner") with { HostId = "gate" };
        var telemetry = host.Telemetry!;
        var installation = capture.Context.Installation!;
        var disk = installation.GateDisk! with { ObservedAt = telemetry.ObservedAt.AddSeconds(seconds) };
        var context = capture.Context with { Installation = installation with { GateDisk = disk } };
        var result = ObservedStructureProjection.Apply(capture.Declared, [host], [], [], [], null,
            "not-required", capture.At, context: context);
        var performance = result.Nodes.Single(n => n.Id == "machine:gate").Facts!.Performance!;
        Assert.Equal(telemetry.ObservedAt, performance.Sample!.ObservedAt);
        Assert.Equal(telemetry.CpuPercent, performance.CpuPercent);
        var mount = performance.Disks!.First();
        var expected = seconds < 0 ? telemetry.Filesystems![0] : disk.Filesystems[0];
        Assert.Equal((expected.Path, expected.FreeBytes), (mount.Mount, mount.FreeBytes));
        Assert.Equal(seconds < 0 ? telemetry.ObservedAt : disk.ObservedAt, (mount.Sample ?? performance.Sample).ObservedAt);
    }

    [Fact]
    public void Drained_service_keeps_running_cards_and_reports_reserved_slots_and_queue()
    {
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        var host = capture.Hosts.Single(h => h.Telemetry?.Structure?.Role == "coding");
        var context = capture.Context;
        var install = context.Installation!;
        var api = install.Api with { Queues = install.Api.Queues with { Coding = 9 } };
        var snapshot = ObservedStructureProjection.Apply(capture.Declared,
            [host with { HostAdmission = host.HostAdmission with { AdmissionState = "drained" } }], [], [], [], null,
            "not-required", capture.At, context: context with { Installation = install with { Api = api } });
        var role = Assert.Single(snapshot.Nodes, node => node.Kind == "host-service" && node.Role == "coding");
        var capacity = role.Facts!.Capacity!;
        Assert.Equal((18, 14, 4, 0, 9), (capacity.TotalSlots, capacity.BusySlots, capacity.HeldSlots, capacity.FreeSlots, capacity.QueueLength));
        Assert.Equal(14, capacity.RunningCards.Count);
        Assert.Equal(host.Telemetry!.Structure!.FreshUntil, role.Facts.Sample!.FreshUntil);
    }

    [Fact]
    public void Five_second_samples_expire_across_gaps_and_only_twenty_events_survive()
    {
        var at = DateTime.UnixEpoch;
        var history = new StructureHistoryBuffer();
        for (var i = 0; i <= 180; i++)
        {
            var instant = at.AddSeconds(5 * i);
            history.Sample(instant, i, i * 2);
            history.Add(new(instant, "gate ledger", "gate-verified", i.ToString()));
        }
        var snapshot = history.Snapshot(at.AddMinutes(15));
        Assert.Equal(180, snapshot.Trends.Count);
        Assert.Equal(1, snapshot.Trends[0].BusySlots);
        Assert.Equal(360, snapshot.Trends[^1].QueuedTasks);
        Assert.Equal(20, snapshot.Events.Count);
        Assert.Equal("180", snapshot.Events[0].Subject);
        Assert.Empty(history.Snapshot(at.AddMinutes(30)).Trends);
    }

    [Fact]
    public void Offline_workspace_reads_the_original_report_and_expires_it()
    {
        using var directory = new TempWorkspace("structure-cache");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = directory.Path }).Build();
        StructureReportCache Open() => new(config, NullLogger<StructureReportCache>.Instance);
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        var host = capture.Hosts.First(h => h.Telemetry?.Structure is not null);
        Open().Merge([host], capture.At, "studio-server", "studio");
        var cache = Open();
        var stale = Assert.Single(cache.Merge([], capture.At.AddMinutes(5), "studio-server", "different-kernel"));
        Assert.Equal(host.Telemetry!.Structure!.ObservedAt, stale.Telemetry!.Structure!.ObservedAt);
        Assert.True(stale.Telemetry.Structure.FreshUntil < capture.At.AddMinutes(5));
        Assert.Equal("studio-server", stale.Telemetry.Placement!.ParentMachineId);
        Assert.Empty(stale.Capabilities);
        Assert.Empty(cache.Merge([], capture.At.AddDays(15), "studio-server", "different-kernel"));
    }

    [Fact]
    public void Legacy_reports_survive_disconnect_until_expiry_or_a_successful_empty_read()
    {
        var cache = new StructureReportCache(new ConfigurationBuilder().Build(), NullLogger<StructureReportCache>.Instance);
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(Path.Combine(Fixture, "observations.json")), Json)!;
        var legacy = capture.Hosts.First(h => h.Telemetry?.Structure is null);
        var seen = legacy.LastSeenAt;

        IReadOnlyList<RunnerCapabilitySnapshotDto> Read(RunnerCapabilitySnapshotDto[]? live, DateTime at)
            => cache.Merge(live, at, "studio-server", null);

        Read([legacy], seen);
        Assert.Same(legacy, Assert.Single(Read(null, seen.AddDays(14))));
        Assert.Empty(Read(null, seen.AddDays(14).AddTicks(1)));
        var returned = legacy with { LastSeenAt = seen.AddDays(15) };
        Assert.Same(returned, Assert.Single(Read([returned], returned.LastSeenAt)));
        Assert.Empty(Read([], returned.LastSeenAt.AddSeconds(5)));
        Assert.Empty(Read(null, returned.LastSeenAt.AddSeconds(10)));
    }

    [Fact]
    public void Request_rate_uses_elapsed_time_and_in_flight_returns_to_zero()
    {
        var clock = new FakeTimeProvider();
        var self = new StudioSelfObserver(new ConfigurationBuilder().Build(), clock);
        Assert.Null(self.RequestLoad(clock.GetUtcNow().UtcDateTime).Rate);
        self.ObserveRequest(new HeaderDictionary());
        self.ObserveRequest(new HeaderDictionary());
        self.FinishRequest();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal((1d, 1), self.RequestLoad(clock.GetUtcNow().UtcDateTime));
        self.FinishRequest();
        Assert.Equal(0, self.RequestLoad(clock.GetUtcNow().UtcDateTime).InFlight);
    }

    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("::ffff:10.2.3.4", "private")]
    [InlineData("100.64.0.1", "shared")]
    [InlineData("2001:db8::1", "public")]
    public void Heartbeat_receiver_records_only_the_address_class(string address, string expected)
    {
        var at = DateTime.UnixEpoch;
        var telemetry = new HostTelemetrySnapshotDto(at, null, null, null, null, null, null, null, null, null, null, 0, 0);
        var request = new CapabilityAdvertisementRequest("runner", "instance", 1, at, 90, 1, [], telemetry);
        var stamped = HeartbeatPeer.Stamp(request, IPAddress.Parse(address), "https", at);
        Assert.Equal(new(expected, "https", at), stamped.Telemetry!.Peer);
        Assert.Null(HeartbeatPeer.Stamp(stamped, null, "https", at).Telemetry!.Peer);
    }
}
