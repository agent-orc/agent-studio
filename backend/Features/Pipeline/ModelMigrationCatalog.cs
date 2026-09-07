using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// The derived rule that needs no per-model entry: inside one family, the newest
/// generation supersedes the older ones. Only the guard rails are data, so a new
/// release migrates without a catalog edit.
/// </summary>
public sealed record ModelMigrationFamilyRule
{
    public string Id { get; init; } = "same-family-newer-generation";
    public bool SafeAuto { get; init; } = true;
    public bool RequireSameOrLowerCostClass { get; init; } = true;
    public bool RequireLadderCompatible { get; init; } = true;
    public string Reason { get; init; } = "";
}

/// <summary>
/// An explicit migration Token Economy asserts, for the cases the family rule
/// cannot derive: leaving a family for a better value tier, or a model an
/// account can no longer run. Explicit entries win over the family rule.
/// </summary>
public sealed record ModelMigrationEntry
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Rule { get; init; } = "";
    public bool SafeAuto { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record ModelMigrationCatalogDocument
{
    public string Version { get; init; } = "";
    public string WikiPath { get; init; } = "";
    public ModelMigrationFamilyRule FamilyRule { get; init; } = new();
    public List<ModelMigrationEntry> Migrations { get; init; } = [];
}

/// <summary>One side of a migration diff: what the model costs and how it thinks.</summary>
public sealed record ModelMigrationSide(
    string ModelId,
    string Label,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    IReadOnlyList<string> ThinkingLevels,
    string? DefaultThinkingLevel);

/// <summary>
/// "This pin is superseded, here is what replacing it changes." Carries the full
/// cost and reasoning-ladder diff so the UI never has to recompute policy, and
/// so the timeline event that records an automatic application is self-contained.
/// </summary>
public sealed record ModelMigrationProposal
{
    public required ModelMigrationSide From { get; init; }
    public required ModelMigrationSide To { get; init; }
    public string Rule { get; init; } = "";
    public string CatalogVersion { get; init; } = "";

    /// <summary>True only when every guard rail the catalog demands is satisfied.</summary>
    public bool SafeAuto { get; init; }

    /// <summary><c>cheaper</c>, <c>same</c>, <c>more-expensive</c>, or <c>unknown</c>.</summary>
    public string CostClass { get; init; } = ModelMigrationCostClasses.Unknown;

    /// <summary>True when every reasoning level the current model offers still exists on the target.</summary>
    public bool LadderCompatible { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>Set when the catalog would migrate but a guard rail vetoed the automatic path.</summary>
    public string? SafeAutoBlockedBy { get; init; }
}

public static class ModelMigrationCostClasses
{
    public const string Cheaper = "cheaper";
    public const string Same = "same";
    public const string MoreExpensive = "more-expensive";
    public const string Unknown = "unknown";
}

/// <summary>
/// Pure policy: given a pinned model id and a catalog, decide whether an update
/// is available and whether it is safe to apply without asking.
///
/// <para>Two sources of truth, in order. An explicit Token Economy entry wins,
/// because only TE can know that a cheaper family is the better value or that an
/// account can no longer run a model. Otherwise the derived family rule applies:
/// same family, newer generation, resolved against the live catalog through
/// <see cref="ModelFamilyResolver"/>.</para>
///
/// <para><c>safeAuto</c> is deliberately conservative. Cost has to be known on
/// both sides to be judged same-or-lower, so an unpriced target blocks the
/// automatic path and leaves an offer. A cross-family entry is never safeAuto:
/// changing family changes the capability floor, which is the operator's call.</para>
/// </summary>
public static class ModelMigrationPlanner
{
    public static ModelMigrationProposal? Propose(
        string? modelId,
        ModelMigrationCatalogDocument catalog,
        IReadOnlyList<CliModelInfo>? liveCatalog = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var from = ModelMetadataRegistry.NormalizeId(modelId);
        if (string.IsNullOrWhiteSpace(from)) return null;

        var explicitEntry = catalog.Migrations.FirstOrDefault(entry =>
            string.Equals(ModelMetadataRegistry.NormalizeId(entry.From), from, StringComparison.OrdinalIgnoreCase));

        var (to, rule, declaredSafeAuto, reason) = explicitEntry != null
            ? (ModelMetadataRegistry.NormalizeId(explicitEntry.To), explicitEntry.Rule,
               explicitEntry.SafeAuto, explicitEntry.Reason)
            : (DeriveFamilyTarget(from, liveCatalog), catalog.FamilyRule.Id,
               catalog.FamilyRule.SafeAuto, catalog.FamilyRule.Reason);

        if (string.IsNullOrWhiteSpace(to) || string.Equals(to, from, StringComparison.OrdinalIgnoreCase))
            return null;

        var fromSide = Describe(from, liveCatalog);
        var toSide = Describe(to, liveCatalog);
        var costClass = ClassifyCost(fromSide, toSide);
        var ladderCompatible = IsLadderCompatible(fromSide, toSide);

        var blockedBy = FirstBlocker(
            declaredSafeAuto,
            explicitEntry == null,
            catalog.FamilyRule,
            from,
            to,
            costClass,
            ladderCompatible);

        return new ModelMigrationProposal
        {
            From = fromSide,
            To = toSide,
            Rule = rule,
            CatalogVersion = catalog.Version,
            SafeAuto = blockedBy == null,
            CostClass = costClass,
            LadderCompatible = ladderCompatible,
            Reason = reason,
            SafeAutoBlockedBy = blockedBy,
        };
    }

    /// <summary>
    /// The newest member of the pin's family, or null when the pin already is
    /// it. Ranks within the supplied catalog when the caller has one, so the
    /// proposal and its diff describe the same catalog; otherwise it asks the
    /// resolver, which consults the published snapshot and then the registry.
    /// </summary>
    private static string? DeriveFamilyTarget(string from, IReadOnlyList<CliModelInfo>? liveCatalog)
    {
        var family = ModelFamilies.Of(from);
        if (family == null) return null;

        string? newest;
        try
        {
            newest = ModelFamilyResolver.Newest(family, liveCatalog)
                     ?? ModelFamilyResolver.Resolve(family);
        }
        catch (InvalidOperationException)
        {
            // The family has no member anywhere: nothing to offer. The resolver
            // still fails loud on the call sites that need a model.
            return null;
        }

        return ModelFamilies.CompareGeneration(
            ModelFamilies.GenerationOf(newest), ModelFamilies.GenerationOf(from)) > 0
            ? newest
            : null;
    }

    private static string? FirstBlocker(
        bool declaredSafeAuto,
        bool isFamilyRule,
        ModelMigrationFamilyRule familyRule,
        string from,
        string to,
        string costClass,
        bool ladderCompatible)
    {
        if (!declaredSafeAuto) return "catalog";
        if (!string.Equals(ModelFamilies.Of(from), ModelFamilies.Of(to), StringComparison.OrdinalIgnoreCase))
            return "cross-family";
        if (!isFamilyRule) return null;
        if (familyRule.RequireSameOrLowerCostClass && costClass is ModelMigrationCostClasses.MoreExpensive)
            return "cost-class";
        if (familyRule.RequireSameOrLowerCostClass && costClass is ModelMigrationCostClasses.Unknown)
            return "cost-unknown";
        if (familyRule.RequireLadderCompatible && !ladderCompatible) return "reasoning-ladder";
        return null;
    }

    private static ModelMigrationSide Describe(string modelId, IReadOnlyList<CliModelInfo>? liveCatalog)
    {
        var metadata = ModelMetadataRegistry.Find(modelId);
        var live = liveCatalog?.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
        var levels = live?.ThinkingLevels?.ToList()
                     ?? metadata?.ThinkingLevels?.ToList()
                     ?? [];
        return new ModelMigrationSide(
            modelId,
            live?.Label ?? metadata?.Label ?? modelId,
            metadata?.InputPricePerMillion,
            metadata?.OutputPricePerMillion,
            levels,
            live?.DefaultThinkingLevel ?? metadata?.DefaultThinkingLevel);
    }

    private static string ClassifyCost(ModelMigrationSide from, ModelMigrationSide to)
    {
        if (from.InputPricePerMillion is not { } fromIn || to.InputPricePerMillion is not { } toIn
            || from.OutputPricePerMillion is not { } fromOut || to.OutputPricePerMillion is not { } toOut)
        {
            return ModelMigrationCostClasses.Unknown;
        }

        if (toIn <= fromIn && toOut <= fromOut)
            return toIn == fromIn && toOut == fromOut
                ? ModelMigrationCostClasses.Same
                : ModelMigrationCostClasses.Cheaper;
        return ModelMigrationCostClasses.MoreExpensive;
    }

    private static bool IsLadderCompatible(ModelMigrationSide from, ModelMigrationSide to)
    {
        // An unknown ladder on either side is not evidence of incompatibility;
        // the CLI simply did not advertise levels for that model.
        if (from.ThinkingLevels.Count == 0 || to.ThinkingLevels.Count == 0) return true;
        return from.ThinkingLevels.All(level =>
            to.ThinkingLevels.Contains(level, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The run-admission gate, kept pure so the matrix (explicit / non-explicit,
/// switch on / off, safeAuto / offer-only) is testable without a runner.
/// </summary>
public static class ModelMigrationAdmission
{
    /// <summary>
    /// The migration run admission should apply on its own, or null when it must
    /// not touch the card. Four independent reasons to decline, in order:
    /// nothing to migrate, an operator pinned the model, the workspace switched
    /// automatic application off, or the catalog only offers the change.
    /// </summary>
    public static ModelMigrationProposal? Decide(
        string? currentModel,
        bool modelExplicit,
        bool autoApplyEnabled,
        ModelMigrationCatalogDocument catalog,
        IReadOnlyList<CliModelInfo>? liveCatalog = null)
    {
        if (string.IsNullOrWhiteSpace(currentModel)) return null;
        if (modelExplicit) return null;
        if (!autoApplyEnabled) return null;

        var proposal = ModelMigrationPlanner.Propose(currentModel, catalog, liveCatalog);
        return proposal?.SafeAuto == true ? proposal : null;
    }
}

/// <summary>
/// Loads the migration catalog. Token Economy owns the rules, exactly as it owns
/// the price catalog: when a TE-published file is present it wins, otherwise the
/// repository-versioned baseline embedded in this assembly applies. Either way
/// the resulting <see cref="ModelMigrationCatalogDocument.Version"/> is what
/// Workspace CLI Management shows, so an operator can tell which rule set a
/// proposal came from.
///
/// <para>Resolution order for the TE file: <c>TokenEconomy:MigrationCatalogPath</c>,
/// then <c>&lt;TaskRepository&gt;/.metadata/model-migration-catalog.json</c>. The
/// parsed document is cached for <see cref="CacheTtl"/> so a per-run proposal
/// never costs a file read.</para>
/// </summary>
public sealed class ModelMigrationCatalogService
{
    public const string TokenEconomySource = "token-economy";
    public const string EmbeddedSource = "repository-baseline";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private const string ResourceSuffix = "Policies.model-migration-catalog.v1.json";
    private const string FileName = "model-migration-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelMigrationCatalogService> _logger;
    private readonly object _lock = new();
    private ModelMigrationCatalogDocument? _cached;
    private string _cachedSource = EmbeddedSource;
    private DateTime _cachedAt = DateTime.MinValue;

    public ModelMigrationCatalogService(
        IConfiguration configuration,
        ILogger<ModelMigrationCatalogService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public ModelMigrationCatalogDocument Catalog => Load().Catalog;

    /// <summary><see cref="TokenEconomySource"/> or <see cref="EmbeddedSource"/>.</summary>
    public string Source => Load().Source;

    public (ModelMigrationCatalogDocument Catalog, string Source) Load(DateTime? now = null)
    {
        lock (_lock)
        {
            var moment = now ?? DateTime.UtcNow;
            if (_cached != null && moment - _cachedAt < CacheTtl) return (_cached, _cachedSource);

            var path = ResolveTokenEconomyPath();
            if (path != null && File.Exists(path))
            {
                try
                {
                    var fromTokenEconomy = JsonSerializer.Deserialize<ModelMigrationCatalogDocument>(
                        File.ReadAllText(path), JsonOptions);
                    if (fromTokenEconomy != null)
                    {
                        Validate(fromTokenEconomy);
                        (_cached, _cachedSource, _cachedAt) = (fromTokenEconomy, TokenEconomySource, moment);
                        return (_cached, _cachedSource);
                    }
                }
                catch (Exception ex)
                {
                    // A malformed TE catalog must not stop runs: fall back to the
                    // repository baseline and say so once per cache window.
                    _logger.LogWarning(ex,
                        "Failed to read the Token Economy migration catalog at {Path}; using the repository baseline",
                        path);
                }
            }

            var embedded = ReadEmbedded();
            Validate(embedded);
            (_cached, _cachedSource, _cachedAt) = (embedded, EmbeddedSource, moment);
            return (_cached, _cachedSource);
        }
    }

    private string? ResolveTokenEconomyPath()
    {
        var configured = _configuration["TokenEconomy:MigrationCatalogPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var root = _configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, ".metadata", FileName);
    }

    internal static ModelMigrationCatalogDocument ReadEmbedded()
    {
        var assembly = typeof(ModelMigrationCatalogService).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resource == null)
            throw new InvalidOperationException($"Embedded migration catalog '{ResourceSuffix}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration catalog '{resource}' could not be opened.");
        return JsonSerializer.Deserialize<ModelMigrationCatalogDocument>(stream, JsonOptions)
               ?? throw new InvalidOperationException("The embedded migration catalog is empty.");
    }

    internal static void Validate(ModelMigrationCatalogDocument catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Version))
            throw new InvalidOperationException("Migration catalog version is required.");
        if (string.IsNullOrWhiteSpace(catalog.FamilyRule.Id))
            throw new InvalidOperationException("Migration catalog familyRule.id is required.");
        foreach (var entry in catalog.Migrations)
        {
            if (string.IsNullOrWhiteSpace(entry.From) || string.IsNullOrWhiteSpace(entry.To))
                throw new InvalidOperationException("Every migration needs a from and a to model id.");
            if (string.Equals(entry.From, entry.To, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Migration '{entry.From}' points at itself.");
            if (string.IsNullOrWhiteSpace(entry.Rule))
                throw new InvalidOperationException($"Migration '{entry.From}' has no rule id.");
        }
    }
}
