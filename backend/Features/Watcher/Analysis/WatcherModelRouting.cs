namespace AgentStudio.Watcher;

/// <summary>
/// Resolves Watcher model routes from the repository routing policy instead of
/// hard-coded ids, so a policy revision moves the Watcher with it.
/// </summary>
/// <remarks>
/// The Watcher never needs a live CLI catalogue to name a route. It builds a
/// catalogue from the policy tiers themselves, which keeps a recommendation on
/// a proposal deterministic and available even while a CLI probe is the very
/// thing that is broken. The card the operator approves is created through the
/// normal API, and that path re-resolves the model against the live catalogue.
/// </remarks>
public static class WatcherModelRouting
{
    /// <summary>Task type the proposal carries, by detector class.</summary>
    public static string TaskTypeFor(string detectorClass) => detectorClass switch
    {
        // A validator already computed the defect list; fixing it is routine work.
        WatcherDetectorClasses.Hygiene => TaskTypes.Chore,
        // Something the platform promised is not happening. That is a defect.
        _ => TaskTypes.Bug,
    };

    /// <summary>
    /// A catalogue synthesised from the policy tiers. Every tier model is
    /// advertised as available with its own thinking level, so
    /// <see cref="ModelRoutingPolicyRegistry.Recommend"/> resolves the exact
    /// tier route rather than a rank-based fallback.
    /// </summary>
    public static CliModelCatalog PolicyCatalogue(ModelRoutingPolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var models = registry.Policy.Tiers
            .GroupBy(tier => tier.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CliModelInfo
            {
                Id = group.Key,
                Label = group.Key,
                Vendor = "policy",
                ThinkingLevels = group
                    .Select(tier => tier.ThinkingLevel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DefaultThinkingLevel = group.First().ThinkingLevel,
            })
            .ToList();

        return new CliModelCatalog
        {
            Models = models,
            Source = "model-routing-policy",
            FetchedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Recommend a route for a proposed card. Title and prompt travel into the
    /// policy so its correctness floors can raise the tier the same way they do
    /// for a hand-written card.
    /// </summary>
    public static WatcherModelRecommendation Recommend(
        ModelRoutingPolicyRegistry registry,
        bool economyMode,
        string detectorClass,
        string title,
        string prompt)
    {
        var recommendation = registry.Recommend(
            TaskTypeFor(detectorClass),
            PolicyCatalogue(registry),
            economyMode,
            title,
            prompt);

        return new WatcherModelRecommendation
        {
            Tier = recommendation.Tier,
            Model = recommendation.Model,
            ThinkingLevel = recommendation.ThinkingLevel,
            PolicyVersion = recommendation.PolicyVersion,
            Score = recommendation.Score,
            CorrectnessFloorTier = recommendation.CorrectnessFloorTier,
            Reason = recommendation.Reason,
        };
    }

    /// <summary>
    /// The concrete model and thinking level behind a policy tier id. Used for
    /// the Watcher's own analysis route so section 5's floor is read from the
    /// policy rather than restated in configuration.
    /// </summary>
    public static (string Model, string ThinkingLevel) Route(ModelRoutingPolicyRegistry registry, string tierId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var tier = registry.Policy.Tiers
            .FirstOrDefault(item => string.Equals(item.Id, tierId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The routing policy has no tier '{tierId}'. The Watcher analysis floor must name a policy tier.");
        return (tier.Model, tier.ThinkingLevel);
    }
}
