using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData("shutdown")]
    [InlineData("operator")]
    [InlineData("superseded")]
    public async Task Per_task_fallback_classifies_cancellation_before_escalating(string reason)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-CANCEL", "docs/cancel.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        _gate.Fallback = request =>
        {
            if (reason == "superseded")
                Assert.NotNull(factory.Services.GetRequiredService<TaskMutationService>().SavePendingIntent(
                    member.Key, ContinueModes.Continue, "Continue the delivery", "operator", null, _watchPath));
            return new BuildTestGateResult(BuildTestGateVerdict.Fail, null, 0, "", "cancelled", false, false)
            {
                FailureKind = BuildTestGateFailureKind.Cancellation,
                CancellationReason = reason == "operator" ? reason : "shutdown",
                ExpectedSha = request.ExpectedSha,
            };
        };

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        var lane = reason == "operator" ? TaskStates.Escalated : TaskStates.AutoReview;
        AssertLane(member.Key, lane);
        var folder = Path.Combine(_watchPath, lane, member.Key);
        Assert.Equal(reason, Assert.Single(timeline.ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);
        if (reason != "operator")
            Assert.DoesNotContain(timeline.ReadAll(folder), e => e.Kind == "escalation-diagnostic");
        if (reason == "shutdown")
        {
            Assert.Equal(1, Ownership(member.Key)!.FallbackGateStarts);
            // The injected pilot clock and the durable ownership marker survive
            // the daemon's next tick. A second cancellation exhausts the budget.
            _clock = _clock.AddMinutes(1);
            await pilot.TickAsync(CancellationToken.None);
            Assert.Equal(2, _gate.FallbackRuns);
            AssertLane(member.Key, TaskStates.Escalated);
        }
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData(false, GateCancellationReasons.Operator)]
    [InlineData(true, GateCancellationReasons.Shutdown)]
    public async Task Thrown_fallback_cancellation_uses_host_token_state(
        bool cancelHostToken, string expectedReason)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-THROWN-CANCEL", "docs/thrown-cancel.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        using var host = new CancellationTokenSource();
        _gate.Fallback = _ =>
        {
            if (cancelHostToken) host.Cancel();
            throw new OperationCanceledException();
        };

        if (cancelHostToken)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pilot.TickAsync(host.Token));
        else
            await pilot.TickAsync(host.Token);

        var lane = cancelHostToken ? TaskStates.AutoReview : TaskStates.Escalated;
        var folder = Path.Combine(_watchPath, lane, member.Key);
        if (cancelHostToken)
            await WaitForAsync(() => timeline.ReadAll(folder).Any(e => e.Kind == "gate-cancelled"));
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, lane);
        Assert.Equal(expectedReason, Assert.Single(timeline.ReadAll(folder),
            e => e.Kind == "gate-cancelled").Details!["reason"]);
        if (cancelHostToken)
            Assert.DoesNotContain(timeline.ReadAll(folder), e => e.Kind == "escalation-diagnostic");
    }
}
