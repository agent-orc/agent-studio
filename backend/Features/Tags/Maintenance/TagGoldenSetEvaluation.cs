using System.Text.Json;
using AgentStudio.Prompts;

namespace AgentStudio.Tags;

public sealed record TagGoldenSetItem
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string[] Tags { get; init; } = [];
}

public sealed record TagGoldenSet
{
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset? ApprovedAt { get; init; }
    public List<TagGoldenSetItem> Items { get; init; } = [];
}

public sealed record TagClassificationPrediction
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public double Confidence { get; init; }
}

public sealed record TagTierMetrics
{
    public int Tier { get; init; }
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "";
    public int Items { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double MeanConfidence { get; init; }
}

public sealed record TagGoldenSetReport
{
    public string Status { get; init; } = "not-available";
    public string? Path { get; init; }
    public string? Message { get; init; }
    public int CardCount { get; init; }
    public int DossierCount { get; init; }
    public int SelectedTier { get; init; } = 1;
    public List<TagTierMetrics> Tiers { get; init; } = [];
}

public interface ITagGoldenSetClassifier
{
    string Model { get; }
    Task<IReadOnlyList<TagClassificationPrediction>> ClassifyAsync(string project,
        IReadOnlyList<TagGoldenSetItem> items, TagMaintenanceSnapshot context,
        string thinkingLevel, CancellationToken ct);
}

public sealed class TagGoldenSetClassifier(CliOneShotRegistry oneShots, RuntimePromptService prompts)
    : ITagGoldenSetClassifier
{
    public const string PromptTemplate = "tag-classification-golden-evaluation.md";
    public string Model => ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet);

    public async Task<IReadOnlyList<TagClassificationPrediction>> ClassifyAsync(string project,
        IReadOnlyList<TagGoldenSetItem> items, TagMaintenanceSnapshot context,
        string thinkingLevel, CancellationToken ct)
    {
        var input = TagMaintenancePolicy.Encode(new
        {
            registry = context.Registry,
            context.AreaIds,
            context.Glossaries,
            items = items.Select(item => new { item.Kind, item.Id, item.Title, item.Text }),
        });
        var prompt = prompts.Render(PromptTemplate, new Dictionary<string, string?> { ["input"] = input },
            new PromptCallContext(project, "tag-classification-golden-evaluation", Model));
        if (prompt.Length > 500_000)
            throw new InvalidOperationException("Golden-set context exceeds the bounded classification budget.");
        var cli = oneShots.Get(CliTypes.Claude)
            ?? throw new InvalidOperationException("Sonnet classification CLI unavailable.");
        var result = await cli.RunAsync(new(CliTypes.Claude, Model, prompt)
        {
            ThinkingLevel = thinkingLevel,
            Timeout = TimeSpan.FromMinutes(5),
            Project = project,
            Source = "tag-classification-evaluation",
            StepId = "tag-classification-golden-evaluation",
        }, ct);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Tag classification evaluation failed.");
        return JsonSerializer.Deserialize<List<TagClassificationPrediction>>(result.ParsedText, TagMaintenancePolicy.Json)
            ?? throw new InvalidOperationException("Tag classification evaluation returned no prediction array.");
    }
}

public sealed class TagGoldenSetEvaluator(ITagGoldenSetClassifier classifier, IConfiguration configuration)
{
    public async Task<TagGoldenSetReport> EvaluateAsync(string project, TagMaintenanceSnapshot context,
        CancellationToken ct)
    {
        var path = ResolvePath(project);
        if (!File.Exists(path))
            return new() { Path = path, Message = "No operator-approved golden set is available; no precision or recall is claimed." };
        try
        {
            var golden = JsonSerializer.Deserialize<TagGoldenSet>(await File.ReadAllTextAsync(path, ct), TagMaintenancePolicy.Json)
                ?? throw new InvalidOperationException("Golden-set file is empty.");
            var cards = golden.Items.Count(item => item.Kind == "card");
            var dossiers = golden.Items.Count(item => item.Kind == "dossier");
            var allowedTags = context.Registry.Select(tag => tag.Id).Concat(context.AreaIds)
                .ToHashSet(StringComparer.Ordinal);
            Validate(golden, cards, dossiers, allowedTags);
            var tier1Predictions = await classifier.ClassifyAsync(project, golden.Items, context, "low", ct);
            ValidatePredictions(tier1Predictions, allowedTags);
            var tier1 = Score(1, classifier.Model, "low", golden.Items, tier1Predictions);
            var tiers = new List<TagTierMetrics> { tier1 };
            var selectedTier = 1;
            if (tier1.Precision < 0.9)
            {
                var tier2Predictions = await classifier.ClassifyAsync(project, golden.Items, context, "high", ct);
                ValidatePredictions(tier2Predictions, allowedTags);
                tiers.Add(Score(2, classifier.Model, "high", golden.Items, tier2Predictions));
                selectedTier = 2;
            }
            return new()
            {
                Status = "evaluated", Path = path, CardCount = cards, DossierCount = dossiers,
                SelectedTier = selectedTier, Tiers = tiers,
                Message = selectedTier == 2
                    ? "Tier 1 precision fell below 0.9; classification was automatically routed to tier 2."
                    : "Tier 1 precision met the 0.9 floor.",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new() { Status = "invalid", Path = path, Message = ex.Message };
        }
    }

    internal static TagTierMetrics Score(int tier, string model, string thinkingLevel,
        IReadOnlyList<TagGoldenSetItem> expected, IReadOnlyList<TagClassificationPrediction> actual)
    {
        var expectedKeys = expected.Select(item => (item.Kind, item.Id)).ToHashSet();
        if (actual.Count != expected.Count || actual.Select(item => (item.Kind, item.Id)).Distinct().Count() != actual.Count
            || actual.Any(item => !expectedKeys.Contains((item.Kind, item.Id)))
            || actual.Any(item => item.Confidence is < 0 or > 1))
            throw new InvalidOperationException("Classifier predictions must cover each golden-set item exactly once with confidence in [0,1].");
        var predictions = actual.ToDictionary(item => (item.Kind, item.Id));
        var truePositive = 0;
        var falsePositive = 0;
        var falseNegative = 0;
        foreach (var item in expected)
        {
            var wanted = item.Tags.ToHashSet(StringComparer.Ordinal);
            var found = predictions[(item.Kind, item.Id)].Tags.ToHashSet(StringComparer.Ordinal);
            truePositive += wanted.Intersect(found).Count();
            falsePositive += found.Except(wanted).Count();
            falseNegative += wanted.Except(found).Count();
        }
        return new()
        {
            Tier = tier, Model = model, ThinkingLevel = thinkingLevel, Items = expected.Count,
            Precision = truePositive + falsePositive == 0 ? 0 : (double)truePositive / (truePositive + falsePositive),
            Recall = truePositive + falseNegative == 0 ? 0 : (double)truePositive / (truePositive + falseNegative),
            MeanConfidence = actual.Count == 0 ? 0 : actual.Average(item => item.Confidence),
        };
    }

    private string ResolvePath(string project)
    {
        var configured = configuration["TagMaintenance:GoldenSetPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Replace("{project}", project, StringComparison.Ordinal));
        var root = configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required for golden-set evaluation.");
        return Path.Combine(root, "tag-golden-sets", TagMaintenancePolicy.Fingerprint(project) + ".json");
    }

    private static void Validate(TagGoldenSet golden, int cards, int dossiers, HashSet<string> allowedTags)
    {
        if (string.IsNullOrWhiteSpace(golden.ApprovedBy) || golden.ApprovedAt == null)
            throw new InvalidOperationException("Golden set lacks operator approval metadata; no metrics were calculated.");
        if (cards < 60 || dossiers < 20)
            throw new InvalidOperationException("Golden set must contain at least 60 cards and 20 Dossiers; no metrics were calculated.");
        if (golden.Items.Any(item => item.Kind is not ("card" or "dossier")
                || string.IsNullOrWhiteSpace(item.Id) || item.Tags.Length == 0
                || item.Tags.Any(tag => !allowedTags.Contains(tag)))
            || golden.Items.Select(item => (item.Kind, item.Id)).Distinct().Count() != golden.Items.Count)
            throw new InvalidOperationException("Golden set contains invalid or duplicate items; no metrics were calculated.");
    }

    private static void ValidatePredictions(IReadOnlyList<TagClassificationPrediction> predictions,
        HashSet<string> allowedTags)
    {
        if (predictions.Any(item => item.Tags.Any(tag => !allowedTags.Contains(tag))))
            throw new InvalidOperationException("Classifier returned a tag outside the closed registry; no metrics were calculated.");
    }
}
