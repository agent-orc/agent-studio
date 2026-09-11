using System.Reflection;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record ModelMigrationEntry
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Family { get; init; } = "";
    /// <summary>Whether the orchestrator may apply this migration automatically
    /// for a non-explicit model without operator confirmation.</summary>
    public bool SafeAuto { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record ModelMigrationCatalogDocument
{
    public string Version { get; init; } = "";
    public string WikiPath { get; init; } = "";
    public List<ModelMigrationEntry> Migrations { get; init; } = [];
}

/// <summary>
/// Replaceable data-source seam for the migration catalog (AGT-2716), the same
/// posture as <see cref="IModelEconomyAdvisor"/> and the TokenEconomy pricing
/// adapter (<c>ITokenPriceProvider</c>): the built-in implementation reads
/// Studio's own embedded, versioned JSON copy of "which model migrations are
/// safe" today; a Token Economy adapter can implement the same contract once
/// TE ships a migration-catalog package artifact of its own, without changing
/// <see cref="ModelMigrationCatalogRegistry"/> or any of its call sites.
/// </summary>
public interface IModelMigrationCatalogSource
{
    ModelMigrationCatalogDocument Load();
}

/// <summary>Reads the repository-versioned <c>Policies/model-migration-catalog.v1.json</c>
/// embedded resource. The interim source until a Token Economy package ships one.</summary>
public sealed class EmbeddedModelMigrationCatalogSource : IModelMigrationCatalogSource
{
    private const string ResourceSuffix = "Policies.model-migration-catalog.v1.json";

    public ModelMigrationCatalogDocument Load()
    {
        var assembly = typeof(EmbeddedModelMigrationCatalogSource).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resource == null)
            throw new InvalidOperationException($"Embedded migration catalog '{ResourceSuffix}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration catalog '{resource}' could not be opened.");
        return JsonSerializer.Deserialize<ModelMigrationCatalogDocument>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("The embedded migration catalog is empty.");
    }
}

/// <summary>
/// Repository-versioned catalog of known-safe model migrations: which
/// superseded model ids have a newer, same-family replacement, and whether the
/// orchestrator may apply that replacement automatically for a non-explicit
/// model (<see cref="ModelMigrationEntry.SafeAuto"/>). Deliberately does not
/// carry a haiku or gpt-mini entry today: the 2026-09-06 fact check against the
/// installed Claude CLI found no newer haiku generation, and leaving the
/// cheap-tier family on gpt-mini vs. promoting it to Sonnet is a Token Economy
/// decision, not a family-generation rule (see model-routing-policy.md).
/// </summary>
public sealed class ModelMigrationCatalogRegistry
{
    public ModelMigrationCatalogDocument Catalog { get; }

    private readonly IReadOnlyDictionary<string, ModelMigrationEntry> _byFromId;

    public ModelMigrationCatalogRegistry() : this(new EmbeddedModelMigrationCatalogSource())
    {
    }

    public ModelMigrationCatalogRegistry(IModelMigrationCatalogSource source)
    {
        var catalog = source.Load();
        Validate(catalog);
        Catalog = catalog;
        _byFromId = catalog.Migrations.ToDictionary(m => m.From, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The migration entry whose <c>From</c> matches <paramref name="modelId"/>,
    /// or null when the catalog has no migration for it.</summary>
    public ModelMigrationEntry? FindMigration(string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? null
            : _byFromId.TryGetValue(modelId.Trim(), out var entry) ? entry : null;

    private static void Validate(ModelMigrationCatalogDocument catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Version))
            throw new InvalidOperationException("Migration catalog version is required.");
        if (string.IsNullOrWhiteSpace(catalog.WikiPath))
            throw new InvalidOperationException("Migration catalog wikiPath is required.");

        var seenFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Migrations)
        {
            if (string.IsNullOrWhiteSpace(entry.From) || string.IsNullOrWhiteSpace(entry.To))
                throw new InvalidOperationException("Migration catalog entries require a non-blank from/to model id.");
            if (string.Equals(entry.From, entry.To, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Migration catalog entry '{entry.From}' migrates to itself.");
            if (!seenFrom.Add(entry.From))
                throw new InvalidOperationException($"Migration catalog has more than one entry for '{entry.From}'.");
        }
    }
}

/// <summary>
/// Pure decision for whether the orchestrator should rewrite a task's stored
/// model to a catalog-safe replacement at run admission (AGT-2716). Kept
/// separate from <c>ProjectRunner</c>'s side effects (task.json write, timeline
/// event) so the branching logic has a direct matrix test independent of the
/// runner's admission machinery.
/// </summary>
public static class ModelMigrationPolicy
{
    /// <summary>
    /// Decide whether to auto-migrate. Returns the migration to apply, or null
    /// when none applies: the workspace switched auto-application off, the
    /// model is an explicit pin, there is no stored model to migrate, or the
    /// catalog has no <c>safeAuto</c> entry for it.
    /// </summary>
    public static ModelMigrationEntry? DecideAutoMigration(
        bool modelExplicit,
        string? model,
        bool autoApplyEnabled,
        ModelMigrationCatalogRegistry catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!autoApplyEnabled || modelExplicit || string.IsNullOrWhiteSpace(model)) return null;

        var entry = catalog.FindMigration(model);
        return entry is { SafeAuto: true } ? entry : null;
    }
}
