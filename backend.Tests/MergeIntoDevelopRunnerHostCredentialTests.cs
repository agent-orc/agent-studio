using AgentStudio.GitHubApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// 2 November 2026: directly after a planned restart, merge-into-develop
/// fetched a private repository before the host credential service had written
/// its App tokens, and the card was parked with <c>could not read Username</c>.
/// The merge now waits for the host's first tokens and says so on every retry.
/// </summary>
public sealed class MergeIntoDevelopRunnerHostCredentialTests
{
    [Fact]
    public async Task Integration_waits_for_the_host_tokens_and_logs_each_retry()
    {
        var readiness = new GitHubAppHostCredentialReadiness();
        readiness.Expect();
        readiness.MarkFailed("GitHub is not reachable yet.");
        var log = new CapturingLogger();
        var runner = Runner(readiness, log);
        runner.HostCredentialRetryInterval = TimeSpan.FromMilliseconds(20);
        runner.HostCredentialWaitLimit = TimeSpan.FromMinutes(1);

        var waiting = runner.WaitForHostGitCredentialsAsync("Fixture", "card-1", CancellationToken.None);
        await WaitUntilAsync(() => log.Count("integration-waits-for-host-git-credentials") >= 2);
        Assert.False(waiting.IsCompleted);

        readiness.MarkReady();
        await waiting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(log.Entries, entry => entry.Contains("integration-waits-for-host-git-credentials", StringComparison.Ordinal)
            && entry.Contains("GitHub is not reachable yet.", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Contains("integration-host-git-credentials-ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Integration_proceeds_after_the_bounded_wait()
    {
        var readiness = new GitHubAppHostCredentialReadiness();
        readiness.Expect();
        var log = new CapturingLogger();
        var runner = Runner(readiness, log);
        runner.HostCredentialRetryInterval = TimeSpan.FromMilliseconds(20);
        runner.HostCredentialWaitLimit = TimeSpan.FromMilliseconds(100);

        await runner.WaitForHostGitCredentialsAsync("Fixture", "card-1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(log.Entries, entry => entry.Contains("integration-proceeds-without-host-git-credentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Integration_does_not_wait_when_the_tokens_are_written()
    {
        var readiness = new GitHubAppHostCredentialReadiness();
        readiness.Expect();
        readiness.MarkReady();
        var log = new CapturingLogger();
        var runner = Runner(readiness, log);
        runner.HostCredentialWaitLimit = TimeSpan.FromMinutes(5);

        await runner.WaitForHostGitCredentialsAsync("Fixture", "card-1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(log.Entries);
    }

    private static MergeIntoDevelopRunner Runner(GitHubAppHostCredentialReadiness readiness, ILogger<MergeIntoDevelopRunner> logger)
    {
        var config = new ConfigurationBuilder().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        return new MergeIntoDevelopRunner(git, log, logger, hostCredentials: readiness);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "The awaited log line never appeared.");
            await Task.Delay(10);
        }
    }

    private sealed class CapturingLogger : ILogger<MergeIntoDevelopRunner>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
        public IReadOnlyList<string> Entries => _entries.ToArray();
        public int Count(string marker) => _entries.Count(entry => entry.Contains(marker, StringComparison.Ordinal));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(formatter(state, exception));
    }
}
