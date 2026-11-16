using AgentStudio.Shared;

namespace AgentStudio.Tokens;

public sealed record SubscriptionUsageReceipt(string? Project, string? Card, DateTime At,
    string Kind, string Model, string Cli, long Tokens, string Source);

public sealed record SubscriptionUsageTotal(string Name, long Tokens, int Receipts);

public sealed record SubscriptionWeekResponse(string Cli, string? SubscriptionKey,
    DateTime? StartUtc, DateTime? ResetAtUtc, double? UsedPct, DateTime? ObservedAt,
    long TotalTokens, int ReceiptCount, IReadOnlyList<SubscriptionUsageTotal> Cards,
    IReadOnlyList<SubscriptionUsageTotal> Kinds, IReadOnlyList<SubscriptionUsageTotal> Models,
    IReadOnlyList<SubscriptionUsageReceipt> Receipts, string? Notice);

/// <summary>Provider weekly reset bounds the receipt attribution, independently of workspace calendars.</summary>
public static class SubscriptionWeekFold
{
    public static SubscriptionWeekResponse Fold(string cli, string? subscriptionKey,
        QuotaWindow? weekly, DateTime? observedAt, DateTime nowUtc,
        IReadOnlyList<SubscriptionUsageReceipt> receipts, bool sharedProvider)
    {
        var reset = weekly?.ResetAt?.ToUniversalTime();
        var resetValid = reset is { } end && end > nowUtc && end <= nowUtc.AddDays(7);
        DateTime? start = resetValid ? weekly?.ObservedStartAt?.ToUniversalTime() ?? reset!.Value.AddDays(-7) : null;
        var valid = resetValid && start is { } from && from <= nowUtc && from < reset!.Value;
        string? notice = !valid
            ? "The provider did not report a current weekly reset; receipt attribution is unavailable."
            : sharedProvider
                ? "Multiple subscriptions use this CLI. Receipts do not carry a subscription identity, so their share cannot be assigned safely."
                : "Provider percentage uses account quota units; captured tokens are a separate local measure and may be incomplete. Workspace cost uses its configured calendar, and admission quota uses Monday UTC. If the footer differs, it shows an earlier probe.";
        List<SubscriptionUsageReceipt> selected = !valid || sharedProvider ? [] : receipts
            .Where(item => string.Equals(item.Cli, cli, StringComparison.OrdinalIgnoreCase)
                && item.At >= start!.Value && item.At < reset!.Value && item.At <= nowUtc && item.Tokens > 0)
            .OrderByDescending(item => item.At).ToList();
        static IReadOnlyList<SubscriptionUsageTotal> Totals(IEnumerable<SubscriptionUsageReceipt> items,
            Func<SubscriptionUsageReceipt, string> name) => items.GroupBy(name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SubscriptionUsageTotal(group.Key, group.Sum(item => item.Tokens),
                    group.Count(item => item.Source == TokenAnalysisFold.SourceLedger)))
                .OrderByDescending(group => group.Tokens).ThenBy(group => group.Name, StringComparer.Ordinal).ToList();
        return new SubscriptionWeekResponse(cli, subscriptionKey, valid ? start : null, valid ? reset : null,
            valid ? weekly?.UsedPct : null, observedAt, selected.Sum(item => item.Tokens),
            selected.Count(item => item.Source == TokenAnalysisFold.SourceLedger),
            Totals(selected, item => item.Card ?? "Outside cards"),
            Totals(selected, item => item.Kind), Totals(selected, item => item.Model), selected, notice);
    }
}
