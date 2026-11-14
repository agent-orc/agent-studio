using System.Globalization;
using System.Text.Json;
using AgentStudio.Personas;

namespace AgentStudio.Magazine;

public sealed record DeckBlock(string Kind, string Label, string? ItemId, string IssueId,
    string? SourceField = null, string? Value = null, string? Role = null);
public sealed record DeckSlide(string Id, string Layout, string Title, IReadOnlyList<DeckBlock> Blocks,
    string? SpeakerNotes = null);
public sealed record PresenterDeck(string Id, string WorkspaceId, string Kind, string Audience,
    IReadOnlyList<string> IssueIds, DateTime CreatedAtUtc, IReadOnlyList<DeckSlide> Slides,
    bool NotesSkipped = true, string? PresenterPersonaId = null, string PresenterRoleId = "presenter")
{
    public long EditorTokens { get; init; }
}

/// <summary>Stable deck layout. Source values are copied from immutable issue snapshots.</summary>
public static class PresenterDeckComposer
{
    public static PresenterDeck Compose(IReadOnlyList<WeeklyIssue> issues, string kind)
    {
        if (issues.Count == 0 || issues.Any(i => i.Kind != "weekly")) throw new ArgumentException("Weekly issues required.");
        if (kind is not ("weekly" or "monthly") || kind == "monthly" && issues.Count != 4)
            throw new ArgumentException("A monthly deck needs four weekly issues.");
        var ordered = issues.OrderBy(i => i.PeriodStart).ToArray();
        if (kind == "monthly" && !Consecutive(ordered))
            throw new ArgumentException("A monthly deck needs four consecutive weekly issues.");
        var latest = ordered[^1];
        var selectedIssues = kind == "weekly" ? [latest] : ordered;
        var stories = selectedIssues.SelectMany(i => i.Sections.SelectMany(s => s.Stories).Select(s => (Issue: i, Story: s)))
            .Where(x => x.Story.Importance >= 40)
            .GroupBy(x => x.Story.Id, StringComparer.Ordinal).Select(g => g.Last())
            .OrderByDescending(x => x.Story.Importance >= 90)
            .ThenByDescending(x => x.Story.Importance).ThenBy(x => x.Story.Id, StringComparer.Ordinal).ToArray();
        DeckBlock Item((WeeklyIssue Issue, IssueStory Story) x) => new("item", x.Story.Title, x.Story.Id,
            x.Issue.Id, "importance", x.Story.Importance.ToString(CultureInfo.InvariantCulture), x.Story.Role);
        var slides = new List<DeckSlide>();
        void Add(string layout, string title, IEnumerable<DeckBlock> blocks) => slides.Add(new DeckSlide(
            "slide-" + (slides.Count + 1), layout, title, blocks.Take(3).ToArray()));
        Add("title", kind == "weekly" ? "Weekly review" : "Monthly review", stories.Take(1).Select(Item));
        Add("ranked-list", "Priority items", stories.Take(3).Select(Item));
        var decisions = selectedIssues.SelectMany(i => i.DecisionsNeeded.Select(s => (Issue: i, Story: s)))
            .Where(x => x.Story.Importance >= 40).GroupBy(x => x.Story.Id, StringComparer.Ordinal)
            .Select(g => g.Last()).OrderByDescending(x => x.Story.Importance >= 90)
            .ThenByDescending(x => x.Story.Importance).ToArray();
        Add("decision-list", "Decisions needed", decisions.Select(Item));
        Add("operations-watch", "Operations watch", latest.OperationsWatch.OrderByDescending(r => r.Importance)
            .Take(3).Select(r => new DeckBlock("watch", r.Label, null, latest.Id,
                "operationsWatch." + r.Id + ".importance", r.Importance.ToString(CultureInfo.InvariantCulture))));
        Add("numbers", "This week in numbers", new[] { "raised", "accepted", "cardsDelivered" }
            .Select(field => new DeckBlock("metric", field, null, latest.Id,
                $"trends.{latest.Trends.Last().Week:yyyy-MM-dd}.{field}",
                Field(latest.Trends.Last(), field))));
        if (ordered.Length >= 4 && Consecutive(ordered.TakeLast(4).ToArray()))
        {
            var weeks = latest.Trends.TakeLast(4).ToArray();
            Add("trend", "Four week trend", [new DeckBlock("metric", "Items raised", null, latest.Id,
                string.Join(';', weeks.Select(w => $"trends.{w.Week:yyyy-MM-dd}.raised")),
                string.Join(" · ", weeks.Select(w => w.Raised.ToString(CultureInfo.InvariantCulture))))]);
        }
        else Add("ranked-list", "More findings", stories.Skip(3).Take(3).Select(Item));
        var roles = stories.GroupBy(x => x.Story.RoleId, StringComparer.Ordinal)
            .Where(g => g.Any(x => x.Story.Importance >= 70))
            .OrderByDescending(g => g.Max(x => x.Story.Importance)).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        foreach (var role in roles.Take(kind == "weekly" ? 1 : 4))
            Add("ranked-list", role.First().Story.Role, role.Take(3).Select(Item));
        var target = kind == "weekly" ? 7 : Math.Clamp(6 + roles.Length, 10, 12);
        while (slides.Count < target)
        {
            var offset = Math.Max(0, (slides.Count - 6) * 3);
            Add("ranked-list", "Other findings", stories.Skip(offset).Take(3).Select(Item));
        }
        var deck = new PresenterDeck(kind + "-" + latest.PeriodStart.ToString("yyyy-MM-dd"), latest.WorkspaceId,
            kind, "owner", ordered.Select(i => i.Id).ToArray(), latest.PublishedAtUtc, slides.Take(target).ToArray());
        PresenterDeckValidator.Validate(deck, ordered);
        return deck;
    }

    internal static string Field(IssueTrendWeek week, string field) => field switch
    {
        "raised" => week.Raised.ToString(CultureInfo.InvariantCulture),
        "accepted" => week.Accepted.ToString(CultureInfo.InvariantCulture),
        "cardsDelivered" => week.CardsDelivered.ToString(CultureInfo.InvariantCulture),
        "tokensUsed" => week.TokensUsed.ToString(CultureInfo.InvariantCulture),
        "budgetUsePercent" => week.BudgetUsePercent?.ToString(CultureInfo.InvariantCulture) ?? "",
        "costPerAcceptedFinding" => week.CostPerAcceptedFinding?.ToString(CultureInfo.InvariantCulture) ?? "",
        _ => throw new ArgumentException("Unknown trend field.")
    };

    private static bool Consecutive(IReadOnlyList<WeeklyIssue> issues) =>
        issues.Zip(issues.Skip(1)).All(pair => pair.Second.PeriodStart.DayNumber - pair.First.PeriodStart.DayNumber == 7);
}

public static class PresenterDeckValidator
{
    public static void Validate(PresenterDeck deck, IReadOnlyList<WeeklyIssue> issues)
    {
        var byIssue = issues.ToDictionary(i => i.Id, StringComparer.Ordinal);
        if (deck.Slides.Count != (deck.Kind == "weekly" ? 7 : deck.Slides.Count) ||
            deck.Kind == "monthly" && deck.Slides.Count is < 10 or > 12 ||
            !deck.Slides.Any(s => s.Layout == "decision-list")) throw new ArgumentException("Invalid deck layout.");
        foreach (var slide in deck.Slides)
        {
            var titleWithoutCitations = System.Text.RegularExpressions.Regex.Replace(slide.Title,
                @"\[pit_[A-Za-z0-9_]+\]", "");
            if (slide.Blocks.Count > 3 || titleWithoutCitations.Any(char.IsDigit))
                throw new ArgumentException("Invalid slide title or item count.");
            var knownTitle = slide.Title is "Weekly review" or "Monthly review" or "Priority items" or
                "Decisions needed" or "Operations watch" or "This week in numbers" or "Four week trend" or
                "More findings" or "Other findings";
            if (!knownTitle && !slide.Blocks.Any(b => b.Role == slide.Title) &&
                !slide.Blocks.Where(b => b.ItemId is not null).Any(b => slide.Title.Contains("[" + b.ItemId + "]", StringComparison.Ordinal)))
                throw new ArgumentException("A slide title claim needs an item id.");
            foreach (var block in slide.Blocks)
            {
                if (!byIssue.TryGetValue(block.IssueId, out var issue)) throw new ArgumentException("Unknown issue source.");
                if (block.Kind == "item" && string.IsNullOrWhiteSpace(block.ItemId))
                    throw new ArgumentException("A claim needs an item id.");
                if (block.Kind == "item")
                {
                    var story = issue.Sections.SelectMany(s => s.Stories).FirstOrDefault(s => s.Id == block.ItemId)
                        ?? throw new ArgumentException("Unknown item id.");
                    if (story.Title != block.Label || block.SourceField != "importance" ||
                        block.Value != story.Importance.ToString(CultureInfo.InvariantCulture))
                        throw new ArgumentException("Item claim or number differs from source.");
                }
                else if (block.Kind == "watch")
                {
                    var row = issue.OperationsWatch.FirstOrDefault(r => "operationsWatch." + r.Id + ".importance" == block.SourceField)
                        ?? throw new ArgumentException("Unknown watch item id.");
                    if (block.ItemId is not null || row.Label != block.Label ||
                        block.Value != row.Importance.ToString(CultureInfo.InvariantCulture))
                        throw new ArgumentException("Watch claim or number differs from source.");
                }
                else if (block.Kind == "metric")
                {
                    if (block.Label is not ("raised" or "accepted" or "cardsDelivered" or "Items raised"))
                        throw new ArgumentException("A metric label must name a known stats field.");
                    if (block.ItemId is not null || block.SourceField is null || block.Value is null)
                        throw new ArgumentException("A number needs a stats source field.");
                    var values = new List<string>();
                    foreach (var source in block.SourceField.Split(';'))
                    {
                        var parts = source.Split('.');
                        if (parts.Length != 3 || parts[0] != "trends" ||
                            !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var date))
                            throw new ArgumentException("Invalid stats source field.");
                        var week = issue.Trends.FirstOrDefault(w => w.Week == date)
                            ?? throw new ArgumentException("Unknown trend week.");
                        values.Add(PresenterDeckComposer.Field(week, parts[2]));
                    }
                    if (block.Value != string.Join(" · ", values))
                        throw new ArgumentException("Number differs from stats source.");
                }
                else throw new ArgumentException("Unknown deck block kind.");
                if (block.Value is not null && string.IsNullOrWhiteSpace(block.SourceField))
                    throw new ArgumentException("A number needs a source field.");
            }
            if (!string.IsNullOrWhiteSpace(slide.SpeakerNotes))
                foreach (var sentence in System.Text.RegularExpressions.Regex.Split(slide.SpeakerNotes.Trim(), @"(?<=[.!?])\s+"))
                    if (!slide.Blocks.Where(b => b.ItemId is not null).Any(b => sentence.Contains("[" + b.ItemId + "]", StringComparison.Ordinal)) ||
                        System.Text.RegularExpressions.Regex.Replace(sentence, @"\[pit_[A-Za-z0-9_]+\]", "").Any(char.IsDigit))
                        throw new ArgumentException("Every speaker note claim needs a slide item id.");
        }
    }

    public static PresenterDeck WithNotes(PresenterDeck deck, string raw, IReadOnlyList<WeeklyIssue> issues)
    {
        using var document = JsonDocument.Parse(raw);
        var output = document.RootElement.GetProperty("slides").EnumerateArray().ToArray();
        if (output.Length != deck.Slides.Count) throw new ArgumentException("Model slide count differs.");
        for (var index = 0; index < output.Length; index++)
        {
            var title = output[index].GetProperty("title").GetString();
            if (title != deck.Slides[index].Title &&
                !deck.Slides[index].Blocks.Where(b => b.ItemId is not null)
                    .Any(b => title?.Contains("[" + b.ItemId + "]", StringComparison.Ordinal) == true))
                throw new ArgumentException("A model title claim needs a slide item id.");
        }
        var slides = deck.Slides.Select((slide, index) => slide with
        {
            Title = output[index].GetProperty("title").GetString() ?? slide.Title,
            SpeakerNotes = output[index].GetProperty("speakerNotes").GetString()
        }).ToArray();
        var edited = deck with { Slides = slides, NotesSkipped = false };
        Validate(edited, issues);
        return edited;
    }
}

public static class PresenterDeckAudience
{
    /// <summary>Only generic labels, sourced counts and item ids leave the owner view.</summary>
    public static PresenterDeck Stakeholder(PresenterDeck deck) => deck with
    {
        Audience = "stakeholder",
        Slides = deck.Slides.Select(slide => slide with
        {
            Title = slide.Layout switch
            {
                "title" => deck.Kind == "weekly" ? "Weekly review" : "Monthly review",
                "decision-list" => "Decisions needed",
                "operations-watch" => "Operations watch",
                "numbers" => "Numbers",
                "trend" => "Trend",
                _ => "Priority items"
            },
            SpeakerNotes = null,
            Blocks = slide.Blocks.Where(block => block.SourceField is null ||
                    !block.SourceField.Contains("cost", StringComparison.OrdinalIgnoreCase))
                .Select(block => block with { Label = block.Kind switch
                {
                    "item" => "Item",
                    "watch" => "Operations item",
                    _ => block.Label
                }, Role = null }).ToArray()
        }).ToArray()
    };
}
