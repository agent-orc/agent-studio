using System.Text.Json;
using AgentStudio.Tags;
using AgentStudio.Areas;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoTaggingTests
{
    private static readonly HashSet<string> Areas = ["execution-and-runner", "gates-and-review"];
    private static readonly HashSet<string> Registry = ["execution-and-runner", "gates-and-review", "testing"];
    private static readonly TagMaintenanceItem Item = new("Agent Studio", "card", "gate-test",
        "Gate test", [], "Review the validation gate and its tests.", true);

    [Fact]
    public void ClosedRegistryRequiresAnAreaAndMatchingIdentity()
    {
        var good = new TagClassificationPrediction
        {
            Kind = "card", Id = "gate-test", Tags = ["gates-and-review", "testing"], Confidence = 0.9,
        };
        Assert.True(AutoTaggingPolicy.IsValid(good, Item, Registry, Areas));
        Assert.False(AutoTaggingPolicy.IsValid(good with { Tags = ["invented"] }, Item, Registry, Areas));
        Assert.False(AutoTaggingPolicy.IsValid(good with { Tags = ["testing"] }, Item, Registry, Areas));
        Assert.False(AutoTaggingPolicy.IsValid(good with { Tags = ["gates-and-review", "gates-and-review"] }, Item, Registry, Areas));
        Assert.False(AutoTaggingPolicy.IsValid(good with { Id = "other" }, Item, Registry, Areas));
        Assert.False(AutoTaggingPolicy.IsValid(good with { Confidence = 1.1 }, Item, Registry, Areas));
    }

    [Theory]
    [InlineData(0.79, "tags-proposed")]
    [InlineData(0.8, "tagged")]
    [InlineData(1.0, "tagged")]
    public void ThresholdSeparatesProposalsFromAppliedTags(double confidence, string expected) =>
        Assert.Equal(expected, AutoTaggingPolicy.Status(confidence));

    [Fact]
    public void CompletedCardsRemainBackfillEligibleWhileArchivedAndFixturesDoNot()
    {
        Assert.True(AutoTaggingPolicy.EligibleCard(new TaskInfo { State = TaskStates.Completed }));
        Assert.False(AutoTaggingPolicy.EligibleCard(new TaskInfo { State = TaskStates.Archive }));
        Assert.False(AutoTaggingPolicy.EligibleCard(new TaskInfo { State = TaskStates.Ready, Fixture = true }));
    }

    [Fact]
    public void ProposedGoldenSetHasRealLifecycleCoverageAndPerItemRationale()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root != null && !File.Exists(Path.Combine(root.FullName, "docs", "quality", "tagging-golden-set", "items.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "quality", "tagging-golden-set", "items.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var objectRoot = doc.RootElement;
        Assert.Equal("proposed", objectRoot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, objectRoot.GetProperty("approvedBy").ValueKind);
        var items = objectRoot.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(60, items.Count(i => i.GetProperty("kind").GetString() == "card"));
        Assert.Equal(20, items.Count(i => i.GetProperty("kind").GetString() == "dossier"));
        Assert.Contains(items, i => i.GetProperty("state").GetString() == "7-archive");
        Assert.Contains(items, i => i.GetProperty("state").GetString() == "6-completed");
        var areaIds = AreaTaxonomy.ProductDefaults.Select(area => area.Id).ToHashSet(StringComparer.Ordinal);
        var allowed = areaIds.Concat(AreaTaxonomy.QualityFacets.Select(facet => facet.Id))
            .Concat(AreaTaxonomy.DocumentFacets.Select(facet => facet.Id))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(items, i =>
        {
            var tags = i.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray();
            Assert.NotEmpty(tags);
            Assert.All(tags, tag => Assert.Contains(tag, allowed));
            Assert.Contains(tags, areaIds.Contains);
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("rationale").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("text").GetString()));
        });
        Assert.Equal(["execution-and-runner"], items[0].GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetString()).ToArray());
        Assert.Equal(["dossiers-and-documentation", "architecture"], items[30].GetProperty("tags")
            .EnumerateArray().Select(tag => tag.GetString()).ToArray());
        Assert.Equal(["dossiers-and-documentation", "decision"], items[79].GetProperty("tags")
            .EnumerateArray().Select(tag => tag.GetString()).ToArray());
    }

    [Fact]
    public void GoldenSetScoreCountsMultiLabelPrecisionAndRecall()
    {
        TagGoldenSetItem[] expected =
        [
            new() { Kind = "card", Id = "a", Tags = ["gates-and-review", "testing"] },
            new() { Kind = "dossier", Id = "b", Tags = ["dossiers-and-documentation"] },
        ];
        TagClassificationPrediction[] actual =
        [
            new() { Kind = "card", Id = "a", Tags = ["gates-and-review", "reliability"], Confidence = 0.9 },
            new() { Kind = "dossier", Id = "b", Tags = ["dossiers-and-documentation"], Confidence = 0.8 },
        ];
        var score = TagGoldenSetEvaluator.Score(1, "sonnet", "low", expected, actual);
        Assert.Equal(2.0 / 3.0, score.Precision);
        Assert.Equal(2.0 / 3.0, score.Recall);
        Assert.Equal(0.85, score.MeanConfidence, precision: 10);
    }
}
