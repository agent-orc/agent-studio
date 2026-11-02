using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3407: on 1 November 2026 six reviewed cards each took a per-task gate
/// of about 50 minutes behind three busy slots, because the gate host counted
/// as idle whenever no batch ran. Busy slots now keep a lone card waiting for
/// a batch partner until its deadline.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Lone_card_behind_busy_gate_slots_waits_and_closes_with_the_next_card()
    {
        using var factory = IdleHostFactory();
        factory.Services.GetRequiredService<GateSlotUsageTracker>().Acquired("other-project-gate", 1);
        SeedMember(factory, "DOC-SLOT-A", "docs/slot-a.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        _clock = _clock.AddMinutes(10);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Empty(store.ListManifests());
        var held = store.LatestPlanDecision(ProjectName)!;
        Assert.Equal("wait", held.Decision);
        Assert.Equal(["DOC-SLOT-A"], held.ProposedMembers);

        SeedMember(factory, "DOC-SLOT-B", "docs/slot-b.md");
        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal(["DOC-SLOT-A", "DOC-SLOT-B"],
            manifest.Members.Select(member => member.TaskKey).Order(StringComparer.Ordinal));
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane("DOC-SLOT-A", TaskStates.HumanReview);
        AssertLane("DOC-SLOT-B", TaskStates.HumanReview);
    }

    [Fact]
    public async Task Lone_card_on_a_free_gate_slot_takes_its_own_gate_at_once()
    {
        using var factory = IdleHostFactory();
        SeedMember(factory, "DOC-SLOT-FREE", "docs/slot-free.md");
        _gate.Fallback = request => Green(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListManifests());
        Assert.Equal("per-task", factory.Services.GetRequiredService<BatchGateStore>()
            .LatestPlanDecision(ProjectName)!.Decision);
    }

    private WebApplicationFactory<Program> IdleHostFactory()
    {
        var factory = BuildFactory(testServices: services =>
        {
            services.RemoveAll<ILoadThrottleGate>();
            services.AddSingleton<ILoadThrottleGate>(new ClearLoadGate());
        });
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(
                Enabled: true, Planner: "look-ahead", MinimumSize: 2,
                MaximumSize: 2, DeadlineMinutes: 20));
        return factory;
    }

    private sealed class ClearLoadGate : ILoadThrottleGate
    {
        public LoadThrottleDecision Current => new(false, 12, TimeSpan.Zero);
        public bool WasRecentlyActive => false;
        public Task WaitUntilReadyAsync(string reason, CancellationToken ct) => Task.CompletedTask;
    }
}
