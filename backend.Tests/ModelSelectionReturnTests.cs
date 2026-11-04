using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelSelectionReturnTests
{
    private static readonly DateTime Start = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SelectionRequest Request = new()
    {
        Consumer = SelectionConsumers.Coding,
        Project = "fixture",
        TaskKey = "AGT-1",
        Pins = new SelectionPins("claude", "claude-opus-5", "high"),
    };

    [Fact]
    public void Recorded_quota_history_at_94_and_96_switches_once_and_returns_once()
    {
        using var directory = new TestDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(Start.AddHours(2)));
        var history = new QuotaHistoryStore(Config(directory.Path), NullLogger<QuotaHistoryStore>.Instance, clock);
        var ledger = new LoggingModelSelectionLedger(NullLogger<LoggingModelSelectionLedger>.Instance, Config(directory.Path), clock);
        var readings = new[] { 96d, 94d, 96d, 94d, 84d };
        var times = new[] { 0, 10, 20, 25, 40 };
        for (var i = 0; i < readings.Length; i++)
            Assert.Equal(1, history.Record(Snapshot("claude", readings[i], Start.AddMinutes(times[i]))));

        var switches = 0;
        var returns = 0;
        foreach (var point in history.Read("claude", Start))
        {
            var facts = Facts(point.At, point.UsedPct, 10) with { ActiveSwitch = ledger.GetActive(Request) };
            var result = ModelSelectionPlanner.Plan(Request, facts);
            if (result.Outcome == SelectionOutcome.Fallback && ledger.GetActive(Request) is null)
            {
                var entry = SelectionLedgerEntry.For(Request, result, "simulation", $"switch-{switches}", point.At)!;
                ledger.Record(entry);
                switches++;
            }
            else if (result.Outcome == SelectionOutcome.Primary && ledger.GetActive(Request) is { } open)
            {
                ledger.Record(open with { Id = $"return-{returns}", At = point.At,
                    Kind = SelectionLedgerKinds.Returned, From = open.To, To = open.From,
                    ClosesId = open.Id });
                returns++;
            }
        }
        Assert.Equal(1, switches);
        Assert.Equal(1, returns);
        Assert.Empty(ledger.Active());
        var replayed = new LoggingModelSelectionLedger(NullLogger<LoggingModelSelectionLedger>.Instance, Config(directory.Path), clock);
        Assert.Empty(replayed.Active());
    }

    [Theory]
    [InlineData(1.99, SelectionOutcome.Fallback)]
    [InlineData(2.0, SelectionOutcome.Fallback)]
    [InlineData(2.01, SelectionOutcome.Wait)]
    public void Cost_guard_applies_only_above_the_factor(double ratio, SelectionOutcome expected)
    {
        var facts = Facts(Start, 96, 10) with
        {
            PricePerMTok = model => model == "claude-opus-5" ? 10m : (decimal)ratio * 10m,
        };
        var result = ModelSelectionPlanner.Plan(Request, facts);
        Assert.Equal(expected, result.Outcome);
        if (expected == SelectionOutcome.Wait) Assert.Equal(SelectionRules.CostGuard, result.Rule);
    }

    [Fact]
    public void Explicit_policy_and_operator_switch_allow_a_pricier_route()
    {
        var expensive = Facts(Start, 96, 10) with
        {
            PricePerMTok = model => model == "claude-opus-5" ? 10m : 30m,
        };
        var explicitResult = ModelSelectionPlanner.Plan(Request, expensive with
        {
            ConfiguredFallbacks = [new SelectionRoute("codex", "gpt-6-sol", "high")],
        });
        var operatorResult = ModelSelectionPlanner.Plan(Request, expensive with
        {
            OperatorSwitch = new CliFallbackPreference("claude", true, Start, Start.AddHours(1)),
        });
        Assert.Equal(SelectionOutcome.Fallback, explicitResult.Outcome);
        Assert.Equal(SelectionOutcome.Fallback, operatorResult.Outcome);
    }

    [Fact]
    public void Fallback_target_projected_to_cross_its_own_cap_waits()
    {
        var facts = Facts(Start, 96, 94) with
        {
            SnapshotFor = cli => Snapshot(cli ?? "", cli == "claude" ? 96 : 94, Start,
                Start.AddDays(3)),
        };
        var result = ModelSelectionPlanner.Plan(Request, facts);
        Assert.Equal(SelectionOutcome.Wait, result.Outcome);
        Assert.Equal(SelectionRules.QuotaCap, result.Rule);
    }

    [Fact]
    public void Suspicious_reading_cannot_return_an_open_switch()
    {
        var first = ModelSelectionPlanner.Plan(Request, Facts(Start, 96, 10));
        var active = SelectionLedgerEntry.For(Request, first, "simulation", "switch", Start)!;
        var facts = Facts(Start.AddMinutes(40), 84, 10) with
        {
            ActiveSwitch = active,
            SnapshotFor = cli => Snapshot(cli ?? "", cli == "claude" ? 84 : 10,
                Start.AddMinutes(40)) with { Suspicious = cli == "claude" },
        };
        Assert.Equal(SelectionOutcome.Fallback, ModelSelectionPlanner.Plan(Request, facts).Outcome);
    }

    private static SelectionFacts Facts(DateTime at, double primary, double target) => new()
    {
        NowUtc = at,
        Caps = new Caps(),
        SnapshotFor = cli => Snapshot(cli ?? "", cli == "claude" ? primary : target, at),
        Routing = new SelectionRoutingFacts(cli => cli == "claude"
            ? new CliModelRouteProfile { CliType = "claude", FallbackCliType = "codex",
                FallbackModel = "gpt-6-sol", FallbackThinkingLevel = "high" }
            : null, null, (_, _) => []),
    };

    private static QuotaSnapshot Snapshot(string cli, double used, DateTime at, DateTime? reset = null) => new()
    {
        CliType = cli, FetchedAt = at,
        Windows = [new QuotaWindow { Label = "Weekly", UsedPct = used,
            ResetAt = reset ?? Start.AddHours(1) }],
    };

    private static IConfiguration Config(string path) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = path }).Build();

    private sealed class Caps : IQuotaCapTable
    {
        public int GetCap(string cliType, string windowLabel) => 95;
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("selection-return-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
