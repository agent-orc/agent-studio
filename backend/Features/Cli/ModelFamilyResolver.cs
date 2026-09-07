using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>Stable references for defaults that follow the latest model in a family.</summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string GptMini = "gpt-mini";
    public const string GptFlagship = "gpt-flagship";

    private static readonly string[] Values =
    [
        ClaudeHaiku,
        ClaudeSonnet,
        ClaudeOpus,
        GptMini,
        GptFlagship,
    ];

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Values);

    public static bool IsFamily(string? value)
        => TryNormalize(value, out _);

    public static bool TryNormalize(string? value, out string family)
    {
        var trimmed = value?.Trim();
        family = Values.FirstOrDefault(candidate =>
            string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) ?? "";
        return family.Length > 0;
    }

    public static string Normalize(string? value)
        => TryNormalize(value, out var family)
            ? family
            : throw new ArgumentException(
                $"Unsupported model family '{value?.Trim() ?? "<null>"}'.",
                nameof(value));

    public static string CliTypeFor(string familyId) => Normalize(familyId) switch
    {
        ClaudeHaiku or ClaudeSonnet or ClaudeOpus => CliTypes.Claude,
        GptMini or GptFlagship => CliTypes.Codex,
        _ => throw new UnreachableException(),
    };

    public static string? ForModel(string? modelId)
        => ModelMetadataRegistry.FamilyFor(modelId);
}

/// <summary>
/// Resolves stable family references against live CLI availability. The pure
/// resolver ranks available entries by the registry generation order. A
/// convention-based registry fallback also recognizes a newly discovered
/// numeric generation before exact metadata for that model ships.
/// </summary>
public sealed class ModelFamilyResolver
{
    public const int DefaultCatalogMaxAgeMinutes = 60;

    private static readonly ConcurrentDictionary<string, CliModelCatalog> PublishedCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, CancellationToken, Task<CliModelCatalog>> _catalogProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelFamilyResolver> _logger;
    private readonly TimeProvider _time;

    public ModelFamilyResolver(
        CliRouter router,
        IConfiguration configuration,
        ILogger<ModelFamilyResolver> logger,
        TimeProvider? time = null)
        : this(
            (cliType, ct) => router.Get(cliType).GetModelCatalogAsync(false, ct),
            configuration,
            logger,
            time)
    {
        ArgumentNullException.ThrowIfNull(router);
    }

    internal ModelFamilyResolver(
        Func<string, CancellationToken, Task<CliModelCatalog>> catalogProvider,
        IConfiguration configuration,
        ILogger<ModelFamilyResolver> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _catalogProvider = catalogProvider;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolve after asking the owning CLI for its current cached or discovered
    /// catalog. Discovery failures preserve the registry fallback.
    /// </summary>
    public async Task<string> ResolveAsync(string familyId, CancellationToken ct = default)
    {
        var family = ModelFamilies.Normalize(familyId);
        var cliType = ModelFamilies.CliTypeFor(family);
        CliModelCatalog catalog;

        try
        {
            catalog = await _catalogProvider(cliType, ct);
            PublishCatalog(cliType, catalog);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "model_family_catalog_unavailable family={Family} cli={CliType}; using registry fallback",
                family,
                cliType);
            return ResolveFromRegistry(family);
        }

        return Resolve(family, catalog, _time.GetUtcNow(), CatalogMaxAge(cliType));
    }

    /// <summary>
    /// Resolve synchronously from the freshest catalog published by CLI
    /// discovery. Supporting defaults fall back to the registry when no fresh
    /// catalog is available or a partial published catalog omits their family.
    /// Migration availability uses <see cref="ResolveAsync"/> instead, where a
    /// fresh catalog remains authoritative.
    /// </summary>
    public static string ResolveCurrent(string familyId)
        => ResolveCurrent(
            familyId,
            TimeProvider.System.GetUtcNow(),
            TimeSpan.FromMinutes(DefaultCatalogMaxAgeMinutes));

    internal static string ResolveCurrent(
        string familyId,
        DateTimeOffset now,
        TimeSpan maxCatalogAge)
    {
        var family = ModelFamilies.Normalize(familyId);
        PublishedCatalogs.TryGetValue(ModelFamilies.CliTypeFor(family), out var catalog);
        try
        {
            return Resolve(family, catalog, now, maxCatalogAge);
        }
        catch (ModelFamilyUnavailableException)
        {
            return ResolveFromRegistry(family);
        }
    }

    /// <summary>
    /// Pure family decision. A fresh catalog is authoritative and fails when
    /// it has no available member of the requested family. Only an absent,
    /// stale, or incomplete catalog falls back to the newest available
    /// registry model.
    /// </summary>
    public static string Resolve(
        string familyId,
        CliModelCatalog? catalog,
        DateTimeOffset now,
        TimeSpan maxCatalogAge)
    {
        var family = ModelFamilies.Normalize(familyId);
        if (maxCatalogAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxCatalogAge));

        if (IsFresh(catalog, now, maxCatalogAge))
        {
            var selected = catalog!.Models
                .Select((model, index) => new
                {
                    Model = model,
                    Index = index,
                    Generation = ModelMetadataRegistry.GenerationOrderFor(model.Id) ?? long.MinValue,
                })
                .Where(candidate =>
                    candidate.Model.Available
                    && !candidate.Model.Deprecated
                    && string.Equals(
                        ModelMetadataRegistry.FamilyFor(candidate.Model.Id),
                        family,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Generation)
                .ThenBy(candidate => candidate.Index)
                .FirstOrDefault();
            if (selected != null)
                return ModelMetadataRegistry.NormalizeId(selected.Model.Id);
            throw new ModelFamilyUnavailableException(
                family,
                ModelFamilies.CliTypeFor(family));
        }

        return ResolveFromRegistry(family);
    }

    public static string ResolveFromRegistry(string familyId)
    {
        var family = ModelFamilies.Normalize(familyId);
        var selected = ModelMetadataRegistry.ForFamily(family)
            .FirstOrDefault(model => model.Available && !model.Deprecated);
        return selected?.Id
               ?? throw new InvalidOperationException(
                   $"No available registry model is configured for family '{family}'.");
    }

    /// <summary>Publish a discovery result for synchronous default resolution.</summary>
    public static void PublishCatalog(string cliType, CliModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!CliTypes.IsValid(cliType))
            throw new ArgumentException($"Unsupported CLI type '{cliType}'.", nameof(cliType));

        var normalizedCli = CliTypes.Normalize(cliType);
        var snapshot = catalog with { Models = catalog.Models.ToList() };
        PublishedCatalogs.AddOrUpdate(
            normalizedCli,
            snapshot,
            (_, current) => snapshot.FetchedAt >= current.FetchedAt ? snapshot : current);
    }

    internal static void ClearPublishedCatalogs()
        => PublishedCatalogs.Clear();

    private static bool IsFresh(
        CliModelCatalog? catalog,
        DateTimeOffset now,
        TimeSpan maxCatalogAge)
    {
        if (catalog?.Models is not { Count: > 0 } || catalog.FetchedAt == default)
            return false;

        var fetchedAt = catalog.FetchedAt.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(catalog.FetchedAt, DateTimeKind.Utc))
            : new DateTimeOffset(catalog.FetchedAt).ToUniversalTime();
        return fetchedAt >= now.ToUniversalTime() - maxCatalogAge;
    }

    private TimeSpan CatalogMaxAge(string cliType)
    {
        var key = string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
            ? "ClaudeModelsCacheMinutes"
            : "CodexModelsCacheMinutes";
        var minutes = _configuration.GetValue<int?>(key) ?? DefaultCatalogMaxAgeMinutes;
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }
}

public sealed class ModelFamilyUnavailableException(string familyId, string cliType)
    : InvalidOperationException(
        $"The current {cliType} catalog has no available model in family '{familyId}'.")
{
    public string FamilyId { get; } = familyId;
    public string CliType { get; } = cliType;
}
