using System.Text.Json;
using AgentStudio.Tags;
using AgentStudio.Areas;
using AgentStudio.Shared;
using AgentStudio.Pipeline;
using AgentStudio.Cli;
using AgentStudio.Prompts;
using AgentStudio.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class AutoTaggingTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Areas = ["execution-and-runner", "gates-and-review"];
    private static readonly HashSet<string> Registry = ["execution-and-runner", "gates-and-review", "testing"];
    private static readonly TagMaintenanceItem Item = new("Agent Studio", "card", "gate-test",
        "Gate test", [], "Review the validation gate and its tests.", true);

    private const string ValidReply = "{\"kind\":\"card\",\"id\":\"gate-test\",\"tags\":[\"gates-and-review\"],\"confidence\":0.9}";

    [Theory]
    [InlineData("```json\n" + ValidReply + "\n```")]
    [InlineData("The classification is:\n" + ValidReply + "\nThank you.")]
    public void ExtractsValidJsonFromModelWrapping(string reply)
    {
        var prediction = AutoTagCallPolicy.Parse(reply, Item, Registry, Areas);
        Assert.NotNull(prediction);
        Assert.Equal("gate-test", prediction.Id);
        Assert.Equal(["gates-and-review"], prediction.Tags);
    }

    [Theory]
    [InlineData("```json\n{\"kind\":\"card\"}\n```")]
    [InlineData("{\"kind\":\"card\",\"id\":\"gate-test\",\"tags\":[\"invented\"],\"confidence\":0.9}")]
    [InlineData("No classification available")]
    public void RejectsInvalidOrIncompleteReply(string reply) =>
        Assert.Null(AutoTagCallPolicy.Parse(reply, Item, Registry, Areas));

    [Fact]
    public void AutoTagRouteHonorsStepCliAndModelWithClaudeDefault()
    {
        var defaultRoute = AutoTagClassifier.ResolveRoute(null);
        Assert.Equal(CliTypes.Claude, defaultRoute.Cli);
        Assert.Equal(ModelIds.ClaudeHaiku55, defaultRoute.Model);
        Assert.Equal(defaultRoute, AutoTagClassifier.ResolveRoute(new ProjectSettings
        {
            OrchestratorModel = ModelFamilyResolver.Resolve(ModelFamilies.GptMini),
        }));
        var selected = AutoTagClassifier.ResolveRoute(new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.AutoTagStepId] = new()
                {
                    CliType = CliTypes.Codex,
                    Model = ModelFamilyResolver.Resolve(ModelFamilies.GptMini),
                },
            },
        });
        Assert.Equal(CliTypes.Codex, selected.Cli);
        Assert.Equal(ModelFamilyResolver.Resolve(ModelFamilies.GptMini), selected.Model);
    }

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
    public async Task GoldenSetReplayValidatesAnswersIndependentlyOfMeteredOutput()
    {
        var (golden, context, classifier) = CreateGoldenReplay(live: false);
        var replay = new List<AutoTagValidation>();
        foreach (var reference in golden.Items)
        {
            var item = new TagMaintenanceItem("Agent Studio", reference.Kind, reference.Id, reference.Title,
                [], reference.Text, true);
            replay.Add(await classifier.Prepare(item.Project, item, context, "low")
                .RunAsync(_ => { }, CancellationToken.None));
        }

        Assert.Equal(80, replay.Count);
        Assert.All(replay, validation =>
        {
            Assert.Equal(AutoTagOutcome.Accepted, validation.Outcome);
            Assert.Equal("tagged", AutoTaggingPolicy.Status(validation.Prediction!.Confidence));
            Assert.Equal(1757, validation.Usage?.OutputTokens);
            Assert.Equal(1500, validation.Usage?.ReasoningTokens);
        });
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task LiveGoldenSetReplayReportsQualityAndProviderUsage()
    {
        Skip.If(Environment.GetEnvironmentVariable("AGENT_STUDIO_LIVE_AUTO_TAG_REPLAY") != "1",
            "The live replay requires explicit opt-in and a signed-in Claude CLI.");
        var (golden, context, classifier) = CreateGoldenReplay(live: true);
        var attempts = new List<AutoTagValidation>();
        var predictions = new List<TagClassificationPrediction>();

        foreach (var reference in golden.Items)
        {
            var item = new TagMaintenanceItem("Agent Studio", reference.Kind, reference.Id, reference.Title,
                [], reference.Text, true);
            var validation = await classifier.Prepare(item.Project, item, context, "low")
                .RunAsync(_ => { }, CancellationToken.None);
            attempts.Add(validation);
            Assert.True(validation.Outcome is not (AutoTagOutcome.TransientFailure or AutoTagOutcome.CapacityDeferred),
                $"The live provider is unavailable at {item.Id}: {validation.Outcome}. Stop the replay and check Claude auth or capacity.");
            if (validation.Prediction is { Confidence: < AutoTaggingPolicy.ConfidenceThreshold })
            {
                validation = await classifier.Prepare(item.Project, item, context, "high")
                    .RunAsync(_ => { }, CancellationToken.None);
                attempts.Add(validation);
                Assert.True(validation.Outcome is not (AutoTagOutcome.TransientFailure or AutoTagOutcome.CapacityDeferred),
                    $"The live provider is unavailable at {item.Id}: {validation.Outcome}. Stop the replay and check Claude auth or capacity.");
            }
            if (validation.Prediction is { } prediction)
                predictions.Add(prediction);
        }

        var tagged = predictions.Count(p => AutoTaggingPolicy.Status(p.Confidence) == "tagged");
        var exact = predictions.Count(p => golden.Items.Single(i => i.Kind == p.Kind && i.Id == p.Id)
            .Tags.ToHashSet(StringComparer.Ordinal).SetEquals(p.Tags));
        var metered = attempts.Where(a => a.Usage != null).Select(a => a.Usage!).ToList();
        output.WriteLine($"Accepted {predictions.Count}/{golden.Items.Count}, tagged coverage {tagged}/{golden.Items.Count}, exact tag sets {exact}/{golden.Items.Count}.");
        output.WriteLine($"Usage recorded for {metered.Count}/{attempts.Count} attempts. Input {metered.Sum(u => (long)u.InputTokens)}, "
            + $"output {metered.Sum(u => (long)u.OutputTokens)}, cache read {metered.Sum(u => (long)u.CacheReadTokens)}, "
            + $"cache creation {metered.Sum(u => (long)u.CacheCreationTokens)}. Reasoning {metered.Sum(u => (long)(u.ReasoningTokens ?? 0))} "
            + $"across {metered.Count(u => u.ReasoningTokens != null)} reporting attempts.");
        foreach (var group in attempts.GroupBy(a => a.Outcome))
            output.WriteLine($"{group.Key}: {group.Count()} attempts.");
    }

    private static (TagGoldenSet Golden, TagMaintenanceSnapshot Context, AutoTagClassifier Classifier) CreateGoldenReplay(bool live)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root != null && !File.Exists(Path.Combine(root.FullName, "docs", "quality", "tagging-golden-set", "items.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        }).Build();
        var golden = JsonSerializer.Deserialize<TagGoldenSet>(
            File.ReadAllText(Path.Combine(root.FullName, "docs", "quality", "tagging-golden-set", "items.json")),
            TagMaintenancePolicy.Json)!;
        var tags = new TagRegistryService(NullLogger<TagRegistryService>.Instance, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var areas = new AreaRegistryService(tags, settings);
        var context = ProposedGlossaryContext.Apply("Agent Studio",
            new TagMaintenanceSnapshot(tags.GetAll().ToList(), areas.List("Agent Studio").Select(area => area.Id).ToList(),
                areas.List("Agent Studio").ToDictionary(area => area.Id, _ => new List<GlossaryTerm>()), []));
        ICliOneShot provider = live
            ? new ClaudeOneShot(config, NullLogger<ClaudeOneShot>.Instance,
                new CliUsageParserRegistry([new ClaudeUsageParser()]), new CliModelRegistry())
            : new GoldenReplyProvider(golden.Items);
        var classifier = new AutoTagClassifier(new CliOneShotRegistry([provider]),
            new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance), areas, settings);

        return (golden, context, classifier);
    }

    // The reference responses exercise the parser and accounting boundary, not model accuracy.
    private sealed class GoldenReplyProvider(IEnumerable<TagGoldenSetItem> items) : ICliOneShot
    {
        private readonly Queue<TagGoldenSetItem> _items = new(items);
        public string CliType => CliTypes.Claude;

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Assert.Equal(512, request.ClaudeMaxOutputTokens);
            var item = _items.Dequeue();
            Assert.Contains(item.Id, request.Prompt);
            var text = TagMaintenancePolicy.Encode(new TagClassificationPrediction
            {
                Kind = item.Kind, Id = item.Id, Tags = item.Tags, Confidence = 0.95,
            });
            var now = DateTime.UnixEpoch;
            return Task.FromResult(CliOneShotResult.SpawnFailure("", now, now) with
            {
                Ok = true, ExitCode = 0, Error = null, ParsedText = text,
                Usage = new() { InputTokens = 9, CacheReadTokens = 9373, OutputTokens = 1757, ReasoningTokens = 1500 },
            });
        }
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
