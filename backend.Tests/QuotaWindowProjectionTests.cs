using AgentStudio.Cli;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Burn-rate projection math (AGT-2055 req 6): does a window's current pace
/// project past its cap before it resets?
/// </summary>
public sealed class QuotaWindowProjectionTests
{
    // Fixed clock; scripts never touch the wall clock.
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private static CliQuotaCapsService Caps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // No TaskRepository -> LocalAppData path that won't exist in CI ->
                // every GetCap returns the 95% default. That is exactly what we want.
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), "atp-proj-" + Guid.NewGuid().ToString("N")),
            })
            .Build();
        return new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
    }

    [Theory]
    [InlineData("5-hour", 5 * 60)]
    [InlineData("5h session", 5 * 60)]
    [InlineData("Weekly", 7 * 24 * 60)]
    [InlineData("7-day", 7 * 24 * 60)]
    [InlineData("Monthly", 30 * 24 * 60)]
    [InlineData("Daily", 24 * 60)]
    public void InferWindowLength_MapsHumanLabels(string label, int expectedMinutes)
    {
        var len = QuotaWindowProjection.InferWindowLength(label);
        Assert.NotNull(len);
        Assert.Equal(expectedMinutes, (int)len!.Value.TotalMinutes);
    }

    [Fact]
    public void InferWindowLength_UnknownLabel_IsNull()
        => Assert.Null(QuotaWindowProjection.InferWindowLength("credits"));

    [Fact]
    public void Project_HalfwayThrough5HourAt60Pct_ProjectsBreach()
    {
        // 5-hour window, resets in 2.5h -> 2.5h elapsed -> 50% of window gone.
        // 60% used at the halfway point extrapolates to 120% by reset.
        var window = new QuotaWindow
        {
            Label = "5-hour",
            UsedPct = 60,
            ResetAt = Now.AddHours(2.5),
        };

        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);

        Assert.NotNull(proj);
        Assert.True(proj!.BreachesBeforeReset);
        Assert.Equal(120, proj.ProjectedUsedPct, precision: 0);
        Assert.Equal(24, proj.BurnRatePctPerHour, precision: 0); // 60% over 2.5h
        Assert.Equal(2.5, proj.HoursRemaining, precision: 1);
    }

    [Fact]
    public void Project_SlowBurn_DoesNotProjectBreach()
    {
        // 20% used at the halfway point -> projects to 40%, well under the cap.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 20, ResetAt = Now.AddHours(2.5) };
        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);
        Assert.NotNull(proj);
        Assert.False(proj!.BreachesBeforeReset);
    }

    [Fact]
    public void Project_TooEarlyInWindow_IsNull()
    {
        // Only 6 minutes into a 5-hour window (2% elapsed) -> refuse to project.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 5, ResetAt = Now.AddHours(4.9) };
        Assert.Null(QuotaWindowProjection.Project(window, Now, capPct: 95));
    }

    [Fact]
    public void Project_UnknownWindowLength_IsNull()
    {
        var window = new QuotaWindow { Label = "credits", UsedPct = 90, ResetAt = Now.AddHours(1) };
        Assert.Null(QuotaWindowProjection.Project(window, Now, capPct: 95));
    }

    [Fact]
    public void Project_AlreadyOverCap_NotFlaggedAsProjection()
    {
        // Over cap already -> that is the strict cap's job, not a projection.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 97, ResetAt = Now.AddHours(2.5) };
        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);
        Assert.NotNull(proj);
        Assert.False(proj!.BreachesBeforeReset);
    }

    [Fact]
    public void EvaluateProjectedBreach_ReturnsWorstProjectedWindow()
    {
        var snapshot = new QuotaSnapshot
        {
            CliType = "claude",
            Windows =
            {
                new QuotaWindow { Label = "5-hour", UsedPct = 60, ResetAt = Now.AddHours(2.5) }, // -> 120%
                new QuotaWindow { Label = "Weekly", UsedPct = 10, ResetAt = Now.AddDays(3) },     // fine
            },
        };

        var ev = QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), Now);

        Assert.NotNull(ev);
        Assert.True(ev!.Blocked);
        Assert.True(ev.Projected);
        Assert.Equal("5-hour", ev.WindowLabel);
        Assert.Equal("claude", ev.CliType);
        Assert.Contains("projected", ev.DescribeReason());
    }

    [Fact]
    public void EvaluateProjectedBreach_HealthyWindows_ReturnsNull()
    {
        var snapshot = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = { new QuotaWindow { Label = "5-hour", UsedPct = 10, ResetAt = Now.AddHours(2.5) } },
        };
        Assert.Null(QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), Now));
    }

    [Fact]
    public void AgT2107_Honest1209Snapshot_ProjectsAbout24Pct()
    {
        var now = new DateTime(2026, 7, 11, 12, 9, 0, DateTimeKind.Utc);
        var window = new QuotaWindow
        {
            Label = "5-hour", UsedPct = 19,
            ResetAt = new DateTime(2026, 7, 11, 13, 14, 0, DateTimeKind.Utc),
        };

        var projection = QuotaWindowProjection.Project(window, now, capPct: 95);

        Assert.NotNull(projection);
        Assert.Equal(0.783, projection!.ElapsedFraction!.Value, precision: 3);
        Assert.Equal(24.3, projection.ProjectedUsedPct, precision: 1);
        Assert.False(projection.BreachesBeforeReset);
    }

    [Fact]
    public void AgT2107_PoisonedResetConflictsWithObservedStart_SuspendsProjection()
    {
        var now = new DateTime(2026, 7, 11, 12, 9, 0, DateTimeKind.Utc);
        var observedStart = new DateTime(2026, 7, 11, 8, 14, 0, DateTimeKind.Utc);
        var snapshot = new QuotaSnapshot
        {
            CliType = "codex",
            Windows =
            {
                new QuotaWindow
                {
                    Label = "5-hour", UsedPct = 19, ResetAt = now.AddHours(4),
                    ObservedStartAt = observedStart,
                },
            },
        };

        Assert.Null(QuotaWindowProjection.Project(snapshot.Windows[0], now, capPct: 95));
        Assert.Null(QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), now));
        var warning = Assert.IsType<QuotaProjectionWarning>(QuotaWindowProjection.FindWarning(snapshot, now));
        Assert.Contains("observed window start", warning.Reason);
        Assert.Equal(observedStart, warning.AssumedStartAt);
    }

    [Fact]
    public void AgT2107_AbsurdFiveXProjectionNearStart_IsWarningNotBreach()
    {
        var now = new DateTime(2026, 7, 11, 12, 9, 0, DateTimeKind.Utc);
        var snapshot = new QuotaSnapshot
        {
            CliType = "codex",
            Windows = { new QuotaWindow { Label = "5-hour", UsedPct = 19, ResetAt = now.AddHours(4) } },
        };

        Assert.Null(QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), now));
        var warning = Assert.IsType<QuotaProjectionWarning>(QuotaWindowProjection.FindWarning(snapshot, now));
        Assert.Equal(0.2, warning.ElapsedFraction, precision: 3);
        Assert.Equal(95, warning.ProjectedUsedPct, precision: 1);
        Assert.Contains("ratio exceeds 4", warning.Reason);
    }

    [Fact]
    public void AnchorWindowStarts_ResetMovesBeforeObservedReset_MarksCandidateSuspicious()
    {
        var now = new DateTime(2026, 7, 11, 12, 9, 0, DateTimeKind.Utc);
        var previous = new QuotaSnapshot
        {
            CliType = "codex",
            Windows = { new QuotaWindow { Label = "5-hour", UsedPct = 18, ResetAt = now.AddHours(1.083333) } },
        };
        var candidate = new QuotaSnapshot
        {
            CliType = "codex",
            Windows = { new QuotaWindow { Label = "5-hour", UsedPct = 19, ResetAt = now.AddHours(4) } },
        };

        var anchored = QuotaWindowProjection.AnchorWindowStarts(previous, candidate, now);

        Assert.Equal(previous.Windows[0].ResetAt!.Value.AddHours(-5), anchored.Windows[0].ObservedStartAt);
        Assert.Contains("resetAt moved", anchored.Windows[0].ProjectionSuspiciousReason);
        Assert.Null(QuotaWindowProjection.Project(anchored.Windows[0], now, capPct: 95));
    }

    [Fact]
    public void AnchorWindowStarts_WeeklyDropWithLaterReset_ReanchorsAndClearsReason()
    {
        // AGT-3080: Codex reset its weekly window early on 2026-10-11 (28 % -> 0 %,
        // confirmed by the plausibility gate's second probe).
        var previous = new QuotaSnapshot
        {
            CliType = "codex",
            Windows =
            {
                new QuotaWindow
                {
                    Label = "Weekly", UsedPct = 28,
                    ResetAt = new DateTime(2026, 10, 14, 4, 10, 0, DateTimeKind.Utc),
                    ObservedStartAt = new DateTime(2026, 10, 7, 4, 10, 0, DateTimeKind.Utc),
                },
            },
        };
        var candidate = new QuotaSnapshot
        {
            CliType = "codex",
            Windows =
            {
                new QuotaWindow
                {
                    Label = "Weekly", UsedPct = 0,
                    ResetAt = new DateTime(2026, 10, 18, 7, 34, 0, DateTimeKind.Utc),
                },
            },
        };

        var anchored = QuotaWindowProjection.AnchorWindowStarts(
            previous, candidate, nowUtc: new DateTime(2026, 10, 11, 8, 4, 0, DateTimeKind.Utc));

        var weekly = anchored.Windows[0];
        Assert.Equal(new DateTime(2026, 10, 11, 7, 34, 0, DateTimeKind.Utc), weekly.ObservedStartAt);
        Assert.Null(weekly.ProjectionSuspiciousReason);

        var later = weekly with { UsedPct = 30 };
        var projection = QuotaWindowProjection.Project(
            later, nowUtc: new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), capPct: 95);
        Assert.NotNull(projection);
        Assert.Equal(0.312, projection!.ElapsedFraction!.Value, precision: 3);
    }

    [Fact]
    public void AnchorWindowStarts_WeeklyResetMovesLaterWithoutDrop_StaysSuspicious()
    {
        var anchoredStart = new DateTime(2026, 10, 7, 4, 10, 0, DateTimeKind.Utc);
        var previous = new QuotaSnapshot
        {
            CliType = "codex",
            Windows =
            {
                new QuotaWindow
                {
                    Label = "Weekly", UsedPct = 22,
                    ResetAt = new DateTime(2026, 10, 14, 4, 10, 0, DateTimeKind.Utc),
                    ObservedStartAt = anchoredStart,
                },
            },
        };
        var candidate = new QuotaSnapshot
        {
            CliType = "codex",
            Windows =
            {
                new QuotaWindow
                {
                    Label = "Weekly", UsedPct = 23,
                    ResetAt = new DateTime(2026, 10, 18, 7, 34, 0, DateTimeKind.Utc),
                },
            },
        };

        var anchored = QuotaWindowProjection.AnchorWindowStarts(
            previous, candidate, nowUtc: new DateTime(2026, 10, 11, 8, 4, 0, DateTimeKind.Utc));

        Assert.Equal(anchoredStart, anchored.Windows[0].ObservedStartAt);
        Assert.Contains("resetAt moved", anchored.Windows[0].ProjectionSuspiciousReason);
    }

    [Fact]
    public void EvaluateProjectedBreach_ParsedCompactClaudeWeekly_ProjectsBreach()
    {
        // 48 h into the week that resets 2026-10-13T12:00Z: elapsed fraction 0.286.
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        static QuotaSnapshot Parse(int usedPct, DateTime nowUtc) => new()
        {
            CliType = "claude",
            Windows = ClaudeQuotaProbe.ParseUsageWindows(
                $"Currentweek(allmodels)███{usedPct}%usedResetsOct13,2pm(Europe/Berlin)", nowUtc: nowUtc),
        };

        var busy = Parse(40, now);
        var breach = QuotaWindowProjection.EvaluateProjectedBreach(busy, Caps(), nowUtc: now);

        Assert.NotNull(breach);
        Assert.True(breach!.Projected);
        Assert.Equal("Weekly (all models)", breach.WindowLabel);
        Assert.Equal(140, QuotaWindowProjection.Project(busy.Windows[0], now, capPct: 95)!.ProjectedUsedPct, precision: 1);

        Assert.Null(QuotaWindowProjection.EvaluateProjectedBreach(Parse(15, now), Caps(), nowUtc: now));
    }
}
