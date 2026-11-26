using AgentStudio.Dispatch;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: Index dates are explicit replay observations; retention never reads the wall clock.
public sealed class DispatchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-store-" + Guid.NewGuid().ToString("N"));
    private DispatchStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Dispatch:StoragePath"] = _root }).Build());

    [Fact]
    public void ReplaySelectsOnlyTheIndexAvailableAtWaveStartAndSurvivesRestart()
    {
        var old = TouchForecast.BuildIndex("old", new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), ["old.cs"], []);
        var newer = TouchForecast.BuildIndex("new", old.AsOf.AddDays(1), ["new.cs"], []);
        var store = Store();
        store.WriteIndex("project", newer);
        store.WriteIndex("project", old);
        Assert.Null(store.IndexAsOf("project", old.AsOf.AddTicks(-1)));
        Assert.Equal(old.Revision, Store().IndexAsOf("PROJECT", old.AsOf.AddHours(2))!.Revision);
        Assert.Equal(newer.Revision, Store().IndexAsOf("project", newer.AsOf)!.Revision);
        Assert.Null(store.IndexAsOf("other", newer.AsOf));
    }

    [Fact]
    public void IndexRetentionKeepsSixtyDaysAndTheBoundaryPredecessorWithoutTouchingOtherProjectsOrCards()
    {
        var store = Store();
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var forecast = TouchForecast.Compute("a.cs", TouchForecast.BuildIndex("tip", start, ["a.cs"], []), []);
        store.Write("project", "card", forecast);
        store.WriteIndex("other", TouchForecast.BuildIndex("other", start, [], []));
        for (var day = 0; day <= 120; day++)
            store.WriteIndex("project", TouchForecast.BuildIndex($"tip-{day}", start.AddDays(day), [], []));

        // A late historical import must not move the retention window backwards.
        store.WriteIndex("PROJECT", TouchForecast.BuildIndex("backfill", start.AddDays(-1), [], []));
        Assert.Null(Store().IndexAsOf("project", start.AddDays(58)));
        Assert.Equal("tip-59", Store().IndexAsOf("project", start.AddDays(60).AddTicks(-1))!.IntegrationTip);
        Assert.Equal("tip-60", Store().IndexAsOf("project", start.AddDays(60))!.IntegrationTip);
        Assert.Equal("tip-120", Store().IndexAsOf("project", start.AddDays(120))!.IntegrationTip);
        Assert.Equal("other", Store().IndexAsOf("other", start)!.IntegrationTip);
        Assert.Equal(forecast.IndexRevision, Store().Read("project", "card")!.IndexRevision);
        Assert.Equal(64, Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void IndexDatesAreOrderedByUtcInstantAcrossOffsets()
    {
        var store = Store();
        var first = TouchForecast.BuildIndex("first", new(2026, 11, 17, 23, 0, 0, TimeSpan.Zero), [], []);
        var second = TouchForecast.BuildIndex("second", first.AsOf.AddHours(1).ToOffset(TimeSpan.FromHours(-5)), [], []);
        store.WriteIndex("project", first);
        store.WriteIndex("project", second);
        Assert.Equal("second", Store().IndexAsOf("project", second.AsOf)!.IntegrationTip);
        Assert.Equal("first", Store().IndexAsOf("project", first.AsOf)!.IntegrationTip);
    }

    [Fact]
    public void CardsAreProjectScopedAndDeliveryEvidenceRetainsItsOriginalForecast()
    {
        var forecast = TouchForecast.Compute("a.cs", TouchForecast.BuildIndex("tip", new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), ["a.cs"], []), []);
        var delivery = new TouchDelivery("result", "base", forecast.ComputedAt, ["a.cs"], ["a.cs"],
            new(1, 1), new(1, 1)) { Forecast = forecast };
        var store = Store();
        store.Write("project", "../card", forecast with { Deliveries = [delivery] });
        Assert.Null(store.Read("other", "../card"));
        var restored = Store().Read("project", "../card")!;
        Assert.Equal("result", Assert.Single(restored.Deliveries).ResultSha);
        Assert.Equal(forecast.IndexRevision, restored.Deliveries[0].Forecast!.IndexRevision);
        Assert.Equal(forecast.CitedFiles, restored.CitedFiles);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
