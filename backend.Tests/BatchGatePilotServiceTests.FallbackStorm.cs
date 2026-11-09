using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3466: 3 November 2026. Deploy 32 restarted the API while batch
/// 944a1c38 was formed and waiting for the scope lease. The batch was
/// abandoned, four of its six members and then most newly reviewed cards took
/// per-task gates, and 13 gates ran on a host with five slots.
/// </summary>
public sealed class BatchGatePilotFallbackStormTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Close_now_below_minimum_keeps_the_policy_reason_in_the_plan()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4, scope: "code");
        var member = SeedMember(factory, "CODE-CLOSE-REASON", "src/close-reason.cs");
        _gate.Fallback = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.RequestCloseNow(ProjectName);

        await pilot.TickAsync(CancellationToken.None);

        var plan = factory.Services.GetRequiredService<BatchGateStore>()
            .LatestPlanDecision(ProjectName)!;
        Assert.Equal("per-task", plan.Decision);
        Assert.Equal("close requested below minimum batch size", plan.Reason);
        Assert.Equal([member.Key], plan.ProposedMembers);
    }

    [Fact]
    public async Task Restart_with_a_formed_batch_keeps_its_members_out_of_per_task_gates()
    {
        var seededAt = _clock;
        BatchGateManifest formed;
        SeededMember[] members;
        using (var factory = BuildFactory())
        {
            _ = factory.CreateClient();
            EnablePreCheck(factory, closeSize: 3);
            members = new SeededMember[3];
            for (var index = 0; index < members.Length; index++)
            {
                members[index] = SeedMember(factory, $"CODE-FORMED-{index}", $"src/formed-{index}.cs");
                _clock = _clock.AddMinutes(5);
            }
            formed = ClosePendingBatch(factory);
        }
        // A publication moves develop while the batch waits; the API restarts.
        AdvanceOriginDevelop("src/drift.cs");
        var movedBase = RemoteTip();
        _clock = seededAt.AddMinutes(22);

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnablePreCheck(restarted, closeSize: 3);
        OccupyGateSlots(restarted, 3);
        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = restarted.Services.GetRequiredService<BatchGatePilotService>();
        var store = restarted.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);

        // Before AGT-3466 the members returned to planning past their deadline;
        // the first one was admitted alone and took a per-task gate.
        Assert.Equal(0, _gate.FallbackRuns);
        var retired = AssertPhase(store, formed.BatchId, BatchPhase.Abandoned);
        var successor = Assert.Single(store.ListManifests(), manifest => manifest.BatchId != formed.BatchId);
        Assert.Contains("re-formed as " + successor.BatchId, retired.Reason);
        Assert.Equal(members.Select(member => member.Key), successor.Members.Select(member => member.TaskKey));
        Assert.Equal(movedBase, successor.BaseSha);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
        Assert.Empty(store.ListFallbackCauses(ProjectName));
    }

    [Fact]
    public async Task With_many_pending_cards_and_a_busy_lease_a_new_card_waits_for_the_next_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnablePreCheck(factory, closeSize: 4);
        var running = new[]
        {
            SeedMember(factory, "CODE-LEASE-X", "src/lease-x.cs"),
            SeedMember(factory, "CODE-LEASE-Y", "src/lease-y.cs"),
        };
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var busy = ClosePendingBatch(factory);
        // The incident host: every slot busy, so no pre-check can run.
        OccupyGateSlots(factory, 3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstBatchStarted;

        // The busy batch has entered its gate. Seed older reviews without
        // advancing the fake lease clock past its two-minute TTL.
        var started = _clock;
        _clock = started.AddMinutes(-21);
        var lone = SeedMember(factory, "CODE-LEASE-A", "src/lease-a.cs");
        _clock = started.AddMinutes(-16);
        var partner = SeedMember(factory, "CODE-LEASE-B", "src/lease-b.cs");
        _clock = started.AddMinutes(-11);
        var late = SeedMember(factory, "CODE-LEASE-C", "src/lease-c.cs");
        // A is past its 20 minute deadline; B and C wait for their pre-check.
        _clock = started.AddSeconds(1);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);

        var plan = store.LatestPlanDecision(ProjectName)!;
        Assert.Equal("wait", plan.Decision);
        Assert.Equal(BatchGateLookAheadPlanner.QueuedPartnersReason, plan.Reason);
        Assert.Equal([lone.Key], plan.ProposedMembers);
        Assert.Equal(0, _gate.PreCheckRuns);
        var waiting = JsonSerializer.SerializeToElement(pilot.State(ProjectName)).GetProperty("members");
        Assert.Equal("pending", waiting.GetProperty(lone.Key).GetProperty("status").GetString());

        release.SetResult();
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        AssertPhase(store, busy.BatchId, BatchPhase.Published);
        Assert.All(running, member => AssertLane(member.Key, TaskStates.HumanReview));
        Assert.Equal(0, _gate.FallbackRuns);

        // B reaches its deadline: the held card and B close the next batch.
        _gate.BatchAsync = null;
        _gate.Batch = request => Green(request.ExpectedSha);
        pilot.WaitForFlightsInTick = true;
        _clock = started.AddMinutes(5);
        await pilot.TickAsync(CancellationToken.None);

        var next = Assert.Single(store.ListManifests(), manifest => manifest.BatchId != busy.BatchId);
        Assert.Equal([lone.Key, partner.Key], next.Members.Select(member => member.TaskKey));
        AssertPhase(store, next.BatchId, BatchPhase.Published);
        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(late.Key, TaskStates.AutoReview);
        Assert.Equal(late.ReviewAttemptId, Assert.Single(store.ListPending()).ReviewAttemptId);
    }

    [Fact]
    public async Task Per_task_fallback_gates_leave_the_batch_slot_free_and_name_their_rule()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var members = Enumerable.Range(0, 4)
            .Select(index => SeedMember(factory, $"DOC-SLOT-{index}", $"docs/slot-{index}.md")).ToArray();
        // Switched off, the batch gate hands every queued card to its own gate at once.
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        factory.Services.GetRequiredService<GateSlotUsageTracker>().Configure(3);
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.FallbackAsync = async (request, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return Red(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;

        await pilot.TickAsync(CancellationToken.None);
        await WaitForAsync(() => _gate.FallbackRuns == 2 && pilot.PerTaskGatesWaiting == 2);

        // Three slots: two per-task gates run, the third slot stays the batch's.
        Assert.Equal(2, pilot.PerTaskGatesRunning);
        var state = JsonSerializer.SerializeToElement(pilot.State(ProjectName)).GetProperty("members");
        Assert.All(members, member =>
        {
            var entry = state.GetProperty(member.Key);
            Assert.Equal("fallback", entry.GetProperty("status").GetString());
            Assert.Equal(PerTaskFallbackRules.BatchDisabled, entry.GetProperty("fallbackRule").GetString());
            Assert.Equal("the batch gate is off or the project integrates post-merge",
                entry.GetProperty("fallbackEvent").GetString());
        });

        release.SetResult();
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(4, _gate.FallbackRuns);
        Assert.Equal(2, peak);
        Assert.Equal(0, pilot.PerTaskGatesRunning);
        Assert.Equal(0, pilot.PerTaskGatesWaiting);
    }
}

public abstract partial class BatchGatePilotServiceTests
{
    private protected static void OccupyGateSlots(WebApplicationFactory<Program> factory, int total)
    {
        var slots = factory.Services.GetRequiredService<GateSlotUsageTracker>();
        for (var slot = 0; slot < total; slot++)
            slots.Acquired($"foreign-{slot}", total);
    }

    private protected static void InterlockedMax(ref int target, int value)
    {
        for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }
}
