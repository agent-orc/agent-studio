using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Two_successive_base_moves_keep_compatible_members_in_one_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3, scope: "code");
        var members = Enumerable.Range(0, 3)
            .Select(index => SeedMember(factory, $"CODE-MOVE-{index}", $"src/move-{index}.cs"))
            .ToArray();
        var original = ClosePendingBatch(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        pilot.WaitForFlightsInTick = false;
        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);

        AdvanceOriginDevelop("src/first-base-move.cs");
        var firstBase = RemoteTip();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        var first = Assert.Single(store.ListManifests(), item => item.ReformedFromBatchId == original.BatchId);
        Assert.Equal(firstBase, first.BaseSha);
        AssertPhase(store, first.BatchId, BatchPhase.Formed);

        AdvanceOriginDevelop("src/second-base-move.cs");
        var secondBase = RemoteTip();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        var second = Assert.Single(store.ListManifests(), item => item.ReformedFromBatchId == first.BatchId);
        Assert.Equal(secondBase, second.BaseSha);
        Assert.Equal(members.Select(member => member.Key), second.Members.Select(member => member.TaskKey));
        Assert.Empty(store.ListRuns(original.BatchId));
        Assert.Empty(store.ListRuns(first.BatchId));

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertPhase(store, second.BatchId, BatchPhase.Published);
        var run = Assert.Single(store.ListRuns(second.BatchId));
        var verdict = store.ReadVerdict(second.BatchId, run.BatchRunId)!;
        Assert.Equal(secondBase, run.BaseSha);
        Assert.Equal(run.CandidateSha, verdict.TestedCandidateSha);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
        using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        var totals = state.RootElement.GetProperty("baseMoveTotals");
        Assert.Equal(2, totals.GetProperty("kept").GetInt32());
        Assert.Equal(0, totals.GetProperty("abandoned").GetInt32());
        Assert.All(state.RootElement.GetProperty("baseMoves").EnumerateArray(), move =>
            Assert.Equal(1, move.GetProperty("kept").GetInt32()));
    }

    [Fact]
    public async Task Base_move_removes_conflicting_member_and_gates_survivors()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3, scope: "code");
        var conflicting = SeedMember(factory, "CODE-MOVE-CONFLICT", "src/shared.cs", content: "ours\n");
        var surviving = SeedMember(factory, "CODE-MOVE-SURVIVE", "src/other.cs");
        var secondSurvivor = SeedMember(factory, "CODE-MOVE-SURVIVE-2", "src/other-2.cs");
        var original = ClosePendingBatch(factory);
        AdvanceOriginDevelop("src/shared.cs");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        pilot.WaitForFlightsInTick = false;
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var retired = AssertPhase(store, original.BatchId, BatchPhase.Abandoned);
        Assert.Equal("merge-conflict", retired.BaseMoveMemberReasons![conflicting.Key]);
        var successor = Assert.Single(store.ListManifests(), item => item.ReformedFromBatchId == original.BatchId);
        Assert.Equal([surviving.Key, secondSurvivor.Key], successor.Members.Select(member => member.TaskKey));
        using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        var batch = state.RootElement.GetProperty("batches").EnumerateArray()
            .Single(item => item.GetProperty("BatchId").GetString() == original.BatchId);
        Assert.Equal("merge-conflict", batch.GetProperty("baseMoveMemberReasons")
            .GetProperty(conflicting.Key).GetString());
        Assert.Equal(1, state.RootElement.GetProperty("baseMoveTotals").GetProperty("kept").GetInt32());

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        AssertLane(surviving.Key, TaskStates.HumanReview);
        AssertLane(secondSurvivor.Key, TaskStates.HumanReview);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Base_move_does_not_close_a_successor_below_minimum_size()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var conflicting = SeedMember(factory, "CODE-MOVE-MIN-CONFLICT", "src/min-shared.cs",
            content: "ours\n");
        var survivor = SeedMember(factory, "CODE-MOVE-MIN-SURVIVOR", "src/min-other.cs");
        var original = ClosePendingBatch(factory);
        AdvanceOriginDevelop("src/min-shared.cs");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertPhase(store, original.BatchId, BatchPhase.Abandoned);
        Assert.DoesNotContain(store.ListManifests(), item =>
            item.ReformedFromBatchId == original.BatchId);
        Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == survivor.Key);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Equal("merge-conflict", store.LatestState(original.BatchId)
            .BaseMoveMemberReasons![conflicting.Key]);
    }

    [Fact]
    public async Task Base_move_resolves_superseded_review_without_carrying_its_verdict()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3, scope: "code");
        var superseded = SeedMember(factory, "CODE-OLD-REVIEW", "src/old-review.cs");
        var retained = new[]
        {
            SeedMember(factory, "CODE-RETAIN-A", "src/retain-a.cs"),
            SeedMember(factory, "CODE-RETAIN-B", "src/retain-b.cs"),
        };
        var original = ClosePendingBatch(factory);
        ReplaceDelivery(factory, superseded);
        AdvanceOriginDevelop("src/review-base-move.cs");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        pilot.WaitForFlightsInTick = false;
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var retired = AssertPhase(store, original.BatchId, BatchPhase.Abandoned);
        Assert.Equal("superseded-review", retired.BaseMoveMemberReasons![superseded.Key]);
        var successor = Assert.Single(store.ListManifests(), item => item.ReformedFromBatchId == original.BatchId);
        Assert.Equal(retained.Select(member => member.Key), successor.Members.Select(member => member.TaskKey));
        Assert.DoesNotContain(store.ListPending(), item => item.Subject.TaskKey == superseded.Key);
        Assert.Empty(store.ListRuns(original.BatchId));
        Assert.Empty(store.ListRuns(successor.BatchId));

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.All(retained, member => AssertLane(member.Key, TaskStates.HumanReview));
    }
}
