using System.Text;
using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Personas;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PresenterDeckTests
{
    // clock-independent: These dates feed pure issue and deck composition; no test compares them with now.
    private static readonly DateTime At = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
    private static PersonaItemV1 Item(string id, int importance, string role = "security", string kind = "finding",
        string title = "Verified finding") => new(id, "prs_a", role, kind, title, "Sensitive body on host-01.example.test costs $42",
            null, [], 5, 5, 1, 0, importance, "fact:" + id, kind == "proposal" ? "draft" : "open", At, At, []);
    private static readonly PersonaItemV1[] Items =
    [
        Item("pit_high", 95, title: "host-01.example.test needs a backup"),
        Item("pit_decision", 80, kind: "proposal", title: "Spend $42 on storage"),
        Item("pit_normal", 55), Item("pit_low", 39),
    ];
    private static WeeklyIssue Issue(DateOnly monday) => WeeklyIssueComposer.Compose("ws-demo", monday,
        false, Items, [], [new IssuePersona("prs_a", "Aster", "security")], null, TimeZoneInfo.Utc);

    [Fact]
    public void Weekly_deck_is_seven_slides_and_every_number_resolves_to_a_source()
    {
        var issue = Issue(new DateOnly(2026, 11, 9));
        var deck = PresenterDeckComposer.Compose([issue], "weekly");
        Assert.Equal(7, deck.Slides.Count);
        Assert.Contains(deck.Slides, slide => slide.Layout == "decision-list");
        Assert.DoesNotContain(deck.Slides, slide => slide.Layout == "trend");
        Assert.All(deck.Slides, slide => Assert.True(slide.Blocks.Count <= 3));
        Assert.DoesNotContain(deck.Slides.SelectMany(s => s.Blocks), b => b.ItemId == "pit_low");
        Assert.Equal("pit_high", deck.Slides[1].Blocks[0].ItemId);
        PresenterDeckValidator.Validate(deck, [issue]);
        var number = deck.Slides.SelectMany(s => s.Blocks).First(b => b.Kind == "metric");
        var broken = deck with { Slides = deck.Slides.Select(s => s with
            { Blocks = s.Blocks.Select(b => b == number ? b with { SourceField = null } : b).ToArray() }).ToArray() };
        Assert.Throws<ArgumentException>(() => PresenterDeckValidator.Validate(broken, [issue]));
        var unsourced = deck with { Slides = deck.Slides.Select((s, i) => i == 1 ? s with
            { Blocks = [s.Blocks[0] with { ItemId = null }] } : s).ToArray() };
        Assert.Throws<ArgumentException>(() => PresenterDeckValidator.Validate(unsourced, [issue]));
    }

    [Fact]
    public void Monthly_deck_uses_four_issues_and_refuses_uncited_notes()
    {
        var issues = Enumerable.Range(0, 4).Select(i => Issue(new DateOnly(2026, 11, 9).AddDays(i * 7))).ToArray();
        var deck = PresenterDeckComposer.Compose(issues, "monthly");
        Assert.InRange(deck.Slides.Count, 10, 12);
        Assert.Contains(deck.Slides, s => s.Layout == "trend");
        Assert.Equal(4, deck.Slides.Single(s => s.Layout == "trend").Blocks[0].SourceField!.Split(';').Length);
        var weekly = PresenterDeckComposer.Compose(issues, "weekly");
        Assert.Equal(7, weekly.Slides.Count);
        Assert.Contains(weekly.Slides, s => s.Layout == "trend");
        Assert.Contains(weekly.Slides, s => s.Layout == "ranked-list" && s.Title == "security");
        var gap = new[] { issues[0], issues[1], issues[2], Issue(new DateOnly(2026, 12, 7)) };
        Assert.DoesNotContain(PresenterDeckComposer.Compose(gap, "weekly").Slides, s => s.Layout == "trend");
        Assert.Throws<ArgumentException>(() => PresenterDeckComposer.Compose(gap, "monthly"));
        Assert.Throws<ArgumentException>(() => PresenterDeckValidator.WithNotes(deck,
            JsonSerializer.Serialize(new { slides = deck.Slides.Select(s => new { title = s.Title,
                speakerNotes = "A claim without a citation." }) }), issues));
        var citedTitle = PresenterDeckValidator.WithNotes(deck, JsonSerializer.Serialize(new
        {
            slides = deck.Slides.Select((slide, index) => new
            {
                title = index == 0 ? "Backup review [pit_high]" : slide.Title,
                speakerNotes = index == 0 ? "Review the item [pit_high]." : ""
            })
        }), issues);
        Assert.Equal("Backup review [pit_high]", citedTitle.Slides[0].Title);
        Assert.Throws<ArgumentException>(() => PresenterDeckValidator.WithNotes(deck, JsonSerializer.Serialize(new
        {
            slides = deck.Slides.Select((slide, index) => new
            {
                title = slide.Title,
                speakerNotes = index == 0 ? "There are 999 gaps [pit_high]." : ""
            })
        }), issues));
    }

    [Fact]
    public void Stakeholder_export_has_no_bodies_costs_or_host_names_and_pdf_has_one_page_per_slide()
    {
        var issue = Issue(new DateOnly(2026, 11, 9));
        var deck = PresenterDeckAudience.Stakeholder(PresenterDeckComposer.Compose([issue], "weekly"));
        var html = PresenterDeckExport.Html(deck);
        Assert.DoesNotContain("host-01", html);
        Assert.DoesNotContain("$42", html);
        Assert.DoesNotContain("Sensitive body", html);
        Assert.DoesNotContain("cost", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArrowRight", html);
        Assert.Contains("aspect-ratio:16/9", html);
        var pdf = Encoding.Latin1.GetString(PresenterDeckExport.Pdf(deck));
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Equal(deck.Slides.Count, System.Text.RegularExpressions.Regex.Matches(pdf, "/Type /Page /Parent").Count);
        Assert.DoesNotContain("host-01", pdf);
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { Length: > 0 } results)
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "presenter-stakeholder.html"), html);
            File.WriteAllBytes(Path.Combine(results, "presenter-stakeholder.pdf"), PresenterDeckExport.Pdf(deck));
        }
    }

    [Fact]
    public void Published_deck_json_is_immutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "presenter-deck-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var store = new PresenterDeckStore(configuration);
            var deck = PresenterDeckComposer.Compose([Issue(new DateOnly(2026, 11, 9))], "weekly");
            store.Publish(deck);
            var path = Path.Combine(root, ".metadata", "presenter-decks", "ws-demo", deck.Id, "deck.json");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("owner", store.Publish(deck with { Audience = "changed" }).Audience);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
