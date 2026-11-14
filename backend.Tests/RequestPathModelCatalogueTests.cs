using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3709: <c>POST /api/tasks</c> and the Create dialog's policy suggestion
/// never wait for a cold catalogue discovery (a CLI started in a PTY).
/// </summary>
// clock-independent: FetchedAt only labels the fake catalogue; nothing compares it with now.
public class RequestPathModelCatalogueTests
{
    private static CliModelCatalog Live() => new()
    {
        Models = [],
        Source = "live",
        FetchedAt = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task AnsweredCatalogue_IsReturnedAsIs()
    {
        var catalogue = await RequestPathModelCatalogue.GetAsync(
            CliTypes.Claude, _ => Task.FromResult(Live()), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal("live", catalogue.Source);
    }

    [Fact]
    public async Task SlowDiscovery_AnswersFromTheRegistryWithinTheBudget_AndKeepsRunning()
    {
        var discovery = new TaskCompletionSource<CliModelCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var catalogue = await RequestPathModelCatalogue.GetAsync(
            CliTypes.Claude,
            token => { observed = token; return discovery.Task; },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"waited {started.Elapsed}");
        Assert.Equal(RequestPathModelCatalogue.RegistrySource, catalogue.Source);
        Assert.NotEmpty(catalogue.Models);
        // The discovery is not cancelled with the request, so it still warms the cache.
        Assert.False(observed.CanBeCanceled);
        discovery.SetResult(Live());
    }

    [Fact]
    public async Task FailedDiscovery_FallsBackToTheRegistry()
    {
        var catalogue = await RequestPathModelCatalogue.GetAsync(
            CliTypes.Codex,
            _ => Task.FromException<CliModelCatalog>(new InvalidOperationException("pty failed")),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Equal(RequestPathModelCatalogue.RegistrySource, catalogue.Source);
    }

    [Fact]
    public async Task SlowDiscovery_WithoutARegistryCatalogue_TimesOut()
    {
        var discovery = new TaskCompletionSource<CliModelCatalog>();

        await Assert.ThrowsAsync<TimeoutException>(() => RequestPathModelCatalogue.GetAsync(
            CliTypes.Gemini, _ => discovery.Task, TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }
}
