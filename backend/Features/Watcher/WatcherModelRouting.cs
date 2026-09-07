namespace AgentStudio.Watcher;

/// <summary>
/// Adapts the canonical model routing policy to a Watcher proposal. The Watcher
/// does not own a routing ladder: it asks the registry the same question the
/// create-task path asks and carries the answer into the draft.
/// </summary>
/// <remarks>
/// The dossier's model economy (section 5) governs what the Watcher spends on
/// itself. This type is about a different question: which route the proposed
/// card should run on once an operator approves it. Quota and cost never lower
/// a correctness floor, so nothing here downgrades the registry's answer.
/// </remarks>
public static class WatcherModelRouting
{
    /// <summary>
    /// Asks the registry for a route for the card this case would produce.
    /// </summary>
    /// <remarks>
    /// The routing text is built from the case, not from the finished draft.
    /// The draft quotes the recommendation, so deriving the recommendation from
    /// the draft would be circular; the case summary, detector rule, and
    /// evidence carry the same correctness-floor signals (a lease, a fence, a
    /// security boundary) that a hand-written card would carry.
    /// </remarks>
    public static WatcherModelRecommendation Recommend(
        ModelRoutingPolicyRegistry registry,
        CliModelCatalog catalogue,
        bool economyMode,
        WatcherCase watcherCase)
    {
        var recommendation = registry.Recommend(
            WatcherProposalDrafting.TaskTypeFor(watcherCase.DetectorClass),
            catalogue,
            economyMode,
            watcherCase.Summary,
            RoutingText(watcherCase));

        return new WatcherModelRecommendation(
            Model: recommendation.Model,
            ThinkingLevel: recommendation.ThinkingLevel,
            Tier: recommendation.Tier,
            PolicyVersion: recommendation.PolicyVersion,
            Reason: BuildReason(watcherCase, recommendation));
    }

    /// <summary>
    /// The text the registry scans for correctness-floor signals: the detector
    /// rule plus every recorded evidence value.
    /// </summary>
    private static string RoutingText(WatcherCase watcherCase)
        => string.Join(
            "\n",
            [
                watcherCase.DetectorRule,
                .. watcherCase.Evidence.Select(item => $"{item.Label}: {item.Value}"),
            ]);

    private static string BuildReason(WatcherCase watcherCase, ModelRoutingRecommendation recommendation)
    {
        var parts = new List<string>
        {
            $"Task type {recommendation.TaskType} for a {watcherCase.DetectorClass.ToString().ToLowerInvariant()} finding, score {recommendation.Score}.",
        };
        if (!string.IsNullOrWhiteSpace(recommendation.Reason)) parts.Add(recommendation.Reason);
        if (!string.IsNullOrWhiteSpace(recommendation.CorrectnessFloorTier))
            parts.Add($"Correctness floor {recommendation.CorrectnessFloorTier}.");
        if (recommendation.EconomyDowngraded)
            parts.Add("Economy mode applied a safe one-step downgrade.");
        return string.Join(" ", parts);
    }
}
