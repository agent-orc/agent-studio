using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// Workspace-wide primary/fallback routing for CLI models. The persisted
/// profile is advisory until the primary CLI reaches a configured quota cap;
/// then the fallback is selected for that run only. No task metadata is
/// rewritten, so the next run returns to primary as soon as the snapshot is
/// below the cap again.
/// </summary>
public sealed class CliQuotaFallbackService
{
    private const string FileName = "cli-model-routing.json";
    private readonly IConfiguration _config;
    private readonly ILogger<CliQuotaFallbackService> _logger;
    private readonly IModelEquivalenceCatalog? _equivalence;
    private readonly ModelSelectionPolicyStore? _policy;
    private readonly object _lock = new();
    private Dictionary<string, CliModelRouteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaFallbackService(
        IConfiguration config,
        ILogger<CliQuotaFallbackService> logger,
        IModelEquivalenceCatalog? equivalence = null,
        ModelSelectionPolicyStore? policy = null)
    {
        _config = config;
        _logger = logger;
        _equivalence = equivalence;
        _policy = policy;
    }

    public IReadOnlyDictionary<string, CliModelRouteProfile> GetAll()
    {
        EnsureLoaded();
        lock (_lock) return new Dictionary<string, CliModelRouteProfile>(_profiles, StringComparer.OrdinalIgnoreCase);
    }

    public CliModelRouteProfile Set(CliModelRouteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CliType)) throw new ArgumentException("cliType is required");
        var normalized = profile with
        {
            CliType = profile.CliType.Trim().ToLowerInvariant(),
            PrimaryModel = Clean(profile.PrimaryModel),
            PrimaryThinkingLevel = Clean(profile.PrimaryThinkingLevel),
            FallbackCliType = Clean(profile.FallbackCliType)?.ToLowerInvariant(),
            FallbackModel = Clean(profile.FallbackModel),
            FallbackThinkingLevel = Clean(profile.FallbackThinkingLevel),
        };
        EnsureLoaded();
        lock (_lock)
        {
            _profiles[normalized.CliType] = normalized;
            Persist();
        }
        _logger.LogInformation(
            "cli_quota_fallback_configured primaryCli={PrimaryCli} primaryModel={PrimaryModel} fallbackCli={FallbackCli} fallbackModel={FallbackModel}",
            normalized.CliType, normalized.PrimaryModel ?? "<cli-default>",
            normalized.FallbackCliType ?? normalized.CliType, normalized.FallbackModel ?? "<disabled>");
        return normalized;
    }

    /// <summary>
    /// Thin adapter over <see cref="ModelSelectionPlanner.ResolveRoute"/> (MS-5):
    /// the primary/fallback rule lives in the shared planner; this service
    /// supplies the persisted profiles and the equivalence catalogue. Kept
    /// until the remaining consumers call the selection service (MS-6).
    /// </summary>
    public CliRouteDecision Resolve(
        string? requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel,
        Func<string?, CapEvaluation> evaluateQuota,
        bool preferFallback = false,
        Func<string, string?, bool>? modelAccepted = null,
        bool sameClassOnly = false,
        Func<string, bool>? fallbackProviderAllowed = null)
        => ModelSelectionPlanner.ResolveRoute(
            requestedCliType, requestedModel, requestedThinkingLevel, RoutingFacts(), evaluateQuota,
            preferFallback, modelAccepted, sameClassOnly, fallbackProviderAllowed);

    /// <summary>The routing map as planner facts: persisted profiles plus the Token Economy catalogue.</summary>
    public SelectionRoutingFacts RoutingFacts()
    {
        EnsureLoaded();
        return new SelectionRoutingFacts(Profile, _equivalence, ModelMetadataRegistry.ThinkingLevelsFor);
    }

    private CliModelRouteProfile? Profile(string cliType)
    {
        lock (_lock) return _profiles.TryGetValue(cliType, out var profile) ? profile : null;
    }

    /// <summary>
    /// Read-only projection of the profile GET /model-routes returns: the
    /// operator's persisted profile when one exists, merged with a
    /// catalogue-derived fallback for display when the operator configured
    /// none. <see cref="CliModelRouteProfile.IsFallbackDerived"/> tells the
    /// frontend which case it is.
    /// </summary>
    public CliModelRouteProfile GetEffectiveProfile(string cliType)
    {
        var cli = Clean(cliType)?.ToLowerInvariant() ?? CliTypes.Claude;
        EnsureLoaded();
        CliModelRouteProfile? profile;
        lock (_lock) _profiles.TryGetValue(cli, out profile);
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.FallbackModel)) return profile;

        var otherFamily = ModelSelectionPlanner.OtherFamily(cli);
        var equivalent = otherFamily is null || _equivalence is null
            ? null
            : _equivalence.TryGetRoute(cli, profile?.PrimaryModel, profile?.PrimaryThinkingLevel, otherFamily);
        if (equivalent is null) return profile ?? new CliModelRouteProfile { CliType = cli };
        return (profile ?? new CliModelRouteProfile { CliType = cli }) with
        {
            FallbackCliType = otherFamily,
            FallbackModel = equivalent.ToModel,
            FallbackThinkingLevel = equivalent.ToThinkingLevel,
            IsFallbackDerived = true,
            CatalogueVersion = equivalent.CatalogueVersion,
        };
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            if (_policy is not null)
            {
                _profiles = new Dictionary<string, CliModelRouteProfile>(_policy.Get().LegacyProfiles,
                    StringComparer.OrdinalIgnoreCase);
                return;
            }
            var path = ResolvePath();
            if (!File.Exists(path)) return;
            try
            {
                var profiles = JsonSerializer.Deserialize<List<CliModelRouteProfile>>(
                    File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                _profiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p.CliType))
                    .ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to read {File}", path); }
        }
    }

    private void Persist()
    {
        if (_policy is not null)
        {
            foreach (var profile in _profiles.Values) _policy.SetLegacyProfile(profile);
            return;
        }
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_profiles.Values.OrderBy(p => p.CliType),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to write {File}", path); }
    }

    private string ResolvePath()
    {
        var root = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = LocalStateRoot.Combine("agent-taskboard");
        return Path.Combine(root, FileName);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// CliModelRouteProfile and CliRouteDecision live in the contracts package
// (ModelSelection/QuotaDecisionContracts.cs).
