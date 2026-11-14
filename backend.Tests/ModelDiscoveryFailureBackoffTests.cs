using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3709: a failed PTY model discovery is not retried by every request. On
/// the playthrough stack each catalogue read restarted a Claude discovery that
/// kept failing, which held the discovery gate for seconds per request.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class ModelDiscoveryFailureBackoffTests : IDisposable
{
    // A path that cannot start, so every spawn attempt fails at once.
    private static readonly string MissingCli = Path.Combine(Path.GetTempPath(), "agt-3709-no-cli", "missing");

    public void Dispose()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        ModelMetadataRegistry.SetDetectedCodexLadders(null);
        ModelMetadataRegistry.ClearDetectedVendorAvailability();
    }

    [Fact]
    public async Task Claude_FailedDiscovery_IsNotRetriedWithinTheBackoff()
    {
        var logger = new FailureLogger<ClaudeModelDiscovery>();
        var discovery = new ClaudeModelDiscovery(logger, new ConfigurationBuilder().Build());

        var failed = await discovery.GetAsync(MissingCli, forceRefresh: true);
        var second = await discovery.GetAsync(MissingCli);
        var third = await discovery.GetAsync(MissingCli);

        Assert.NotEmpty(failed.Models);
        Assert.NotEmpty(second.Models);
        Assert.NotEmpty(third.Models);
        Assert.Equal(1, logger.DiscoveryFailures);
    }

    [Fact]
    public async Task Codex_FailedDiscovery_IsNotRetriedWithinTheBackoff()
    {
        var logger = new FailureLogger<CodexModelDiscovery>();
        var discovery = new CodexModelDiscovery(logger, new ConfigurationBuilder().Build());

        await discovery.GetAsync(MissingCli, forceRefresh: true);
        var second = await discovery.GetAsync(MissingCli);
        await discovery.GetAsync(MissingCli);

        Assert.NotEmpty(second.Models);
        Assert.Equal(1, logger.DiscoveryFailures);
    }

    /// <summary>Counts the "PTY model discovery failed" warning, one per attempt.</summary>
    private sealed class FailureLogger<T> : ILogger<T>
    {
        private int _failures;
        public int DiscoveryFailures => Volatile.Read(ref _failures);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("PTY model discovery failed", StringComparison.Ordinal))
                Interlocked.Increment(ref _failures);
        }
    }
}
