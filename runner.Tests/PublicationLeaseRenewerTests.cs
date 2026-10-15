using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3061: a publication that outlasts its lease keeps the fence by renewing
/// it, and stops when the server refuses the fence or the lease runs out.
/// </summary>
public sealed class PublicationLeaseRenewerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];

    [Fact]
    public async Task A_long_publication_renews_the_lease_at_a_third_of_its_lifetime()
    {
        var lease = Lease(ttlSeconds: 60);
        var requested = new List<int>();
        using var leaseLoss = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var renewer = new PublicationLeaseRenewer((ttl, _) =>
        {
            lock (requested) requested.Add(ttl);
            return Task.FromResult(lease with { ExpiresAt = _time.GetUtcNow().UtcDateTime.AddSeconds(ttl) });
        }, _time, _log.Add).RunAsync(lease, leaseLoss, stop.Token);

        await AdvanceUntilAsync(() => { lock (requested) return requested.Count >= 4; });

        // Four renewals span 80 seconds, beyond the initial 60-second lease.
        Assert.All(requested, ttl => Assert.Equal(60, ttl));
        Assert.False(leaseLoss.IsCancellationRequested);
        await stop.CancelAsync();
        await renewer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(leaseLoss.IsCancellationRequested);
    }

    [Theory]
    [InlineData("stale-fence")]
    [InlineData("publication-lease-expired")]
    [InlineData("publication-lease-missing")]
    public async Task A_refused_fence_stops_the_publication(string code)
    {
        var lease = Lease(ttlSeconds: 60);
        using var leaseLoss = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var renewer = new PublicationLeaseRenewer(
            (_, _) => Task.FromException<PublicationLeaseDto>(new TaskServerException(409, "refused", code)),
            _time, _log.Add).RunAsync(lease, leaseLoss, stop.Token);

        await AdvanceUntilAsync(() => leaseLoss.IsCancellationRequested);
        await renewer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_time.GetUtcNow().UtcDateTime < lease.ExpiresAt);
        Assert.Contains(_log, line => line.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transient_renewal_failures_keep_publishing_until_the_lease_runs_out()
    {
        var lease = Lease(ttlSeconds: 60);
        var calls = 0;
        using var leaseLoss = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var renewer = new PublicationLeaseRenewer((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<PublicationLeaseDto>(new HttpRequestException("server unreachable"));
        }, _time, _log.Add).RunAsync(lease, leaseLoss, stop.Token);

        await AdvanceUntilAsync(() => Volatile.Read(ref calls) >= 1);
        Assert.False(leaseLoss.IsCancellationRequested);

        await AdvanceUntilAsync(() => leaseLoss.IsCancellationRequested);
        await renewer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_time.GetUtcNow().UtcDateTime >= lease.ExpiresAt);
        Assert.Contains(_log, line => line.Contains("server unreachable", StringComparison.Ordinal));
    }

    private PublicationLeaseDto Lease(int ttlSeconds)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new PublicationLeaseDto("pub_test", "prj", "develop", "int_test", "integration-a",
            "instance-a", 3, now, now.AddSeconds(ttlSeconds));
    }

    /// <summary>
    /// Advances the fake clock one second at a time and lets the renewer's
    /// continuations run, until the condition holds or ten simulated minutes pass.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        for (var step = 0; step < 600 && !condition(); step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            for (var spin = 0; spin < 3 && !condition(); spin++) await Task.Delay(1);
        }
        Assert.True(condition(), "The renewer did not reach the expected state.");
    }
}
