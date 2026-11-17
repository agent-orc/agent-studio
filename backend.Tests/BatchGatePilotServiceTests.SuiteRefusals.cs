using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3856: on 3 and 4 November 2026 36 of 40 green per-card suites were
/// followed by a batch gate for the same card. A lone card past its deadline
/// takes the per-task route; when a partner arrives while its suite waits
/// behind a running batch, the batch gates it instead.
/// </summary>
public sealed class BatchGatePilotSuiteRefusalTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task A_lone_card_whose_partner_arrives_while_its_suite_waits_gets_no_per_card_suite()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        LookAhead(factory);
        var (pilot, store, release, start) = await RunBatchWithLoneCardAsync(factory, "DOC-REFUSE-A");
        var lone = store.ListPending().Single(item => item.Subject.TaskKey == "DOC-REFUSE-A");
        // The partner's review passes while the lone card's suite waits for the batch.
        _clock = start.AddMinutes(1);
        var partner = SeedMember(factory, "DOC-REFUSE-B", "docs/refuse-b.md");

        release.SetResult();
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        var refusal = Assert.Single(store.ListSuiteRefusals(ProjectName));
        Assert.Equal((lone.Subject.TaskKey, lone.ReviewAttemptId, PerTaskFallbackRules.PlannerPerTask),
            (refusal.TaskKey, refusal.ReviewAttemptId, refusal.Rule));
        Assert.Equal("1 batch partner waits: the next batch gates this card instead of a per-card suite",
            refusal.Reason);
        Assert.Empty(store.ListFallbackCauses(ProjectName));
        // The batch panel and the card read the reason from the state.
        var state = JsonSerializer.SerializeToElement(pilot.State(ProjectName));
        var member = state.GetProperty("members").GetProperty(lone.Subject.TaskKey);
        Assert.Equal("pending", member.GetProperty("status").GetString());
        Assert.Equal(BatchGatePilotService.SuiteRefusedReason, member.GetProperty("reason").GetString());
        Assert.Equal(refusal.Reason, member.GetProperty("reasonDetail").GetString());
        Assert.Equal(lone.Subject.TaskKey,
            state.GetProperty("suiteRefusals")[0].GetProperty("TaskKey").GetString());
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, lone.Subject.TaskKey);
        Assert.Contains(factory.Services.GetRequiredService<TimelineLog>().ReadAll(folder),
            entry => entry.Kind == BatchGatePilotService.SuiteRefusedEventKind
                && entry.Details?.GetValueOrDefault("reason") == refusal.Reason);

        // The next tick closes the lone card and its partner as one batch.
        _gate.BatchAsync = null;
        _gate.Batch = request => Green(request.ExpectedSha);
        pilot.WaitForFlightsInTick = true;
        _clock = start.AddMinutes(2);
        await pilot.TickAsync(CancellationToken.None);

        var next = store.ListManifests().Single(manifest => manifest.Members.Any(item => item.TaskKey == partner.Key));
        Assert.Equal([lone.Subject.TaskKey, partner.Key], next.Members.Select(item => item.TaskKey));
        AssertPhase(store, next.BatchId, BatchPhase.Published);
        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(lone.Subject.TaskKey, TaskStates.HumanReview);
        AssertLane(partner.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task A_lone_card_without_a_partner_still_gets_its_suite()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        LookAhead(factory);
        var (pilot, store, release, _) = await RunBatchWithLoneCardAsync(factory, "DOC-ALONE-A");

        release.SetResult();
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Empty(store.ListSuiteRefusals(ProjectName));
        AssertLane("DOC-ALONE-A", TaskStates.HumanReview);
    }

    private static void LookAhead(WebApplicationFactory<Program> factory)
    {
        EnableBatchGate(factory, closeSize: 2);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(
                Enabled: true, Planner: "look-ahead", MinimumSize: 2, MaximumSize: 4,
                DeadlineMinutes: 20, LookAheadMinutes: 0));
    }

    /// <summary>
    /// A batch of two runs and holds its gate; a lone card past its deadline
    /// takes the per-task route and its suite waits for the running batch.
    /// </summary>
    private async Task<(BatchGatePilotService Pilot, BatchGateStore Store, TaskCompletionSource Release,
        DateTimeOffset Start)> RunBatchWithLoneCardAsync(WebApplicationFactory<Program> factory, string loneKey)
    {
        var start = _clock;
        SeedMember(factory, loneKey + "-X", "docs/" + loneKey.ToLowerInvariant() + "-x.md");
        SeedMember(factory, loneKey + "-Y", "docs/" + loneKey.ToLowerInvariant() + "-y.md");
        ClosePendingBatch(factory);
        _clock = start.AddMinutes(-25);
        SeedMember(factory, loneKey, "docs/" + loneKey.ToLowerInvariant() + ".md");
        _clock = start;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        _gate.Fallback = request => Green(request.ExpectedSha);
        _gate.Merge = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        pilot.WaitForFlightsInTick = false;

        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstBatchStarted;
        await WaitForAsync(() => pilot.PerTaskGatesRunning == 1);

        var cause = Assert.Single(store.ListFallbackCauses(ProjectName));
        Assert.Equal(loneKey, cause.TaskKey);
        Assert.Equal(PerTaskFallbackRules.PlannerPerTask, cause.Rule);
        Assert.EndsWith(BatchGateLookAheadPlanner.BelowMinimumReason, cause.Event);
        Assert.Equal(0, _gate.FallbackRuns);
        return (pilot, store, release, start);
    }
}
