using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Tick_does_not_form_when_a_member_leaves_pending_during_planning()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var removed = SeedMember(factory, "DOC-FORM-LEAVE", "docs/leave.md");
        SeedMember(factory, "DOC-FORM-STAY", "docs/stay.md");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        pilot.BeforeFormationAdmissionForTesting = () =>
            store.ResolvePending(removed.ReviewAttemptId, "left-pending-during-planning");

        await pilot.TickAsync(CancellationToken.None);

        Assert.Empty(store.ListManifests());
        Assert.Contains("below minimum", store.LatestPlanDecision(ProjectName)!.Reason);
        Assert.Contains("NoLongerPending", store.LatestPlanDecision(ProjectName)!.Reason);
        Assert.Equal(0, _gate.BatchRuns);
    }

    [Fact]
    public async Task Tick_does_not_form_on_a_base_that_moved_during_planning()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-FORM-BASE-A", "docs/base-a.md");
        SeedMember(factory, "DOC-FORM-BASE-B", "docs/base-b.md");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        string? movedBase = null;
        pilot.BeforeFormationAdmissionForTesting = () =>
        {
            pilot.BeforeFormationAdmissionForTesting = null;
            AdvanceOriginDevelop("docs/new-base.md");
            movedBase = RemoteTip();
        };

        await pilot.TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal(movedBase, manifest.BaseSha);
    }

    [Fact]
    public async Task Tick_routes_a_replay_conflict_before_forming_a_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-FORM-CONFLICT-A", "docs/shared.md", content: "first\n");
        SeedMember(factory, "DOC-FORM-CONFLICT-B", "docs/shared.md", content: "second\n");

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListManifests());
        Assert.Contains("ReplayConflict", store.LatestPlanDecision(ProjectName)!.Reason);
        Assert.Equal(0, _gate.BatchRuns);
    }

    [Fact]
    public async Task Tick_closes_only_the_replay_compatible_survivors()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var first = SeedMember(factory, "DOC-FORM-KEEP-A", "docs/keep-shared.md",
            content: "first\n");
        var conflict = SeedMember(factory, "DOC-FORM-REJECT", "docs/keep-shared.md",
            content: "second\n");
        var third = SeedMember(factory, "DOC-FORM-KEEP-B", "docs/keep-other.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = true;

        await pilot.TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal([first.Key, third.Key], manifest.Members.Select(item => item.TaskKey));
        Assert.Contains(manifest.Exclusions, item => item.TaskKey == conflict.Key
            && item.Reason == BatchExclusionReason.ReplayConflict);
    }

    [Fact]
    public async Task Tick_routes_an_unattributable_result_before_forming_a_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var changed = SeedMember(factory, "DOC-FORM-REF-CHANGED", "docs/ref-changed.md");
        var other = SeedMember(factory, "DOC-FORM-REF-OTHER", "docs/ref-other.md");
        Git(_repo, "push", "-q", "--force", "origin",
            other.ResultSha + ":refs/heads/agent-studio/results/" + changed.Key);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = true;

        await pilot.TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListManifests());
        Assert.Contains("UnattributableReplay", store.LatestPlanDecision(ProjectName)!.Reason);
        Assert.Equal(0, _gate.BatchRuns);
    }

    [Fact]
    public async Task Tick_does_not_form_with_an_already_integrated_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var integrated = SeedMember(factory, "DOC-FORM-INTEGRATED", "docs/integrated.md");
        SeedMember(factory, "DOC-FORM-REMAINING", "docs/remaining.md");
        Git(_repo, "push", "-q", "origin", integrated.ResultSha + ":refs/heads/develop");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = true;

        await pilot.TickAsync(CancellationToken.None);

        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListManifests());
        Assert.Equal(0, _gate.BatchRuns);
    }
}
