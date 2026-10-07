using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderRealRequestHostFlightTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "provider-real-flight-tests", Guid.NewGuid().ToString("N"));

    public ProviderRealRequestHostFlightTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_probe_deadline_stops_the_run_and_releases_the_host_lock()
    {
        var lockPath = Path.Combine(_root, ".provider-real-probe.lock");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var first = ProviderRealRequestHostFlight.RunAsync(lockPath, async stop =>
        {
            // Stands in for CAR: the CLI runs until its stop token fires.
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
            catch (OperationCanceledException) { }
            stopped.SetResult();
            return (new ProcessResult(124, "", "Runner timeout"), true, false);
        }, deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await AcquireWithinAsync(lockPath, TimeSpan.FromSeconds(5));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("OK", second.StdOut);
    }

    [Fact]
    public async Task A_running_request_keeps_a_second_probe_out_of_the_clean_context()
    {
        var lockPath = Path.Combine(_root, ".provider-real-probe.lock");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ProviderRealRequestHostFlight.RunAsync(lockPath, async _ =>
        {
            await release.Task;
            return (new ProcessResult(0, "OK", ""), false, false);
        }, CancellationToken.None);

        var blocked = await ProviderRealRequestHostFlight.RunAsync(
            lockPath, _ => Task.FromResult((new ProcessResult(0, "OK", ""), false, false)), CancellationToken.None);
        Assert.Equal(ProviderRealRequestHostFlight.InProgress, blocked.StdErr);

        release.SetResult();
        Assert.Equal(0, (await first).ExitCode);
        Assert.Equal(0, (await AcquireWithinAsync(lockPath, TimeSpan.FromSeconds(5))).ExitCode);
    }

    [Fact]
    public async Task A_run_that_fails_to_start_releases_the_host_lock()
    {
        var lockPath = Path.Combine(_root, ".provider-real-probe.lock");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderRealRequestHostFlight.RunAsync(
            lockPath,
            _ => throw new InvalidOperationException("start failed"),
            CancellationToken.None));

        Assert.Equal(0, (await AcquireWithinAsync(lockPath, TimeSpan.FromSeconds(5))).ExitCode);
    }

    // The lock is released by a continuation of the ended run; allow it to land.
    private static async Task<ProcessResult> AcquireWithinAsync(string lockPath, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (true)
        {
            var result = await ProviderRealRequestHostFlight.RunAsync(
                lockPath, _ => Task.FromResult((new ProcessResult(0, "OK", ""), false, false)), CancellationToken.None);
            if (result.StdErr != ProviderRealRequestHostFlight.InProgress || DateTime.UtcNow > until)
                return result;
            await Task.Delay(20);
        }
    }
}
