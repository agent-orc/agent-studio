using System.Text.Json;
using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Tests.Architecture;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: historical wave timestamps and simulated event time are fixed inputs.
public sealed class DispatchReplayHarnessTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Fixture = Path.Combine(CSharpSourceScanner.RepoRoot(),
        "backend.Tests/Fixtures/dispatch/replay/wave-2026-11-17.json");

    private static DispatchReplayCard Card(string key, string file, string? forecast = null) => new(
        new(key, 999, Start, [], true,
            new([forecast ?? file], [TouchForecast.Module(forecast ?? file)], DispatchForecastConfidence.High), []), [file]);

    private static DispatchReplayWave Wave(params DispatchReplayCard[] cards) => new("project", "wave", Start,
        cards, [], DispatchLedgerSummary.Empty, "index-at-start");

    [Fact]
    public void FixtureIsExactCopyAndAllFiveStrategiesUsePolicyPlans()
    {
        Assert.Equal(File.ReadAllBytes(Path.Combine(CSharpSourceScanner.RepoRoot(),
            "docs/dispatcher-personas/replay/wave-2026-11-17.json")), File.ReadAllBytes(Fixture));
        var report = DispatchReplaySimulator.Run(DispatchReplayCommand.ReadFixture(Fixture, "project"), new());
        Assert.Equal(5, report.Strategies.Count);
        Assert.Equal(57, report.ConflictFreeBound.Runs);
        Assert.Equal(0, report.ConflictFreeBound.CardsNeedingSync);
        Assert.All(report.Strategies, row =>
        {
            Assert.Equal(57, row.Runs);
            Assert.NotEmpty(row.Plans);
            Assert.All(row.Plans, plan => Assert.Equal(row.Strategy, plan.StrategyId));
            Assert.InRange(row.Parallelism, 0, 1);
            Assert.Equal(row.ValuePlain * Math.Min(1, row.Parallelism / 0.8), row.Value);
        });
        var baseline = report.Strategies.Single(row => row.Strategy == DispatchStrategies.LaneEntry);
        Assert.Equal(20, baseline.CardsNeedingSync);
        Assert.Equal(31, baseline.ExposedPairs);
        Assert.Equal(15, baseline.Makespan);
        Assert.InRange(report.Strategies.Single(row => row.Strategy == DispatchStrategies.FileOverlap).ForecastF1, 0.53, 0.59);
    }

    [Fact]
    public void LagLeavesFinishedCodingCardsExposedUntilIntegration()
    {
        var wave = Wave(Card("A", "backend/F/a.cs"), Card("B", "backend/F/a.cs"));
        var running = DispatchReplaySimulator.Run(wave, new(1, IntegrationLag: 1, Check: DispatchOverlapWindow.Running));
        var flight = DispatchReplaySimulator.Run(wave, new(1, IntegrationLag: 1));
        var exposed = running.Strategies.Single(row => row.Strategy == DispatchStrategies.FileOverlap);
        var safe = flight.Strategies.Single(row => row.Strategy == DispatchStrategies.FileOverlap);
        Assert.Equal(1, exposed.CardsNeedingSync);
        Assert.Equal(1, exposed.ExposedPairs);
        Assert.Equal(0, safe.CardsNeedingSync);
        Assert.Equal(3, exposed.Makespan);
        Assert.Equal(4, safe.Makespan);
        Assert.Equal(1.5, exposed.RoundsPerCard);
        Assert.Equal(1.0 / 3, exposed.ReworkShare);
        Assert.Null(exposed.Value);
    }

    [Fact]
    public void SizeDurationAndHubTruthAreMeasuredWithoutForecastLeakage()
    {
        const string hub = "backend/Host/Program.cs";
        var wave = Wave(Card("A", hub), Card("B", hub)) with { HubFiles = [hub] };
        var report = DispatchReplaySimulator.Run(wave, new(2, "size"));
        var row = report.Strategies.Single(row => row.Strategy == DispatchStrategies.FileOverlap);
        Assert.Equal(1, row.ExposedPairs);
        Assert.Equal(1, row.CardsNeedingSync);
        Assert.Equal(1 + 1.0 / 15, row.Makespan, 8);
        Assert.Equal(0, report.ConflictFreeBound.ExposedPairs);
    }

    [Fact]
    public void DependencyChainsWaitUntilIntegrationAndPinsRemainFirst()
    {
        var a = Card("A", "backend/F/a.cs");
        var b = Card("B", "backend/G/b.cs");
        var c = Card("C", "backend/H/c.cs");
        a = a with { Ready = a.Ready with { Order = 1 } };
        b = b with { Ready = b.Ready with { DependsOn = ["A"], DependenciesSatisfied = false } };
        var report = DispatchReplaySimulator.Run(Wave(a, b, c), new(2, IntegrationLag: 1));
        Assert.All(report.Strategies, row =>
        {
            Assert.Equal("A", row.Schedule[0].Card);
            Assert.True(row.Schedule.Single(run => run.Card == "B").Start >= row.Schedule.Single(run => run.Card == "A").IntegratedAt);
        });
    }

    [Theory]
    [InlineData(0, "unit", 0)]
    [InlineData(1, "unknown", 0)]
    [InlineData(1, "size", -1)]
    [InlineData(1, "unit", double.NaN)]
    public void InvalidSimulationInputsFail(int slots, string duration, double lag)
        => Assert.Throws<ArgumentException>(() => DispatchReplaySimulator.Run(Wave(Card("A", "a.cs")), new(slots, duration, lag)));

    [Fact]
    public void MissingHistoricalIndexAndPostStartTextFailBeforeReadingGit()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "dispatch-replay-" + Guid.NewGuid().ToString("N"));
        var store = new DispatchStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Dispatch:StoragePath"] = temporary }).Build());
        var card = new DispatchReplayRecordedCard("wave", Card("A", "a.cs").Ready, "a.cs", Start.AddMinutes(1), new('a', 40), new('b', 40));
        var recording = new DispatchReplayRecording("project", [card]);
        try
        {
            store.WriteIndex("project", TouchForecast.BuildIndex("future", Start.AddDays(1), ["a.cs"], []));
            Assert.Throws<InvalidOperationException>(() => DispatchReplayCommand.Build(recording, "project", "unused", store, "wave", null, null));
            store.WriteIndex("project", TouchForecast.BuildIndex("past", Start.AddDays(-1), ["a.cs"], []));
            Assert.Throws<ArgumentException>(() => DispatchReplayCommand.Build(recording, "project", "unused", store, "wave", null, null));
            Assert.Throws<ArgumentException>(() => DispatchReplayCommand.Build(recording, "other", "unused", store, "wave", null, null));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    [Fact]
    public void Command_returns_an_error_for_unreadable_or_incomplete_wave_evidence()
    {
        var path = Path.Combine(Path.GetTempPath(), "dispatch-bad-wave-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (var body in new[] { "{invalid", "{}", "{\"schema\":\"dispatch-replay-wave/1\"}" })
            {
                File.WriteAllText(path, body);
                using var output = new StringWriter();
                using var error = new StringWriter();
                Assert.Equal(2, DispatchReplayCommand.Execute(["--fixture", path, "--project", "p"], output, error));
                Assert.Equal("", output.ToString());
                Assert.NotEmpty(error.ToString());
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"project\":\"p\"}")]
    [InlineData("{\"project\":\"p\",\"cards\":null}")]
    [InlineData("{\"project\":\"p\",\"cards\":[null]}")]
    [InlineData("{\"project\":\"p\",\"cards\":[{\"waveId\":\"w\"}]}")]
    public void Incomplete_recordings_return_a_command_error_without_reading_Git(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), "dispatch-recording-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, body);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, DispatchReplayCommand.Execute(["--recording", path, "--project", "p",
                "--store", path + ".store", "--repo", "unused", "--wave", "w"], output, error));
            Assert.Equal("", output.ToString());
            Assert.NotEmpty(error.ToString());
            Assert.False(Directory.Exists(path + ".store"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CommandEmitsReplayJsonAndRejectsUnknownOptions()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, DispatchReplayCommand.Execute(["--fixture", Fixture, "--project", "project", "--lag", "1", "--check", "running"], output, error));
        Assert.Equal("", error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("replay", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal("running", json.RootElement.GetProperty("options").GetProperty("check").GetString());
        Assert.Equal(100, json.RootElement.GetProperty("weights").EnumerateObject().Sum(value => value.Value.GetInt32()));
        Assert.Equal(5, json.RootElement.GetProperty("strategies").GetArrayLength());
        Assert.Equal(2, DispatchReplayCommand.Execute(["--typo", "value"], output, error));
    }
}
