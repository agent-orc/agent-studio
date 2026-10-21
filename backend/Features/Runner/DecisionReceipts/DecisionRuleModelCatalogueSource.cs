using AgentStudio.OrchestrationDecisions;
using TokenEconomy;

namespace AgentStudio.Runner;

/// <summary>
/// Projects the Token Economy model catalogue for rule validation. A model
/// clears the trusted decision floor when the catalogue rates it frontier and
/// selectable; the configured decision CLI must be the CLI that serves it.
/// </summary>
public static class DecisionRuleModelCatalogueSource
{
    private static readonly IReadOnlyList<DecisionModelOptionV1> Options = ModelRoutingKnowledgeBase.Default.Models
        .SelectMany(model => new[] { model.CanonicalId }.Concat(model.Aliases ?? [])
            .Select(id => new DecisionModelOptionV1(id, NormalizeCli(model.CliId), Trusted(model))))
        .ToArray();

    public static DecisionRuleModelCatalogue For(IConfiguration configuration)
        => For(DecisionGroundsCollector.Route(configuration).Cli);

    public static DecisionRuleModelCatalogue For(string cli) => new(NormalizeCli(cli), Options);

    private static bool Trusted(ModelRoutingModel model)
        => model.CapabilityTier == CapabilityTier.Frontier
            && string.Equals(model.RoutingStatus.ToString(), "Selectable", StringComparison.OrdinalIgnoreCase)
            && ModelMetadataRegistry.Find(model.CanonicalId)?.Deprecated != true;

    private static string NormalizeCli(string? cli)
        => cli?.Trim().ToLowerInvariant() switch
        {
            "claude-code" or "claude" => CliTypes.Claude,
            "codex" => CliTypes.Codex,
            var value => value ?? string.Empty,
        };
}
