using System.Reflection;
using TokenEconomy;

namespace AgentStudio.Cli;

/// <summary>
/// Versioned Token Economy provider pairs. Admission never infers equivalence
/// from a model name, price, supported reasoning flag or capability tier.
/// </summary>
public sealed class ModelEquivalenceCatalog : IModelEquivalenceCatalog
{
    private static readonly ModelRoutingKnowledgeBase Knowledge = ModelRoutingKnowledgeBase.Default;
    private readonly ITokenPricing _pricing;
    public string Version { get; } = BuildVersion();
    public IReadOnlyList<ModelEquivalenceRoute> Routes { get; }

    public ModelEquivalenceCatalog(ITokenPricing pricing)
    {
        _pricing = pricing;
        var rows = new List<ModelEquivalenceRoute>();
        foreach (var route in Knowledge.Routes)
        {
            var source = Knowledge.FindModel(route.ModelId);
            if (source is null) continue;
            foreach (var fallback in Knowledge.FallbacksFor(route.Id))
            {
                var target = Knowledge.FindModel(fallback.ModelId);
                if (target is null || NormalizeCli(source.CliId) == NormalizeCli(target.CliId)) continue;
                rows.Add(Create(source, route.ThinkingLevel, target, fallback.ThinkingLevel));
                // Equivalence is a pair of complete routes. Reversing it retains
                // both levels, including a mapping such as medium to high.
                rows.Add(Create(target, fallback.ThinkingLevel, source, route.ThinkingLevel));
            }
        }
        Routes = rows.DistinctBy(row => (row.FromCliType, row.FromModel, row.FromThinkingLevel,
                row.ToCliType, row.ToModel, row.ToThinkingLevel))
            .OrderBy(row => row.FromCliType, StringComparer.Ordinal)
            .ThenBy(row => row.FromModel, StringComparer.Ordinal)
            .ThenBy(row => row.FromThinkingLevel, StringComparer.Ordinal)
            .ToArray();
    }

    public bool SameCapabilityClass(string? fromModel, string? toModel)
    {
        var source = fromModel is null ? null : Knowledge.FindModel(fromModel);
        var target = toModel is null ? null : Knowledge.FindModel(toModel);
        return source is not null && target is not null && source.CapabilityTier == target.CapabilityTier;
    }

    public (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType)
        => TryGetRoute(fromCliType, fromModel, fromThinkingLevel, toCliType) is { } route
            ? (route.ToModel, route.ToThinkingLevel) : null;

    public ModelEquivalenceRoute? TryGetRoute(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType)
    {
        if (string.IsNullOrWhiteSpace(fromModel)) return null;
        var model = Knowledge.FindModel(fromModel.Trim())?.CanonicalId ?? fromModel.Trim();
        return Routes.Where(route => route.FromCliType == NormalizeCli(fromCliType)
                && string.Equals(route.FromModel, model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.FromThinkingLevel, fromThinkingLevel, StringComparison.OrdinalIgnoreCase)
                && route.ToCliType == NormalizeCli(toCliType))
            .OrderBy(route => SameCapabilityClass(route.FromModel, route.ToModel) ? 0 : 1)
            .ThenBy(route => PriceScore(route.ToModel))
            .ThenBy(route => route.ToModel, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private ModelEquivalenceRoute Create(ModelRoutingModel source, string? sourceThinking,
        ModelRoutingModel target, string? targetThinking)
    {
        var fromPrice = CurrentPrice(source.PriceCatalogId);
        var toPrice = CurrentPrice(target.PriceCatalogId);
        return new(NormalizeCli(source.CliId), source.CanonicalId, sourceThinking,
            NormalizeCli(target.CliId), target.CanonicalId, targetThinking, Version,
            fromPrice?.InputPerMillion, fromPrice?.OutputPerMillion,
            toPrice?.InputPerMillion, toPrice?.OutputPerMillion, source.CapabilityTier.ToString());
    }

    private decimal PriceScore(string model)
        => CurrentPrice(model) is { } price ? price.InputPerMillion + price.OutputPerMillion : decimal.MaxValue;

    private TokenPriceBasis? CurrentPrice(string model)
        => _pricing.Estimate(model, 0, 0, 0, 0).PriceBasis;

    private static string NormalizeCli(string cli) => cli.Trim().ToLowerInvariant() switch
    {
        "claude-code" => CliTypes.Claude,
        var value => value,
    };

    private static string BuildVersion()
    {
        var package = typeof(ModelRoutingKnowledgeBase).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"TokenEconomy {package}; routing {Knowledge.PolicyVersion:yyyy-MM-dd}";
    }
}
