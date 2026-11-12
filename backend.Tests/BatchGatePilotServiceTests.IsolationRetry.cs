using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePilotIsolationRetryTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Cancelled_solo_flight_requeues_without_a_proven_red_finding()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-CANCELLED-SOLO", "docs/cancelled-solo.md");
        SeedMember(factory, "DOC-CANCELLED-PARTNER", "docs/cancelled-partner.md");
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/cancelled-partner.md")
            && TreeHas(request.ExpectedSha!, "docs/cancelled-solo.md")
                ? Red(request.ExpectedSha) : Cancelled();
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("Cancellation ran a fallback.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.DoesNotContain(store.ListIsolationFindings(ProjectName), finding =>
            finding.TaskKey == member.Key && finding.Status == "proven-red");
        Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == member.Key);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal("isolation flight cancelled (InfrastructureRed); member re-queued",
            store.LatestState(store.ListManifests()
                .Single(manifest => manifest.ParentBatchId is null).BatchId).Reason);
    }

    [Fact]
    public async Task Repeated_cancelled_solo_flight_parks_with_infrastructure_cause()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-CANCEL-LIMIT", "docs/cancel-limit.md");
        SeedMember(factory, "DOC-CANCEL-OTHER", "docs/cancel-other.md");
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/cancel-other.md")
            && TreeHas(request.ExpectedSha!, "docs/cancel-limit.md")
                ? Red(request.ExpectedSha) : Cancelled();
        _gate.Base = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        for (var attempt = 0; attempt <= BatchGatePilotService.IsolationFlightRetryLimit; attempt++)
            await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        Assert.DoesNotContain(store.ListIsolationFindings(ProjectName), finding =>
            finding.TaskKey == member.Key && finding.Status == "proven-red");
    }

    [Fact]
    public async Task Cancelled_solo_retries_after_a_green_sibling_was_published()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var green = SeedMember(factory, "DOC-EARLY-GREEN", "docs/early-green.md");
        var cancelled = SeedMember(factory, "DOC-LATE-CANCEL", "docs/late-cancel.md");
        SeedMember(factory, "DOC-LATE-PARTNER", "docs/late-partner.md");
        var soloAttempts = 0;
        _gate.Batch = request =>
        {
            var hasA = TreeHas(request.ExpectedSha!, "docs/early-green.md");
            var hasB = TreeHas(request.ExpectedSha!, "docs/late-cancel.md");
            var hasC = TreeHas(request.ExpectedSha!, "docs/late-partner.md");
            // AGT-3752: after the green sibling publishes, the solo starts on its tip.
            var ownA = hasA && TreeHas(RemoteTip(), "docs/early-green.md");
            return hasB && (!hasA || ownA) && !hasC && ++soloAttempts == 1 ? Cancelled()
                : hasB ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        AssertLane(green.Key, TaskStates.HumanReview);
        AssertLane(cancelled.Key, TaskStates.AutoReview);

        await pilot.TickAsync(CancellationToken.None);
        Assert.Equal(2, soloAttempts);
        AssertLane(cancelled.Key, TaskStates.Escalated);
        Assert.Contains(store.ListIsolationFindings(ProjectName), finding =>
            finding.TaskKey == cancelled.Key && finding.Status == "proven-red");
    }

    [Fact]
    public async Task Deterministic_solo_flight_still_proves_the_member_red()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-RED-SOLO", "docs/red-solo.md");
        SeedMember(factory, "DOC-GREEN-PARTNER", "docs/green-partner.md");
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/red-solo.md")
            ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        _gate.Base = request => Green(request.ExpectedSha);
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        Assert.Contains(store.ListIsolationFindings(ProjectName), finding =>
            finding.TaskKey == member.Key && finding.Status == "proven-red");
        AssertLane(member.Key, TaskStates.Escalated);
    }

    [Fact]
    public async Task Abandoned_per_task_flight_is_replanned_on_next_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var member = SeedMember(factory, "CODE-ABANDONED-FALLBACK", "src/abandoned.cs");
        var calls = 0;
        _gate.Fallback = request => ++calls == 1 ? Cancelled() : Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.RequestCloseNow(ProjectName);

        await pilot.TickAsync(CancellationToken.None);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.True(Ownership(member.Key)?.FallbackGateActive);
        Assert.Equal(1, calls);

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        AssertLane(member.Key, TaskStates.HumanReview);
    }

    private static BuildTestGateResult Cancelled()
        => new(BuildTestGateVerdict.Fail, null, 1, "", "Remote verification was cancelled.", false, false)
        {
            FailureKind = BuildTestGateFailureKind.Cancellation,
        };
}
