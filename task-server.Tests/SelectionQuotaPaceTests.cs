using AgentStudio.Cli;
using AgentStudio.Shared;
using Xunit;

namespace TaskServer.Tests;

// clock-independent: compare the quota lines at fixed instants in one provider window.
public sealed class SelectionQuotaPaceTests
{
    private static readonly DateTime Start = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly QuotaWindow Window = new() { Label = "Weekly", ResetAt = Start.AddDays(7), UsedPct = 60 };

    [Fact]
    public void Steady_keeps_the_existing_halfway_and_early_reading_expectations()
    {
        var halfway = Start.AddDays(3.5);
        var steady = SelectionQuotaMath.Project(Window, halfway, 95, QuotaPace.Steady);
        Assert.Equal(SelectionQuotaMath.Project(Window, halfway, 95), steady);
        Assert.Equal(120, steady!.ProjectedUsedPct);
        Assert.True(steady.BreachesBeforeReset);
        Assert.Null(SelectionQuotaMath.Project(Window with { UsedPct = 0 }, Start.AddHours(8), 95));
        Assert.Null(SelectionQuotaMath.Project(Window, Start.AddDays(1), 95));
    }

    [Fact]
    public void Deadline_line_releases_at_the_crossing_and_stops_at_the_cap()
    {
        var oneDay = new QuotaPace("useBy", Start.AddDays(1));
        Assert.Equal(40, SelectionQuotaMath.Project(Window, Start.AddHours(12), 80, oneDay)!.AllowedPct);
        var pace = new QuotaPace("useBy", Start.AddDays(4));
        var projection = SelectionQuotaMath.Project(Window, Start.AddDays(2), 80, pace)!;
        Assert.Equal(40, projection.AllowedPct);
        Assert.True(projection.BreachesBeforeReset);
        Assert.Equal(Start.AddDays(3), projection.FreeAt);
        Assert.False(SelectionQuotaMath.Project(Window, projection.FreeAt!.Value, 80, pace)!.BreachesBeforeReset);
        Assert.Equal(80, SelectionQuotaMath.Project(Window, Start.AddDays(5), 80, pace)!.AllowedPct);
        Assert.False(SelectionQuotaMath.Project(Window, Start.AddDays(5), 80, pace)!.BreachesBeforeReset);
        Assert.Equal(40, SelectionQuotaMath.Project(Window, Start.AddDays(3.5), 80, new("useBy", Start.AddDays(10)))!.AllowedPct);
        Assert.Null(SelectionQuotaMath.Project(Window, Start.AddDays(3), 80, new("unpaced")));
    }
}
