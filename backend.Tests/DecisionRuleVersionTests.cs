using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DecisionRuleVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "decision-rules-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Activation_pin_and_rollback_are_durable()
    {
        var store = new DecisionRuleVersionStore(_root, _time);
        var first = store.Draft("workspace", null, new(null, "ignored", "initial rules", [Rule("queue") ]));
        Assert.Null(store.ActiveVersionId("workspace"));
        store.Activate("workspace", null, first.VersionId, new("operator", "enable"));
        var second = store.Draft("workspace", null, new(first.VersionId, "operator", "tune", [Rule("queue", "A2")]));
        Assert.Equal(first.VersionId, store.ActiveVersionId("workspace"));
        store.Activate("workspace", null, second.VersionId, new("operator", "enable tune"));
        var rollback = store.Rollback("workspace", null, first.VersionId, new("operator", "restore"));
        Assert.NotEqual(first.VersionId, rollback.VersionId);
        Assert.Equal(second.VersionId, rollback.ParentVersionId);
        var restarted = new DecisionRuleVersionStore(_root, _time);
        Assert.Equal(rollback.VersionId, restarted.ActiveVersionId("workspace"));
        Assert.Equal("A1", restarted.Effective("workspace")!.Rules.Single().AutonomyClass);
        Assert.Equal(3, restarted.View("workspace").Activations.Count);
    }

    [Fact]
    public void Dangerous_edits_are_refused_before_a_version_is_written()
    {
        var store = new DecisionRuleVersionStore(_root, _time);
        var bad = new[]
        {
            Rule("floor.fence"), Rule("raw") with { FactId = "raw-session.log" },
            Rule("weak") with { Model = "gpt-5.6-luna" },
            Rule("loop") with { FreshnessMinutes = int.MaxValue },
        };
        var error = Assert.Throws<ArgumentException>(() => store.Draft("workspace", null,
            new(null, "operator", "try dangerous edits", bad)));
        Assert.Contains("authority floors", error.Message);
        Assert.Contains("trusted correctness floor", error.Message);
        Assert.Empty(store.View("workspace").Versions);
        Assert.Contains("cannot govern host-wide", Assert.Throws<ArgumentException>(() =>
            store.Draft("workspace", "project", new(null, "operator", "host override",
                [Rule("host") with { Trigger = "capacity-check" }]))).Message);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionRuleV1>(
            """{"id":"unsafe","disableBreaker":true}"""));
    }

    [Fact]
    public void Card_free_capacity_receipt_appears_in_host_history_and_workspace_spend()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var directory = Path.Combine(_root, "receipts");
        var store = new OrchestratorDecisionStore(directory, _time, TokenTestServices.Pricing());
        var fact = new GroundsFactV1("host.queue", "capacity", "Eligible queue", "6", "cards",
            new("capacity-snapshot", "snapshot:host-1"), at, "complete", GroundsReliability.Reliable, true);
        var grounds = new DecisionGroundsInputV1("capacity:host-1:2026-10-15T10:00Z",
            new("capacity-check", "capacity-check", at, "scheduled"),
            new("host", null, null, null, null, null, "host-1", "workspace"),
            null, null, "drv_pinned", new("codex", "gpt-5.6-sol", "high",
                DecisionModelCustody.RunnerHostJob), [fact],
            [new(DecisionActionKinds.Adapt, "role-share:review", "reversible", "A1", true),
                new(DecisionActionKinds.Defer, "intake:15m", "reversible", "A1", true)],
            [], new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0,
                DecisionTokenUsageV1.Zero, 0, 0, 0, [], DecisionPriceStatuses.NoUsage, "host", []),
            new("host.v1", "No card rounds", null, 0, 0, 0), []);
        store.FreezeInputs(new(grounds, DecisionActionKinds.Observe, "host-1", "host occurrence",
            DecisionReceiptPolicy.ActiveMode));
        var frozen = DecisionGroundsPolicy.Freeze(grounds);
        Assert.Equal("drv_pinned", frozen.PolicyVersion);
        Assert.Equal(DecisionGroundsPolicy.MaxCapacityPackTokens, frozen.Pack.MaxTokens);
        var job = store.CreateJob(new(grounds.OccurrenceId, "", "", 1, frozen.ContentDigest,
            DecisionReceiptPolicy.RenderPrompt(frozen), "codex", "gpt-5.6-sol", "high", 8_000, 1_000));
        var claim = store.Claim(new("runner-1", "runner-1")).Work!;
        Assert.True(store.Complete(new(claim.JobId, claim.ClaimToken, "runner-1", true,
            """{"chosenActions":[{"action":"adapt","target":"role-share:review"},{"action":"defer","target":"intake:15m"}],"confidence":0.8,"reasoning":"The queue is nonempty.","citedFacts":["host.queue"],"assessment":"Busy","report":"Review pressure is rising."}""",
            "gpt-5.6-sol", [new("call-1", "gpt-5.6-sol", 100, 0, 0, 20, false)],
            at, at.AddSeconds(2), null, false)));
        var receipt = DecisionReceiptPolicy.Build(new(frozen,
            new(DecisionActionKinds.Observe, "host-1", "none"), "host occurrence",
            store.GetJob(job.JobId), 1, false, null, DecisionReceiptPolicy.ActiveMode));
        store.PutReceipt(receipt);
        Assert.Equal(2, receipt.Chosen.Count);
        Assert.Equal("Review pressure is rising.", receipt.Report);
        Assert.Equal(DecisionReceiptPolicy.PeriodicDispatch, receipt.DispatchOutcome);
        var reloaded = new OrchestratorDecisionStore(directory, _time, TokenTestServices.Pricing());
        var host = Assert.Single(reloaded.ListReceipts(null, 10, hostId: "host-1"));
        Assert.Null(host.TaskId);
        Assert.Equal(120, host.TotalTokens);
        Assert.Equal(1, reloaded.SpendSummary(null, null, hostId: "host-1").Occurrences);
        Assert.Equal(1, reloaded.SpendSummary(null, null, workspaceId: "workspace").Occurrences);
        Assert.Empty(reloaded.ListReceipts(null, 10, workspaceId: "other"));
        var rules = new DecisionRuleVersionStore(Path.Combine(_root, "rules"), _time);
        var draft = rules.Draft("workspace", null, new(null, "operator", "check queue", [Rule("queue")]));
        var replay = rules.DryRun("workspace", null, draft.VersionId, [receipt]);
        var row = Assert.Single(replay.Occurrences);
        Assert.Equal("counterfactual-model-result", row.ModelResultLabel);
        Assert.Contains("queue: 6 (warning)", row.NewSignals);
        Assert.Empty(row.OldSignals);
    }

    private static DecisionRuleV1 Rule(string id, string autonomy = "A1")
        => new(id, "capacity-check", "host.queue", "5", ["observe", "adapt"], autonomy,
            "gpt-5.6-sol", "ask", 30);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
