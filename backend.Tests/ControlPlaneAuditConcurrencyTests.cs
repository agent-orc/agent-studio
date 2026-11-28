using AgentStudio.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ControlPlaneAuditConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-concurrent-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<int> Runs => new(Enumerable.Range(1, 50));

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Concurrent_mutations_preserve_every_entry_and_verification_uses_one_snapshot(int run)
    {
        var clock = new FakeTimeProvider();
        var log = new ControlPlaneAuditLog(Path.Combine(_root, run.ToString()), clock);
        log.Append(new("operator-agent", "operator-agent:test", "seed", "200"));
        var monitor = new ControlPlaneAuditMonitor(log, new("none", null, null, null), clock);
        using var verifier = new ControlPlaneAuditVerifier(log, monitor, null, clock);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var appended = 0;
        log.VerificationSnapshotCaptured = () =>
        {
            captured.TrySetResult();
            // Writers must not wait for the verifier. Every append flushes to disk,
            // so on a busy host 256 of them took longer than the former fixed 60 s.
            // The verifier therefore waits while the writers make progress and
            // fails only when no append completes for a whole stall window.
            WaitWhileProgressing(release.WaitHandle, () => Volatile.Read(ref appended));
        };
        // AGT-4360: the verifier blocks a thread inside the hook while sixteen
        // writers must run. As pool work items they waited for the thread pool,
        // which adds workers slowly or not at all while the CPU is saturated.
        // Verifier and writers run on dedicated threads; the test waits for
        // completion signals, not for the machine.
        var verifying = Dedicated(() => verifier.VerifyAsync(CancellationToken.None).GetAwaiter().GetResult());
        await captured.Task.WaitAsync(DeadlockBreaker);
        try
        {
            // Sixteen concurrent card mutation streams, 256 distinct audited operations.
            using var go = new ManualResetEventSlim();
            var writers = Enumerable.Range(0, 16).Select(card => Dedicated(() =>
            {
                go.Wait();
                for (var mutation = 0; mutation < 16; mutation++)
                {
                    log.Append(new("operator-agent", "operator-agent:test", (mutation % 3) switch
                    {
                        0 => "POST /api/tasks",
                        1 => "PUT /api/tasks/references",
                        _ => "POST /api/tasks/move",
                    }, "200", $"card-{card}", Details: new { card, mutation }));
                    Interlocked.Increment(ref appended);
                }
                return 0;
            })).ToArray();
            go.Set();
            var all = Task.WhenAll(writers);
            await Dedicated(() =>
            {
                WaitWhileProgressing(((IAsyncResult)all).AsyncWaitHandle, () => Volatile.Read(ref appended));
                return 0;
            });
            await all;
        }
        finally
        {
            log.VerificationSnapshotCaptured = null;
            release.Set();
        }
        var during = await verifying;
        var entries = log.Read(1, 1000);
        Assert.Equal(257, entries.Count);
        Assert.Equal(Enumerable.Range(1, 257).Select(i => (long)i), entries.Select(e => e.Sequence));
        Assert.Equal(256, entries.Skip(1).Select(e => (e.Target, e.Details!.Value.GetProperty("mutation").GetInt32())).Distinct().Count());
        Assert.True(log.VerifyChain().Ok);
        Assert.Equal(ControlPlaneAuditIntegrityStates.Verified, during.State);
        Assert.Equal(ControlPlaneAuditIntegrityStates.Verified, (await verifier.VerifyAsync(CancellationToken.None)).State);
        Assert.Empty(monitor.Raised);
    }

    private static readonly TimeSpan DeadlockBreaker = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits for <paramref name="done"/> while <paramref name="progress"/> advances.
    /// The bound is on one append, not on all 256, so a slow disk only slows the test.
    /// </summary>
    private static void WaitWhileProgressing(WaitHandle done, Func<int> progress)
    {
        var seen = progress();
        while (!done.WaitOne(DeadlockBreaker))
        {
            var now = progress();
            Assert.True(now != seen, $"No audit append completed within {DeadlockBreaker.TotalSeconds:0} s ({now} of 256 done): the writers wait for the verifier.");
            seen = now;
        }
    }

    private static Task<T> Dedicated<T>(Func<T> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
