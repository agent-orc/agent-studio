using System.Text.Json;

namespace AgentStudio.ModelMigrations;

public sealed record ModelMigrationCatalogSnapshot
{
    public ModelMigrationCatalog? Catalog { get; init; }
    public string? SourcePath { get; init; }
    public DateTime? SourceLastWriteTimeUtc { get; init; }
    public bool IsStale { get; init; }
    public string? Error { get; init; }
    public bool IsAvailable => Catalog != null;
}

public sealed record ModelMigrationCatalogStatus
{
    public bool IsAvailable { get; init; }
    public bool IsStale { get; init; }
    public string? CatalogVersion { get; init; }
    public string? EvidenceAsOfDate { get; init; }
    public string? SourcePath { get; init; }
    public DateTime? SourceLastWriteTimeUtc { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Reads the Token Economy migration catalog from its registered project
/// repository. Reads are cached by absolute source path and last-write time.
/// A bad refresh retains the last valid in-memory snapshot.
/// </summary>
public sealed class ModelMigrationCatalogService
{
    public const string CatalogRelativePath = "src/TokenEconomy/catalog/model-migrations.v1.json";
    internal const long MaxCatalogBytes = 1_048_576;
    private static readonly TimeSpan RepositoryResolutionTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    private readonly Func<string?> _repositoryRoot;
    private readonly ILogger<ModelMigrationCatalogService> _logger;
    private readonly object _gate = new();
    private CacheKey? _lastAttemptKey;
    private ModelMigrationCatalogSnapshot? _lastAttempt;
    private ModelMigrationCatalogSnapshot? _lastKnownGood;
    private string? _cachedRepositoryRoot;
    private DateTime _repositoryRootObservedAtUtc;

    public ModelMigrationCatalogService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        ILogger<ModelMigrationCatalogService> logger)
        : this(() => ResolveTokenEconomyRepository(scanner, registry), logger)
    {
    }

    internal ModelMigrationCatalogService(
        Func<string?> repositoryRoot,
        ILogger<ModelMigrationCatalogService> logger)
    {
        _repositoryRoot = repositoryRoot;
        _logger = logger;
    }

    public string? CurrentCatalogVersion => GetSnapshot().Catalog?.CatalogVersion;

    public ModelMigrationCatalogStatus GetStatus(bool forceRefresh = false)
    {
        var snapshot = GetSnapshot(forceRefresh);
        return new ModelMigrationCatalogStatus
        {
            IsAvailable = snapshot.IsAvailable,
            IsStale = snapshot.IsStale,
            CatalogVersion = snapshot.Catalog?.CatalogVersion,
            EvidenceAsOfDate = snapshot.Catalog?.EvidenceAsOfDate,
            SourcePath = snapshot.SourcePath,
            SourceLastWriteTimeUtc = snapshot.SourceLastWriteTimeUtc,
            Error = snapshot.Error
        };
    }

    public ModelMigrationCatalogSnapshot GetSnapshot(bool forceRefresh = false)
    {
        lock (_gate)
        {
            var observation = ObserveSource(forceRefresh);
            var key = new CacheKey(observation.SourcePath ?? "<unresolved>", observation.LastWriteTimeUtc);
            if (!forceRefresh && key == _lastAttemptKey && _lastAttempt != null)
                return _lastAttempt;

            if (observation.Error != null)
                return RecordFailure(key, observation.SourcePath, observation.Error);

            try
            {
                var sourcePath = observation.SourcePath!;
                var length = new FileInfo(sourcePath).Length;
                if (length > MaxCatalogBytes)
                    throw new InvalidDataException($"Catalog exceeds the {MaxCatalogBytes} byte limit.");

                var json = File.ReadAllText(sourcePath);
                var catalog = JsonSerializer.Deserialize<ModelMigrationCatalog>(json, JsonOptions)
                              ?? throw new InvalidDataException("Catalog JSON contains null.");
                var validationErrors = ModelMigrationCatalogPolicy.Validate(catalog);
                if (validationErrors.Count > 0)
                    throw new ModelMigrationCatalogValidationException(validationErrors);

                var snapshot = new ModelMigrationCatalogSnapshot
                {
                    Catalog = catalog,
                    SourcePath = sourcePath,
                    SourceLastWriteTimeUtc = observation.LastWriteTimeUtc,
                    IsStale = false
                };
                _lastAttemptKey = key;
                _lastAttempt = snapshot;
                _lastKnownGood = snapshot;
                _logger.LogInformation(
                    "Loaded Token Economy model migration catalog {CatalogVersion} from {CatalogPath}",
                    catalog.CatalogVersion,
                    sourcePath);
                return snapshot;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or ModelMigrationCatalogValidationException)
            {
                return RecordFailure(key, observation.SourcePath, ex.Message);
            }
        }
    }

    public ModelMigrationProposal? FindProposal(
        string? currentModel,
        Func<string, bool> isTargetAvailable,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(isTargetAvailable);
        var catalog = GetSnapshot(forceRefresh).Catalog;
        return catalog == null
            ? null
            : ModelMigrationCatalogPolicy.FindProposal(catalog, currentModel, isTargetAvailable);
    }

    /// <summary>
    /// Returns catalog facts for UI proposal rendering without guessing live
    /// CLI availability. Call <see cref="FindProposal"/> at admission with the
    /// selected CLI's current availability predicate.
    /// </summary>
    public ModelMigrationProposal? GetProposal(string? currentModel, bool forceRefresh = false)
    {
        var catalog = GetSnapshot(forceRefresh).Catalog;
        return catalog == null
            ? null
            : ModelMigrationCatalogPolicy.FindProposal(catalog, currentModel);
    }

    internal static string? ResolveTokenEconomyRepository(
        TaskScannerService scanner,
        ProjectRegistry registry)
    {
        foreach (var project in new[] { "PROJ-015", "Token Economy", "TE" })
        {
            var repository = ProjectRepoResolver.ResolveForProject(project, scanner, registry);
            if (!string.IsNullOrWhiteSpace(repository)) return repository;
        }

        return null;
    }

    private SourceObservation ObserveSource(bool forceRepositoryRefresh)
    {
        string? repository;
        try
        {
            var now = DateTime.UtcNow;
            if (!forceRepositoryRefresh
                && _repositoryRootObservedAtUtc != default
                && now - _repositoryRootObservedAtUtc <= RepositoryResolutionTtl)
            {
                repository = _cachedRepositoryRoot;
            }
            else
            {
                repository = _repositoryRoot();
                _cachedRepositoryRoot = repository;
                _repositoryRootObservedAtUtc = now;
            }
        }
        catch (Exception ex)
        {
            return new SourceObservation(null, null, $"Token Economy repository lookup failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(repository))
            return new SourceObservation(null, null, "The registered Token Economy repository could not be resolved.");

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(Path.Combine(repository, CatalogRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SourceObservation(null, null, $"The Token Economy repository path is invalid: {ex.Message}");
        }

        try
        {
            if (!File.Exists(sourcePath))
                return new SourceObservation(sourcePath, null, $"Model migration catalog was not found at '{sourcePath}'.");
            return new SourceObservation(sourcePath, File.GetLastWriteTimeUtc(sourcePath), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SourceObservation(sourcePath, null, $"Model migration catalog could not be inspected: {ex.Message}");
        }
    }

    private ModelMigrationCatalogSnapshot RecordFailure(
        CacheKey key,
        string? attemptedSourcePath,
        string error)
    {
        var snapshot = new ModelMigrationCatalogSnapshot
        {
            Catalog = _lastKnownGood?.Catalog,
            SourcePath = _lastKnownGood?.SourcePath ?? attemptedSourcePath,
            SourceLastWriteTimeUtc = _lastKnownGood?.SourceLastWriteTimeUtc,
            IsStale = _lastKnownGood != null,
            Error = error
        };
        _lastAttemptKey = key;
        _lastAttempt = snapshot;
        _logger.LogWarning(
            "Token Economy model migration catalog refresh failed for {CatalogPath}; lastKnownGoodRetained={LastKnownGoodRetained}: {Error}",
            attemptedSourcePath ?? "<unresolved>",
            _lastKnownGood != null,
            error);
        return snapshot;
    }

    private readonly record struct CacheKey(string SourcePath, DateTime? LastWriteTimeUtc);
    private sealed record SourceObservation(string? SourcePath, DateTime? LastWriteTimeUtc, string? Error);
}
