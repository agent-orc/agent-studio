using TokenEconomy;

namespace AgentStudio.Cli;

/// <summary>
/// Adapts Token Economy's evidence-qualified provider fallbacks to Studio CLI
/// names. The catalogue remains the authority for equivalence: an absent entry
/// means that quota admission must wait or use an explicit operator override.
/// </summary>
public sealed class ModelEquivalenceRouteCatalog
{
    private readonly ModelRoutingKnowledgeBase _knowledge;

    public ModelEquivalenceRouteCatalog()
        : this(ModelRoutingKnowledgeBase.Default)
    {
    }

    internal ModelEquivalenceRouteCatalog(ModelRoutingKnowledgeBase knowledge)
    {
        _knowledge = knowledge;
    }

    public string PolicyVersion => _knowledge.PolicyVersion.ToString("yyyy-MM-dd");

    /// <summary>
    /// Resolve an explicitly qualified route in the other CLI family. Direct
    /// catalogue fallbacks and their reverse edge form one equivalence tier;
    /// reverse resolution chooses the strongest qualifying source route so it
    /// cannot silently lower the original correctness floor.
    /// </summary>
    public CatalogueEquivalentRoute? Resolve(
        string? requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel)
    {
        var requestedCli = NormalizeCli(requestedCliType);
        var model = _knowledge.FindModel(requestedModel);
        if (model is null || !string.Equals(NormalizeCli(model.CliId), requestedCli, StringComparison.OrdinalIgnoreCase))
            return null;

        var thinking = requestedThinkingLevel?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(thinking)) return null;

        var sourceRoute = _knowledge.Routes
            .Where(route => SameModel(route.ModelId, model.CanonicalId)
                            && string.Equals(route.ThinkingLevel, thinking, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(route => route.Rank)
            .FirstOrDefault();
        if (sourceRoute is not null)
        {
            var direct = _knowledge.FallbacksFor(sourceRoute.Id)
                .Select(fallback => ToEquivalent(sourceRoute, fallback))
                .FirstOrDefault(route => route is not null
                                         && !string.Equals(route.CliType, requestedCli, StringComparison.OrdinalIgnoreCase));
            if (direct is not null) return direct;
        }

        // Token Economy records the safe substitution edge from the primary
        // policy route to its fallback-only model. Treat that edge as one
        // equivalence tier for provider recovery in the opposite direction.
        var reverse = _knowledge.ProviderFallbacks
            .Where(fallback => SameModel(fallback.ModelId, model.CanonicalId)
                               && string.Equals(fallback.ThinkingLevel, thinking, StringComparison.OrdinalIgnoreCase))
            .SelectMany(fallback => fallback.ForRouteIds
                .Select(routeId => (Fallback: fallback, Route: _knowledge.FindRoute(routeId))))
            .Where(item => item.Route is not null)
            .OrderByDescending(item => item.Route!.Rank)
            .FirstOrDefault(item =>
            {
                var target = _knowledge.FindModel(item.Route!.ModelId);
                return target is not null
                       && !string.Equals(NormalizeCli(target.CliId), requestedCli, StringComparison.OrdinalIgnoreCase);
            });
        if (reverse.Route is null) return null;

        var reverseModel = _knowledge.FindModel(reverse.Route.ModelId);
        if (reverseModel is null) return null;
        return new CatalogueEquivalentRoute(
            reverse.Route.Id,
            NormalizeCli(reverseModel.CliId),
            reverseModel.CanonicalId,
            reverse.Route.ThinkingLevel,
            PolicyVersion,
            reverse.Fallback.Note);
    }

    public IReadOnlyList<CatalogueEquivalentRouteDefinition> GetAll()
    {
        var rows = new List<CatalogueEquivalentRouteDefinition>();
        foreach (var route in _knowledge.Routes.OrderBy(route => route.Rank))
        {
            var source = _knowledge.FindModel(route.ModelId);
            if (source is null) continue;
            foreach (var fallback in _knowledge.FallbacksFor(route.Id))
            {
                var target = _knowledge.FindModel(fallback.ModelId);
                if (target is null) continue;
                rows.Add(new CatalogueEquivalentRouteDefinition(
                    route.Id,
                    NormalizeCli(source.CliId),
                    source.CanonicalId,
                    route.ThinkingLevel,
                    NormalizeCli(target.CliId),
                    target.CanonicalId,
                    fallback.ThinkingLevel,
                    fallback.EvidenceStatus.ToString().ToLowerInvariant(),
                    fallback.Provisional,
                    PolicyVersion,
                    fallback.Note));
            }
        }
        return rows;
    }

    private CatalogueEquivalentRoute? ToEquivalent(TokenEconomy.ModelRoutingTier source, ModelProviderFallback fallback)
    {
        var target = _knowledge.FindModel(fallback.ModelId);
        return target is null
            ? null
            : new CatalogueEquivalentRoute(
                source.Id,
                NormalizeCli(target.CliId),
                target.CanonicalId,
                fallback.ThinkingLevel,
                PolicyVersion,
                fallback.Note);
    }

    private bool SameModel(string left, string right)
    {
        var a = _knowledge.FindModel(left)?.CanonicalId ?? left;
        var b = _knowledge.FindModel(right)?.CanonicalId ?? right;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCli(string? cliType) => cliType?.Trim().ToLowerInvariant() switch
    {
        "claude-code" => CliTypes.Claude,
        "openai" => CliTypes.Codex,
        { Length: > 0 } value => value,
        _ => CliTypes.Claude,
    };
}

public sealed record CatalogueEquivalentRoute(
    string TierId,
    string CliType,
    string Model,
    string ThinkingLevel,
    string PolicyVersion,
    string Reason);

public sealed record CatalogueEquivalentRouteDefinition(
    string TierId,
    string PrimaryCliType,
    string PrimaryModel,
    string PrimaryThinkingLevel,
    string FallbackCliType,
    string FallbackModel,
    string FallbackThinkingLevel,
    string EvidenceStatus,
    bool Provisional,
    string PolicyVersion,
    string Reason);
