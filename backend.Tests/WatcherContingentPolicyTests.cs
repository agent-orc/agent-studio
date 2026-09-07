using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests for the contingent. Admission is pure, so every budget
/// edge is a table row rather than a wired-up sweep.
/// </summary>
public class WatcherContingentPolicyTests
{
    // A Wednesday, so the ISO week start is a distinct earlier day.
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    private static WatcherContingentEntry Entry(string kind, DateTime atUtc, long tokens = 0, int count = 1) =>
        new() { AtUtc = atUtc, Kind = kind, Tokens = tokens, Count = count };

    [Fact]
    public void DayStart_AndWeekStart_AnchorTheTwoWindows()
    {
        Assert.Equal(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), WatcherContingentPolicy.DayStart(Now));
        // Monday of that week.
        Assert.Equal(new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc), WatcherContingentPolicy.WeekStart(Now));
    }

    [Theory]
    // Sunday belongs to the week that started the preceding Monday.
    [InlineData(2026, 9, 13, 2026, 9, 7)]
    // Monday is its own week start.
    [InlineData(2026, 9, 7, 2026, 9, 7)]
    [InlineData(2026, 9, 14, 2026, 9, 14)]
    public void WeekStart_TreatsMondayAsTheFirstDay(int y, int m, int d, int ey, int em, int ed)
    {
        var start = WatcherContingentPolicy.WeekStart(new DateTime(y, m, d, 8, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(ey, em, ed, 0, 0, 0, DateTimeKind.Utc), start);
    }

    [Fact]
    public void Usage_SplitsTheDayWindowOutOfTheWeekWindow()
    {
        var ledger = new[]
        {
            Entry(WatcherContingentKinds.ModelCall, Now.AddHours(-2), tokens: 100),
            Entry(WatcherContingentKinds.ModelCall, Now.AddDays(-1), tokens: 200),
            // Before the week start: outside both windows.
            Entry(WatcherContingentKinds.ModelCall, Now.AddDays(-9), tokens: 999),
            Entry(WatcherContingentKinds.Proposal, Now.AddHours(-1)),
            Entry(WatcherContingentKinds.Comment, Now.AddDays(-1)),
        };

        var usage = WatcherContingentPolicy.Usage(ledger, Now);

        Assert.Equal(1, usage.ModelCallsDay);
        Assert.Equal(2, usage.ModelCallsWeek);
        Assert.Equal(100, usage.TokensDay);
        Assert.Equal(300, usage.TokensWeek);
        Assert.Equal(1, usage.ProposalsDay);
        Assert.Equal(0, usage.CommentsDay);
        Assert.Equal(1, usage.CommentsWeek);
    }

    [Fact]
    public void Usage_CountsAnUnpricedCallWithoutInventingACost()
    {
        var ledger = new[]
        {
            new WatcherContingentEntry
            {
                AtUtc = Now, Kind = WatcherContingentKinds.ModelCall,
                Tokens = 10, CostUsd = null, PriceKnown = false,
            },
        };

        var usage = WatcherContingentPolicy.Usage(ledger, Now);

        Assert.Equal(1, usage.UnpricedCallsDay);
        Assert.Equal(0m, usage.CostUsdDay);
    }

    [Fact]
    public void Snapshot_ShowsAnUnknownCostRatherThanZeroWhenAPriceIsMissing()
    {
        var snapshot = new WatcherContingentSnapshot
        {
            Limits = WatcherContingentLimits.Default,
            Usage = new WatcherContingentUsage { UnpricedCallsDay = 1, CostUsdDay = 0m },
            DayStartUtc = WatcherContingentPolicy.DayStart(Now),
            WeekStartUtc = WatcherContingentPolicy.WeekStart(Now),
        };

        Assert.Equal("unknown", snapshot.CostUsdDayDisplay);
    }

    [Theory]
    // Zero closes the dimension immediately.
    [InlineData(0, 0, false)]
    // Room left.
    [InlineData(5, 4, true)]
    // Exactly at the ceiling: the next unit does not fit.
    [InlineData(5, 5, false)]
    [InlineData(5, 6, false)]
    public void Admit_RefusesOnceTheCountCeilingIsReached(int limit, int used, bool expected)
    {
        var limits = new WatcherContingentLimits { ProposalsPerDay = limit, ProposalsPerWeek = 1_000 };
        var usage = new WatcherContingentUsage { ProposalsDay = used, ProposalsWeek = used };

        var verdict = WatcherContingentPolicy.Admit(WatcherContingentKinds.Proposal, limits, usage);

        Assert.Equal(expected, verdict.Allowed);
        if (!expected) Assert.Contains("proposals per day", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Admit_ChecksTheWeekCeilingEvenWhenTheDayHasRoom()
    {
        var limits = new WatcherContingentLimits { ProposalsPerDay = 10, ProposalsPerWeek = 3 };
        var usage = new WatcherContingentUsage { ProposalsDay = 0, ProposalsWeek = 3 };

        var verdict = WatcherContingentPolicy.Admit(WatcherContingentKinds.Proposal, limits, usage);

        Assert.False(verdict.Allowed);
        Assert.Contains("proposals per week", verdict.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    // The estimate is checked before the call, so an oversized prompt is refused
    // rather than discovered after it was paid for.
    [InlineData(1_000, 0, 900, true)]
    [InlineData(1_000, 0, 1_001, false)]
    [InlineData(1_000, 900, 200, false)]
    public void Admit_ChecksTheTokenEstimateBeforeTheCall(
        long limit, long used, long estimate, bool expected)
    {
        var limits = new WatcherContingentLimits
        {
            ModelCallsPerDay = 100, ModelCallsPerWeek = 100,
            TokensPerDay = limit, TokensPerWeek = limit,
        };
        var usage = new WatcherContingentUsage { TokensDay = used, TokensWeek = used };

        var verdict = WatcherContingentPolicy.Admit(
            WatcherContingentKinds.ModelCall, limits, usage, estimate);

        Assert.Equal(expected, verdict.Allowed);
    }

    [Fact]
    public void Admit_TreatsAnAbsentCeilingAsUnlimited()
    {
        var limits = new WatcherContingentLimits();
        var usage = new WatcherContingentUsage { ProposalsDay = 10_000 };

        Assert.True(WatcherContingentPolicy
            .Admit(WatcherContingentKinds.Proposal, limits, usage).Allowed);
    }

    [Fact]
    public void Admit_RefusesAnUnknownKindRatherThanWavingItThrough()
    {
        var verdict = WatcherContingentPolicy.Admit(
            "free-money", WatcherContingentLimits.Default, new WatcherContingentUsage());

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void ZeroLimits_CloseEveryDimension()
    {
        var usage = new WatcherContingentUsage();

        Assert.All(WatcherContingentKinds.All, kind => Assert.False(
            WatcherContingentPolicy.Admit(kind, WatcherContingentLimits.Zero, usage).Allowed));
    }

    [Fact]
    public void Options_ReadAConfiguredZeroAsAClosedDimensionAndNotAsAnAbsentValue()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Watcher:Contingent:ProposalsPerDay"] = "0",
        }).Build();

        var options = WatcherOptions.FromConfiguration(configuration);

        Assert.Equal(0, options.Contingent.ProposalsPerDay);
        // An absent value still falls back to the default.
        Assert.Equal(
            WatcherContingentLimits.Default.CommentsPerDay, options.Contingent.CommentsPerDay);
    }

    [Fact]
    public void Options_ClampEveryNumericValue()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Watcher:IntervalSeconds"] = "1",
            ["Watcher:RepetitionThreshold"] = "0",
            ["Watcher:MaxProposalsPerSweep"] = "9999",
            ["Watcher:SuppressionDays"] = "-5",
            ["Watcher:Contingent:TokensPerDay"] = "-1",
        }).Build();

        var options = WatcherOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(30), options.Interval);
        Assert.Equal(2, options.Detectors.RepetitionThreshold);
        Assert.Equal(50, options.MaxProposalsPerSweep);
        Assert.Equal(TimeSpan.FromDays(1), options.SuppressionPeriod);
        Assert.Equal(0, options.Contingent.TokensPerDay);
    }

    [Fact]
    public void Options_DefaultToADisabledWatcherAndTheFiveMinuteCadence()
    {
        var options = WatcherOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Interval);
        Assert.Equal("sol-medium", options.AnalysisTier);
    }

    [Fact]
    public void AnalysisTier_MustNameATierOfTheRoutingPolicy()
    {
        var registry = new ModelRoutingPolicyRegistry();
        var options = WatcherOptions.Default;

        var (model, thinkingLevel) = WatcherModelRouting.Route(registry, options.AnalysisTier);

        Assert.False(string.IsNullOrWhiteSpace(model));
        Assert.Equal("medium", thinkingLevel);
        Assert.Throws<InvalidOperationException>(() => WatcherModelRouting.Route(registry, "no-such-tier"));
    }
}
