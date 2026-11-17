using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3864: the live store held 918 manifests and every tick derived the
/// isolation findings of each parent by listing every manifest again, which
/// kept the API near 185 % CPU with no gate running. A tick now parses each
/// manifest at most once and a store parses an unchanged manifest only once.
/// </summary>
public sealed class BatchGatePilotTickReadTests(ITestOutputHelper output) : BatchGatePilotServiceTests
{
    // About the live store: 600 settled batches and 200 isolated parents with one child each.
    private const int StandaloneBatches = 600;
    private const int IsolatedParents = 200;
    // The tick lists the manifests once per step, not once per parent.
    private const int MaxListingsPerTick = 64;

    private sealed record TickReads(long Listings, long Reads, long Parses);

    private static async Task<TickReads> MeasuredTickAsync(BatchGatePilotService pilot, BatchGateStore store)
    {
        var (listings, reads, parses) = (store.ManifestListings, store.ManifestReads, store.ManifestParses);
        await pilot.TickAsync(CancellationToken.None);
        return new TickReads(store.ManifestListings - listings, store.ManifestReads - reads,
            store.ManifestParses - parses);
    }

    [Fact]
    public async Task One_tick_over_many_manifests_parses_each_manifest_at_most_once()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-READS-A", "docs/reads-a.md");
        SeedMember(factory, "DOC-READS-B", "docs/reads-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var template = ClosePendingBatch(factory);
        BatchGateStates.Reach(store, new BatchGateState(template.BatchId, template.MembershipDigest,
            BatchPhase.Abandoned, null, 1, _clock, "fixture"));
        AddSettledHistory(store, template);

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        var cold = restarted.Services.GetRequiredService<BatchGateStore>();
        var pilot = restarted.Services.GetRequiredService<BatchGatePilotService>();
        var first = await MeasuredTickAsync(pilot, cold);
        var second = await MeasuredTickAsync(pilot, cold);
        // The tick may form a batch of its own; count the manifests it ended with.
        var manifests = cold.ListManifestFolders().Count;
        output.WriteLine($"manifests={manifests} first={first} second={second}");

        Assert.True(manifests >= 1000, $"fixture holds {manifests} manifests");
        Assert.True(first.Parses <= manifests, $"the first tick parsed {first.Parses} manifests of {manifests}");
        Assert.True(second.Parses == 0, $"the second tick parsed {second.Parses} unchanged manifests of {manifests}");
        // Linear: the number of listings does not grow with the number of parents.
        Assert.True(first.Listings < MaxListingsPerTick && second.Listings < MaxListingsPerTick,
            $"the ticks listed every manifest {first.Listings} and {second.Listings} times");
        Assert.True(second.Reads <= second.Listings * (manifests + 4),
            $"the second tick served {second.Reads} manifest reads over {second.Listings} listings");
    }

    [Fact]
    public async Task Isolation_findings_are_derived_for_a_parent_whose_child_just_finished()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var a = SeedMember(factory, "DOC-READS-RED", "docs/reads-red.md");
        SeedMember(factory, "DOC-READS-GREEN", "docs/reads-green.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var parent = ClosePendingBatch(factory);
        AddSettledHistory(store, parent);
        BatchGateStates.Reach(store, new BatchGateState(parent.BatchId, parent.MembershipDigest,
            BatchPhase.Running, null, 1, _clock));
        var child = CloseChild(store, parent, [a.Key]);
        BatchGateStates.Reach(store, new BatchGateState(child.BatchId, child.MembershipDigest,
            BatchPhase.Assembled, child.BaseSha, 1, _clock));
        // A live coordinator still owns the isolation, so recovery leaves it alone.
        var leases = factory.Services.GetRequiredService<BatchGateLeaseService>();
        var held = leases.TryAcquire(parent.Scope, "isolation-flight")!;
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(store.ListIsolationFindings(ProjectName), finding => finding.TaskKey == a.Key);
        FinishRed(store, child);
        var tick = await MeasuredTickAsync(pilot, store);
        leases.Release(held);
        output.WriteLine($"after the child finished: {tick}");

        Assert.Contains(store.ListIsolationFindings(ProjectName), finding =>
            finding.ParentBatchId == parent.BatchId && finding.TaskKey == a.Key
            && finding.Status == "proven-red" && finding.ProvingBatchId == child.BatchId);
        Assert.True(tick.Parses == 0, $"the tick after the child finished parsed {tick.Parses} unchanged manifests");
        Assert.True(tick.Listings < MaxListingsPerTick, $"the tick listed every manifest {tick.Listings} times");
    }

    [Fact]
    public async Task A_restart_over_many_manifests_still_recovers_an_interrupted_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-READS-RESTART-A", "docs/reads-restart-a.md");
        SeedMember(factory, "DOC-READS-RESTART-B", "docs/reads-restart-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var parent = ClosePendingBatch(factory);
        AddSettledHistory(store, parent);
        BatchGateStates.Reach(store, new BatchGateState(parent.BatchId, parent.MembershipDigest,
            BatchPhase.Red, null, 1, _clock, "deterministic suite red"));
        var child = CloseChild(store, parent, [parent.Members[0].TaskKey]);
        BatchGateStates.Reach(store, new BatchGateState(child.BatchId, child.MembershipDigest,
            BatchPhase.Running, null, 1, _clock));
        foreach (var item in store.ListPending())
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject));
        _gate.Batch = request => Green(request.ExpectedSha);

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        var cold = restarted.Services.GetRequiredService<BatchGateStore>();
        var tick = await MeasuredTickAsync(restarted.Services.GetRequiredService<BatchGatePilotService>(), cold);
        var manifests = cold.ListManifestFolders().Count;
        output.WriteLine($"manifests={manifests} restarted tick: {tick}");

        Assert.Equal(BatchPhase.Abandoned, cold.LatestState(child.BatchId).Phase);
        Assert.Equal(BatchPhase.Paused, cold.LatestState(parent.BatchId).Phase);
        Assert.Equal("isolation interrupted", cold.LatestState(parent.BatchId).Reason);
        foreach (var member in parent.Members)
            Assert.NotEqual(parent.BatchId, Ownership(member.TaskKey)?.BatchId);
        Assert.True(tick.Parses <= manifests, $"the restarted tick parsed {tick.Parses} manifests of {manifests}");
        Assert.True(tick.Listings < MaxListingsPerTick, $"the restarted tick listed every manifest {tick.Listings} times");
    }

    // Settled batches of earlier days: abandoned batches and abandoned isolated
    // parents whose child ended red or abandoned.
    private void AddSettledHistory(BatchGateStore store, BatchGateManifest template)
    {
        for (var index = 0; index < StandaloneBatches; index++)
            Settle(store, template with { BatchId = Guid.NewGuid().ToString("N") }, BatchPhase.Abandoned);
        for (var index = 0; index < IsolatedParents; index++)
        {
            var parent = template with { BatchId = Guid.NewGuid().ToString("N") };
            Settle(store, parent, BatchPhase.Abandoned);
            var child = CloseChild(store, parent, [template.Members[0].TaskKey]);
            if (index % 2 == 0) Settle(store, child, BatchPhase.Abandoned);
            else FinishRed(store, child, classification: "InfrastructureRed");
        }

        void Settle(BatchGateStore target, BatchGateManifest manifest, BatchPhase phase)
        {
            if (!target.HasManifest(manifest.BatchId)) target.CloseManifest(manifest);
            BatchGateStates.Reach(target, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                phase, null, 1, _clock, "fixture"));
        }
    }

    private static BatchGateManifest CloseChild(BatchGateStore store, BatchGateManifest parent, string[] keys)
    {
        var members = parent.Members.Where(member => keys.Contains(member.TaskKey)).ToArray();
        var child = parent with
        {
            BatchId = Guid.NewGuid().ToString("N"),
            MembershipDigest = BatchGatePolicy.MembershipDigest(parent.BaseSha, parent.Scope, members),
            Members = members,
            ParentBatchId = parent.BatchId,
            Diagnostic = true,
        };
        store.CloseManifest(child);
        return child;
    }

    // The run and verdict land before the red phase, as the isolation flight writes them.
    private void FinishRed(BatchGateStore store, BatchGateManifest child,
        string classification = "DeterministicSuiteRed")
    {
        var runId = Guid.NewGuid().ToString("N");
        var evidence = Path.Combine(_workspace, runId + ".jsonl");
        File.WriteAllText(evidence, JsonSerializer.Serialize(Red(child.BaseSha)));
        BatchGateStates.Reach(store, new BatchGateState(child.BatchId, child.MembershipDigest,
            BatchPhase.Assembled, child.BaseSha, 1, _clock));
        store.RecordRun(new BatchGateRunRecord(runId, child.BatchId, child.MembershipDigest,
            child.BaseSha, child.BaseSha, child.Scope.GateProfile,
            child.Scope.GateProfileDigest, "fixture", 1, ["echo test"], _clock, evidence));
        store.RecordVerdict(new BatchGateRunVerdict(runId, child.BatchId, child.MembershipDigest,
            child.BaseSha, child.Scope.GateProfileDigest, "red", classification, evidence, _clock));
        BatchGateStates.Reach(store, new BatchGateState(child.BatchId, child.MembershipDigest,
            BatchPhase.Red, child.BaseSha, 1, _clock));
    }
}
