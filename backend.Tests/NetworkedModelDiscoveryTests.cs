using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3411: in the networked profile host CLI discovery is never attempted.
/// The catalogue comes from the registry and the decision is logged once,
/// without a warning or an exception.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class NetworkedModelDiscoveryTests : IDisposable
{
    // A path that cannot start, so any spawn attempt would surface as a warning.
    private static readonly string MissingCli = Path.Combine(Path.GetTempPath(), "agt-3411-no-cli", "missing");

    public void Dispose()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        ModelMetadataRegistry.SetDetectedCodexLadders(null);
        ModelMetadataRegistry.ClearDetectedVendorAvailability();
    }

    [Fact]
    public async Task Codex_Networked_UsesTheRegistryCatalogueAndLogsOnce()
    {
        var logger = new CapturingLogger<CodexModelDiscovery>();
        var discovery = new CodexModelDiscovery(logger, Networked());

        var first = await discovery.GetAsync(MissingCli, forceRefresh: true);
        var second = await discovery.GetAsync(MissingCli);

        Assert.Equal(HostCliDiscovery.NetworkedRegistrySource, first.Source);
        Assert.Equal(HostCliDiscovery.NetworkedRegistrySource, second.Source);
        Assert.Contains(first.Models, model => model.Available);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning || entry.Exception is not null);
    }

    [Fact]
    public async Task Claude_Networked_UsesTheRegistryCatalogueAndLogsOnce()
    {
        var logger = new CapturingLogger<ClaudeModelDiscovery>();
        var discovery = new ClaudeModelDiscovery(logger, Networked());

        var first = await discovery.GetAsync(MissingCli, forceRefresh: true);
        await discovery.GetAsync(MissingCli);

        Assert.Equal(HostCliDiscovery.NetworkedRegistrySource, first.Source);
        Assert.Contains(first.Models, model => model.Available);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning || entry.Exception is not null);
    }

    /// <summary>
    /// Time-compressed replay of the reported hour: one batch tick per minute
    /// asks the economy advisor for the batch-plan-advisor model, which reads
    /// the Codex catalogue. Neither the discovery nor the economy advisor may
    /// log a warning or an exception.
    /// </summary>
    [Fact]
    public async Task OneHourOfMinuteTicks_LogsNoWarningAndNoException()
    {
        var discoveryLog = new CapturingLogger<CodexModelDiscovery>();
        var economyLog = new CapturingLogger<PipelineStepEconomyAdvisor>();
        var discovery = new CodexModelDiscovery(discoveryLog, Networked());
        var economy = new PipelineStepEconomyAdvisor(new CatalogueModelEconomyAdvisor(),
            new DiscoveryCatalogues(discovery, MissingCli), economyLog);

        for (var minute = 0; minute < 60; minute++)
            Assert.Null(await economy.SuggestModelAsync(new ProjectSettings(), "batch-plan-advisor",
                CancellationToken.None, forceEconomy: true));

        Assert.Single(discoveryLog.Entries);
        Assert.DoesNotContain(discoveryLog.Entries.Concat(economyLog.Entries),
            entry => entry.Level >= LogLevel.Warning || entry.Exception is not null);
    }

    [Fact]
    public void Local_Profile_AllowsHostDiscovery()
    {
        Assert.True(HostCliDiscovery.Allowed(new ConfigurationBuilder().Build()));
        Assert.False(HostCliDiscovery.Allowed(Networked()));
    }

    private static IConfiguration Networked() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = "networked" })
        .Build();

    private sealed class DiscoveryCatalogues(CodexModelDiscovery discovery, string cliPath)
        : IPipelineModelCatalogueProvider
    {
        public Task<CliModelCatalog> GetAsync(string cliType, CancellationToken ct)
            => discovery.GetAsync(cliPath, ct: ct);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information)
                lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
