using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Configuration for the Watcher: the hot kill switch, the sweep cadence, the
/// detector thresholds, and the dedicated contingent.
/// </summary>
public sealed class WatcherOptionsTests
{
    [Fact]
    public void Defaults_AreTheDossiersFiveMinuteObserveFirstSweep()
    {
        var options = WatcherOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SweepInterval);
        Assert.Equal(2, options.PersistenceSweeps);
        Assert.Equal(TimeSpan.FromDays(14), options.SuppressionDuration);
    }

    [Fact]
    public void ShippedAppsettingsMatchThePlatformDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath(), optional: false)
            .Build();

        var options = WatcherOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(WatcherDefaults.SweepIntervalSeconds), options.SweepInterval);
        Assert.Equal(WatcherContingentOptions.Default, options.Contingent);
    }

    [Fact]
    public void TheKillSwitchIsReadableFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WatcherDefaults.EnabledKey] = "false",
            })
            .Build();

        Assert.False(WatcherOptions.FromConfiguration(configuration).Enabled);
    }

    [Fact]
    public void TheSweepIntervalIsClampedToASaneRange()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Interval("1"));
        Assert.Equal(TimeSpan.FromHours(1), Interval("999999"));
    }

    [Fact]
    public void AZeroContingentIsHonouredRatherThanTreatedAsUnset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watcher:Contingent:DailyProposals"] = "0",
                ["Watcher:Contingent:DailyTokens"] = "0",
            })
            .Build();

        var contingent = WatcherOptions.FromConfiguration(configuration).Contingent;

        Assert.Equal(0, contingent.DailyProposals);
        Assert.Equal(0, contingent.DailyTokens);
        // Unset keys keep their defaults; only what was configured changes.
        Assert.Equal(WatcherContingentOptions.Default.WeeklyProposals, contingent.WeeklyProposals);
    }

    [Fact]
    public void DetectorThresholdsAreConfigurableWithinBounds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watcher:ProbeRepeatThreshold"] = "5",
                ["Watcher:ReviewAttemptThreshold"] = "1",
                ["Watcher:HygieneGraceHours"] = "72",
            })
            .Build();

        var detectors = WatcherOptions.FromConfiguration(configuration).Detectors;

        Assert.Equal(5, detectors.ProbeRepeatThreshold);
        // Below the floor: a threshold of one would fire on a single occurrence,
        // which is not a repetition.
        Assert.Equal(2, detectors.ReviewAttemptThreshold);
        Assert.Equal(TimeSpan.FromHours(72), detectors.HygieneGrace);
    }

    [Fact]
    public void TheKillSwitchIsInTheRuntimeConfigCatalogue()
    {
        var catalogue = OrchestratorConfigCatalog.Definitions;

        var entry = Assert.Single(catalogue, row => row.Key == WatcherDefaults.EnabledKey);
        Assert.Equal("bool", entry.Type);
        Assert.Equal(true, entry.DefaultValue);
    }

    private static TimeSpan Interval(string seconds)
        => WatcherOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Watcher:SweepIntervalSeconds"] = seconds,
                })
                .Build()).SweepInterval;

    private static string AppSettingsPath()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            var candidate = Path.Combine(current, "backend", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("backend/appsettings.json not found above the test base directory.");
    }
}
