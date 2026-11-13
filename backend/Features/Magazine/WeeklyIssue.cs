using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Personas;

namespace AgentStudio.Magazine;

public sealed record IssueStory(string Id, string Title, string PersonaId, string Persona, string RoleId,
    string Role, string Kind, string State, int Importance, DateTime CreatedAtUtc,
    string? Teaser = null, string? RecommendedOption = null, DateTime? DeadlineUtc = null);
public sealed record IssueSection(string RoleId, string Role, int Count, IReadOnlyList<IssueStory> Stories,
    string? Teaser = null);
public sealed record IssueWatchRow(string Id, string Group, string Label, string State, int Importance,
    int ItemCount, DateTime? ObservedAtUtc);
public sealed record IssueTrendWeek(DateOnly Week, int Raised, int Accepted, long TokensUsed,
    int CardsDelivered, double? CostPerAcceptedFinding, double? BudgetUsePercent);
public sealed record IssueArticle(string Scope, string Slug, int Revision, string Title,
    string Summary, string Persona, string? PersonaId, string Role);
public sealed record WeeklyIssue(
    string Id, string WorkspaceId, string Kind, DateTime PublishedAtUtc, DateOnly PeriodStart,
    DateOnly PeriodEnd, string Masthead, string CoverageLine, int StrapTotal,
    IssueStory? Lead, IReadOnlyList<IssueStory> SideStories, IReadOnlyList<IssueStory> DecisionsNeeded,
    IReadOnlyList<IssueWatchRow> OperationsWatch, IReadOnlyList<IssueSection> Sections,
    IReadOnlyList<IssueTrendWeek> Trends, IReadOnlyList<IssueStory> Corrections,
    IReadOnlyList<string> Colophon, string? LeadParagraph = null, bool EditorSkipped = true)
{
    public IReadOnlyList<IssueArticle> Articles { get; init; } = [];
    public long EditorTokens { get; init; }
    public long? PoolTokensSnapshot { get; init; }
}

/// <summary>Pure, stable layout from a snapshot of items, reports and warden facts.</summary>
public static class WeeklyIssueComposer
{
    private static readonly (string Id, string Group, string Label, string[] Facts)[] WatchRows =
    [
        ("backup.schedule", "Backups", "Schedule and destination", ["backup.schedule", "backup.destination"]),
        ("backup.last-good", "Backups", "Last good backup age", ["backup.last-good"]),
        ("backup.coverage.instance", "Backups", "Instance state coverage", ["backup.coverage.instance"]),
        ("backup.coverage.configuration", "Backups", "Configuration coverage", ["backup.coverage.configuration"]),
        ("backup.coverage.secrets", "Backups", "Secrets coverage", ["backup.coverage.secrets"]),
        ("backup.restore-test", "Backups", "Restore test", ["backup.restore-test"]),
        ("backup.off-host", "Backups", "Off-host copy", ["backup.off-host"]),
        ("backup.escrow", "Backups", "Key escrow", ["backup.escrow"]),
        ("security.exposure", "Security posture", "Exposed endpoints and firewall", ["security.exposure"]),
        ("security.tls", "Security posture", "TLS and origin protection", ["security.tls", "security.origin"]),
        ("security.credentials", "Security posture", "Credential and token age", ["security.credentials"]),
        ("security.second-factor", "Security posture", "Second factor coverage", ["security.second-factor"]),
        ("security.patch", "Security posture", "Patch level", ["security.patch"]),
        ("security.audit", "Security posture", "Audit anomalies", ["security.audit", "security.break-glass"]),
        ("security.findings", "Security posture", "Open security cards", ["security.findings"]),
    ];

    public static WeeklyIssue Compose(string workspaceId, DateOnly date, bool daily,
        IReadOnlyList<PersonaItemV1> items, IReadOnlyList<PersonaReportRecordV1> reports,
        IReadOnlyList<IssuePersona> personas, OpsPostureFactsV1? watch,
        TimeZoneInfo zone, IReadOnlyList<PersonaOccurrenceReceipt>? receipts = null, long? poolTokens = null)
    {
        var monday = date.AddDays(-((int)date.DayOfWeek + 6) % 7);
        var start = daily ? date : monday;
        var end = daily ? date : monday.AddDays(6);
        var localTime = date.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Unspecified);
        var published = TimeZoneInfo.ConvertTimeToUtc(localTime, zone);
        var byId = personas.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var publishingIds = personas.Where(p => p.Active).Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var selected = items.Where(i => publishingIds.Contains(i.PersonaId) &&
                i.CreatedAtUtc <= published &&
                (i.CreatedAtUtc >= TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone)
                 || i.UpdatedAtUtc >= TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone)
                 || PersonaItemStates.IsDecidable(i.State)))
            .OrderByDescending(i => i.Importance).ThenBy(i => i.CreatedAtUtc).ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToArray();
        IssueStory Story(PersonaItemV1 i)
        {
            byId.TryGetValue(i.PersonaId, out var persona);
            var role = persona?.RoleId ?? i.RoleId;
            return new(i.Id, i.Title, i.PersonaId, persona?.Name ?? i.PersonaId, role,
                role.Replace('-', ' '), i.Kind, i.State, i.Importance, i.CreatedAtUtc,
                RecommendedOption: i.RecommendedOption, DeadlineUtc: i.DeadlineUtc);
        }
        var stories = selected.Select(Story).ToArray();
        var open = stories.Where(s => PersonaItemStates.IsDecidable(s.State)).ToArray();
        var lead = open.FirstOrDefault();
        var sides = new List<IssueStory>();
        var scheduleMissing = watch?.Facts.Any(f => f.Id == "backup.schedule" && f.Value == "missing") == true;
        foreach (var story in open.Where(s => s.Id != lead?.Id))
        {
            if (scheduleMissing && selected.First(i => i.Id == story.Id).Fingerprint.StartsWith("ops:backup.", StringComparison.Ordinal)
                && !selected.First(i => i.Id == story.Id).Fingerprint.StartsWith("ops:backup.schedule:", StringComparison.Ordinal)
                && !selected.First(i => i.Id == story.Id).Fingerprint.StartsWith("ops:backup.escrow:", StringComparison.Ordinal)) continue;
            if (sides.Count(s => s.PersonaId == story.PersonaId) + (lead?.PersonaId == story.PersonaId ? 1 : 0) >= 2
                && story.Importance < 90) continue;
            if (sides.Count < 4 || story.Importance >= 90) sides.Add(story);
        }
        var decisions = daily ? [] : open.Where(s => s.Kind is PersonaItemKinds.DecisionRequest or PersonaItemKinds.Proposal).ToArray();
        var sections = daily ? [] : stories.GroupBy(s => s.RoleId, StringComparer.Ordinal)
            .Select(g => new IssueSection(g.Key, g.First().Role, g.Count(), g.ToArray()))
            .OrderByDescending(s => s.Stories[0].Importance).ThenBy(s => s.RoleId, StringComparer.Ordinal).ToArray();
        var reportIds = reports.Where(r => r.State != PersonaStates.Shadow &&
            publishingIds.Contains(r.PersonaId) &&
            r.PublishedAtUtc >= published.AddDays(daily ? -1 : -7)
            && r.PublishedAtUtc <= published).Select(r => r.PersonaId).Distinct(StringComparer.Ordinal).Count();
        var count = daily ? (lead is null ? 0 : 1) + sides.Count : sections.Sum(s => s.Count);
        var corrections = daily ? [] : stories.Where(s => s.State is "rejected" or "retracted").ToArray();
        var colophon = daily ? [] : personas.Where(p => p.Active && !reports.Any(r => r.PersonaId == p.Id &&
                r.PublishedAtUtc >= published.AddDays(-7) && r.PublishedAtUtc <= published))
            .Select(p => "Report missing: " + p.Name).Concat(reports.Where(r => r.State == "stub" &&
                publishingIds.Contains(r.PersonaId) &&
                r.PublishedAtUtc >= published.AddDays(-7) && r.PublishedAtUtc <= published)
            .Select(r => "Stub report: " + r.PersonaId + " (" + r.Reason + ")"))
            .Order(StringComparer.Ordinal).ToArray();
        return new WeeklyIssue((daily ? "daily-" : "weekly-") + date.ToString("yyyy-MM-dd"), workspaceId,
            daily ? "daily" : "weekly", published, start, end, daily ? "Studio Daily" : "Studio Weekly",
            $"{reportIds} personas reported · {count} items", count, lead, sides, decisions,
            daily ? [] : BuildWatch(watch, selected, published), sections,
            daily ? [] : Trends(monday, items, reports, receipts ?? [], poolTokens),
            corrections, colophon);
    }

    public static WeeklyIssue WithEditor(WeeklyIssue issue, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var lead = root.GetProperty("leadParagraph").GetString();
        var teaserMap = root.GetProperty("teasers").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var sectionMap = root.TryGetProperty("sectionTeasers", out var sectionTeasers)
            ? sectionTeasers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(issue.Sections.SelectMany(s => s.Stories).Select(s => s.Id), StringComparer.Ordinal);
        if (issue.Lead is { } leadStory) allowed.Add(leadStory.Id);
        foreach (var side in issue.SideStories) allowed.Add(side.Id);
        Validate(lead, allowed);
        foreach (var (id, teaser) in teaserMap)
        {
            if (!allowed.Contains(id)) throw new ArgumentException("Editor teaser cites an unknown item id.");
            Validate(teaser, allowed);
        }
        var sectionIds = issue.Sections.Select(section => section.RoleId).ToHashSet(StringComparer.Ordinal);
        foreach (var (roleId, teaser) in sectionMap)
        {
            if (!sectionIds.Contains(roleId)) throw new ArgumentException("Editor teaser names an unknown role section.");
            Validate(teaser, allowed);
        }
        IssueStory Decorate(IssueStory story) => story with { Teaser = teaserMap.GetValueOrDefault(story.Id) };
        return issue with { LeadParagraph = lead, EditorSkipped = false,
            SideStories = issue.SideStories.Select(Decorate).ToArray(),
            Sections = issue.Sections.Select(s => s with { Teaser = sectionMap.GetValueOrDefault(s.RoleId),
                Stories = s.Stories.Select(Decorate).ToArray() }).ToArray() };
    }

    private static void Validate(string? text, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Editor sentence is empty.");
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[.!?])\s+"))
        {
            if (sentence.Length == 0) continue;
            var refs = Regex.Matches(sentence, @"\[(pit_[A-Za-z0-9]+)\]");
            if (refs.Count == 0 || refs.Any(m => !allowed.Contains(m.Groups[1].Value)))
                throw new ArgumentException("Every editor sentence must cite a known item id.");
        }
    }

    private static IssueWatchRow[] BuildWatch(OpsPostureFactsV1? snapshot, IReadOnlyList<PersonaItemV1> items,
        DateTime published)
    {
        return WatchRows.Select(row =>
        {
            var facts = snapshot?.Facts.Where(f => row.Facts.Contains(f.Id)).ToArray() ?? [];
            var linked = items.Where(i => row.Facts.Any(id => i.Fingerprint.StartsWith("ops:" + id + ":", StringComparison.Ordinal))
                && PersonaItemStates.IsDecidable(i.State)).ToArray();
            var unknown = facts.Length != row.Facts.Length || facts.Any(f => f.Value is null || f.ObservedAt is null ||
                published - f.ObservedAt > f.Freshness);
            var floor = facts.Select(f => FailureFloor(f, published)).DefaultIfEmpty(0).Max();
            var bad = floor > 0 || linked.Length > 0;
            var impact = row.Id == "security.tls" ? 5 : row.Id == "security.second-factor" ? 4 : 3;
            var urgency = row.Id == "security.tls" && facts.Any(f =>
                double.TryParse(f.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var days) && days < 28) ? 2 : 1;
            var number = bad ? Math.Max(floor, linked.Select(i => i.Importance).DefaultIfEmpty(0).Max())
                : unknown ? 70 : Math.Min(39, PersonaImportanceService.Score(impact, urgency, 1));
            return new IssueWatchRow(row.Id, row.Group, row.Label, unknown ? "unknown" : bad ? "finding" : "passing",
                number, linked.Length, facts.Select(f => f.ObservedAt).Where(at => at is not null).Max());
        }).ToArray();
    }

    private static int FailureFloor(OpsPostureFactV1 fact, DateTime published)
    {
        var value = fact.Value;
        return fact.Id switch
        {
            "backup.schedule" or "backup.destination" when value == "missing" => 95,
            "backup.last-good" when value == "none" => 90,
            "backup.last-good" when double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var age) => age > 48 ? 90 : age > 26 ? 70 : 0,
            var id when id.StartsWith("backup.coverage.", StringComparison.Ordinal) && value == "uncovered" => 80,
            "backup.restore-test" when value is "never" or "failed" => 85,
            "backup.restore-test" when fact.ObservedAt < published.AddDays(-35) => 70,
            "backup.off-host" when value == "missing" => 85,
            "backup.escrow" when value is "unconfirmed" or "mismatched" => 85,
            "security.exposure" when value == "unexpected" => 80,
            "security.tls" when value == "expired" => 95,
            "security.tls" when value == "within-14d" => 80,
            "security.tls" when double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var days) =>
                days <= 0 ? 95 : days < 14 ? 80 : 0,
            "security.origin" when value == "unprotected" => 90,
            "security.credentials" when value == "expired-referenced" => 95,
            "security.credentials" when value == "within-7d" => 80,
            "security.credentials" when value == "no-expiry" => 70,
            "security.credentials" when value == "within-14d" => 60,
            "security.second-factor" when value == "owner-missing" => 85,
            "security.patch" when value == "overdue" => 75,
            "security.audit" when value == "broken" => 95,
            "security.break-glass" when value == "open-unreviewed" => 80,
            "security.findings" when value == "high-overdue" => 75,
            _ => 0,
        };
    }

    private static IssueTrendWeek[] Trends(DateOnly monday, IReadOnlyList<PersonaItemV1> items,
        IReadOnlyList<PersonaReportRecordV1> reports, IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        long? poolTokens)
    {
        return Enumerable.Range(0, 8).Select(index =>
        {
            var week = monday.AddDays((index - 7) * 7);
            var from = week.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var to = from.AddDays(7);
            var raised = items.Count(i => i.CreatedAtUtc >= from && i.CreatedAtUtc < to);
            var accepted = items.Count(i => i.Kind == PersonaItemKinds.Proposal && i.State == "accepted"
                && i.UpdatedAtUtc >= from && i.UpdatedAtUtc < to);
            var tokens = reports.Where(r => r.PublishedAtUtc >= from && r.PublishedAtUtc < to).Sum(r => r.TokensUsed);
            var delivered = items.Count(i => i.PromotedTaskKey is not null && i.UpdatedAtUtc >= from && i.UpdatedAtUtc < to);
            var cost = receipts.Where(r => r.FinishedAtUtc >= from && r.FinishedAtUtc < to)
                .Sum(r => r.CostUsd ?? 0);
            return new IssueTrendWeek(week, raised, accepted, tokens, delivered,
                accepted == 0 ? null : cost / accepted,
                poolTokens is > 0 ? Math.Round(100d * tokens / poolTokens.Value, 1) : null);
        }).ToArray();
    }
}

public sealed record IssuePersona(string Id, string Name, string RoleId, bool Active = true);
