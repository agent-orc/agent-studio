using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Tick_recovers_a_formed_manifest_missing_from_the_pilots_flights()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var members = new[]
        {
            SeedMember(factory, "CODE-LOST-A", "src/lost-a.cs"),
            SeedMember(factory, "CODE-LOST-B", "src/lost-b.cs"),
        };
        var formed = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        AssertPhase(store, formed.BatchId, BatchPhase.Formed);
        Assert.Empty(store.ListRuns(formed.BatchId));
        _gate.Batch = request => Green(request.ExpectedSha);

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertPhase(store, formed.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
    }

    [Fact]
    public async Task Formed_manifest_starts_even_when_later_planning_fails()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        SeedMember(factory, "CODE-RECOVER-A", "src/recover-a.cs");
        SeedMember(factory, "CODE-RECOVER-B", "src/recover-b.cs");
        var formed = ClosePendingBatch(factory);
        SeedMember(factory, "CODE-NEXT-A", "src/next-a.cs");
        SeedMember(factory, "CODE-NEXT-B", "src/next-b.cs");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.BeforeFormationAdmissionForTesting = () => throw new InvalidOperationException("planner interrupted");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.TickAsync(CancellationToken.None));
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        AssertPhase(store, formed.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Tick_starts_a_formed_batch_past_its_maximum_wait_before_a_later_larger_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3, scope: "code");
        var olderMembers = new[]
        {
            SeedMember(factory, "CODE-OLD-A", "src/old-a.cs"),
            SeedMember(factory, "CODE-OLD-B", "src/old-b.cs"),
        };
        var older = ClosePendingBatch(factory);
        // AGT-3562: within the maximum wait the larger batch starts first; past
        // it the older batch starts regardless of size.
        _clock = _clock.AddMinutes(Settings(factory).MaximumWaitMinutes + 1);
        var newerMembers = new[]
        {
            SeedMember(factory, "CODE-NEW-A", "src/new-a.cs"),
            SeedMember(factory, "CODE-NEW-B", "src/new-b.cs"),
            SeedMember(factory, "CODE-NEW-C", "src/new-c.cs"),
        };
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var newPending = store.ListPending().Where(item => newerMembers.Any(member =>
            member.Key == item.Subject.TaskKey)).ToArray();
        var close = BatchGatePolicy.Close(newPending.Select(item => item.Subject), older.Scope,
            older.BaseSha, Settings(factory), newerMembers.Select(member => member.Key).ToArray(), _clock);
        var newer = Assert.IsType<BatchGateManifest>(close.Manifest);
        store.CloseManifest(newer);
        foreach (var item in newPending)
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject, newer.BatchId));
        // Keep both closed snapshots distinct while testing the start queue.
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBatchGate(ProjectName, Settings(factory) with { Planner = "look-ahead" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        try
        {
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted.WaitAsync(TimeSpan.FromSeconds(45));

            AssertPhase(store, older.BatchId, BatchPhase.Running);
            AssertPhase(store, newer.BatchId, BatchPhase.Formed);
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }

        // The finished flight started the newer batch at once; it re-formed on
        // the moved base and its successor started at once as well.
        var successor = Assert.Single(store.ListManifests(), item =>
            item.ReformedFromBatchId == newer.BatchId);
        AssertPhase(store, newer.BatchId, BatchPhase.Abandoned);

        AssertPhase(store, older.BatchId, BatchPhase.Published);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        Assert.All(olderMembers.Concat(newerMembers), member =>
            AssertLane(member.Key, TaskStates.HumanReview));
    }

    [Fact]
    public void State_keeps_an_old_formed_batch_visible_and_explains_overdue_members()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var members = new[]
        {
            SeedMember(factory, "DOC-ORPHAN-A", "docs/orphan-a.md"),
            SeedMember(factory, "DOC-ORPHAN-B", "docs/orphan-b.md"),
        };
        var formed = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        store.AppendPlanDecision(ProjectName, new BatchPlanDecision("idle",
            "waiting for threshold or deadline", _clock.AddMinutes(20), [], 3, []));
        _clock = _clock.AddMinutes(150);

        using var state = JsonDocument.Parse(JsonSerializer.Serialize(
            factory.Services.GetRequiredService<BatchGatePilotService>().State(ProjectName, history: 0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var plan = state.RootElement.GetProperty("plan");
        Assert.Equal("wait", plan.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("closeAtUtc").ValueKind);
        foreach (var member in members)
            Assert.Contains(member.Key + ": batch " + formed.BatchId,
                plan.GetProperty("reason").GetString());
        Assert.Contains(state.RootElement.GetProperty("batches").EnumerateArray(), item =>
            item.GetProperty("batchId").GetString() == formed.BatchId);
    }

    [Fact]
    public void State_never_reports_idle_for_an_overdue_unowned_pending_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var member = SeedMember(factory, "DOC-OVERDUE", "docs/overdue.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        store.AppendPlanDecision(ProjectName, new BatchPlanDecision("idle",
            "waiting for threshold or deadline", _clock.AddMinutes(20), [], 3, []));
        _clock = _clock.AddMinutes(150);

        using var state = JsonDocument.Parse(JsonSerializer.Serialize(
            factory.Services.GetRequiredService<BatchGatePilotService>().State(ProjectName),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var plan = state.RootElement.GetProperty("plan");
        Assert.Equal("wait", plan.GetProperty("decision").GetString());
        Assert.Contains(member.Key + ": pending member awaits the next planner tick",
            plan.GetProperty("reason").GetString());
        Assert.Contains(plan.GetProperty("proposedMembers").EnumerateArray(), key =>
            key.GetString() == member.Key);
    }
}
