using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Personas;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WeeklyIssueTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly DateTime At = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
    private static PersonaItemV1 Item(string id, string persona, string role, int importance, string kind = "finding") =>
        new(id, persona, role, kind, "Story " + id, "Fact body", null, [], 5, 5, 1, 0, importance,
            "fact:" + id, kind == "proposal" ? "draft" : "open", At, At, []);
    private static readonly IssuePersona[] Personas =
    [
        new("prs_design_a", "Ada", "design"), new("prs_design_b", "Bela", "design"),
        new("prs_ops", "Ora", "operations"),
    ];
    private static readonly PersonaItemV1[] Items =
    [
        Item("pit_1", "prs_design_a", "design", 98),
        Item("pit_2", "prs_design_b", "design", 92),
        Item("pit_3", "prs_ops", "operations", 82),
        Item("pit_4", "prs_design_a", "design", 61, "proposal"),
        Item("pit_5", "prs_ops", "operations", 50),
    ];

    private static WeeklyIssue Compose(IReadOnlyList<PersonaItemV1>? items = null) =>
        WeeklyIssueComposer.Compose("ws-demo", Monday, false, items ?? Items, [], Personas, null, TimeZoneInfo.Utc);

    [Fact]
    public void Previous_calendar_month_starts_on_the_first_day_even_if_the_issue_is_later()
    {
        Assert.Equal(new DateOnly(2026, 10, 1), WeeklyIssueService.PreviousCalendarMonth(new DateOnly(2026, 11, 2)));
        Assert.Equal(new DateOnly(2026, 12, 1), WeeklyIssueService.PreviousCalendarMonth(new DateOnly(2027, 1, 4)));
    }

    [Fact]
    public void Reordered_source_items_produce_byte_identical_layout_and_reconciled_counts()
    {
        // clock-independent: Fixed issue dates are compared only with fixed workspace times.
        var first = Compose();
        var again = Compose(Items.Reverse().ToArray());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again));
        Assert.Equal(new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc), first.PublishedAtUtc);
        var workspaceZone = TimeZoneInfo.CreateCustomTimeZone("workspace-plus-one", TimeSpan.FromHours(1),
            "Workspace plus one", "Workspace plus one");
        Assert.Equal(new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc),
            WeeklyIssueComposer.Compose("ws-demo", Monday, false, Items, [], Personas, null, workspaceZone).PublishedAtUtc);
        Assert.Equal(first.StrapTotal, first.Sections.Sum(s => s.Count));
        Assert.Equal(5, first.StrapTotal);
        Assert.Equal(2, first.Sections.Count);
        Assert.Equal("design", first.Sections[0].RoleId);
        Assert.Equal("Ada", first.Lead!.Persona);
        Assert.Contains(first.SideStories, s => s.Id == "pit_2");
        Assert.Equal(15, first.OperationsWatch.Count);
    }

    [Fact]
    public void Editor_rejects_any_sentence_without_a_known_item_citation()
    {
        var issue = Compose();
        var invalid = "{\"leadParagraph\":\"A fact [pit_1]. Another claim.\",\"teasers\":{}}";
        Assert.Throws<ArgumentException>(() => WeeklyIssueComposer.WithEditor(issue, invalid));
        var accepted = WeeklyIssueComposer.WithEditor(issue,
            "{\"leadParagraph\":\"A fact [pit_1].\",\"teasers\":{\"pit_2\":\"Another fact [pit_2].\"},\"sectionTeasers\":{\"design\":\"Design news [pit_1].\"}}");
        Assert.Equal("A fact [pit_1].", accepted.LeadParagraph);
        Assert.Equal("Design news [pit_1].", accepted.Sections.Single(s => s.RoleId == "design").Teaser);
        Assert.False(accepted.EditorSkipped);
    }

    [Fact]
    public void Export_is_self_contained_theme_aware_printable_and_escapes_item_text()
    {
        var issue = Compose([Items[0] with { Title = "<script>bad</script>" }, .. Items.Skip(1)]);
        var html = WeeklyIssueHtml.Render(issue);
        Assert.Contains("data-article-template=\"v2\"", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.Contains("@media print", html);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>bad</script>", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script", html);
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { Length: > 0 } results)
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "weekly-rendered.html"), html);
        }
    }

    [Fact]
    public void Monthly_review_export_keeps_persona_role_and_owner_decision_visible()
    {
        var value = new PersonaValueV1(90, .2, 80, .1, 20, 1, 5, 2, 1,
            [new PersonaMissV1("Missed incident", "card", "INC-1", "#/tasks/INC-1", 90, At, 1)],
            [], [new PersonaSuggestionV1("focus", "Review scope or focus.")], false);
        var issue = Compose() with { ReviewMonth = "2026-10", MonthlyReview =
            [new IssueMonthlyReview("prs_design_a", "Ada", "design", value),
             new IssueMonthlyReview("prs_design_b", "Bela", "design", value)] };

        var html = WeeklyIssueHtml.Render(issue);

        Assert.Contains("Monthly team review · 2026-10", html);
        Assert.Contains("Ada · design", html);
        Assert.Contains("Bela · design", html);
        Assert.Contains("href=\"#/tasks/INC-1\"", html);
        Assert.Contains("Suggestions require an Owner decision.", html);
    }

    [Fact]
    public void Send_summary_contains_titles_and_numbers_only()
    {
        var summary = WeeklyIssueHtml.SendSummary(Compose(), "https://studio.example.test/sign-in");
        Assert.Contains("98 · Story pit_1", summary);
        Assert.DoesNotContain("Fact body", summary);
        Assert.DoesNotContain("Ada", summary);
    }

    [Fact]
    public void Expired_numeric_tls_fact_keeps_its_critical_floor_without_a_model_item()
    {
        // clock-independent: This fixed issue and fact timestamp never read the wall clock.
        var published = new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc);
        var snapshot = new OpsPostureFactsV1(published,
            [new OpsPostureFactV1("security.tls", "installation", "0", published,
                "edge-certificate", TimeSpan.FromHours(26))]);
        var issue = WeeklyIssueComposer.Compose("ws-demo", Monday, false, Items, [], Personas, snapshot,
            TimeZoneInfo.Utc);
        Assert.Equal(95, issue.OperationsWatch.Single(row => row.Id == "security.tls").Importance);
    }

    [Fact]
    public void Published_issue_is_immutable_even_when_a_later_composition_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "weekly-issue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var store = new WeeklyIssueStore(configuration);
            var first = store.Publish(Compose());
            var path = Path.Combine(root, ".metadata", "weekly-issues", "ws-demo", first.Id + ".json");
            var bytes = File.ReadAllBytes(path);
            var returned = store.Publish(first with { Masthead = "Changed later" });
            Assert.Equal(first.Masthead, returned.Masthead);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            var brief = WeeklyIssueComposer.Compose("ws-demo", Monday.AddDays(1), true, Items, [], Personas,
                null, TimeZoneInfo.Utc);
            store.Publish(brief);
            Assert.Equal(brief.Id, store.List("ws-demo")[0].Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
