using System.Text.Json;
using AgentStudio.Tags;
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
        Assert.All(items, i =>
        {
            Assert.NotEmpty(i.GetProperty("tags").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("rationale").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("text").GetString()));
        });
    }
}
