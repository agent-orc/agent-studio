using System.Reflection;
using TokenEconomy;

namespace AgentStudio.Cli;

/// <summary>One route projected from Token Economy's routing and price catalogues.</summary>
public sealed record ModelEquivalenceRoute(
    string FromCliType,
    string FromModel,
    string? FromThinkingLevel,
    string ToCliType,
    string ToModel,
    string? ToThinkingLevel,
    string CatalogueVersion,
    decimal? FromInputPerMTok,
    decimal? FromOutputPerMTok,
    decimal? ToInputPerMTok,
    decimal? ToOutputPerMTok,
    string CapabilityClass);

public interface IModelEquivalenceCatalog
{
    string Version { get; }
    (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType);
    ModelEquivalenceRoute? TryGetRoute(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType);
    IReadOnlyList<ModelEquivalenceRoute> Routes { get; }
}

/// <summary>
/// Adapter over the embedded, versioned Token Economy artefacts. Admission
/// performs no network call.
/// </summary>
public sealed class ModelEquivalenceCatalog : IModelEquivalenceCatalog
{
    private static readonly ModelRoutingKnowledgeBase Knowledge = ModelRoutingKnowledgeBase.Default;
    private static readonly ModelPriceCatalog Prices = ModelPriceCatalog.Default;
    public string Version { get; } = BuildVersion();
    public IReadOnlyList<ModelEquivalenceRoute> Routes { get; }

    public ModelEquivalenceCatalog() => Routes = BuildRoutes();

    public (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType)
    {
        var route = TryGetRoute(fromCliType, fromModel, fromThinkingLevel, toCliType);
        return route is null ? null : (route.ToModel, route.ToThinkingLevel);
    }

    public ModelEquivalenceRoute? TryGetRoute(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType)
    {
        var fromCli = NormalizeCli(fromCliType);
        var toCli = NormalizeCli(toCliType);
        var requestedThinking = Clean(fromThinkingLevel);
        var model = string.IsNullOrWhiteSpace(fromModel)
            ? DefaultModelFor(fromCli)
            : Knowledge.FindModel(fromModel.Trim())?.CanonicalId ?? fromModel.Trim();

        return Routes.FirstOrDefault(route =>
                string.Equals(route.FromCliType, fromCli, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.FromModel, model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.FromThinkingLevel, requestedThinking, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.ToCliType, toCli, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ModelEquivalenceRoute> BuildRoutes()
    {
        var rows = new List<ModelEquivalenceRoute>();

        foreach (var source in Knowledge.Models.Where(model =>
                     NormalizeCli(model.CliId) == CliTypes.Claude && IsUsableSource(model)))
        {
            var target = CheapestSelectable(source.CapabilityTier, CliTypes.Codex);
            if (target is null) continue;

            rows.Add(Create(source, null, target, DefaultThinkingFor(target.CanonicalId)));
            foreach (var thinkingLevel in source.SupportedThinkingLevels.Where(level =>
                         target.SupportedThinkingLevels.Contains(level, StringComparer.OrdinalIgnoreCase)))
                rows.Add(Create(source, thinkingLevel, target, thinkingLevel));
        }

        // The reverse direction is limited to Token Economy's explicitly
        // evidence-scoped providerFallbacks. An absent rule means wait.
        foreach (var route in Knowledge.Routes)
        {
            var source = Knowledge.FindModel(route.ModelId);
            if (source is null || !IsUsableSource(source)) continue;
            foreach (var fallback in Knowledge.FallbacksFor(route.Id))
            {
                var target = Knowledge.FindModel(fallback.ModelId);
                if (target is not null && IsUsableSource(target))
                    rows.Add(Create(source, route.ThinkingLevel, target, fallback.ThinkingLevel));
            }
        }

        return rows
            .GroupBy(
                row => $"{row.FromCliType}|{row.FromModel}|{row.FromThinkingLevel}|{row.ToCliType}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => row.FromCliType, StringComparer.Ordinal)
            .ThenBy(row => row.FromModel, StringComparer.Ordinal)
            .ThenBy(row => row.FromThinkingLevel, StringComparer.Ordinal)
            .ToArray();
    }

    private ModelRoutingModel? CheapestSelectable(CapabilityTier capability, string cliType)
        => Knowledge.Models
            .Where(model => NormalizeCli(model.CliId) == NormalizeCli(cliType)
                && model.CapabilityTier.Equals(capability) && IsSelectable(model))
            .OrderBy(model => PriceScore(model.PriceCatalogId))
            .ThenBy(model => model.CanonicalId, StringComparer.Ordinal)
            .FirstOrDefault();

    private ModelEquivalenceRoute Create(
        ModelRoutingModel source, string? sourceThinking,
        ModelRoutingModel target, string? targetThinking)
    {
        var fromPrice = CurrentPrice(source.PriceCatalogId);
        var toPrice = CurrentPrice(target.PriceCatalogId);
        return new ModelEquivalenceRoute(
            NormalizeCli(source.CliId), source.CanonicalId, sourceThinking,
            NormalizeCli(target.CliId), target.CanonicalId, targetThinking,
            Version,
            fromPrice?.InputPerMTok, fromPrice?.OutputPerMTok,
            toPrice?.InputPerMTok, toPrice?.OutputPerMTok,
            source.CapabilityTier.ToString());
    }

    private static bool IsUsableSource(ModelRoutingModel model)
        => ModelMetadataRegistry.Find(model.CanonicalId)?.Deprecated != true
           && !string.Equals(model.RoutingStatus.ToString(), "Deprecated", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectable(ModelRoutingModel model)
        => IsUsableSource(model)
           && string.Equals(model.RoutingStatus.ToString(), "Selectable", StringComparison.OrdinalIgnoreCase);

    private static decimal PriceScore(string modelId)
    {
        var price = CurrentPrice(modelId);
        return price is null ? decimal.MaxValue : price.InputPerMTok + price.OutputPerMTok;
    }

    private static ModelPrice? CurrentPrice(string modelId)
        => Prices.ResolvePrice(modelId, DateTime.UtcNow).Price;

    private static string? DefaultThinkingFor(string modelId)
        => Knowledge.Routes
               .Where(candidate => string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
               .OrderBy(candidate => candidate.Rank)
               .FirstOrDefault()?.ThinkingLevel
           ?? ModelMetadataRegistry.Find(modelId)?.DefaultThinkingLevel
           ?? "medium";

    private static string DefaultModelFor(string cliType)
        => cliType == CliTypes.Claude ? ModelIds.ClaudeOpus5 : ModelIds.Gpt56Sol;

    private static string NormalizeCli(string? cliType)
        => cliType?.Trim().ToLowerInvariant() switch
        {
            "claude-code" or "claude" => CliTypes.Claude,
            "codex" => CliTypes.Codex,
            var value => value ?? string.Empty,
        };

    private static string BuildVersion()
    {
        var package = typeof(ModelRoutingKnowledgeBase).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ModelRoutingKnowledgeBase).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return $"TokenEconomy {package}; routing {Knowledge.PolicyVersion:yyyy-MM-dd}";
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
