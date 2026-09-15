using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2831. The decision matrix is exercised directly; the loop and the
/// <c>/proc</c> sampler get one end-to-end case each against a real process.
/// </summary>
public sealed class CommandProgressWatchdogTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(100);

    // The floor over a 100s window is 1s of CPU (1 percent of one core).
    [Theory]
    // sampled cpu, cpu at window start, elapsed, expected
    [InlineData(5.0, 0.0, 10.0, CommandProgress.Advanced)]   // busy build, window restarts
    [InlineData(1.01, 0.0, 99.0, CommandProgress.Advanced)]  // just clears the floor
    [InlineData(0.5, 0.0, 10.0, CommandProgress.Waiting)]    // quiet, window still open
    [InlineData(0.5, 0.0, 100.0, CommandProgress.Stalled)]   // quiet for the whole window
    [InlineData(0.1, 0.0, 600.0, CommandProgress.Stalled)]   // 0.1 percent of a core: the incident shape
    [InlineData(1.0, 0.0, 100.0, CommandProgress.Stalled)]   // exactly at the floor is not progress
    public void Progress_is_decided_on_consumed_cpu_not_wall_clock(
        double sampledCpuSeconds,
        double windowStartCpuSeconds,
        double elapsedSeconds,
        CommandProgress expected)
        => Assert.Equal(
            expected,
            CommandProgressPolicy.Evaluate(
                TimeSpan.FromSeconds(sampledCpuSeconds),
                TimeSpan.FromSeconds(windowStartCpuSeconds),
                TimeSpan.FromSeconds(elapsedSeconds),
                Window));

    [Fact]
    public void A_host_without_cpu_truth_never_produces_a_kill_verdict()
        => Assert.Equal(
            CommandProgress.Unobservable,
            CommandProgressPolicy.Evaluate(null, TimeSpan.Zero, TimeSpan.FromHours(1), Window));

    [Fact]
    public void A_zero_window_disables_the_verdict()
        => Assert.Equal(
            CommandProgress.Unobservable,
            CommandProgressPolicy.Evaluate(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromHours(1),
                TimeSpan.Zero));

    [SkippableFact]
    public async Task Watchdog_fires_for_a_sleeping_tree_and_stays_quiet_for_a_busy_one()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The CPU sampler reads /proc.");

        Assert.True(await StalledAsync("sleep 30"));
        Assert.False(await StalledAsync("end=$(( $(date +%s) + 3 )); while [ $(date +%s) -lt $end ]; do :; done"));
    }

    [SkippableFact]
    public void Cpu_sample_covers_descendants_not_only_the_direct_child()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The CPU sampler reads /proc.");

        using var process = Start("sh -c 'end=$(( $(date +%s) + 3 )); while [ $(date +%s) -lt $end ]; do :; done' & wait");
        try
        {
            var samples = new List<TimeSpan>();
            for (var attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(250);
                if (ProcessTreeCpu.Sample(process.Id) is { } sample) samples.Add(sample);
            }

            // The parent shell only waits; every tick observed here was burned by
            // the grandchild loop.
            Assert.NotEmpty(samples);
            Assert.True(
                samples[^1] > TimeSpan.FromMilliseconds(200),
                $"expected descendant CPU to accumulate, saw {samples[^1]}");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [SkippableFact]
    public void A_process_that_does_not_exist_is_unobservable()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The CPU sampler reads /proc.");

        Assert.Null(ProcessTreeCpu.Sample(int.MaxValue));
        Assert.Null(ProcessTreeCpu.Sample(0));
    }

    private static async Task<bool> StalledAsync(string shellCommand)
    {
        using var process = Start(shellCommand);
        var stalled = false;
        // A 2s window puts the CPU floor at 20ms: a sleeping tree never reaches
        // it, a spinning tree clears it on the first sample.
        await using (var watchdog = new CommandProgressWatchdog(
                         () => ProcessTreeCpu.Sample(process.Id),
                         TimeSpan.FromSeconds(2),
                         onStalled: () => stalled = true,
                         checkEvery: TimeSpan.FromMilliseconds(200)))
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!stalled && DateTime.UtcNow < deadline) await Task.Delay(100);
        }
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        return stalled;
    }

    private static Process Start(string shellCommand)
    {
        var info = new ProcessStartInfo(PosixShell.RequirePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(shellCommand);
        var process = Process.Start(info)
                      ?? throw new InvalidOperationException("Could not start the fixture process.");
        return process;
    }
}
