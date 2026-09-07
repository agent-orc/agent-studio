using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class CliProcessReaperTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "agent-runner-reaper-" + Guid.NewGuid().ToString("N"));

    public CliProcessReaperTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task ReapAsync_KillsAFakeLongLivedChildAfterTheBoundedGrace()
    {
        var startedAt = DateTime.UtcNow.AddHours(-3);
        var killed = false;
        TimeSpan? observedGrace = null;
        var logs = new List<string>();
        var reaper = new CliProcessReaper(
            inspect: _ => killed ? null : new CliProcessReaper.ProcessSnapshot(startedAt),
            kill: (_, _) =>
            {
                killed = true;
                return Task.CompletedTask;
            },
            delay: (delay, _) =>
            {
                observedGrace = delay;
                return Task.CompletedTask;
            });

        var count = await reaper.ReapAsync(
            4242,
            startedAt,
            "review-attempt-7",
            _workspace,
            logs.Add,
            CancellationToken.None,
            TimeSpan.FromSeconds(3));

        Assert.True(killed);
        Assert.Equal(TimeSpan.FromSeconds(3), observedGrace);
        Assert.Equal(1, count);
        Assert.Contains(logs, line =>
            line.Contains("cli-process-reaped pid=4242", StringComparison.Ordinal)
            && line.Contains("attempt=review-attempt-7", StringComparison.Ordinal)
            && line.Contains("ageSeconds=", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }
}
