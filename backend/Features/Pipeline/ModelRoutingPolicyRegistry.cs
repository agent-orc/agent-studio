using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public sealed record ModelRoutingVendorOverride
{
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "medium";
}

public sealed record ModelRoutingTier
{
    public string Id { get; init; } = "";
    public int Rank { get; init; }
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "medium";
    public int EstimatedSavingsPercent { get; init; }
    public Dictionary<string, ModelRoutingVendorOverride> VendorOverrides { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModelRoutingTaskTypeDefault
{
    public string Tier { get; init; } = "";
    public string? HardFloorTier { get; init; }
    public string? EconomyFloorTier { get; init; }
    public int Score { get; init; }
}

public sealed record ModelRoutingEconomyMode
{
    public int DowngradeSteps { get; init; } = 1;
    public string Label { get; init; } = "Economy mode";
}

public sealed record ProviderRejectionModelFallback
{
    public string CliType { get; init; } = "";
    public string FromModel { get; init; } = "";
    public string ToModel { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed record ModelRoutingPolicyDocument
{
    public string Version { get; init; } = "";
    public string WikiPath { get; init; } = "";
    public List<ModelRoutingTier> Tiers { get; init; } = [];
    public Dictionary<string, ModelRoutingTaskTypeDefault> TaskTypeDefaults { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<ProviderRejectionModelFallback> ProviderRejectionFallbacks { get; init; } = [];
    public ModelRoutingEconomyMode EconomyMode { get; init; } = new();
}

public sealed record ModelRoutingRecommendation
{
    public string PolicyVersion { get; init; } = "";
    public string PolicyWikiPath { get; init; } = "";
    public string TaskType { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Model { get; init; } = "";
    public string? ThinkingLevel { get; init; }
    public int Score { get; init; }
    public bool EconomyMode { get; init; }
    public bool EconomyDowngraded { get; init; }
    public string? CorrectnessFloorTier { get; init; }
    public string Reason { get; init; } = "";
    public int EstimatedSavingsPercent { get; init; }
}

/// <summary>
/// Repository-versioned routing rules. The JSON resource is the machine view;
/// Policy.WikiPath identifies the human-readable view of the same contract.
/// No appsettings value can change tiers, defaults, or correctness floors.
/// </summary>
public sealed class ModelRoutingPolicyRegistry
{
    private const string ResourceSuffix = "Policies.model-routing-policy.v1.json";

    private static readonly Regex CriticalFloorSignal = new(
        @"\b(P0|fencing|lease (ownership|authorit)|stale[- ]write|distributed (authority|concurr)|security|data[- ]loss|destructive migration|concurrent state machine)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SolFloorSignal = new(
        @"\b(public protocol|persistent[- ]state migration|schema migration)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RuntimeSubsystemSignal = new(
        @"\b(frontend|backend|runner|orchestrator|cli|database|storage)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ModelRoutingPolicyDocument Policy { get; }

    public ModelRoutingPolicyRegistry()
        : this(ReadEmbeddedPolicy())
    {
    }

    internal ModelRoutingPolicyRegistry(ModelRoutingPolicyDocument policy)
    {
        Validate(policy);
        Policy = policy with
        {
            TaskTypeDefaults = new Dictionary<string, ModelRoutingTaskTypeDefault>(
                policy.TaskTypeDefaults, StringComparer.OrdinalIgnoreCase),
        };
    }

    public ModelRoutingRecommendation Recommend(
        string? taskType,
        CliModelCatalog catalogue,
        bool economyMode,
        string? title = null,
        string? prompt = null)
    {
        var normalizedType = TaskTypes.Normalize(taskType);
        var typeDefault = Policy.TaskTypeDefaults[normalizedType];
        var requested = Tier(typeDefault.Tier);
        var score = typeDefault.Score;
        var correctnessFloor = string.IsNullOrWhiteSpace(typeDefault.HardFloorTier)
            ? null
            : Tier(typeDefault.HardFloorTier);
        var economyFloor = string.IsNullOrWhiteSpace(typeDefault.EconomyFloorTier)
            ? null
            : Tier(typeDefault.EconomyFloorTier);
        var text = $"{title}\n{prompt}";

        if (CriticalFloorSignal.IsMatch(text))
        {
            requested = Tier("sol-xhigh");
            correctnessFloor = requested;
            score = Math.Max(score, 70);
        }
        else
        {
            var subsystemCount = RuntimeSubsystemSignal.Matches(text)
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (SolFloorSignal.IsMatch(text) || subsystemCount >= 3)
            {
                requested = Stronger(requested, Tier("sol-medium"));
                correctnessFloor = Stronger(correctnessFloor, Tier("sol-medium"));
                score = Math.Max(score, 51);
            }
        }

        var selectedTier = requested;
        var downgraded = false;
        if (economyMode && Policy.EconomyMode.DowngradeSteps > 0)
        {
            var candidateRank = Math.Max(0, requested.Rank - Policy.EconomyMode.DowngradeSteps);
            var candidate = Policy.Tiers
                .Where(tier => tier.Rank <= candidateRank)
                .OrderByDescending(tier => tier.Rank)
                .First();
            var floorRank = Math.Max(correctnessFloor?.Rank ?? 0, economyFloor?.Rank ?? 0);
            if (candidate.Rank >= floorRank && candidate.Rank < requested.Rank)
            {
                selectedTier = candidate;
                downgraded = true;
            }
        }

        var (model, thinkingLevel) = ResolveCatalogueRoute(selectedTier, catalogue);
        var floorReason = correctnessFloor == null
            ? ""
            : $"; correctness floor {correctnessFloor.Id}";
        var economyReason = economyMode
            ? downgraded
                ? $"; economy mode lowered {requested.Id} to {selectedTier.Id}"
                : "; economy mode retained the policy floor"
            : "";
        return new ModelRoutingRecommendation
        {
            PolicyVersion = Policy.Version,
            PolicyWikiPath = Policy.WikiPath,
            TaskType = normalizedType,
            Tier = selectedTier.Id,
            Model = model,
            ThinkingLevel = thinkingLevel,
            Score = score,
            EconomyMode = economyMode,
            EconomyDowngraded = downgraded,
            CorrectnessFloorTier = correctnessFloor?.Id,
            EstimatedSavingsPercent = selectedTier.EstimatedSavingsPercent,
            Reason = $"Policy {Policy.Version}: {normalizedType} defaults to {requested.Id}{floorReason}{economyReason}; selected {model} at {thinkingLevel ?? "model default"}",
        };
    }

    /// <summary>The explicitly declared same-provider sibling for a request refusal.</summary>
    public ProviderRejectionModelFallback? ProviderRejectionFallback(
        string? cliType,
        string? model)
        => Policy.ProviderRejectionFallbacks.FirstOrDefault(candidate =>
            string.Equals(candidate.CliType, CliTypes.Normalize(cliType), StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.FromModel, model?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes only the hard correctness floor. It deliberately ignores live
    /// model availability and economy state, so a recovery decision cannot
    /// weaken because a catalogue probe failed after the original run.
    /// </summary>
    public ModelRoutingTier? CorrectnessFloor(
        string? taskType,
        string? title,
        string? prompt)
    {
        var normalizedType = TaskTypes.Normalize(taskType);
        var typeDefault = Policy.TaskTypeDefaults[normalizedType];
        var floor = string.IsNullOrWhiteSpace(typeDefault.HardFloorTier)
            ? null
            : Tier(typeDefault.HardFloorTier);
        var text = $"{title}\n{prompt}";
        if (CriticalFloorSignal.IsMatch(text)) return Tier("sol-xhigh");

        var subsystemCount = RuntimeSubsystemSignal.Matches(text)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return SolFloorSignal.IsMatch(text) || subsystemCount >= 3
            ? Stronger(floor, Tier("sol-medium"))
            : floor;
    }

    /// <summary>
    /// Whether a concrete sibling route clears the named policy floor. Older
    /// generations of the same Claude family inherit the current generation's
    /// tier; this is a provider sibling substitution, not an economy downgrade.
    /// </summary>
    public bool RouteMeetsFloor(string model, string? thinkingLevel, ModelRoutingTier? floor)
    {
        if (floor is null) return true;
        var normalizedModel = ModelMetadataRegistry.NormalizeId(model);
        var normalizedThinking = thinkingLevel?.Trim().ToLowerInvariant();
        var rank = Policy.Tiers
            .Where(tier => RouteMatchesTier(tier, normalizedModel, normalizedThinking))
            .Select(tier => (int?)tier.Rank)
            .Max();
        return rank is not null && rank.Value >= floor.Rank;
    }

    private static bool RouteMatchesTier(
        ModelRoutingTier tier,
        string? model,
        string? thinkingLevel)
    {
        static string FamilyEquivalent(string? value) => value switch
        {
            ModelIds.ClaudeOpus48 or ModelIds.ClaudeOpus47 or ModelIds.ClaudeOpus46 or ModelIds.ClaudeOpus45
                => ModelIds.ClaudeOpus5,
            ModelIds.ClaudeSonnet46 or ModelIds.ClaudeSonnet45 => ModelIds.ClaudeSonnet5,
            _ => value ?? string.Empty,
        };

        var route = tier.VendorOverrides.Values
            .Append(new ModelRoutingVendorOverride { Model = tier.Model, ThinkingLevel = tier.ThinkingLevel });
        return route.Any(candidate =>
            string.Equals(FamilyEquivalent(ModelMetadataRegistry.NormalizeId(candidate.Model)), FamilyEquivalent(model), StringComparison.OrdinalIgnoreCase)
            && ThinkingAtLeast(thinkingLevel, candidate.ThinkingLevel));
    }

    private static bool ThinkingAtLeast(string? actual, string required)
    {
        string[] order = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
        var actualRank = Array.FindIndex(order, value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase));
        var requiredRank = Array.FindIndex(order, value => string.Equals(value, required, StringComparison.OrdinalIgnoreCase));
        return actualRank >= 0 && requiredRank >= 0 && actualRank >= requiredRank;
    }

    private static ModelRoutingTier Stronger(ModelRoutingTier? left, ModelRoutingTier right)
        => left == null || right.Rank > left.Rank ? right : left;

    private ModelRoutingTier Tier(string id)
        => Policy.Tiers.First(tier => string.Equals(tier.Id, id, StringComparison.OrdinalIgnoreCase));

    private static (string Model, string? ThinkingLevel) ResolveCatalogueRoute(
        ModelRoutingTier tier,
        CliModelCatalog catalogue)
    {
        var available = catalogue.Models
            .Where(model => model.Available && !model.Deprecated)
            .ToList();
        if (available.Count == 0)
            throw new InvalidOperationException("The CLI reported no available models.");

        var vendor = available.Select(model => model.Vendor).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var overrideRoute = vendor != null && tier.VendorOverrides.TryGetValue(vendor, out var vendorOverride)
            ? vendorOverride
            : null;
        var targetModelId = overrideRoute?.Model ?? tier.Model;
        var targetThinkingLevel = overrideRoute?.ThinkingLevel ?? tier.ThinkingLevel;

        var selected = available.FirstOrDefault(model =>
            string.Equals(model.Id, targetModelId, StringComparison.OrdinalIgnoreCase));
        if (selected == null && overrideRoute == null)
        {
            // Unknown/future CLI vendor with no exact model or vendor
            // override: fall back to a positional pick from the ranked
            // catalogue as a last resort.
            var index = tier.Rank switch
            {
                >= 2 => 0,
                1 => Math.Min(available.Count - 1, available.Count / 2),
                _ => available.Count - 1,
            };
            selected = available[index];
        }
        selected ??= available[0];

        var levels = selected.ThinkingLevels ?? [];
        var thinking = levels.FirstOrDefault(level =>
            string.Equals(level, targetThinkingLevel, StringComparison.OrdinalIgnoreCase))
            ?? selected.DefaultThinkingLevel
            ?? levels.FirstOrDefault()
            ?? targetThinkingLevel;
        return (selected.Id, thinking);
    }

    private static ModelRoutingPolicyDocument ReadEmbeddedPolicy()
    {
        var assembly = typeof(ModelRoutingPolicyRegistry).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resource == null)
            throw new InvalidOperationException($"Embedded routing policy '{ResourceSuffix}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded routing policy '{resource}' could not be opened.");
        return JsonSerializer.Deserialize<ModelRoutingPolicyDocument>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("The embedded routing policy is empty.");
    }

    private static void Validate(ModelRoutingPolicyDocument policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Version))
            throw new InvalidOperationException("Routing policy version is required.");
        if (string.IsNullOrWhiteSpace(policy.WikiPath))
            throw new InvalidOperationException("Routing policy wikiPath is required.");
        if (policy.Tiers.Count == 0 || policy.Tiers.Select(tier => tier.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Tiers.Count)
            throw new InvalidOperationException("Routing policy tiers must be non-empty and uniquely named.");
        foreach (var taskType in TaskTypes.All)
        {
            if (!policy.TaskTypeDefaults.TryGetValue(taskType, out var route))
                throw new InvalidOperationException($"Routing policy has no default for task type '{taskType}'.");
            if (!policy.Tiers.Any(tier => string.Equals(tier.Id, route.Tier, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Routing policy task type '{taskType}' references unknown tier '{route.Tier}'.");
            if (!string.IsNullOrWhiteSpace(route.HardFloorTier)
                && !policy.Tiers.Any(tier => string.Equals(tier.Id, route.HardFloorTier, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Routing policy task type '{taskType}' references unknown floor '{route.HardFloorTier}'.");
            if (!string.IsNullOrWhiteSpace(route.EconomyFloorTier)
                && !policy.Tiers.Any(tier => string.Equals(tier.Id, route.EconomyFloorTier, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Routing policy task type '{taskType}' references unknown economy floor '{route.EconomyFloorTier}'.");
        }
        foreach (var tier in policy.Tiers)
        {
            foreach (var (vendor, route) in tier.VendorOverrides)
            {
                if (string.IsNullOrWhiteSpace(route.Model))
                    throw new InvalidOperationException($"Routing policy tier '{tier.Id}' has an empty vendor override model for '{vendor}'.");
            }
        }
        foreach (var fallback in policy.ProviderRejectionFallbacks)
        {
            if (!CliTypes.IsValid(fallback.CliType)
                || string.IsNullOrWhiteSpace(fallback.FromModel)
                || string.IsNullOrWhiteSpace(fallback.ToModel)
                || string.Equals(fallback.FromModel, fallback.ToModel, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Provider-rejection fallbacks require a valid CLI and distinct source and sibling models.");
        }
    }
}

public interface IModelRoutingModeProvider
{
    bool EconomyMode { get; }
}
