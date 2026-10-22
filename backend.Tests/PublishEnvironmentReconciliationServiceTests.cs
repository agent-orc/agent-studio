using System.Threading.Channels;
using AgentStudio.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PublishEnvironmentReconciliationServiceTests
{
    [Fact]
    public async Task Reconciles_at_startup_and_after_clock_advances_without_a_panel_refresh()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var reconciler = new RecordingReconciler();
        using var service = new PublishEnvironmentReconciliationService(reconciler, clock,
            NullLogger<PublishEnvironmentReconciliationService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, await reconciler.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, await reconciler.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingReconciler : IPublishEnvironmentReconciler
    {
        private int _count;
        public Channel<int> Calls { get; } = Channel.CreateUnbounded<int>();
        public void ReconcileRunningWebsites() => Calls.Writer.TryWrite(Interlocked.Increment(ref _count));
    }
}
