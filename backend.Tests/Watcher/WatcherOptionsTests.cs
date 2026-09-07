using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests.Watcher;

/// <summary>
/// Configuration binding for the Watcher section. The kill switch defaults to
/// off because the dossier decision in section 9 is still pending, and every
/// numeric value is clamped so a bad setting cannot turn the sweep into a busy
/// loop or a budget into a negative number.
/// </summary>
public sealed class WatcherOptionsTests
{
    [Fact]
    public void TheKillSwitchDefaultsToOff()
    {
        var options = WatcherOptions.FromConfiguration(Configuration([]));

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(WatcherDefaults.IntervalSeconds), options.Interval);
    }

    [Fact]
    public void TheKillSwitchAndCadenceAreReadFromTheWatcherSection()
    {
        var options = WatcherOptions.FromConfiguration(Configuration(new()
        {
            ["Watcher:Enabled"] = "true",
            ["Watcher:IntervalSeconds"] = "600",
        }));

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Interval);
    }

    [Theory]
    [InlineData("1", 30)]
    [InlineData("100000", 3600)]
    public void TheSweepCadenceIsClamped(string configured, int expectedSeconds)
    {
        var options = WatcherOptions.FromConfiguration(Configuration(new()
        {
            ["Watcher:IntervalSeconds"] = configured,
        }));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.Interval);
    }

    [Fact]
    public void ContingentValuesBindAndNeverGoNegative()
    {
        var options = WatcherOptions.FromConfiguration(Configuration(new()
        {
            ["Watcher:Contingent:ProposalsPerDay"] = "3",
            ["Watcher:Contingent:TokensPerWeek"] = "-500",
        }));

        Assert.Equal(3, options.Contingent.ProposalsPerDay);
        Assert.Equal(0, options.Contingent.TokensPerWeek);
    }

    [Fact]
    public void DetectorThresholdsBindFromTheSameSection()
    {
        var options = WatcherOptions.FromConfiguration(Configuration(new()
        {
            ["Watcher:RepetitionThreshold"] = "5",
            ["Watcher:HygieneGracePeriodHours"] = "48",
        }));

        Assert.Equal(5, options.Thresholds.RepetitionThreshold);
        Assert.Equal(TimeSpan.FromHours(48), options.Thresholds.HygieneGracePeriod);
    }

    [Fact]
    public void ARepetitionThresholdBelowTwoIsClampedBecauseOneOccurrenceIsNotARepetition()
    {
        var options = WatcherOptions.FromConfiguration(Configuration(new()
        {
            ["Watcher:RepetitionThreshold"] = "1",
        }));

        Assert.Equal(2, options.Thresholds.RepetitionThreshold);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
