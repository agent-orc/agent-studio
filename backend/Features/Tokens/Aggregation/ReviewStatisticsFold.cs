namespace AgentStudio.Tokens;

public sealed record ReviewUsageRow(
    string Project, string JobId, string Attempt, string Step, DateTime At,
    string Model, string Level, string Host, long Tokens, decimal? Usd);

public sealed record ReviewMetric(double? Median, double? P90, int Samples, IReadOnlyList<double?> Last7d,
    IReadOnlyList<double?> Last30d);

public sealed record ReviewStatistics(
    ReviewMetric DeliveryTokens, ReviewMetric DeliveryUsd, ReviewMetric RoundsUntilAcceptance,
    ReviewMetric TokensPerAcceptedCard, ReviewMetric UsdPerAcceptedCard,
    ReviewMetric ReviewSharePercent, IReadOnlyDictionary<string, ReviewMetric> ByStepTokens,
    IReadOnlyDictionary<string, ReviewMetric> ByStepUsd,
    IReadOnlyDictionary<string, ReviewMetric> ByModelLevelTokens,
    IReadOnlyDictionary<string, ReviewMetric> ByModelLevelUsd,
    IReadOnlyList<string> AcceptedCards, IReadOnlyList<ReviewUsageRow> Reviews, string? Warning);

/// <summary>Pure review economics fold over the canonical, already de-duplicated ledger.</summary>
public static class ReviewStatisticsFold
{
    private const string RemotePrefix = "support:remote-review:";
    private const string LocalPrefix = "support:review-attempt:";

    public static string? Step(string? participant)
    {
        if (participant is null) return null;
        if (participant.StartsWith(RemotePrefix, StringComparison.OrdinalIgnoreCase)
            || participant.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var marker = participant.IndexOf(":step:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return "review, step unknown";
            var step = participant[(marker + 6)..];
            var usageMarker = step.IndexOf(":usage:", StringComparison.OrdinalIgnoreCase);
            return NormalizeStep(usageMarker < 0 ? step : step[..usageMarker]);
        }
        if (participant.StartsWith("support:aspect-", StringComparison.OrdinalIgnoreCase))
            return NormalizeStep(participant["support:".Length..]);
        if (participant.Equals("support:post-code-review-grade", StringComparison.OrdinalIgnoreCase)) return "grade";
        if (participant.Equals("support:review-decision", StringComparison.OrdinalIgnoreCase)) return "review decision";
        if (participant.Equals("support:post-orchestrator-review", StringComparison.OrdinalIgnoreCase)) return "orchestrator review";
        return null;
    }

    public static string Attempt(string? participant, DateTime at)
    {
        if (participant?.StartsWith(RemotePrefix, StringComparison.OrdinalIgnoreCase) == true
            || participant?.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            var prefixLength = participant.StartsWith(RemotePrefix, StringComparison.OrdinalIgnoreCase)
                ? RemotePrefix.Length : LocalPrefix.Length;
            var suffix = participant[prefixLength..];
            var marker = suffix.IndexOf(":step:", StringComparison.OrdinalIgnoreCase);
            return marker < 0 ? suffix : suffix[..marker];
        }
        // Older local receipts have no attempt identity. Keep each call distinct
        // instead of fabricating a review round from nearby timestamps.
        return "legacy-" + at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeStep(string step) => step.ToLowerInvariant() switch
    {
        "post-code-review-grade" or "grade" => "grade",
        "post-orchestrator-review" or "orchestrator-review" => "orchestrator review",
        var value when value.StartsWith("aspect-", StringComparison.Ordinal) => value[7..],
        var value => value,
    };

    public static ReviewStatistics Build(IReadOnlyList<ReviewUsageRow> rows,
        IReadOnlyDictionary<string, long> cardTotals, IReadOnlySet<string> acceptedCards,
        DateTime nowUtc, int days, string? model = null, string? level = null, string? host = null)
    {
        var now = nowUtc.ToUniversalTime();
        var selected = rows.Where(r => r.At.ToUniversalTime() >= now.AddDays(-days) && r.At.ToUniversalTime() <= now)
            .Where(r => model is null || string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase))
            .Where(r => level is null || string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase))
            .Where(r => host is null || string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var deliveries = selected.GroupBy(r => (r.Project, r.JobId, r.Attempt))
            .Select(g => new { g.Key.Project, g.Key.JobId, At = g.Max(r => r.At),
                Tokens = g.Sum(r => r.Tokens),
                Usd = g.All(r => r.Usd.HasValue) ? (decimal?)g.Sum(r => r.Usd!.Value) : null })
            .ToList();
        var cards = deliveries.GroupBy(d => (d.Project, d.JobId)).ToList();
        var accepted = cards.Where(g => acceptedCards.Contains(g.Key.Project + "/" + g.Key.JobId)).ToList();
        var roundPoints = accepted.Select(g => (g.Max(d => d.At), (double)g.Count())).ToList();
        var cardTokens = accepted.Select(g => (g.Max(d => d.At), (double)g.Sum(d => d.Tokens))).ToList();
        var cardUsd = accepted.Where(g => g.All(d => d.Usd.HasValue))
            .Select(g => (g.Max(d => d.At), (double)g.Sum(d => d.Usd!.Value))).ToList();
        var share = accepted.Select(g => (Group: g, Total: cardTotals.GetValueOrDefault(g.Key.Project + "/" + g.Key.JobId)))
            .Where(x => x.Total > 0)
            .Select(x => (x.Group.Max(d => d.At), 100d * x.Group.Sum(d => d.Tokens) / x.Total)).ToList();
        var steps = Subtotals(selected, r => r.Step);
        var models = Subtotals(selected, r => r.Model + " / " + r.Level);
        return new ReviewStatistics(
            Metric(deliveries.Select(d => (d.At, (double)d.Tokens)), now),
            Metric(deliveries.Where(d => d.Usd.HasValue).Select(d => (d.At, (double)d.Usd!.Value)), now),
            Metric(roundPoints, now), Metric(cardTokens, now), Metric(cardUsd, now), Metric(share, now),
            steps.GroupBy(r => r.Key).ToDictionary(g => g.Key,
                g => Metric(g.Select(r => (r.At, (double)r.Tokens)), now)),
            steps.GroupBy(r => r.Key).ToDictionary(g => g.Key,
                g => Metric(g.Where(r => r.Usd.HasValue).Select(r => (r.At, (double)r.Usd!.Value)), now)),
            models.GroupBy(r => r.Key).ToDictionary(g => g.Key,
                g => Metric(g.Select(r => (r.At, (double)r.Tokens)), now)),
            models.GroupBy(r => r.Key).ToDictionary(g => g.Key,
                g => Metric(g.Where(r => r.Usd.HasValue).Select(r => (r.At, (double)r.Usd!.Value)), now)),
            accepted.Select(g => g.Key.Project + "/" + g.Key.JobId).ToList(),
            selected.OrderByDescending(r => r.At).ToList(),
            rows.Any(r => r.Step == "review, step unknown")
                ? "Older remote review receipts have no step identity and appear under review, step unknown." : null);
    }

    private sealed record Subtotal(string Key, DateTime At, long Tokens, decimal? Usd);

    private static IReadOnlyList<Subtotal> Subtotals(
        IReadOnlyList<ReviewUsageRow> rows, Func<ReviewUsageRow, string> key)
        => rows.GroupBy(r => (r.Project, r.JobId, r.Attempt, Key: key(r)))
            .Select(g => new Subtotal(g.Key.Key, g.Max(r => r.At), g.Sum(r => r.Tokens),
                g.All(r => r.Usd.HasValue) ? g.Sum(r => r.Usd!.Value) : null))
            .ToList();

    public static ReviewMetric Metric(IEnumerable<(DateTime At, double Value)> points, DateTime nowUtc)
    {
        var values = points.ToList();
        var now = nowUtc.ToUniversalTime().Date;
        IReadOnlyList<double?> Trend(int days) => Enumerable.Range(0, days).Select(offset =>
        {
            var day = now.AddDays(offset - days + 1);
            var daily = values.Where(p => p.At.ToUniversalTime().Date == day).Select(p => p.Value).ToList();
            return daily.Count == 0 ? (double?)null : Quantile(daily, 0.5);
        }).ToList();
        return new ReviewMetric(Quantile(values.Select(p => p.Value), 0.5),
            Quantile(values.Select(p => p.Value), 0.9), values.Count, Trend(7), Trend(30));
    }

    public static double? Quantile(IEnumerable<double> points, double probability)
    {
        var sorted = points.Order().ToArray();
        if (sorted.Length == 0) return null;
        if (probability == 0.5 && sorted.Length % 2 == 0)
            return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var rank = Math.Max(0, (int)Math.Ceiling(probability * sorted.Length) - 1);
        return sorted[rank];
    }
}
