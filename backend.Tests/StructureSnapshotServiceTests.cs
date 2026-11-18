using System.Net;
using System.Net.Http.Json;
using AgentStudio.Management;
using AgentStudio.Runner;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3954: the structure endpoint serves one cached snapshot, observed hosts
/// show without any declaration, and every change is published once as a
/// delta for the open views instead of being rebuilt per client.
/// </summary>
public sealed class StructureSnapshotServiceTests : IDisposable
{
    private readonly ManagementTestFolders _folders = ManagementTestFolders.Create(
        Path.Combine(Path.GetTempPath(), "atp-structure-snapshot-" + Guid.NewGuid().ToString("N")));

    public void Dispose() => _folders.Delete();

    private sealed class CapturingPublisher : IStructureDeltaPublisher
    {
        public List<StructureDelta> Deltas { get; } = [];
        public Task PublishAsync(StructureDelta delta, CancellationToken ct)
        {
            lock (Deltas) Deltas.Add(delta);
            return Task.CompletedTask;
        }
    }

    private static void Advertise(V1ReviewExecutorRegistry registry, DateTime at, string runnerId, string hostId, double cpu,
        Contract.HostMachineDto machine)
    {
        registry.Register(runnerId, new Contract.RegisterRunnerRequest(runnerId, hostId, "instance-1", "1.0",
            Contract.TaskServerProtocol.Current, [Contract.ReviewCapabilities.ReviewExecutor]));
        registry.AdvertiseCapabilities(runnerId, new Contract.CapabilityAdvertisementRequest(runnerId, "instance-1",
            Contract.CapabilityProtocol.CurrentSchemaVersion, at, 180, 1,
            [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.ReviewExecutor, "executor")],
            new Contract.HostTelemetrySnapshotDto(at, cpu, 1, 1, 1, 1L << 30, 4L << 30, null, null, null,
                null, 4, 0, Machine: machine)));
    }

    [Fact]
    public async Task Observed_hosts_show_without_declarations_and_changes_are_published_as_deltas()
    {
        var publisher = new CapturingPublisher();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ManagementApiTests.Configure(builder, _folders);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStructureDeltaPublisher>();
                services.AddSingleton<IStructureDeltaPublisher>(publisher);
                // Fixed kernel counters: the suite's own machine load must not appear as a change.
                services.AddSingleton<Func<DateTime, Contract.LinuxHostCounterReading?>>(at =>
                    new(at, 1000, 500, 4, 0.5, 0.5, 0.5, 4L << 30, 2L << 30, null));
            });
        });
        using var client = factory.CreateClient();
        var snapshot = factory.Services.GetRequiredService<StructureSnapshotService>();
        var registry = factory.Services.GetRequiredService<V1ReviewExecutorRegistry>();
        var clock = factory.Services.GetRequiredService<TimeProvider>();

        // The first read waits for the producer's first snapshot; the read itself never runs Git (strict guard in tests).
        var initial = await client.GetAsync("/api/v1/operations/structure");
        Assert.True(initial.StatusCode == HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());
        // The first read starts the slow directory sizes; absorb them before measuring deltas.
        await factory.Services.GetRequiredService<StudioSelfObserver>().MeasuredAsync();
        await snapshot.RefreshAsync(CancellationToken.None);
        lock (publisher.Deltas) publisher.Deltas.Clear();
        var first = await client.GetAsync("/api/v1/operations/structure");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var empty = (await first.Content.ReadFromJsonAsync<StructureResponse>())!;
        // Nothing is declared on this instance; the Studio server still shows with its API.
        Assert.Contains(empty.Nodes, node => node.Id.StartsWith("component:", StringComparison.Ordinal)
                                             && node.Id.EndsWith(":api", StringComparison.Ordinal));
        Assert.True(empty.Revision > 0);
        var etag = first.Headers.ETag!.Tag;
        using (var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/operations/structure"))
        {
            again.Headers.TryAddWithoutValidation("If-None-Match", etag);
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
        }

        Advertise(registry, clock.GetUtcNow().UtcDateTime, "review-a", "review-host-a", 10,
            new(Contract.HostMachineKinds.Container, "kvm", "docker", "review-host-a", "lab-vm", "kernel-lab"));
        await snapshot.RefreshAsync(CancellationToken.None);
        var added = Assert.Single(publisher.Deltas);
        Assert.Equal(empty.Revision, added.FromRevision);
        Assert.Contains(added.UpsertNodes, node => node.Id == "machine:review-host-a" && node.ParentId == "machine:lab-vm");
        Assert.Contains(added.UpsertNodes, node => node.Id == "machine:lab-vm" && node.Kind == "vm");
        Assert.Contains(added.UpsertNodes, node => node.Id == "runner:review-a");

        // A refresh without a change publishes nothing; a fact change publishes only what changed.
        await snapshot.RefreshAsync(CancellationToken.None);
        Assert.Single(publisher.Deltas);
        Advertise(registry, clock.GetUtcNow().UtcDateTime, "review-a", "review-host-a", 77,
            new(Contract.HostMachineKinds.Container, "kvm", "docker", "review-host-a", "lab-vm", "kernel-lab"));
        await snapshot.RefreshAsync(CancellationToken.None);
        var changed = publisher.Deltas[^1];
        Assert.Equal(added.Revision, changed.FromRevision);
        Assert.Contains(changed.UpsertNodes, node => node.Id == "machine:review-host-a" && node.Facts!.CpuPercent == 77);
        Assert.DoesNotContain(changed.UpsertNodes, node => node.Id == "machine:lab-vm");
        Assert.Empty(changed.RemoveNodes);

        var current = (await client.GetFromJsonAsync<StructureResponse>("/api/v1/operations/structure"))!;
        Assert.Equal(changed.Revision, current.Revision);
        Assert.Contains(current.Nodes, node => node.Id == "machine:review-host-a" && node.Provenance == "observed");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/operations/structure?scope=other")).StatusCode);
        // A scoped read is built from the collected facts, again without Git on the request.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/operations/structure?environment=production")).StatusCode);
    }
}
