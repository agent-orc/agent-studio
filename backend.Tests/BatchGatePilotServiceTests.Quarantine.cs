using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3681 (playthrough AGT-3648 S1-F1): one batch folder whose manifest does
/// not deserialize failed every pilot tick, so a batch left running by the API
/// restart was never recovered and every reader of the store answered 500.
/// </summary>
public sealed class BatchGatePilotQuarantineTests : BatchGatePilotServiceTests
{
    private const string StaleBatchId = "batch-stale-binary";

    [Fact]
    public async Task Truncated_manifest_next_to_a_running_batch_is_quarantined_and_the_running_batch_recovers()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var members = new[]
        {
            SeedMember(factory, "DOC-QUARANTINE-A", "docs/quarantine-a.md"),
            SeedMember(factory, "DOC-QUARANTINE-B", "docs/quarantine-b.md"),
        };
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var running = ClosePendingBatch(factory);
        store.AppendState(new BatchGateState(running.BatchId, running.MembershipDigest,
            BatchPhase.Running, members[0].ResultSha, 1, _clock));
        var stalePath = SeedTruncatedManifest(store, running);
        _gate.Batch = request => Green(request.ExpectedSha);

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        // The batch the restart left running is abandoned and its members are
        // requeued into a new batch on the same tick, which publishes.
        var abandoned = AssertPhase(store, running.BatchId, BatchPhase.Abandoned);
        Assert.Contains("interrupted before a publishable verdict", abandoned.Reason);
        var successor = Assert.Single(store.ListManifests(), manifest => manifest.BatchId != running.BatchId);
        Assert.Equal(members.Select(member => member.Key).Order(),
            successor.Members.Select(member => member.TaskKey).Order());
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));

        var quarantined = Assert.Single(store.ListQuarantined());
        Assert.Equal(StaleBatchId, quarantined.BatchId);
        Assert.Equal(ProjectName, quarantined.Project);
        Assert.Equal(BatchGateRecordKinds.Manifest, quarantined.Kind);
        Assert.Equal(stalePath, quarantined.File);

        // The state route answers 200 with the readable batches and the quarantine.
        using var response = await client.GetAsync($"/api/projects/{ProjectName}/batch-gate/state");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batchIds = state.RootElement.GetProperty("batches").EnumerateArray()
            .Select(batch => batch.GetProperty("batchId").GetString()).ToArray();
        Assert.Contains(running.BatchId, batchIds);
        Assert.DoesNotContain(StaleBatchId, batchIds);
        var listed = Assert.Single(state.RootElement.GetProperty("quarantined").EnumerateArray());
        Assert.Equal(StaleBatchId, listed.GetProperty("batchId").GetString());
        Assert.Equal(stalePath, listed.GetProperty("file").GetString());
        Assert.Contains("move the batch folder", listed.GetProperty("nextStep").GetString());

        // The operator decision source raises one attention item for it.
        var facts = new OperatorBatchGateFactSource(store).Collect(_clock.UtcDateTime);
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<OperatorDecisionFact>>(facts),
            fact => fact.Type == OperatorDecisionTypes.BatchRecordUnreadable);
        Assert.Equal(ProjectName, item.Project);
        Assert.StartsWith("Batch record unreadable", item.Reason);
        Assert.Contains(StaleBatchId, item.Reason);
        Assert.Contains(stalePath, item.Reason);
        Assert.Contains(quarantined.Error, item.Reason);
        Assert.Contains("Next step:", item.Reason);
    }

    [Fact]
    public void Restored_manifest_leaves_the_quarantine_on_the_next_read()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-RESTORE-A", "docs/restore-a.md");
        SeedMember(factory, "DOC-RESTORE-B", "docs/restore-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var formed = ClosePendingBatch(factory);
        var path = Path.Combine(store.BatchDirectory(formed.BatchId), "manifest.json");
        var intact = File.ReadAllBytes(path);
        File.WriteAllBytes(path, intact[..(intact.Length / 2)]);

        Assert.Empty(store.ListManifests());
        Assert.Equal(formed.BatchId, Assert.Single(store.ListQuarantined()).BatchId);
        Assert.Throws<BatchGateRecordUnreadableException>(() => store.ReadManifest(formed.BatchId));

        File.WriteAllBytes(path, intact);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Assert.Empty(store.ListQuarantined());
        Assert.Equal(formed.BatchId, Assert.Single(store.ListManifests()).BatchId);
    }

    // A truncated copy stands in for a record an older or newer binary cannot read.
    private static string SeedTruncatedManifest(BatchGateStore store, BatchGateManifest template)
    {
        var folder = store.BatchDirectory(StaleBatchId);
        Directory.CreateDirectory(folder);
        var json = File.ReadAllText(Path.Combine(store.BatchDirectory(template.BatchId), "manifest.json"))
            .Replace(template.BatchId, StaleBatchId, StringComparison.Ordinal);
        var path = Path.Combine(folder, "manifest.json");
        File.WriteAllText(path, json[..(json.Length * 3 / 5)]);
        return path;
    }
}
