using System.Text.Json;
using AgentStudio.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelSelectionLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "selection-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Attention_uses_class_waits_instead_of_a_single_provider_limit()
    {
        var ledger = Create(_now);
        var limits = new AgentStudio.Runner.ProviderLimitRegistry();
        limits.Record(new("claude", _now.UtcDateTime, _now.AddHours(1).UtcDateTime, "quota", true));
        var source = new AgentStudio.Operator.OperatorProviderLimitFactSource(limits, ledger);
        Assert.Empty(source.Collect(_now.UtcDateTime)!);
        Assert.True(ledger.Append(Sample(_now) with { Kind = "waiting", To = null,
            Reason = "waiting: no provider can take this class" }));
        Assert.Single(source.Collect(_now.UtcDateTime)!);
        Assert.True(ledger.Append(Sample(_now) with { Id = "switch" }));
        Assert.Empty(source.Collect(_now.UtcDateTime)!);
        Assert.True(ledger.Append(Sample(_now) with { Id = "hard-wait", Kind = "waiting", To = null, Rule = "pin" }));
        Assert.Empty(source.Collect(_now.UtcDateTime)!);
    }

    [Fact]
    public void Execution_usage_counts_completed_calls_once_and_filters_projects()
    {
        var ledger = Create(_now);
        var selected = Sample(_now);
        Assert.True(ledger.Append(selected));
        Assert.Empty(ledger.ExecutionUsage(_now.AddMinutes(-1), new HashSet<string> { "demo" }));
        Assert.True(ledger.RecordExecution(selected.Id));
        Assert.False(ledger.RecordExecution(selected.Id));
        var usage = Assert.Single(ledger.ExecutionUsage(_now.AddMinutes(-1), new HashSet<string> { "demo" }));
        Assert.Equal("codex", usage.Provider);
        Assert.Equal(1, usage.Executions);
        Assert.Equal(1, usage.SwitchExecutions);
        Assert.Empty(ledger.ExecutionUsage(_now.AddMinutes(-1), new HashSet<string> { "other" }));
    }

    [Fact]
    public void Return_to_primary_does_not_count_as_a_fallback_execution()
    {
        var ledger = Create(_now);
        var returned = Sample(_now) with { Kind = "returned" };
        ledger.Append(returned);
        Assert.True(ledger.RecordExecution(returned.Id));
        var usage = Assert.Single(ledger.ExecutionUsage(_now.AddMinutes(-1), new HashSet<string> { "demo" }));
        Assert.Equal(0, usage.SwitchExecutions);
    }

    [Fact]
    public void RoundTrip_RedactsAndFiltersWithIdempotentIngestion()
    {
        var ledger = Create(_now);
        var entry = Sample(_now) with { Evidence = new ModelSelectionEvidence("token=private " + new string('a', 350), "provider-rejected") };
        Assert.True(ledger.Append(entry));
        Assert.False(ledger.Append(entry));
        var restored = Create(_now);
        var result = restored.Query(provider: "codex", project: "demo", consumer: "remote-claim", rule: "quota-cap", from: "opus-5", to: "gpt-6-sol");
        var saved = Assert.Single(result.Events);
        Assert.Equal(entry.Id, saved.Id);
        Assert.Equal("opus-5", saved.From?.Model);
        Assert.Equal("gpt-6-sol", saved.To?.Model);
        Assert.DoesNotContain("private", saved.Evidence?.Excerpt);
        Assert.True(saved.Evidence?.Excerpt?.Length <= 300);
        Assert.Empty(restored.Query(provider: "gemini").Events);
        Assert.Equal(entry.Id, Assert.Single(restored.Query(taskKey: "DEMO-1", eventId: entry.Id).Events).Id);
        Assert.Empty(restored.Query(taskKey: "other-card", eventId: entry.Id).Events);
        Assert.Empty(restored.Query(since: _now.AddMinutes(1)).Events);
        Assert.Equal(entry.Id, Assert.Single(restored.Query(since: _now.AddMinutes(-1), until: _now.AddMinutes(1)).Events).Id);
    }

    [Fact]
    public void Retention_RemovesDay91AndKeepsSummary()
    {
        var old = _now.AddDays(-91);
        var ledger = Create(old);
        Assert.True(ledger.Append(Sample(old)));
        var restarted = Create(_now);
        Assert.Empty(restarted.Query().Events);
        Assert.False(File.Exists(Path.Combine(restarted.DirectoryPath, $"{old:yyyy-MM-dd}.jsonl")));
        var summary = Path.Combine(restarted.DirectoryPath, $"{old:yyyy-MM-dd}.summary.json");
        Assert.True(File.Exists(summary));
        Assert.Contains("quota-cap", File.ReadAllText(summary));
    }

    [Fact]
    public void RepeatedWaitsCollapseAndActiveProjectionRestarts()
    {
        var ledger = Create(_now);
        var wait = Sample(_now) with { Kind = "waiting", To = null };
        Assert.True(ledger.Append(wait));
        Assert.True(ledger.Append(wait with { Id = "second" }));
        Assert.Equal(2, Assert.Single(ledger.Query().Events).Count);
        Assert.True(ledger.Append(Sample(_now) with { Id = "switch" }));
        Assert.Equal(ledger.Active(), Create(_now).Active());
        var returned = ledger.ReturnToPrimary("remote-claim", "demo", "DEMO-1",
            new ModelSelectionRoute("claude", "opus-5", "high"), "Primary quota recovered");
        Assert.NotNull(returned);
        Assert.Equal("switch", returned.ClosesId);
        Assert.Equal("returned", returned.Kind);
        Assert.Empty(ledger.Active());
        Assert.Empty(Create(_now).Active());
    }

    [Fact]
    public void HealthClearClosesTheMatchingHostModelWithoutAStoredMarkId()
    {
        var ledger = Create(_now);
        var mark = Sample(_now) with
        {
            Kind = "health-marked", Host = "host-a", Rule = "model-capacity",
            From = new("codex", "gpt-6-sol", "medium"),
            To = new("codex", "gpt-6-sol", "medium"), ReturnToPrimaryAt = _now.AddHours(1),
        };
        Assert.True(ledger.Append(mark));
        Assert.True(ledger.Append(mark with { Id = "other-host", Host = "host-b" }));
        Assert.True(ledger.Append(mark with { Id = "cleared", Kind = "health-cleared",
            At = _now.AddMinutes(1), ReturnToPrimaryAt = null }));

        Assert.Equal("other-host", Assert.Single(ledger.Active()).Id);
        Assert.Equal("other-host", Assert.Single(Create(_now).Active()).Id);
    }

    private ModelSelectionLedger Create(DateTimeOffset now)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        return new ModelSelectionLedger(config, new FixedTime(now));
    }

    private static ModelSelectionEvent Sample(DateTimeOffset at) => new()
    {
        Id = "first", At = at, Kind = "switched", Consumer = "remote-claim", Project = "demo", TaskKey = "DEMO-1",
        From = new("claude", "opus-5", "high"), To = new("codex", "gpt-6-sol", "high"),
        Rule = "quota-cap", Reason = "Weekly cap reached", ReturnToPrimaryAt = at.AddHours(3),
        Quota = new("claude", "Weekly", 98, 95, 100, at.AddHours(3), 12),
        Equivalence = new("demanding", "TokenEconomy 0.3.7", "catalogue"),
        PricePerMTok = new(15, 10),
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
