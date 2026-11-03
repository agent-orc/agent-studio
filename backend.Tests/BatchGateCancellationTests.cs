using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Operator_cancelled_fallback_returns_to_batch_planning()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-OPERATOR-RETURN", "docs/operator-return.md");
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request =>
        {
            EnableBatchGate(factory, closeSize: 1);
            return new BuildTestGateResult(BuildTestGateVerdict.Fail, null, 0, "",
                "Gate cancelled: operator.", false, false)
            {
                FailureKind = BuildTestGateFailureKind.Cancellation,
                CancellationReason = GateCancellationReasons.Operator,
                ExpectedSha = request.ExpectedSha,
            };
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.False(Ownership(member.Key)?.FallbackGateActive ?? false);
        Assert.Equal(1, _gate.FallbackRuns);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = Assert.Single(store.ListPending());
        store.ResolvePending(pending.ReviewAttemptId, "operator-cancelled");
        Assert.Empty(store.ListPending());

        _gate.Batch = request => Green(request.ExpectedSha);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Single(store.ListManifests());
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(1, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Passed_review_moved_from_escalated_back_to_auto_review_reenters_queue()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-ESCALATED-RETURN", "docs/escalated-return.md");
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);
        AssertLane(member.Key, TaskStates.Escalated);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListPending());

        EnableBatchGate(factory, closeSize: 1);
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            member.Key, TaskStates.AutoReview, _watchPath, CancellationToken.None,
            cause: "operator-return", suppressProductExecution: true,
            expectedSourceState: TaskStates.Escalated);
        Assert.True(moved.Status == MoveJobStatus.Success, moved.Message);
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        Assert.Single(store.ListManifests());
        AssertLane(member.Key, TaskStates.HumanReview);
    }

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
        var lane = TaskStates.AutoReview;
        AssertLane(member.Key, lane);
        var folder = Path.Combine(_watchPath, lane, member.Key);
        Assert.Equal(reason, Assert.Single(timeline.ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);
        if (reason == "operator")
            Assert.False(Ownership(member.Key)?.FallbackGateActive ?? false);
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

        var lane = TaskStates.AutoReview;
        var folder = Path.Combine(_watchPath, lane, member.Key);
        // The cancelled tick stops waiting; the fallback flight still records
        // its cancellation before it leaves the flight registry.
        if (cancelHostToken)
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, lane);
        Assert.Equal(expectedReason, Assert.Single(timeline.ReadAll(folder),
            e => e.Kind == "gate-cancelled").Details!["reason"]);
        if (cancelHostToken)
            Assert.DoesNotContain(timeline.ReadAll(folder), e => e.Kind == "escalation-diagnostic");
    }
}
