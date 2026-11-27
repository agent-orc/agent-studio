using AgentStudio.Dispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: probe instants and retry boundaries are supplied explicitly.
public sealed class DispatchRepositoryRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-repository-" + Guid.NewGuid().ToString("N"));
    private DispatchStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Dispatch:StoragePath"] = _root }).Build());
    private sealed class CapturedLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            => Entries.Add((level, format(state, error)));
    }

    [Fact]
    public void Missing_tip_backs_off_across_restarts_reports_no_repository_and_logs_only_transitions()
    {
        var at = new DateTimeOffset(2026, 11, 27, 1, 40, 0, TimeSpan.Zero);
        var store = Store();
        var original = TouchForecast.Compute("a.cs", TouchForecast.BuildIndex("tip", at, ["a.cs"], []), []);
        store.Write("p", "card", original with { Deliveries = [new("result", "base", at,
            ["a.cs"], ["a.cs"], new(1, 1), new(1, 1)) { Forecast = original }] });
        var log = new CapturedLogger();
        var refresh = new DispatchRepositoryRefresh(store, log);
        var probes = 0;
        (string, string)? Missing() { probes++; return null; }
        Assert.Null(refresh.Probe("p", at, Missing));
        Assert.Equal("no repository", Store().Read("P", "card")!.RepositoryStatus);
        Assert.Empty(store.Read("p", "card")!.ForecastFiles);
        Assert.Equal("result", Assert.Single(store.Read("p", "card")!.Deliveries).ResultSha);
        Assert.Equal("no repository", store.Read("p", "new-card")!.RepositoryStatus);
        refresh = new DispatchRepositoryRefresh(Store(), log);
        for (var seconds = 20; seconds < 120; seconds += 20)
            Assert.Null(refresh.Probe("p", at.AddSeconds(seconds), Missing));
        Assert.Equal(1, probes);
        Assert.Null(refresh.Probe("p", at.AddMinutes(2), Missing));
        Assert.Equal(2, probes);
        Assert.Equal(at.AddMinutes(6), store.ReadRepositoryState("p")!.NextProbeAt);
        Assert.Single(log.Entries);
        Assert.All(log.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
        // A project with no cards has the same project-level recovery path.
        Assert.NotNull(refresh.Probe("p", at.AddMinutes(6), () => ("repo", "tip")));
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("available", store.Read("p", "card")!.RepositoryStatus);
        Assert.Equal(original.ForecastFiles, store.Read("p", "card")!.ForecastFiles);
        Assert.NotNull(refresh.Probe("p", at.AddMinutes(7), () => ("repo", "tip")));
        Assert.Equal(2, log.Entries.Count);
        Assert.Null(store.ReadRepositoryState("other"));
    }

    [Fact]
    public void Retry_delay_is_bounded_and_a_new_outage_logs_a_new_transition()
    {
        var at = DateTimeOffset.UnixEpoch;
        var store = Store();
        var log = new CapturedLogger();
        var refresh = new DispatchRepositoryRefresh(store, log);
        for (var i = 0; i < 10; i++)
        {
            refresh.Probe("p", at, () => null);
            var next = store.ReadRepositoryState("p")!.NextProbeAt;
            Assert.InRange(next - at, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
            at = next;
        }
        Assert.Single(log.Entries);
        refresh.Probe("p", at, () => ("repo", "tip"));
        refresh.Probe("p", at.AddSeconds(1), () => null);
        Assert.Equal(3, log.Entries.Count);
        Assert.Equal(1, store.ReadRepositoryState("p")!.Failures);
    }

    [Fact]
    public void Fourteen_minutes_of_twenty_second_refreshes_produce_four_probes_and_one_log()
    {
        var at = DateTimeOffset.UnixEpoch;
        var log = new CapturedLogger();
        var refresh = new DispatchRepositoryRefresh(Store(), log);
        var probes = 0;
        for (var seconds = 0; seconds <= 14 * 60; seconds += 20)
            refresh.Probe("empty-project", at.AddSeconds(seconds), () => { probes++; return null; });
        Assert.Equal(4, probes);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, log.Entries[0].Level);
        Assert.Equal("no repository", Store().Read("empty-project", "not-yet-created")!.RepositoryStatus);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
