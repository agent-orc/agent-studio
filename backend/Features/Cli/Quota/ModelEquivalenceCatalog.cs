using System.Reflection;
using AgentStudio.Pipeline;
using TokenEconomy;

namespace AgentStudio.Cli;

// ModelEquivalenceRoute and IModelEquivalenceCatalog live in the contracts
// package (ModelSelection/QuotaDecisionContracts.cs).

/// <summary>
/// Adapter over the embedded, versioned Token Economy artefacts. Admission
/// performs no network call.
/// </summary>
public sealed class ModelEquivalenceCatalog : IModelEquivalenceCatalog
{
    private static readonly ModelRoutingKnowledgeBase Knowledge = ModelRoutingKnowledgeBase.Default;

    private static readonly ModelMigrationCatalogRegistry MigrationCatalog = new();
    private readonly ITokenPricing _pricing;
    public string Version { get; } = BuildVersion();
    public IReadOnlyList<ModelEquivalenceRoute> Routes { get; }

    public ModelEquivalenceCatalog(ITokenPricing pricing)
    {
        _pricing = pricing;
        Routes = BuildRoutes();
    }

    public bool SameCapabilityClass(string? fromModel, string? toModel)
    {
        // D5: the routing registry treats Sol at xhigh and Opus at xhigh
        // as the same review tier while Token Economy's reverse entry lags.
        if (string.Equals(fromModel, ModelIds.Gpt6Sol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(toModel, ModelIds.ClaudeOpus5, StringComparison.OrdinalIgnoreCase))
            return true;
        var source = string.IsNullOrWhiteSpace(fromModel) ? null : Knowledge.FindModel(fromModel.Trim());
        var target = string.IsNullOrWhiteSpace(toModel) ? null : Knowledge.FindModel(toModel.Trim());
        return source is not null && target is not null
            && source.CapabilityTier == target.CapabilityTier;
    }

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

        var route = FindRoute(fromCli, model, requestedThinking, toCli);
        if (route is not null) return route;

        // Proposal-only successors inherit their predecessor's comparable
        // cross-provider tier, and a predecessor pin inherits the route Token
        // Economy now declares for its successor (TokenEconomy 0.3.6 moved
        // the Sol/medium fallback to gpt-6-sol, AGT-2903). This is a lookup,
        // not an automatic migration.
        var related = ComparablePredecessor(model) ?? ComparableSuccessor(model);
        route = related is null ? null : FindRoute(fromCli, related, requestedThinking, toCli);
        if (route is null) return null;
        // The D5 reverse route is scoped to GPT-6 Sol itself. A legacy
        // predecessor must not inherit a route absent from its own catalogue.
        if (string.Equals(model, ModelIds.Gpt56Sol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(related, ModelIds.Gpt6Sol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestedThinking, "xhigh", StringComparison.OrdinalIgnoreCase)
            && string.Equals(toCli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
            return null;
        var price = CurrentPrice(model);
        return route with
        {
            FromModel = model,
            FromInputPerMTok = price?.InputPerMillion,
            FromOutputPerMTok = price?.OutputPerMillion,
        };
    }

    private ModelEquivalenceRoute? FindRoute(
        string fromCli, string model, string? requestedThinking, string toCli)
        => Routes.FirstOrDefault(route =>
                string.Equals(route.FromCliType, fromCli, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.FromModel, model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.FromThinkingLevel, requestedThinking, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.ToCliType, toCli, StringComparison.OrdinalIgnoreCase));

    private static string? ComparablePredecessor(string model)
    {
        var canonical = ModelMetadataRegistry.NormalizeId(model);
        return MigrationCatalog.Catalog.Migrations
            .FirstOrDefault(migration => !migration.SafeAuto
                && string.Equals(migration.To, canonical, StringComparison.OrdinalIgnoreCase))?.From;
    }

    private static string? ComparableSuccessor(string model)
    {
        var canonical = ModelMetadataRegistry.NormalizeId(model);
        return MigrationCatalog.Catalog.Migrations
            .FirstOrDefault(migration => !migration.SafeAuto
                && string.Equals(migration.From, canonical, StringComparison.OrdinalIgnoreCase))?.To;
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
        // A policy route on a proposal successor (gpt-6-sol since TokenEconomy
        // 0.3.6) is a declared source, so its fallback stays reachable.
        foreach (var route in Knowledge.Routes)
        {
            var source = Knowledge.FindModel(route.ModelId);
            if (source is null || !IsRoutable(source)) continue;
            foreach (var fallback in Knowledge.FallbacksFor(route.Id))
            {
                var target = Knowledge.FindModel(fallback.ModelId);
                if (target is not null && IsUsableSource(target))
                    rows.Add(Create(source, route.ThinkingLevel, target, fallback.ThinkingLevel));
            }
        }

        // D5: this routing-policy tier is an explicit reverse provider choice
        // until Token Economy publishes the corresponding fallback row.
        if (Knowledge.FindModel(ModelIds.Gpt6Sol) is { } sol
            && Knowledge.FindModel(ModelIds.ClaudeOpus5) is { } opus)
            rows.Add(Create(sol, "xhigh", opus, "xhigh"));

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
            fromPrice?.InputPerMillion, fromPrice?.OutputPerMillion,
            toPrice?.InputPerMillion, toPrice?.OutputPerMillion,
            source.CapabilityTier.ToString());
    }

    // Proposal-only successors never enter the automatic route table; they
    // are reached only through their predecessor (see ComparablePredecessor).
    private static bool IsUsableSource(ModelRoutingModel model)
        => ModelMetadataRegistry.Find(model.CanonicalId)?.Deprecated != true
           && ComparablePredecessor(model.CanonicalId) is null
           && !string.Equals(model.RoutingStatus.ToString(), "Deprecated", StringComparison.OrdinalIgnoreCase);

    private static bool IsRoutable(ModelRoutingModel model)
        => ModelMetadataRegistry.Find(model.CanonicalId)?.Deprecated != true
           && !string.Equals(model.RoutingStatus.ToString(), "Deprecated", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectable(ModelRoutingModel model)
        => IsUsableSource(model)
           && string.Equals(model.RoutingStatus.ToString(), "Selectable", StringComparison.OrdinalIgnoreCase);

    private decimal PriceScore(string modelId)
    {
        var price = CurrentPrice(modelId);
        return price is null ? decimal.MaxValue : price.InputPerMillion + price.OutputPerMillion;
    }

    private TokenPriceBasis? CurrentPrice(string modelId)
        => _pricing.Estimate(modelId, 0, 0, 0, 0).PriceBasis;

    private static string? DefaultThinkingFor(string modelId)
        => Knowledge.Routes
               .Where(candidate => string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
               .OrderBy(candidate => candidate.Rank)
               .FirstOrDefault()?.ThinkingLevel
           ?? ModelMetadataRegistry.Find(modelId)?.DefaultThinkingLevel
           ?? "medium";

    // A blank Codex model runs on the detected Codex default (gpt-6-sol when
    // offered, AGT-2903); gpt-5.6-sol stays the answer before discovery ran.
    private static string DefaultModelFor(string cliType)
        => cliType == CliTypes.Claude
            ? ModelIds.ClaudeOpus5
            : ModelMetadataRegistry.DetectedCodexDefault ?? ModelIds.Gpt56Sol;

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
