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
