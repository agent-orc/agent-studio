using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorktreeOrphanDirectorySweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "worktree-orphan-sweep-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sweep_ReapsDirectoryThatLostGitLink_AndLogsOneWarning()
    {
        var orphan = Path.Combine(_root, "project", "task-orphan");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "leftover.txt"), "stale");
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));
        var logger = new RecordingLogger();
        var sweeper = new WorktreeOrphanDirectorySweeper(logger);

        sweeper.Sweep(_root, DateTime.UtcNow, TimeSpan.FromMinutes(2));
        sweeper.Sweep(_root, DateTime.UtcNow, TimeSpan.FromMinutes(2));

        Assert.False(Directory.Exists(orphan));
        var warning = Assert.Single(
            logger.Messages,
            message => message.Contains("worktree-orphan-directory", StringComparison.Ordinal));
        Assert.Contains(orphan, warning);
        Assert.Contains("reaped", warning);
    }

    [Fact]
    public void Sweep_LeavesRegisteredAndYoungDirectoriesAlone()
    {
        var registered = Path.Combine(_root, "project", "registered");
        var young = Path.Combine(_root, "project", "young");
        Directory.CreateDirectory(registered);
        File.WriteAllText(Path.Combine(registered, ".git"), "gitdir: elsewhere");
        Directory.CreateDirectory(young);
        var logger = new RecordingLogger();
        var sweeper = new WorktreeOrphanDirectorySweeper(logger);

        sweeper.Sweep(_root, DateTime.UtcNow, TimeSpan.FromMinutes(2));

        Assert.True(Directory.Exists(registered));
        Assert.True(Directory.Exists(young));
        Assert.Empty(logger.Messages);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
