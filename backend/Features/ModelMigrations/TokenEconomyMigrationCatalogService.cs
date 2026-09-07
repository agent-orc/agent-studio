using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Cli;
using AgentStudio.Docs;
using AgentStudio.Registry;

namespace AgentStudio.ModelMigrations;

public interface IModelMigrationCatalogProvider
{
    ModelMigrationCatalogSnapshot GetCatalog(bool refresh = false);
}

/// <summary>
/// Translates Token Economy's persisted family vocabulary to Agent Studio's
/// stable runtime family ids. Token Economy calls the non-Mini GPT line
/// <c>gpt</c>; Studio calls the same line <c>gpt-flagship</c> so it cannot be
/// confused with the bounded supporting-agent family.
/// </summary>
public static class ModelMigrationFamilyMap
{
    public static string ToStudioFamily(string tokenEconomyFamily) => tokenEconomyFamily switch
    {
        "claude-opus" => ModelFamilies.ClaudeOpus,
        "claude-sonnet" => ModelFamilies.ClaudeSonnet,
        "claude-haiku" => ModelFamilies.ClaudeHaiku,
        "gpt-mini" => ModelFamilies.GptMini,
        "gpt" => ModelFamilies.GptFlagship,
        _ => throw new ArgumentException(
            $"Unknown Token Economy model family '{tokenEconomyFamily}'.",
            nameof(tokenEconomyFamily)),
    };
}

/// <summary>
/// Reads the versioned model-migration catalog owned by the registered Token
/// Economy project. The catalog remains project data: Agent Studio does not
/// bundle a second copy that can drift from Token Economy's evidence and rules.
/// </summary>
public sealed class TokenEconomyMigrationCatalogService : IModelMigrationCatalogProvider
{
    public const string CatalogRelativePath = "src/TokenEconomy/catalog/model-migrations.v1.json";

    internal const int MaxCatalogBytes = 512 * 1024;
    internal static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyDictionary<string, int> CostClassRanks =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["economy"] = 0,
            ["standard"] = 1,
            ["premium"] = 2,
        };

    private static readonly IReadOnlySet<string> Families = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-opus",
        "claude-sonnet",
        "claude-haiku",
        "gpt",
        "gpt-mini",
    };

    private readonly Func<string?> _resolveRepositoryRoot;
    private readonly ILogger<TokenEconomyMigrationCatalogService> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheDuration;
    private readonly object _gate = new();

    private ModelMigrationCatalogSnapshot? _cached;
    private ModelMigrationCatalogSnapshot? _lastGood;
    private DateTime _refreshAfterUtc = DateTime.MinValue;

    public TokenEconomyMigrationCatalogService(
        ProjectRegistry projects,
        ILogger<TokenEconomyMigrationCatalogService> logger)
        : this(
            () => ResolveRepositoryRoot(projects),
            logger,
            TimeProvider.System,
            DefaultCacheDuration)
    {
    }

    /// <summary>
    /// Test seam for repository resolution and virtual time. Production uses
    /// the registered Token Economy project through the public constructor.
    /// </summary>
    internal TokenEconomyMigrationCatalogService(
        Func<string?> resolveRepositoryRoot,
        ILogger<TokenEconomyMigrationCatalogService> logger,
        TimeProvider time,
        TimeSpan cacheDuration)
    {
        _resolveRepositoryRoot = resolveRepositoryRoot ?? throw new ArgumentNullException(nameof(resolveRepositoryRoot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (cacheDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheDuration));
        _cacheDuration = cacheDuration;
    }

    /// <summary>
    /// Returns the current catalog snapshot. A failed refresh retains the last
    /// valid catalog and marks it stale; without any prior valid catalog the
    /// result contains no catalog, so migration proposals and automation fail
    /// closed. Set <paramref name="refresh"/> to bypass the bounded cache.
    /// </summary>
    public ModelMigrationCatalogSnapshot GetCatalog(bool refresh = false)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (!refresh && _cached is not null && now < _refreshAfterUtc)
                return _cached;

            string? source = null;
            try
            {
                var repositoryRoot = _resolveRepositoryRoot();
                if (string.IsNullOrWhiteSpace(repositoryRoot))
                    throw new InvalidOperationException(
                        "The registered Token Economy project has no repositoryPath or rootPath.");

                source = ResolveCatalogPath(repositoryRoot);
                var json = ReadBoundedUtf8(source);
                var catalog = JsonSerializer.Deserialize<ModelMigrationCatalog>(json, JsonOptions)
                    ?? throw new JsonException("The migration catalog root is null.");
                Validate(catalog);

                var loaded = new ModelMigrationCatalogSnapshot(
                    catalog,
                    source,
                    Error: null,
                    CapturedAtUtc: now,
                    IsStale: false);
                _lastGood = loaded;
                _cached = loaded;
                _refreshAfterUtc = now + _cacheDuration;
                _logger.LogInformation(
                    "model-migration-catalog-loaded source={Source} catalogVersion={CatalogVersion} rules={RuleCount}",
                    source,
                    catalog.CatalogVersion,
                    catalog.Migrations.Count);
                return loaded;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
                                       or InvalidOperationException
                                       or ArgumentException
                                       or NotSupportedException
                                       or DecoderFallbackException)
            {
                var error = ex.Message;
                _cached = _lastGood is null
                    ? new ModelMigrationCatalogSnapshot(
                        Catalog: null,
                        Source: source,
                        Error: error,
                        CapturedAtUtc: null,
                        IsStale: false)
                    : _lastGood with
                    {
                        Error = error,
                        IsStale = true,
                    };
                _refreshAfterUtc = now + _cacheDuration;
                _logger.LogWarning(
                    ex,
                    "model-migration-catalog-load-failed source={Source} retainedLastGood={RetainedLastGood}",
                    source ?? "<unresolved>",
                    _lastGood is not null);
                return _cached;
            }
        }
    }

    internal static string? ResolveRepositoryRoot(ProjectRegistry projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var project = projects.FindByShortCode("TE")
                      ?? projects.FindByIdOrDisplayName("Token Economy");
        if (project is null) return null;

        if (!string.IsNullOrWhiteSpace(project.RepositoryPath))
            return project.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(project.RootPath))
            return project.RootPath;
        return ProjectRepoResolver.DeriveFromStorage(project.StorageLocation);
    }

    private static string ResolveCatalogPath(string repositoryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The Token Economy repository does not exist: {root}");

        var path = Path.GetFullPath(Path.Combine(
            root,
            CatalogRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The migration catalog path escaped the Token Economy repository.");
        return path;
    }

    private static string ReadBoundedUtf8(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
            throw new InvalidDataException("The migration catalog is empty.");
        if (stream.Length > MaxCatalogBytes)
            throw new InvalidDataException(
                $"The migration catalog exceeds the {MaxCatalogBytes}-byte read limit.");

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes);
    }

    private static void Validate(ModelMigrationCatalog catalog)
    {
        if (!string.Equals(catalog.Schema, "model-migrations.v1.schema.json", StringComparison.Ordinal))
            throw new JsonException("The migration catalog does not declare the v1 schema.");
        if (catalog.SchemaVersion != 1)
            throw new JsonException($"Unsupported migration catalog schemaVersion '{catalog.SchemaVersion}'.");
        if (!IsIsoDate(catalog.CatalogVersion))
            throw new JsonException("The migration catalog catalogVersion must be an ISO date.");
        if (!IsIsoDate(catalog.EvidenceAsOfDate))
            throw new JsonException("The migration catalog evidenceAsOfDate must be an ISO date.");
        if (!string.Equals(catalog.DefaultStrategy, "latestInFamily", StringComparison.Ordinal))
            throw new JsonException("The migration catalog defaultStrategy must be latestInFamily.");
        if (!catalog.CostClassOrder.SequenceEqual(["economy", "standard", "premium"], StringComparer.Ordinal))
            throw new JsonException("The migration catalog costClassOrder is invalid.");
        if (catalog.Authority.ValueKind != JsonValueKind.Object)
            throw new JsonException("The migration catalog authority object is missing.");
        if (catalog.TaskClassRecommendations.ValueKind != JsonValueKind.Array)
            throw new JsonException("The migration catalog taskClassRecommendations array is missing.");
        if (catalog.Migrations.Count == 0)
            throw new JsonException("The migration catalog contains no migration rules.");

        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in catalog.Migrations)
        {
            if (string.IsNullOrWhiteSpace(rule.From)
                || string.IsNullOrWhiteSpace(rule.To)
                || string.IsNullOrWhiteSpace(rule.Vendor)
                || string.IsNullOrWhiteSpace(rule.Note)
                || !IsIsoDate(rule.Since))
                throw new JsonException("A migration rule is missing required identity or provenance fields.");
            if (!sources.Add(rule.From))
                throw new JsonException($"The migration catalog declares duplicate rules for '{rule.From}'.");
            if (!Families.Contains(rule.Family))
                throw new JsonException($"Migration rule '{rule.From}' has unknown family '{rule.Family}'.");
            if (rule.Vendor is not ("anthropic" or "openai"))
                throw new JsonException($"Migration rule '{rule.From}' has unknown vendor '{rule.Vendor}'.");
            if (rule.ContextChange is not ("same" or "increase" or "decrease" or "unknown"))
                throw new JsonException($"Migration rule '{rule.From}' has invalid contextChange '{rule.ContextChange}'.");
            if (!BelongsToFamily(rule.From, rule.Family))
                throw new JsonException(
                    $"Migration rule '{rule.From}' does not belong to its declared source family '{rule.Family}'.");
            if (rule.GenerationOrder is null
                || rule.GenerationOrder.From < 0
                || rule.GenerationOrder.To < 0)
                throw new JsonException($"Migration rule '{rule.From}' has no generation order.");
            if (!IsKnownCostClass(rule.CostClassFrom) || !IsKnownCostClass(rule.CostClassTo))
                throw new JsonException($"Migration rule '{rule.From}' has an invalid cost class.");
            if (!HasValidEvidence(rule.Evidence))
                throw new JsonException($"Migration rule '{rule.From}' has invalid evidence.");

            if (!rule.SafeAuto) continue;
            if (string.Equals(rule.From, rule.To, StringComparison.Ordinal))
                throw new JsonException($"Safe migration rule '{rule.From}' does not change the model.");
            if (!BelongsToFamily(rule.To, rule.Family))
                throw new JsonException(
                    $"Safe migration rule '{rule.From}' crosses its declared family '{rule.Family}'.");
            if (rule.GenerationOrder.To <= rule.GenerationOrder.From)
                throw new JsonException($"Safe migration rule '{rule.From}' does not advance the generation.");
            if (!rule.LadderCompatible)
                throw new JsonException($"Safe migration rule '{rule.From}' has an incompatible reasoning ladder.");
            if (!CostClassRanks.TryGetValue(rule.CostClassFrom, out var fromRank)
                || !CostClassRanks.TryGetValue(rule.CostClassTo, out var toRank)
                || toRank > fromRank)
                throw new JsonException($"Safe migration rule '{rule.From}' raises or cannot resolve the cost class.");
            if (rule.Evidence.ValueKind != JsonValueKind.Object
                || !rule.Evidence.TryGetProperty("conclusion", out var conclusion)
                || !string.Equals(conclusion.GetString(), "noRegression", StringComparison.Ordinal))
                throw new JsonException($"Safe migration rule '{rule.From}' lacks no-regression evidence.");
        }
    }

    private static bool IsIsoDate(string? value)
        => DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static bool IsKnownCostClass(string? value)
        => string.Equals(value, "unknown", StringComparison.Ordinal)
           || (value is not null && CostClassRanks.ContainsKey(value));

    private static bool HasValidEvidence(JsonElement evidence)
    {
        if (evidence.ValueKind == JsonValueKind.String)
            return string.Equals(evidence.GetString(), "none", StringComparison.Ordinal);
        if (evidence.ValueKind != JsonValueKind.Object)
            return false;

        return evidence.TryGetProperty("kind", out var kind)
               && kind.GetString() is "controlledBenchmark" or "abTest"
               && evidence.TryGetProperty("reference", out var reference)
               && !string.IsNullOrWhiteSpace(reference.GetString())
               && evidence.TryGetProperty("conclusion", out var conclusion)
               && conclusion.GetString() is "noRegression" or "regression" or "inconclusive"
               && evidence.TryGetProperty("summary", out var summary)
               && !string.IsNullOrWhiteSpace(summary.GetString());
    }

    private static bool BelongsToFamily(string model, string family)
        => string.Equals(
            ModelMetadataRegistry.FamilyFor(model),
            ModelMigrationFamilyMap.ToStudioFamily(family),
            StringComparison.Ordinal);
}

public sealed record ModelMigrationCatalogSnapshot(
    ModelMigrationCatalog? Catalog,
    string? Source,
    string? Error,
    DateTime? CapturedAtUtc,
    bool IsStale);

public sealed record ModelMigrationCatalog
{
    [JsonPropertyName("$schema")]
    [JsonRequired]
    public string Schema { get; init; } = "";

    [JsonRequired]
    public int SchemaVersion { get; init; }
    [JsonRequired]
    public string CatalogVersion { get; init; } = "";
    [JsonRequired]
    public string EvidenceAsOfDate { get; init; } = "";
    [JsonRequired]
    public string DefaultStrategy { get; init; } = "";
    [JsonRequired]
    public JsonElement Authority { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> CostClassOrder { get; init; } = [];
    [JsonRequired]
    public IReadOnlyList<ModelMigrationRule> Migrations { get; init; } = [];
    [JsonRequired]
    public JsonElement TaskClassRecommendations { get; init; }
}

public sealed record ModelMigrationRule
{
    [JsonRequired]
    public string From { get; init; } = "";
    [JsonRequired]
    public string To { get; init; } = "";
    [JsonRequired]
    public string Family { get; init; } = "";
    [JsonRequired]
    public string Vendor { get; init; } = "";
    [JsonRequired]
    public ModelMigrationGenerationOrder? GenerationOrder { get; init; }
    [JsonRequired]
    public string CostClassFrom { get; init; } = "";
    [JsonRequired]
    public string CostClassTo { get; init; } = "";
    [JsonRequired]
    public bool LadderCompatible { get; init; }
    [JsonRequired]
    public string ContextChange { get; init; } = "";
    [JsonRequired]
    public JsonElement Evidence { get; init; }
    [JsonRequired]
    public bool SafeAuto { get; init; }
    [JsonRequired]
    public string Since { get; init; } = "";
    [JsonRequired]
    public string Note { get; init; } = "";
}

public sealed record ModelMigrationGenerationOrder
{
    [JsonRequired]
    public int From { get; init; }
    [JsonRequired]
    public int To { get; init; }
}
