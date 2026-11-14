using System.Text.Json;
using AgentStudio.Shared;
using AgentStudio.Tokens;

namespace AgentStudio.Personas;

public sealed record PersonaMissV1(string Name, string Source, string SourceId, string Url,
    int Importance, DateTime AtUtc, int ActivationRevision);
public sealed record PersonaPromotionV1(string ItemId, string TaskKey, string Title, string Outcome, string Url);
public sealed record PersonaSuggestionV1(string Kind, string Reason);
public sealed record PersonaValueV1(double? WeightedAcceptedPer100kTokens, double? CostPerAcceptedUsd,
    double? TeamMedianWeightedPer100kTokens, double? TeamMedianCostPerAcceptedUsd,
    double? NoisePercent, int NoiseNumerator, int Raised, int Useful, int NotUseful,
    IReadOnlyList<PersonaMissV1> Misses, IReadOnlyList<PersonaPromotionV1> Outcomes,
    IReadOnlyList<PersonaSuggestionV1> Suggestions, bool ShowRevisionComparison);

public sealed record PersonaReportRatingV1(string ReportId, string PersonaId, string Rating, DateTime AtUtc);

/// <summary>One optional Owner rating per immutable report. Ratings are separate from scored items.</summary>
public sealed class PersonaReportRatingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly Dictionary<string, List<PersonaReportRatingV1>> _memory = new(StringComparer.Ordinal);
    public PersonaReportRatingStore(IConfiguration configuration) : this(
        string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null :
            Path.Combine(configuration["TaskRepository"]!, ".metadata", "persona-report-ratings")) { }
    public PersonaReportRatingStore(string? root) => _root = root;
    public IReadOnlyList<PersonaReportRatingV1> List(string workspaceId)
    {
        lock (_gate) return [.. Load(workspaceId)];
    }
    public PersonaReportRatingV1 Rate(string workspaceId, PersonaReportRecordV1 report, string rating, DateTime now)
    {
        if (rating is not ("useful" or "not-useful")) throw new ArgumentException("Invalid report rating.");
        lock (_gate)
        {
            var rows = Load(workspaceId);
            var previous = rows.FirstOrDefault(row => row.ReportId == report.Id);
            if (previous is not null) return previous;
            var row = new PersonaReportRatingV1(report.Id, report.PersonaId, rating, now);
            rows.Add(row);
            if (PathFor(workspaceId) is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(rows, Json));
                File.Move(temporary, path, true);
            }
            return row;
        }
    }
    private List<PersonaReportRatingV1> Load(string workspaceId)
    {
        if (_memory.TryGetValue(workspaceId, out var rows)) return rows;
        var path = PathFor(workspaceId);
        return _memory[workspaceId] = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<List<PersonaReportRatingV1>>(File.ReadAllText(path), Json) ?? [] : [];
    }
    private string? PathFor(string workspaceId)
    {
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId)) throw new ArgumentException("Invalid workspace id.");
        return _root is null ? null : Path.Combine(_root, workspaceId + ".json");
    }
}

public static class PersonaValueProjection
{
    public static PersonaValueV1 Build(string workspaceId, PersonaActivationV1 activation,
        IReadOnlyList<PersonaActivationV1> team, IReadOnlyList<PersonaItemV1> items,
        IReadOnlyList<PersonaOccurrenceReceipt> receipts, IReadOnlyList<PersonaReportRatingV1> ratings,
        IReadOnlyList<TaskInfo> cards, DateTime now, DateTime? periodStart = null, DateTime? periodEnd = null,
        ITokenPricing? pricing = null)
    {
        var own = items.Where(i => i.PersonaId == activation.PersonaId && i.Kind != PersonaItemKinds.ReportLine).ToArray();
        var ownReceipts = receipts.Where(r => r.PersonaId == activation.PersonaId).ToArray();
        bool InPeriod(DateTime at) => (!periodStart.HasValue || at >= periodStart.Value)
            && (!periodEnd.HasValue || at < periodEnd.Value);
        var metricOwn = own.Where(i => InPeriod(i.CreatedAtUtc)).ToArray();
        var metricReceipts = ownReceipts.Where(r => InPeriod(r.CreatedAtUtc)).ToArray();
        var teamValues = team.Where(a => a.State is PersonaStates.Active or PersonaStates.Shadow or PersonaStates.Paused)
            .Select(a =>
        {
            var rows = items.Where(i => i.PersonaId == a.PersonaId && i.Kind != PersonaItemKinds.ReportLine
                && InPeriod(i.CreatedAtUtc)).ToArray();
            var runs = receipts.Where(r => r.PersonaId == a.PersonaId && InPeriod(r.CreatedAtUtc)).ToArray();
            return (Value: Weighted(rows, runs), Cost: Cost(rows, runs, pricing));
        }).ToArray();
        var misses = Misses(workspaceId, activation, items, cards, now);
        var metricMisses = misses.Where(m => InPeriod(m.AtUtc)).ToArray();
        var outcomes = metricOwn.Where(i => i.PromotedTaskKey is not null).Select(i =>
        {
            var card = cards.FirstOrDefault(c => c.TaskKey == i.PromotedTaskKey || c.Id == i.PromotedTaskKey);
            var key = i.PromotedTaskKey!;
            return new PersonaPromotionV1(i.Id, key, i.Title, card?.State ?? "unknown",
                "#/tasks/" + Uri.EscapeDataString(key));
        }).ToArray();
        var ownRatings = ratings.Where(r => r.PersonaId == activation.PersonaId && InPeriod(r.AtUtc)).ToArray();
        var lastRevision = activation.History.Where(h => h.Action is "attributes" or "adopt")
            .OrderByDescending(h => h.At).FirstOrDefault();
        double? MedianForMonth(DateTime month) => Median(team
            .Where(a => a.State is PersonaStates.Active or PersonaStates.Shadow or PersonaStates.Paused)
            .Select(a => Cost(items.Where(i => i.PersonaId == a.PersonaId
                        && i.CreatedAtUtc >= month && i.CreatedAtUtc < month.AddMonths(1)).ToArray(),
                    receipts.Where(r => r.PersonaId == a.PersonaId
                        && r.CreatedAtUtc >= month && r.CreatedAtUtc < month.AddMonths(1)).ToArray(), pricing)));
        return new PersonaValueV1(Weighted(metricOwn, metricReceipts), Cost(metricOwn, metricReceipts, pricing),
            Median(teamValues.Select(v => v.Value)), Median(teamValues.Select(v => v.Cost)),
            metricOwn.Length == 0 ? null : Math.Round(100d * metricOwn.Count(i => i.State is "rejected" or "duplicate") / metricOwn.Length, 1),
            metricOwn.Count(i => i.State is "rejected" or "duplicate"), metricOwn.Length,
            ownRatings.Count(r => r.Rating == "useful"), ownRatings.Count(r => r.Rating == "not-useful"),
            metricMisses, outcomes, Suggest(activation, own, ownReceipts, misses,
                Median(teamValues.Select(v => v.Cost)), now, pricing, MedianForMonth),
            lastRevision is not null && now >= lastRevision.At && now < lastRevision.At.AddDays(28));
    }

    public static IReadOnlyList<PersonaSuggestionV1> Suggest(PersonaActivationV1 activation,
        IReadOnlyList<PersonaItemV1> items, IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        IReadOnlyList<PersonaMissV1> misses, double? medianCost, DateTime now, ITokenPricing? pricing = null,
        Func<DateTime, double?>? medianForMonth = null)
    {
        if (activation.State is PersonaStates.Inactive or PersonaStates.Retired) return [];
        var result = new List<PersonaSuggestionV1>();
        var activated = activation.History.Where(h => h.Action == "activate").Select(h => h.At)
            .DefaultIfEmpty(activation.ActivatedAt).Min();
        if (now < activated.AddDays(28)) result.Add(new("probation", "First four weeks after activation."));
        var decided = items.Count(i => i.State is "accepted" or "rejected");
        if (decided >= 10 && items.Count(i => i.State == "accepted") * 100 < decided * 30)
            result.Add(new("pause", "Acceptance is below 30% across at least ten decided items; review the charter."));
        if (medianCost is > 0 || medianForMonth is not null)
        {
            var highMonths = Enumerable.Range(0, 2).All(offset =>
            {
                var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-offset - 1);
                var monthItems = items.Where(i => i.CreatedAtUtc >= month && i.CreatedAtUtc < month.AddMonths(1)).ToArray();
                var monthRuns = receipts.Where(r => r.CreatedAtUtc >= month && r.CreatedAtUtc < month.AddMonths(1)).ToArray();
                var comparison = medianForMonth?.Invoke(month) ?? medianCost;
                return comparison is > 0 && Cost(monthItems, monthRuns, pricing) is { } cost
                    && cost > 3 * comparison;
            });
            if (highMonths) result.Add(new("pause", "Cost per accepted finding exceeded three times the team median for two months; review the charter."));
        }
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (misses.Count(m => m.AtUtc >= monthStart && m.AtUtc < monthStart.AddMonths(1) && m.Importance >= 70) >= 2)
            result.Add(new("focus", "Two high importance misses this month; review scope or focus."));
        if (now >= activated.AddDays(56) && !items.Any(i => i.State == "accepted" && i.UpdatedAtUtc >= now.AddDays(-56)))
            result.Add(new("retirement", "No accepted item in eight weeks; Owner may retire this persona."));
        return result;
    }

    private static double? Weighted(IReadOnlyList<PersonaItemV1> items, IReadOnlyList<PersonaOccurrenceReceipt> receipts)
    {
        var tokens = receipts.Sum(r => r.Tokens);
        return tokens <= 0 ? null : Math.Round(items.Where(i => i.State == "accepted").Sum(i => i.Importance) * 100_000d / tokens, 2);
    }
    private static double? Cost(IReadOnlyList<PersonaItemV1> items, IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        ITokenPricing? pricing)
    {
        var accepted = items.Count(i => i.State == "accepted" && i.Kind == PersonaItemKinds.Finding);
        var costs = receipts.Where(r => r.Tokens > 0).Select(r => r.CostUsd ?? PersonaStatsProjection.Price(r, pricing)).ToArray();
        return accepted == 0 || costs.Length == 0 || costs.Any(cost => cost is null)
            ? null : Math.Round(costs.Sum(cost => cost ?? 0) / accepted, 6);
    }
    private static double? Median(IEnumerable<double?> source)
    {
        var values = source.OfType<double>().Order().ToArray();
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }
    private static IReadOnlyList<PersonaMissV1> Misses(string workspaceId, PersonaActivationV1 activation,
        IReadOnlyList<PersonaItemV1> items, IReadOnlyList<TaskInfo> cards, DateTime now)
    {
        var first = activation.History.Where(h => h.Action == "activate").Select(h => h.At)
            .DefaultIfEmpty(activation.ActivatedAt).Min();
        var own = items.Where(i => i.PersonaId == activation.PersonaId).ToArray();
        bool FocusAllows(string area)
        {
            if (activation.Attributes.Focus is not { } focus) return true;
            if (focus.TryGetValue(area, out var weight)) return weight > 0;
            return !focus.TryGetValue(area[(area.LastIndexOf('.') + 1)..], out weight) || weight > 0;
        }
        bool InScope(string? project, string? area) => area is not null
            && activation.Scope.Areas.Contains(area, StringComparer.Ordinal)
            && (activation.Scope.Projects.Contains("*", StringComparer.Ordinal) || project is not null
                && activation.Scope.Projects.Contains(project, StringComparer.Ordinal))
            && FocusAllows(area);
        int Revision(DateTime at) => activation.History.Where(h => h.At <= at)
            .OrderByDescending(h => h.At).Select(h => (int?)h.ActivationRevision).FirstOrDefault()
            ?? activation.ActivationRevision;
        var found = items.Where(i => i.PersonaId != activation.PersonaId && i.Kind != PersonaItemKinds.ReportLine
                && i.Importance >= 90 && i.CreatedAtUtc >= first && i.CreatedAtUtc <= now && InScope(i.Project, i.Area))
            .GroupBy(i => i.Fingerprint, StringComparer.Ordinal).Select(g => g.MinBy(i => i.CreatedAtUtc)!)
            .Where(i => !own.Any(o => o.Fingerprint == i.Fingerprint && o.CreatedAtUtc <= i.CreatedAtUtc))
            .Select(i => new PersonaMissV1(i.Title, "item", i.Id,
                "/api/workspaces/" + Uri.EscapeDataString(workspaceId) + "/personas/items/" + Uri.EscapeDataString(i.Id),
                i.Importance, i.CreatedAtUtc, Revision(i.CreatedAtUtc)));
        var incidents = cards.Where(c => c.CreatedAt >= first && c.CreatedAt <= now
                && c.Tags.Contains("incident", StringComparer.OrdinalIgnoreCase)
                && c.Tags.Any(t => t.StartsWith("area:", StringComparison.Ordinal)
                    && InScope(c.ProjectName, t[5..]))
                && !own.Any(i => i.PromotedTaskKey == c.TaskKey || i.PromotedTaskKey == c.Id))
            .Select(c => new PersonaMissV1(c.Title, "card",
                string.IsNullOrWhiteSpace(c.TaskKey) ? c.Id : c.TaskKey,
                "#/tasks/" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(c.TaskKey) ? c.Id : c.TaskKey),
                90, c.CreatedAt, Revision(c.CreatedAt)));
        return found.Concat(incidents).OrderByDescending(m => m.AtUtc).ThenBy(m => m.SourceId, StringComparer.Ordinal).ToArray();
    }
}
