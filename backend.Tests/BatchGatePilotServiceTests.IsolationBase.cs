using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3752: the base of later isolation subsets after a partial publication.</summary>
public sealed class BatchGatePilotIsolationBaseTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Subsets_after_an_own_publication_run_on_the_published_tip()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4, scope: "code");
        var members = new[] { "a", "b", "c", "d" }
            .Select(name => SeedMember(factory, $"CODE-ISO-{name.ToUpperInvariant()}", $"src/iso-{name}.cs"))
            .ToArray();
        var runs = new List<(string Candidate, string Tip)>();
        _gate.Batch = request =>
        {
            runs.Add((request.ExpectedSha!, RemoteTip()));
            return TreeHas(request.ExpectedSha!, "src/iso-d.cs")
                ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.All(runs, run => Assert.Equal(run.Tip, Git(_repo, "merge-base", run.Tip, run.Candidate)));
        Assert.DoesNotContain(store.ListManifests().Where(manifest => manifest.Diagnostic),
            manifest => store.LatestState(manifest.BatchId).Reason == nameof(BatchPublishDecision.StaleBase));
        foreach (var member in members.Take(3))
        {
            AssertLane(member.Key, TaskStates.HumanReview);
            Assert.True(TreeHas(RemoteTip(), $"src/iso-{member.Key[^1..].ToLowerInvariant()}.cs"));
        }
        Assert.False(TreeHas(RemoteTip(), "src/iso-d.cs"));
        // The culprit is known by elimination; the unchanged per-task fallback
        // proves it red on its own and keeps it out of the tip.
        Assert.Equal(1, _gate.FallbackRuns);
        Assert.False(TreeHas(RemoteTip(), "src/iso-d.cs"));
        AssertLane(members[3].Key, TaskStates.Escalated);
        Assert.Equal(0, Assert.Single(store.ListPlanObservations(ProjectName)).IsolationDiscardedPasses);
    }

    [Fact]
    public async Task External_tip_move_during_isolation_keeps_stale_base_handling()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4, scope: "code");
        var members = new[] { "a", "b", "c", "d" }
            .Select(name => SeedMember(factory, $"CODE-EXT-{name.ToUpperInvariant()}", $"src/ext-{name}.cs"))
            .ToArray();
        var moved = false;
        _gate.Batch = request =>
        {
            var red = TreeHas(request.ExpectedSha!, "src/ext-d.cs");
            if (!red && !moved)
            {
                moved = true;
                AdvanceOriginDevelop("src/external-move.cs");
            }
            return red ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.True(moved);
        Assert.Contains(store.ListManifests().Where(manifest => manifest.Diagnostic),
            manifest => store.LatestState(manifest.BatchId).Reason == nameof(BatchPublishDecision.StaleBase));
        Assert.All(members.Take(3), member => Assert.False(TreeHas(RemoteTip(),
            $"src/ext-{member.Key[^1..].ToLowerInvariant()}.cs")));
        Assert.True(Assert.Single(store.ListPlanObservations(ProjectName)).IsolationDiscardedPasses > 0);
    }
}
