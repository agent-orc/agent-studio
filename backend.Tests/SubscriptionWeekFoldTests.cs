using AgentStudio.Shared;
using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the pure fold receives the fixed "now" argument used by every dated fixture.
public sealed class SubscriptionWeekFoldTests
{
    [Fact]
    public void Uses_each_providers_reset_and_excludes_both_window_edges_correctly()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var claudeReset = new DateTime(2026, 11, 18, 21, 34, 0, DateTimeKind.Utc);
        var codexReset = claudeReset.AddDays(1);
        var receipts = new[]
        {
            new SubscriptionUsageReceipt("Studio", "AGT-1", claudeReset.AddDays(-7).AddMinutes(-1), "coding", "claude-sonnet", "claude", 10, "ledger"),
            new SubscriptionUsageReceipt("Studio", "AGT-1", claudeReset.AddDays(-7), "coding", "claude-sonnet", "claude", 20, "ledger"),
            new SubscriptionUsageReceipt("Studio", "AGT-2", now, "review", "claude-opus", "claude", 30, "pipeline"),
            new SubscriptionUsageReceipt("Studio", "AGT-3", now, "coding", "gpt-5", "codex", 40, "ledger"),
            new SubscriptionUsageReceipt(null, null, codexReset, "chat", "gpt-5", "codex", 50, "ledger"),
        };
        var claude = SubscriptionWeekFold.Fold("claude", "account-a",
            new QuotaWindow { Label = "Weekly", UsedPct = 35, ResetAt = claudeReset }, now, now, receipts, false);
        var codex = SubscriptionWeekFold.Fold("codex", "account-b",
            new QuotaWindow { Label = "Weekly", UsedPct = 42, ResetAt = codexReset }, now, now, receipts, false);

        Assert.Equal(claudeReset.AddDays(-7), claude.StartUtc);
        Assert.Equal(codexReset.AddDays(-7), codex.StartUtc);
        Assert.Equal(35, claude.UsedPct);
        Assert.Equal(42, codex.UsedPct);
        Assert.Equal(50, claude.TotalTokens);
        Assert.Equal(40, codex.TotalTokens);
        Assert.Equal(1, claude.ReceiptCount);
        Assert.Equal(2, claude.Cards.Count);
        Assert.Equal(claude.TotalTokens, claude.Cards.Sum(row => row.Tokens));
        Assert.Equal(claude.TotalTokens, claude.Kinds.Sum(row => row.Tokens));
        Assert.Equal(claude.TotalTokens, claude.Models.Sum(row => row.Tokens));
        Assert.Equal(30, Assert.Single(claude.Kinds, row => row.Name == "review").Tokens);
    }

    [Fact]
    public void Does_not_assign_unkeyed_receipts_to_two_sign_ins_of_one_provider()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var result = SubscriptionWeekFold.Fold("claude", "account-a",
            new QuotaWindow { Label = "Weekly", UsedPct = 35, ResetAt = now.AddDays(2) },
            now, now, [new SubscriptionUsageReceipt("Studio", "AGT-1", now,
                "coding", "claude-sonnet", "claude", 120, "ledger")], true);

        Assert.Equal(35, result.UsedPct);
        Assert.Empty(result.Receipts);
        Assert.Equal(0, result.TotalTokens);
        Assert.Contains("subscription identity", result.Notice);
    }

    [Fact]
    public void Uses_observed_cycle_start_when_provider_resets_early()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var observedStart = now.AddDays(-2);
        var result = SubscriptionWeekFold.Fold("claude", null,
            new QuotaWindow { Label = "Weekly", UsedPct = 12, ResetAt = now.AddDays(5),
                ObservedStartAt = observedStart }, now, now,
            [new SubscriptionUsageReceipt(null, null, observedStart.AddMinutes(-1), "chat", "claude-sonnet", "claude", 10, "ledger"),
             new SubscriptionUsageReceipt(null, null, observedStart, "chat", "claude-sonnet", "claude", 20, "ledger")], false);
        Assert.Equal(observedStart, result.StartUtc);
        Assert.Equal(20, result.TotalTokens);
    }
}
