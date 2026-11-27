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
        log.VerificationSnapshotCaptured = () =>
        {
            captured.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "Concurrent mutations did not finish.");
        };
        var verifying = Task.Run(() => verifier.VerifyAsync(CancellationToken.None));
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(60));
        try
        {
            // Sixteen concurrent card mutation streams, 256 distinct audited operations.
            await Task.WhenAll(Enumerable.Range(0, 16).Select(card => Task.Run(() =>
            {
                for (var mutation = 0; mutation < 16; mutation++)
                    log.Append(new("operator-agent", "operator-agent:test", (mutation % 3) switch
                    {
                        0 => "POST /api/tasks",
                        1 => "PUT /api/tasks/references",
                        _ => "POST /api/tasks/move",
                    }, "200", $"card-{card}", Details: new { card, mutation }));
            })));
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
