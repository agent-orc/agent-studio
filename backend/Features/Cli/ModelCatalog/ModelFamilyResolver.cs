using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Resolves a stable model-family reference to the newest available concrete
/// model. Fresh CLI discovery is authoritative; the registry is the bounded
/// fallback before discovery runs or after its snapshot becomes stale.
/// </summary>
public static partial class ModelFamilyResolver
{
    /// <summary>
    /// Published catalogs remain authoritative slightly longer than the
    /// discovery services' default one-hour cache. A failed refresh keeps the
    /// last catalog useful briefly, then resolution returns to the registry.
    /// </summary>
    public static readonly TimeSpan DefaultCatalogFreshness = TimeSpan.FromHours(2);

    private static readonly ConcurrentDictionary<string, CliModelCatalog> PublishedCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, int> RegistryIndexes =
        ModelMetadataRegistry.All
            .Select((model, index) => (model.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve against the most recently published catalog for the family CLI.</summary>
    public static string Resolve(string family)
    {
        var cliType = RequireCliType(family);
        PublishedCatalogs.TryGetValue(cliType, out var catalog);
        return Resolve(family, catalog, DateTime.UtcNow, DefaultCatalogFreshness);
    }

    /// <summary>
    /// Report whether a concrete model is available in the latest fresh
    /// catalog. This is snapshot-only and never starts CLI discovery. Before a
    /// catalog is published, or after it expires, registry availability is the
    /// bounded fallback.
    /// </summary>
    public static bool IsAvailable(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var canonicalId = ModelMetadataRegistry.NormalizeId(modelId);
        var cliType = CliTypeForModel(canonicalId);
        PublishedCatalogs.TryGetValue(cliType ?? string.Empty, out var catalog);
        return IsAvailable(canonicalId, catalog, DateTime.UtcNow, DefaultCatalogFreshness);
    }

    /// <summary>
    /// Report whether a concrete model is explicitly available in a fresh
    /// installed-CLI catalog. Unlike <see cref="IsAvailable(string)"/>, this
    /// never falls back to registry availability and is therefore suitable for
    /// admission-time automatic migrations.
    /// </summary>
    public static bool IsAvailableInFreshCatalog(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var canonicalId = ModelMetadataRegistry.NormalizeId(modelId);
        var cliType = CliTypeForModel(canonicalId);
        PublishedCatalogs.TryGetValue(cliType ?? string.Empty, out var catalog);
        return IsAvailableInFreshCatalog(
            canonicalId,
            catalog,
            DateTime.UtcNow,
            DefaultCatalogFreshness);
    }

    /// <summary>Pure fresh-catalog availability check for tests and policy code.</summary>
    public static bool IsAvailableInFreshCatalog(
        string modelId,
        CliModelCatalog? catalog,
        DateTime? utcNow = null,
        TimeSpan? catalogFreshness = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var now = utcNow ?? DateTime.UtcNow;
        var freshness = catalogFreshness ?? DefaultCatalogFreshness;
        if (freshness < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(catalogFreshness));
        if (!IsFresh(catalog, now, freshness)) return false;

        var canonicalId = ModelMetadataRegistry.NormalizeId(modelId);
        return catalog!.Models.Any(model =>
            string.Equals(
                ModelMetadataRegistry.NormalizeId(model.Id),
                canonicalId,
                StringComparison.OrdinalIgnoreCase)
            && model.Available
            && !model.Deprecated);
    }

    /// <summary>Pure availability check with the same fresh-catalog precedence.</summary>
    public static bool IsAvailable(
        string modelId,
        CliModelCatalog? catalog,
        DateTime? utcNow = null,
        TimeSpan? catalogFreshness = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var canonicalId = ModelMetadataRegistry.NormalizeId(modelId);
        var now = utcNow ?? DateTime.UtcNow;
        var freshness = catalogFreshness ?? DefaultCatalogFreshness;
        if (freshness < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(catalogFreshness));

        if (IsFresh(catalog, now, freshness))
            return IsAvailableInFreshCatalog(canonicalId, catalog, now, freshness);

        var metadata = ModelMetadataRegistry.Find(canonicalId);
        return metadata is { Available: true, Deprecated: false };
    }

    /// <summary>
    /// Return the first non-blank configured pin, or resolve the family when
    /// no key is set. Keys are checked in priority order.
    /// </summary>
    public static string ResolveConfigured(
        IConfiguration configuration,
        string family,
        params string[] configurationKeys)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var key in configurationKeys)
        {
            var configured = configuration[key];
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        }
        return Resolve(family);
    }

    /// <summary>
    /// Pure resolution entry point used by discovery tests and migration
    /// policy. A null or stale catalog deliberately falls back to the registry.
    /// </summary>
    public static string Resolve(
        string family,
        CliModelCatalog? catalog,
        DateTime? utcNow = null,
        TimeSpan? catalogFreshness = null)
    {
        RequireCliType(family);
        var now = utcNow ?? DateTime.UtcNow;
        var freshness = catalogFreshness ?? DefaultCatalogFreshness;
        if (freshness < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(catalogFreshness));

        if (IsFresh(catalog, now, freshness))
        {
            var live = SelectNewest(family, catalog!.Models);
            if (live != null) return live;
            throw new InvalidOperationException(
                $"The current {RequireCliType(family)} catalog has no available model in family '{family}'.");
        }

        var fallback = SelectNewest(
            family,
            ModelMetadataRegistry.ForFamily(family)
                .Where(model => model.Available && !model.Deprecated)
                .Select(model => ModelMetadataRegistry.ToCliModelInfo(
                    model,
                    ModelFamilies.CliTypeFor(model.Family) ?? string.Empty)));
        return fallback ?? throw new InvalidOperationException(
            $"Model family '{family}' has no available registry fallback.");
    }

    /// <summary>
    /// Publish the catalog returned by the common CLI execution-service access
    /// path. Runtime defaults subsequently see this snapshot without spawning
    /// another CLI process.
    /// </summary>
    public static void PublishCatalog(string cliType, CliModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!CliTypes.IsValid(cliType))
            throw new ArgumentException($"Unknown cliType '{cliType}'.", nameof(cliType));

        var normalized = CliTypes.Normalize(cliType);
        PublishedCatalogs[normalized] = catalog with
        {
            Models = catalog.Models?.ToList() ?? [],
        };
    }

    internal static void ClearPublishedCatalogs() => PublishedCatalogs.Clear();

    private static string? CliTypeForModel(string canonicalId)
    {
        var metadata = ModelMetadataRegistry.Find(canonicalId);
        var family = metadata?.Family ?? ModelFamilies.InferFromModelId(canonicalId);
        return ModelFamilies.CliTypeFor(family) ?? metadata?.Vendor switch
        {
            "anthropic" => CliTypes.Claude,
            "openai" => CliTypes.Codex,
            _ => null,
        };
    }

    private static bool IsFresh(CliModelCatalog? catalog, DateTime now, TimeSpan freshness)
    {
        if (catalog == null || catalog.FetchedAt == default) return false;
        var age = now.ToUniversalTime() - catalog.FetchedAt.ToUniversalTime();
        return age >= TimeSpan.FromMinutes(-5) && age <= freshness;
    }

    private static string? SelectNewest(string family, IEnumerable<CliModelInfo> models)
        => models
            .Where(model => model.Available && !model.Deprecated && BelongsToFamily(family, model.Id))
            .Select((model, index) => CandidateFor(model.Id, index))
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.GenerationOrder)
            .ThenBy(candidate => candidate.RegistryIndex)
            .ThenBy(candidate => candidate.CatalogIndex)
            .Select(candidate => candidate.Id)
            .FirstOrDefault();

    private static bool BelongsToFamily(string family, string? modelId)
    {
        var metadata = ModelMetadataRegistry.Find(modelId);
        var candidateFamily = metadata?.Family ?? ModelFamilies.InferFromModelId(modelId);
        return string.Equals(candidateFamily, family, StringComparison.OrdinalIgnoreCase);
    }

    private static Candidate CandidateFor(string modelId, int catalogIndex)
    {
        var canonicalId = ModelMetadataRegistry.NormalizeId(modelId);
        var metadata = ModelMetadataRegistry.Find(canonicalId);
        var generation = metadata?.GenerationOrder > 0
            ? metadata.GenerationOrder
            : ParseGenerationOrder(canonicalId);
        var registryIndex = RegistryIndexes.TryGetValue(canonicalId, out var index)
            ? index
            : int.MaxValue;
        return new Candidate(canonicalId, generation, registryIndex, catalogIndex);
    }

    private static int ParseGenerationOrder(string modelId)
    {
        var family = ModelFamilies.InferFromModelId(modelId);
        var prefix = family switch
        {
            ModelFamilies.ClaudeHaiku => "claude-haiku-",
            ModelFamilies.ClaudeSonnet => "claude-sonnet-",
            ModelFamilies.ClaudeOpus => "claude-opus-",
            ModelFamilies.GptMini or ModelFamilies.GptFlagship => "gpt-",
            _ => string.Empty,
        };
        var version = prefix.Length > 0 && modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
        var match = GenerationRegex().Match(version);
        if (!match.Success) return 0;
        var major = int.Parse(match.Groups["major"].Value);
        var minor = match.Groups["minor"].Success
            ? int.Parse(match.Groups["minor"].Value)
            : 0;
        return checked((major * 10_000) + (minor * 100));
    }

    private static string RequireCliType(string family)
    {
        var cliType = ModelFamilies.CliTypeFor(family);
        if (cliType == null)
            throw new ArgumentException($"Unknown model family '{family}'.", nameof(family));
        return cliType;
    }

    [GeneratedRegex(@"^(?<major>\d+)(?:[.-](?<minor>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex GenerationRegex();

    private sealed record Candidate(
        string Id,
        int GenerationOrder,
        int RegistryIndex,
        int CatalogIndex);
}
