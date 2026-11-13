using AgentStudio.Shared;
using AgentStudio.Tokens;

namespace AgentStudio.Personas;

public sealed record PersonaRateV1(int Numerator, int Denominator, double? Percent);
public sealed record PersonaCardOutcomesV1(int Promoted, int Completed, int Open, int RejectedInReview, int ArchivedWithoutDelivery);
public sealed record PersonaStatsWindowV1(
    string Window, int? ActivationRevision, int Raised, int Accepted, int Rejected, int Duplicate,
    int Open, PersonaRateV1 Acceptance, PersonaCardOutcomesV1 Cards, long Tokens,
    double PricedCostUsd, int UnknownPriceSessions, double? CostPerAcceptedUsd,
    double? MedianResponseHours, double? P90ResponseHours, PersonaRateV1 Punctuality,
    double? Calibration, int Misses, bool TooFewDecisions);
public sealed record PersonaSheetV1(double Experience, string Rank, string Eligibility,
    IReadOnlyList<PersonaStatsWindowV1> Windows, IReadOnlyList<PersonaStatsWindowV1> Revisions);

/// <summary>A read-only record from items, reports and durable occurrence receipts. Rank never writes authority.</summary>
public static class PersonaStatsProjection
{
    public static PersonaSheetV1 Build(PersonaActivationV1 activation,
        IReadOnlyList<PersonaItemV1> allItems, IReadOnlyList<PersonaOccurrenceReceipt> allReceipts,
        IReadOnlyList<PersonaReportRecordV1> allReports, DateTime now,
        IReadOnlyList<TaskInfo>? cards = null, ITokenPricing? pricing = null)
    {
        var items = allItems.Where(i => i.PersonaId == activation.PersonaId
            && i.Kind != PersonaItemKinds.ReportLine).ToArray();
        var receipts = allReceipts.Where(r => r.PersonaId == activation.PersonaId).ToArray();
        var reports = allReports.Where(r => r.PersonaId == activation.PersonaId).ToArray();
        cards ??= [];
        var misses = FindMisses(activation, allItems, cards).ToArray();
        var lifetime = Window("lifetime", null, items, receipts, reports, cards, pricing, misses.Length);
        var recent = Window("30 days", null, items.Where(i => i.CreatedAtUtc >= now.AddDays(-30)),
            receipts.Where(r => r.CreatedAtUtc >= now.AddDays(-30)),
            reports.Where(r => r.PublishedAtUtc >= now.AddDays(-30)), cards, pricing,
            misses.Count(m => m.At >= now.AddDays(-30)));
        var experience = items.Where(i => i.State == "accepted")
            .Sum(i => i.Importance * Math.Pow(.5, Math.Max(0, (now - i.UpdatedAtUtc).TotalDays) / 90));
        var firstActivation = activation.History.Where(e => e.Action == "activate")
            .Select(e => e.At).DefaultIfEmpty(activation.ActivatedAt).Min();
        var age = now - firstActivation;
        var ninety = Window("90 days", null, items.Where(i => i.CreatedAtUtc >= now.AddDays(-90)),
            receipts.Where(r => r.CreatedAtUtc >= now.AddDays(-90)),
            reports.Where(r => r.PublishedAtUtc >= now.AddDays(-90)), cards, pricing, 0);
        var rank = age < TimeSpan.FromDays(28) || lifetime.Acceptance.Denominator < 10 ||
            (lifetime.Acceptance.Percent is < 30 && lifetime.Acceptance.Denominator >= 10)
            ? "Probation" : ninety.Acceptance.Percent is >= 60 && (ninety.Calibration ?? 0) <= 10
                ? "Senior" : "Member";
        var revisions = items.Select(i => i.ActivationRevision)
            .Concat(receipts.Select(r => r.ActivationRevision))
            .Concat(misses.Select(m => m.Revision))
            .Concat(activation.History.Select(e => e.ActivationRevision))
            .Append(activation.ActivationRevision).Distinct().OrderByDescending(x => x)
            .Select(revision => Window("lifetime", revision,
                items.Where(i => i.ActivationRevision == revision),
                receipts.Where(r => r.ActivationRevision == revision),
                reports.Where(r => receipts.Any(receipt => receipt.Key == r.OccurrenceKey &&
                    receipt.ActivationRevision == revision)), cards, pricing,
                misses.Count(m => m.Revision == revision))).ToArray();
        return new PersonaSheetV1(Math.Round(experience, 1), rank,
            rank == "Senior" ? "Eligible for owner review of higher autonomy or allowance" :
                "Rank does not change grants or allowance", [recent, lifetime], revisions);
    }

    private static PersonaStatsWindowV1 Window(string name, int? revision,
        IEnumerable<PersonaItemV1> sourceItems, IEnumerable<PersonaOccurrenceReceipt> sourceReceipts,
        IEnumerable<PersonaReportRecordV1> sourceReports, IReadOnlyList<TaskInfo> cards,
        ITokenPricing? pricing, int misses)
    {
        var items = sourceItems.ToArray();
        var receipts = sourceReceipts.ToArray();
        var reports = sourceReports.ToArray();
        var accepted = items.Count(i => i.State == "accepted");
        var rejected = items.Count(i => i.State == "rejected");
        var decided = accepted + rejected;
        var promoted = items.Where(i => i.PromotedTaskKey is not null).ToArray();
        var promotedCards = promoted.Select(i => cards.FirstOrDefault(card =>
            card.TaskKey == i.PromotedTaskKey || card.Id == i.PromotedTaskKey)).ToArray();
        var completed = promotedCards.Count(card => card?.State == TaskStates.Completed);
        var archived = promotedCards.Count(card => card?.State == TaskStates.Archive);
        var rejectedInReview = promotedCards.Count(card => card?.State is not (TaskStates.Completed or TaskStates.Archive)
            && card?.OrchestratorVerdict == "reissue");
        var costs = receipts.Where(r => r.Tokens > 0).Select(r => r.CostUsd is { } stored
            ? (double?)stored : Price(r, pricing)).ToArray();
        var priced = costs.Sum(value => value ?? 0);
        var unknown = costs.Count(value => value is null);
        var response = (from item in items
            let receipt = receipts.FirstOrDefault(r => r.Key == item.History.FirstOrDefault()?.OccurrenceKey)
            where receipt is not null && item.FirstPublishedAtUtc.HasValue
            select Math.Max(0, (item.FirstPublishedAtUtc.Value -
                (receipt.FirstTriggerAtUtc ?? receipt.CreatedAtUtc)).TotalHours)).Order().ToArray();
        var due = receipts.Length;
        var onTime = reports.Count(r => receipts.Any(receipt => receipt.Key == r.OccurrenceKey &&
            r.PublishedAtUtc <= receipt.WindowEndUtc));
        var changes = items.SelectMany(i => i.History.Where(h => h.Reason is "manager-adjusted" or "owner-adjusted")
            .Select(h => Math.Abs(h.Importance - h.PreviousImportance))).ToArray();
        return new PersonaStatsWindowV1(name, revision, items.Length, accepted, rejected,
            items.Count(i => i.State == "duplicate"),
            items.Count(i => PersonaItemStates.IsDecidable(i.State)),
            Rate(accepted, decided, decided),
            new PersonaCardOutcomesV1(promoted.Length, completed,
                promoted.Length - completed - archived - rejectedInReview, rejectedInReview, archived),
            receipts.Sum(r => r.Tokens), Math.Round(priced, 6), unknown,
            decided < 5 || accepted == 0 || unknown > 0 ? null : Math.Round(priced / accepted, 6),
            Percentile(response, .5), Percentile(response, .9), Rate(onTime, due, decided),
            changes.Length == 0 ? null : Math.Round(changes.Average(), 1), misses, decided < 5);
    }

    private static IEnumerable<(DateTime At, int Revision)> FindMisses(
        PersonaActivationV1 activation, IReadOnlyList<PersonaItemV1> allItems, IReadOnlyList<TaskInfo> cards)
    {
        var ownItems = allItems.Where(i => i.PersonaId == activation.PersonaId).ToArray();
        var firstActive = activation.History.Where(e => e.Action == "activate")
            .Select(e => e.At).DefaultIfEmpty(activation.ActivatedAt).Min();
        bool FocusAllows(string area)
        {
            if (activation.Attributes.Focus is not { } focus) return true;
            if (focus.TryGetValue(area, out var weight)) return weight > 0;
            return !focus.TryGetValue(area[(area.LastIndexOf('.') + 1)..], out weight) || weight > 0;
        }
        bool InScope(string? project, string? area) => area is not null
            && activation.Scope.Areas.Contains(area, StringComparer.Ordinal)
            && FocusAllows(area)
            && (activation.Scope.Projects.Contains("*", StringComparer.Ordinal)
                || project is not null && activation.Scope.Projects.Contains(project, StringComparer.Ordinal));
        int RevisionAt(DateTime at) => activation.History.Where(e => e.At <= at)
            .OrderByDescending(e => e.At).Select(e => (int?)e.ActivationRevision)
            .FirstOrDefault() ?? activation.ActivationRevision;

        foreach (var first in allItems.Where(i => i.PersonaId != activation.PersonaId
                && i.Kind != PersonaItemKinds.ReportLine && i.Importance >= 90 && i.CreatedAtUtc >= firstActive
                && InScope(i.Project, i.Area))
            .GroupBy(i => i.Fingerprint, StringComparer.Ordinal)
            .Select(g => g.MinBy(i => i.CreatedAtUtc)!))
        {
            if (ownItems.Any(i => i.Fingerprint == first.Fingerprint && i.CreatedAtUtc <= first.CreatedAtUtc))
                continue;
            yield return (first.CreatedAtUtc, RevisionAt(first.CreatedAtUtc));
        }

        foreach (var card in cards.Where(c => c.CreatedAt >= firstActive
            && c.Tags.Contains("incident", StringComparer.OrdinalIgnoreCase)
            && c.Tags.Any(tag => tag.StartsWith("area:", StringComparison.Ordinal)
                && InScope(c.ProjectName, tag[5..]))))
        {
            if (ownItems.Any(i => i.PromotedTaskKey == card.TaskKey || i.PromotedTaskKey == card.Id))
                continue;
            yield return (card.CreatedAt, RevisionAt(card.CreatedAt));
        }
    }

    private static PersonaRateV1 Rate(int numerator, int denominator, int decided)
        => new(numerator, denominator, decided < 5 || denominator == 0
            ? null : Math.Round(100d * numerator / denominator, 1));

    private static double? Price(PersonaOccurrenceReceipt receipt, ITokenPricing? pricing)
    {
        if (pricing is null) return null;
        var estimate = pricing.Estimate(receipt.ActualModel ?? receipt.Model,
            receipt.InputTokens, receipt.OutputTokens, receipt.CacheReadTokens,
            receipt.CacheWriteTokens, receipt.FinishedAtUtc ?? receipt.CreatedAtUtc);
        return estimate.ModelKnown ? (double)estimate.Total : null;
    }

    private static double? Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return null;
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower), 1);
    }
}
