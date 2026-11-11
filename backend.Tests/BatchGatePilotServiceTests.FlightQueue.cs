using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3710: formed batches start first in, first out. On 10 November 2026
/// batches formed 03:47 to 04:40 UTC waited while batches formed at 04:50 and
/// 05:11 started within seconds: the start pass followed the enumeration order
/// of the hashed batch folders, not the formation order.
/// </summary>
public sealed class BatchGatePilotFlightQueueTests : BatchGatePilotServiceTests
{
    // Formed oldest first. The store names a batch folder by the SHA-256 of its
    // ID, so the oldest batch gets the highest folder name and the ordinal
    // folder order is the reverse of the formation order.
    private static readonly (string BatchId, string Prefix)[] Formation =
        new[] { new string('a', 32), new string('b', 32), new string('c', 32) }
            .OrderByDescending(Folder, StringComparer.Ordinal)
            .Zip(new[] { "CODE-FIFO-OLDEST", "CODE-FIFO-MIDDLE", "CODE-FIFO-YOUNGEST" })
            .ToArray();

    private static string Folder(string batchId)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(batchId)));

    [Fact]
    public async Task Formed_batches_start_oldest_first_although_their_folders_enumerate_in_another_order()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        Configure(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        FormInReverseFolderOrder(factory, store);
        Assert.Equal(Formation.Select(item => item.BatchId),
            store.ListRecoverableManifests().Select(manifest => manifest.BatchId));
        var queue = FlightQueue(factory);
        Assert.Equal(Formation.Select(item => item.BatchId), queue.Select(entry => entry.BatchId));
        Assert.Equal([1, 2, 3], queue.Select(entry => entry.Position));

        var started = await RunQueueAsync(factory);

        Assert.Equal(Formation.Select(item => item.Prefix), started);
        Assert.All(Formation, item => AssertLane(item.Prefix + "-A", TaskStates.HumanReview));
    }

    [Fact]
    public async Task Formed_batches_keep_their_formation_order_after_a_service_restart()
    {
        using (var before = BuildFactory())
        {
            _ = before.CreateClient();
            Configure(before);
            FormInReverseFolderOrder(before, before.Services.GetRequiredService<BatchGateStore>());
        }

        // A new process reads the store from disk and recovers the formed
        // snapshots before planning; no in-memory order survives the restart.
        using var after = BuildFactory();
        _ = after.CreateClient();
        Configure(after);
        Assert.Equal(Formation.Select(item => item.BatchId),
            FlightQueue(after).Select(entry => entry.BatchId));

        var started = await RunQueueAsync(after);

        Assert.Equal(Formation.Select(item => item.Prefix), started);
    }

    [Fact]
    public void State_snapshot_positions_equal_the_start_order_of_the_pilot()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        Configure(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        FormInReverseFolderOrder(factory, store);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var formed = store.ListRecoverableManifests()
            .Where(manifest => store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .ToArray();

        // Formation order first, then with the oldest batch deferred after a
        // lease-deadline cancellation, so the start order is not the formation order.
        Assert.Equal(Formation.Select(item => item.BatchId), StartOrder(pilot, formed));
        AssertSnapshotFollowsStartOrder(factory, pilot, formed);
        pilot.DeferAtLeaseDeadlineForTesting(ProjectName, Formation[0].BatchId);
        Assert.Equal(Formation.Skip(1).Append(Formation[0]).Select(item => item.BatchId),
            StartOrder(pilot, formed));
        AssertSnapshotFollowsStartOrder(factory, pilot, formed);
    }

    private static IReadOnlyList<string> StartOrder(BatchGatePilotService pilot,
        IReadOnlyList<BatchGateManifest> formed)
        => pilot.StartOrderForTesting(formed).Select(manifest => manifest.BatchId).ToArray();

    private static void AssertSnapshotFollowsStartOrder(WebApplicationFactory<Program> factory,
        BatchGatePilotService pilot, IReadOnlyList<BatchGateManifest> formed)
    {
        var queue = FlightQueue(factory);
        Assert.Equal(StartOrder(pilot, formed), queue.Select(entry => entry.BatchId));
        Assert.Equal(Enumerable.Range(1, formed.Count), queue.Select(entry => entry.Position));
    }

    private static void Configure(WebApplicationFactory<Program> factory)
    {
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        // Keep the closed snapshots distinct while the test drives the start queue.
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBatchGate(ProjectName, Settings(factory) with { Planner = "look-ahead" });
    }

    private void FormInReverseFolderOrder(WebApplicationFactory<Program> factory, BatchGateStore store)
    {
        foreach (var (batchId, prefix) in Formation)
        {
            var keys = new[] { prefix + "-A", prefix + "-B" };
            foreach (var key in keys) SeedMember(factory, key, "src/" + key.ToLowerInvariant() + ".cs");
            var pending = store.ListPending().Where(item => keys.Contains(item.Subject.TaskKey)).ToArray();
            var first = pending[0].Subject;
            var scope = new BatchGateScope(first.Project, first.Repository, first.IntegrationBranch,
                first.GateProfile, first.GateProfileDigest, first.PlatformVersion);
            var close = BatchGatePolicy.Close(pending.Select(item => item.Subject), scope,
                RemoteTip(), Settings(factory), keys, _clock);
            var manifest = Assert.IsType<BatchGateManifest>(close.Manifest) with { BatchId = batchId };
            store.CloseManifest(manifest);
            foreach (var item in pending)
                BatchGateOwnershipStore.Write(item.JobFolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId, item.Subject, manifest.BatchId));
            _clock = _clock.AddMinutes(7);
        }
        Assert.Equal(Formation.Select(item => store.BatchDirectory(item.BatchId)).Reverse(),
            Directory.EnumerateDirectories(Path.Combine(_workspace, "batch-gates"))
                .Where(folder => Formation.Any(item => store.BatchDirectory(item.BatchId) == folder))
                .Order(StringComparer.Ordinal));
    }

    private async Task<IReadOnlyList<string>> RunQueueAsync(WebApplicationFactory<Program> factory)
    {
        var started = new List<string>();
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.BeforeReservationForTesting = manifest =>
        {
            lock (started)
                started.Add(Formation.Single(item => manifest.Members[0].TaskKey
                    .StartsWith(item.Prefix + "-", StringComparison.Ordinal)).Prefix);
            return Task.CompletedTask;
        };
        for (var tick = 0; tick < 3 && started.Count < Formation.Length; tick++)
        {
            await pilot.TickAsync(CancellationToken.None);
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
            _clock = _clock.AddMinutes(1);
        }
        lock (started) return started.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<(string BatchId, int Position)> FlightQueue(
        WebApplicationFactory<Program> factory)
    {
        using var state = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            factory.Services.GetRequiredService<BatchGatePilotService>().State(ProjectName)));
        var positions = state.RootElement.GetProperty("batches").EnumerateArray()
            .Where(batch => batch.GetProperty("queuePosition").ValueKind == System.Text.Json.JsonValueKind.Number)
            .ToDictionary(batch => batch.GetProperty("BatchId").GetString()!,
                batch => batch.GetProperty("queuePosition").GetInt32());
        var queue = state.RootElement.GetProperty("flightQueue").EnumerateArray()
            .Select(entry => (entry.GetProperty("BatchId").GetString()!, entry.GetProperty("position").GetInt32()))
            .ToArray();
        Assert.All(queue, entry => Assert.Equal(entry.Item2, positions[entry.Item1]));
        return queue;
    }
}
