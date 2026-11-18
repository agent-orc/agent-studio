using AgentStudio.Deploy;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure projections over fixed release records.
public sealed class ReleaseNotesAndQualityProjectionTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static Release ReleaseOf(params string[] keys)
        => new("r1", "demo", "2026.11.16.1", new string('1', 40), [], "process", "variables", keys, T0, "default");

    [Fact]
    public void Notes_prefer_the_edited_text_and_list_maintenance_cards_last()
    {
        var notes = ReleaseNotesProjection.Project(ReleaseOf("SHOP-2", "SHOP-1", "SHOP-3"),
            new Dictionary<string, string> { ["SHOP-1"] = "Add basket", ["SHOP-2"] = "Let releases use the pre-main gate",
                ["SHOP-3"] = "Show prices" },
            edits: new Dictionary<string, string> { ["SHOP-1"] = "  Customers can keep items in a basket. " },
            maintenance: new HashSet<string>(["SHOP-2"]));

        Assert.Equal(
            [
                new ReleaseNote("SHOP-1", "Customers can keep items in a basket.", Maintenance: false, Edited: true),
                new ReleaseNote("SHOP-3", "Show prices"),
                new ReleaseNote("SHOP-2", "Let releases use the pre-main gate", Maintenance: true),
            ],
            notes.Notes);
    }

    [Fact]
    public void Card_quality_falls_back_to_the_card_review_and_says_why_it_is_missing()
    {
        var tasks = new[] { "SHOP-1", "SHOP-2" }.Select(key => new TaskInfo
        {
            Key = key, TaskKey = key, State = "5-human-review",
            FolderPath = Path.Combine(Path.GetTempPath(), "release-quality-absent-" + key),
        }).ToArray();
        var verdicts = new Dictionary<string, ReleaseQualityProjection.ReviewVerdict?>
        {
            ["SHOP-1"] = new("pass"),
            ["SHOP-2"] = null,
        };

        var quality = ReleaseQualityProjection.Project([ReleaseOf("SHOP-1", "SHOP-2", "SHOP-9")], tasks, null,
            (_, _) => null, reviewVerdict: task => verdicts[task.Key!]).Single().Quality!;

        Assert.Equal((1, 2), (quality.Pass, quality.NotReviewed));
        Assert.Equal(new ReleaseQualityCard("SHOP-1", "pass", "review"), quality.Cards[0]);
        Assert.Equal("The card has no recorded review.", quality.Cards[1].Reason);
        Assert.Equal("The card is not on this project's board.", quality.Cards[2].Reason);
        Assert.Contains("no Quality Studio repository is linked", quality.RangeUnavailableReason);
    }

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("ProductFailure", "block")]
    [InlineData("IntegrationBranchDefect", "concerns")]
    [InlineData("InfrastructureFailure", null)]
    [InlineData(null, null)]
    public void Review_outcomes_map_to_release_verdicts(string? outcome, string? verdict)
    {
        var mapped = ReleaseQualityProjection.VerdictForOutcome(outcome);
        Assert.Equal(verdict, mapped.Verdict);
        Assert.Equal(verdict is null, mapped.Reason is not null);
    }
}
