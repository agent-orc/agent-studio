namespace AgentStudio.Cli;

/// <summary>
/// Which concrete model a family reference resolved to, and where that answer
/// came from. Call sites only need <see cref="ModelId"/>; the audit surfaces
/// (operator feed, CLI Management) render the source so "why is this run on
/// Opus 5" is answerable without reading code.
/// </summary>
/// <param name="Source">
/// The discovery snapshot's own source when a fresh one answered
/// (<c>cli-pty</c>, <c>cli-debug-models</c>, a cache label), or
/// <c>registry</c> when discovery was absent or stale. Passing the snapshot's
/// label through rather than a flat "cli-discovery" keeps the audit honest when
/// the catalog the CLI produced was itself a fallback.
public sealed record ModelFamilyResolution(
    string FamilyId,
    string ModelId,
    string Source,
    DateTime? DiscoveredAt);

/// <summary>
/// Resolves a <see cref="ModelFamilies"/> reference to the newest model that
/// family currently offers.
///
/// <para>Ranking is generation first (<see cref="ModelFamilies.GenerationOf"/>),
/// then the catalog's own order, which for both CLIs is the order the installed
/// CLI advertises: Codex sorts by its <c>priority</c> field and Claude by picker
/// position, so <c>gpt-5.6-sol</c> wins over its same-generation siblings
/// without Studio maintaining a ranking table.</para>
///
/// <para>The live catalog comes from the snapshot each discovery publishes into
/// <see cref="ModelMetadataRegistry"/>. When no snapshot exists, or the last one
/// is older than <see cref="DiscoveryMaxAge"/>, resolution falls back to the
/// registry so a call site is never left without a model. A family that neither
/// source knows is a configuration defect and throws: silently substituting a
/// foreign model would spend correctness margin without an audit trail.</para>
/// </summary>
public static class ModelFamilyResolver
{
    /// <summary>
    /// How long a published discovery snapshot is trusted. Discovery's own cache
    /// TTL is an hour; this is the wider "the CLI has not been probed in a long
    /// time, prefer what the repository knows" boundary.
    /// </summary>
    public static readonly TimeSpan DiscoveryMaxAge = TimeSpan.FromHours(24);

    /// <summary>The newest model in a family. Throws when the family is unknown.</summary>
    public static string Resolve(string familyId) => ResolveDetailed(familyId).ModelId;

    public static ModelFamilyResolution ResolveDetailed(string familyId, DateTime? now = null)
    {
        var cliType = ModelFamilies.CliFor(familyId)
            ?? throw new InvalidOperationException($"Unknown model family '{familyId}'.");
        var snapshot = ModelMetadataRegistry.DiscoveredCatalogFor(cliType);
        var fresh = snapshot != null
                    && (now ?? DateTime.UtcNow) - snapshot.FetchedAt < DiscoveryMaxAge;

        if (fresh && Newest(familyId, snapshot!.Models) is { } discovered)
            return new(familyId, discovered, snapshot.Source, snapshot.FetchedAt);

        var vendorCatalog = ModelMetadataRegistry.ForVendor(VendorFor(cliType))
            .Where(entry => entry.Available && !entry.Deprecated)
            .Select(entry => ModelMetadataRegistry.ToCliModelInfo(entry, cliType))
            .ToList();
        if (Newest(familyId, vendorCatalog) is { } known)
            return new(familyId, known, "registry", snapshot?.FetchedAt);

        throw new InvalidOperationException(
            $"Model family '{familyId}' has no available member in the {cliType} catalog or the registry.");
    }

    /// <summary>
    /// The newest available member of a family within one catalog, or null when
    /// the catalog has none. Pure, so the ranking is testable against a captured
    /// CLI catalog without touching the published snapshot.
    /// </summary>
    public static string? Newest(string familyId, IReadOnlyList<CliModelInfo>? catalog)
    {
        if (string.IsNullOrWhiteSpace(familyId) || catalog is not { Count: > 0 }) return null;

        string? best = null;
        IReadOnlyList<int> bestGeneration = [];
        var bestIsCatalogDefault = false;

        foreach (var model in catalog)
        {
            if (model.Available == false || model.Deprecated) continue;
            if (!string.Equals(ModelFamilies.Of(model.Id), familyId, StringComparison.OrdinalIgnoreCase)) continue;

            var generation = ModelFamilies.GenerationOf(model.Id);
            if (best == null)
            {
                (best, bestGeneration, bestIsCatalogDefault) = (model.Id, generation, model.IsDefault);
                continue;
            }

            var comparison = ModelFamilies.CompareGeneration(generation, bestGeneration);
            // Same generation: the CLI's own pick wins, then catalog order -
            // which is why the first match is kept when neither is flagged.
            if (comparison > 0 || (comparison == 0 && model.IsDefault && !bestIsCatalogDefault))
                (best, bestGeneration, bestIsCatalogDefault) = (model.Id, generation, model.IsDefault);
        }

        return best;
    }

    private static string VendorFor(string cliType) => cliType switch
    {
        CliTypes.Claude => "anthropic",
        CliTypes.Codex => "openai",
        _ => throw new InvalidOperationException($"No vendor is mapped to CLI '{cliType}'."),
    };
}
