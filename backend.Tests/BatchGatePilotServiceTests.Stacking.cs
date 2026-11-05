using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    private void EnableStacking(WebApplicationFactory<Program> factory)
    {
        EnableBatchGate(factory, closeSize: 2);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, Settings(factory) with { Stacking = "1" });
        var slots = factory.Services.GetRequiredService<GateSlotUsageTracker>();
        slots.Acquired("stack-capacity-probe", 3);
        slots.Released("stack-capacity-probe");
    }

    [Fact]
    public async Task Stacked_green_batches_publish_in_order_after_a_flaky_predecessor_rerun()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns == 1)
            {
                await releaseFirst.Task.WaitAsync(ct);
                return Green(request.ExpectedSha) with
                {
                    RetryPerformed = true,
                    FlakyQuarantinedFailures = ["Product.Tests.Flaky"],
                };
            }
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-STACK-1", "docs/stack-1.md");
            SeedMember(factory, "DOC-STACK-2", "docs/stack-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-STACK-3", "docs/stack-3.md");
            SeedMember(factory, "DOC-STACK-4", "docs/stack-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var successor = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);
            await WaitForAsync(() => store.LatestState(successor.BatchId).Phase == BatchPhase.Green);
            Assert.Null(store.ReadPublication(successor.BatchId));
            var predecessor = store.ReadManifest(successor.StackedOnBatchId!);
            Assert.Equal(store.LatestState(predecessor.BatchId).CandidateSha, successor.BaseSha);
            releaseFirst.SetResult();
            await pilot.WaitForFlightsAsync(true, CancellationToken.None);
            AssertPhase(store, predecessor.BatchId, BatchPhase.Published);
            AssertPhase(store, successor.BatchId, BatchPhase.Published);
            Assert.True(store.ReadPublication(predecessor.BatchId)!.VerifiedAtUtc
                <= store.ReadPublication(successor.BatchId)!.VerifiedAtUtc);
            Assert.Equal(store.ReadPublication(successor.BatchId)!.TestedCandidateSha, RemoteTip());
            using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
            Assert.Equal(1, state.RootElement.GetProperty("stacking").GetProperty("started").GetInt32());
            Assert.Equal(1, state.RootElement.GetProperty("stacking").GetProperty("published").GetInt32());
        }
        finally { releaseFirst.TrySetResult(); }
    }

    [Fact]
    public async Task Active_stacked_successor_holds_a_later_ordinary_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            var runNumber = _gate.BatchRuns;
            if (runNumber == 1) await releaseFirst.Task.WaitAsync(ct);
            if (runNumber == 2) await releaseStack.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-ORDER-1", "docs/order-1.md");
            SeedMember(factory, "DOC-ORDER-2", "docs/order-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-ORDER-3", "docs/order-3.md");
            SeedMember(factory, "DOC-ORDER-4", "docs/order-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var successor = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);

            releaseFirst.SetResult();
            await WaitForAsync(() => store.ReadPublication(successor.StackedOnBatchId!) is not null);
            SeedMember(factory, "DOC-ORDER-5", "docs/order-5.md");
            SeedMember(factory, "DOC-ORDER-6", "docs/order-6.md");
            await pilot.TickAsync(CancellationToken.None);

            Assert.Equal(2, _gate.BatchRuns);
            Assert.Contains(store.ListManifests(), item => item.StackedOnBatchId is null
                && item.Members.Any(member => member.TaskKey == "DOC-ORDER-5")
                && store.LatestState(item.BatchId).Phase == BatchPhase.Formed);
            releaseStack.SetResult();
            await pilot.WaitForFlightsAsync(true, CancellationToken.None);
            AssertPhase(store, successor.BatchId, BatchPhase.Published);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseStack.TrySetResult();
            await pilot.WaitForFlightsAsync(true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Red_predecessor_cancels_the_stacked_gate_and_returns_its_members()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns == 1)
            {
                await releaseFirst.Task.WaitAsync(ct);
                return Red(request.ExpectedSha);
            }
            if (_gate.BatchRuns == 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Cancelled gate returned.");
            }
            return Red(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-RED-STACK-1", "docs/red-stack-1.md");
            SeedMember(factory, "DOC-RED-STACK-2", "docs/red-stack-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-RED-STACK-3", "docs/red-stack-3.md");
            SeedMember(factory, "DOC-RED-STACK-4", "docs/red-stack-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var successor = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);
            releaseFirst.SetResult();
            await WaitForAsync(() => store.LatestState(successor.BatchId).Phase == BatchPhase.Abandoned);
            Assert.Null(store.ReadPublication(successor.BatchId));
            await WaitForAsync(() => store.ListPending().Any(item =>
                item.Subject.TaskKey == "DOC-RED-STACK-3")
                && store.ListPending().Any(item => item.Subject.TaskKey == "DOC-RED-STACK-4"));
            Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == "DOC-RED-STACK-3");
            Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == "DOC-RED-STACK-4");
            using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
            Assert.Equal(1, state.RootElement.GetProperty("stacking").GetProperty("cancelled").GetInt32());
        }
        finally { releaseFirst.TrySetResult(); }
    }

    [Fact]
    public async Task Stacking_off_keeps_a_formed_successor_waiting()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns == 1) await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-OFF-1", "docs/off-1.md");
            SeedMember(factory, "DOC-OFF-2", "docs/off-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-OFF-3", "docs/off-3.md");
            SeedMember(factory, "DOC-OFF-4", "docs/off-4.md");
            await pilot.TickAsync(CancellationToken.None);
            Assert.Equal(1, _gate.BatchRuns);
            Assert.Contains(store.ListManifests(), item =>
                store.LatestState(item.BatchId).Phase == BatchPhase.Formed);
            Assert.DoesNotContain(store.ListManifests(), item => item.StackedOnBatchId is not null);
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task External_push_cancels_a_stacked_candidate_on_the_old_base()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns == 1) await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-PUSH-STACK-1", "docs/push-stack-1.md");
            SeedMember(factory, "DOC-PUSH-STACK-2", "docs/push-stack-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-PUSH-STACK-3", "docs/push-stack-3.md");
            SeedMember(factory, "DOC-PUSH-STACK-4", "docs/push-stack-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var successor = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);
            AdvanceOriginDevelop("docs/external-stack.md");
            release.SetResult();
            await WaitForAsync(() => store.LatestState(successor.BatchId).Phase == BatchPhase.Abandoned);
            Assert.Null(store.ReadPublication(successor.BatchId));
            Assert.NotEqual(store.LatestState(successor.BatchId).CandidateSha, RemoteTip());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Superseded_stacked_member_never_publishes_the_old_candidate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns == 1) await releaseFirst.Task.WaitAsync(ct);
            if (_gate.BatchRuns == 2) await releaseSecond.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-SUPER-STACK-1", "docs/super-stack-1.md");
            SeedMember(factory, "DOC-SUPER-STACK-2", "docs/super-stack-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            var superseded = SeedMember(factory, "DOC-SUPER-STACK-3", "docs/super-stack-3.md");
            SeedMember(factory, "DOC-SUPER-STACK-4", "docs/super-stack-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var successor = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);
            Supersede(factory.Services.GetRequiredService<AttemptAuthorityService>(), superseded.Key);
            releaseSecond.SetResult();
            await WaitForAsync(() => store.LatestState(successor.BatchId).Phase == BatchPhase.Abandoned);
            releaseFirst.SetResult();
            await WaitForAsync(() => store.ReadPublication(successor.StackedOnBatchId!) is not null);
            Assert.Null(store.ReadPublication(successor.BatchId));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [Fact]
    public async Task Restart_reforms_an_interrupted_stack_from_the_real_tip()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableStacking(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var oldGateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            if (_gate.BatchRuns <= 2) await oldGateRelease.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-RESTART-STACK-1", "docs/restart-stack-1.md");
            SeedMember(factory, "DOC-RESTART-STACK-2", "docs/restart-stack-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            SeedMember(factory, "DOC-RESTART-STACK-3", "docs/restart-stack-3.md");
            SeedMember(factory, "DOC-RESTART-STACK-4", "docs/restart-stack-4.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 2);
            var oldStack = Assert.Single(store.ListManifests(), item => item.StackedOnBatchId is not null);
            var realTip = RemoteTip();
            _clock = _clock.AddMinutes(3);
            using var restarted = BuildFactory();
            _ = restarted.CreateClient();
            EnableStacking(restarted);
            var resumed = restarted.Services.GetRequiredService<BatchGatePilotService>();
            resumed.WaitForFlightsInTick = false;
            await resumed.TickAsync(CancellationToken.None);
            AssertPhase(store, oldStack.BatchId, BatchPhase.Abandoned);
            Assert.Null(store.ReadPublication(oldStack.BatchId));
            await resumed.TickAsync(CancellationToken.None);
            await WaitForAsync(() => store.ListManifests().Any(item =>
                item.BatchId != oldStack.BatchId && item.BaseSha == realTip
                && item.Members.Any(member => member.TaskKey == "DOC-RESTART-STACK-3")));
        }
        finally { oldGateRelease.TrySetResult(); }
    }
}
