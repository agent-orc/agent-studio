using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MachineGateSlotsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("machine-gate-slots");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-13T22:20:00Z"));

    public void Dispose() => _workspace.Dispose();

    private MachineGateSlots Slots() => new(_workspace.Combine("gate"), _clock);

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Fake_gate_requests_respect_slot_capacity(int count, bool secondStarts)
    {
        var slots = Slots();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource<(int Busy, int Total)>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task FakeRunner(TaskCompletionSource started, Task release, Action<int, int>? onWait = null)
        {
            using var lease = await slots.AcquireAsync(count, TimeSpan.FromMinutes(1), onWait, CancellationToken.None);
            Assert.NotNull(lease);
            started.SetResult();
            await release;
        }
        var first = FakeRunner(firstStarted, releaseFirst.Task);
        await firstStarted.Task;
        var second = FakeRunner(secondStarted, Task.CompletedTask,
            (busy, total) => waiting.TrySetResult((busy, total)));
        if (secondStarts)
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
        }
        else
        {
            Assert.Equal((1, 1), await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(secondStarted.Task.IsCompleted);
        }
        releaseFirst.SetResult();
        for (var attempt = 0; attempt < 10 && !second.IsCompleted; attempt++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failure_and_cancellation_release_a_slot()
    {
        var slots = Slots();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task FakeRunner(bool fail, CancellationToken ct)
        {
            using var lease = await slots.AcquireAsync(1, TimeSpan.FromMinutes(1), null, ct);
            Assert.NotNull(lease);
            if (fail) throw new InvalidOperationException("fake gate failed");
            started.SetResult();
            await Task.Delay(TimeSpan.FromMinutes(1), _clock, ct);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => FakeRunner(true, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        var running = FakeRunner(false, cancelled.Token);
        await started.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        using var released = await slots.AcquireAsync(1, TimeSpan.FromMinutes(1), null, CancellationToken.None);
        Assert.NotNull(released);
    }

    [Fact]
    public async Task Different_slot_files_do_not_block_each_other()
    {
        var first = Slots();
        var second = Slots();
        using var slotZero = await first.AcquireAsync(1, TimeSpan.FromMinutes(1), null, CancellationToken.None);
        Assert.NotNull(slotZero);
        using var slotOne = await second.AcquireAsync(2, TimeSpan.FromMinutes(1), null, CancellationToken.None);
        Assert.NotNull(slotOne);
        Assert.Equal(1, slotOne.Slot);
        Assert.NotEqual(first.SlotPath(0), second.SlotPath(1));
    }

    [Fact]
    public async Task Short_gate_takes_the_short_slot_and_a_long_gate_waits_until_it_ages()
    {
        var slots = Slots();
        using var shortGate = await slots.AcquireAsync(3, TimeSpan.FromHours(2), null, CancellationToken.None,
            GateDurationClass.Short);
        Assert.Equal(2, shortGate!.Slot);
        shortGate.Dispose();
        using var first = await slots.AcquireAsync(3, TimeSpan.FromHours(2), null, CancellationToken.None,
            GateDurationClass.Long);
        using var second = await slots.AcquireAsync(3, TimeSpan.FromHours(2), null, CancellationToken.None,
            GateDurationClass.Long);
        Assert.Equal((0, 1), (first!.Slot, second!.Slot));
        var waiting = new TaskCompletionSource<(int Busy, int Total)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var third = slots.AcquireAsync(3, TimeSpan.FromHours(2), (busy, total) => waiting.TrySetResult((busy, total)),
            CancellationToken.None, GateDurationClass.Long);

        Assert.Equal((2, 3), await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(third.IsCompleted);
        Assert.Empty(await slots.TryAcquireFreeAsync(3, 1, CancellationToken.None, GateDurationClass.Long));
        _clock.Advance(GateSlotFairnessPolicy.AgeLimit);
        for (var attempt = 0; attempt < 50 && !third.IsCompleted; attempt++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }
        using var aged = await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, aged!.Slot);
    }

    [Fact]
    public async Task Short_gate_takes_a_shared_slot_when_the_short_slot_is_busy()
    {
        var slots = Slots();
        using var held = await slots.AcquireAsync(2, TimeSpan.FromMinutes(1), null, CancellationToken.None,
            GateDurationClass.Short);
        using var next = await slots.AcquireAsync(2, TimeSpan.FromMinutes(1), null, CancellationToken.None,
            GateDurationClass.Short);
        Assert.Equal((1, 0), (held!.Slot, next!.Slot));
    }
}
