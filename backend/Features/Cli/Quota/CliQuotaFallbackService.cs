using System.Text.Json;
using System.Collections.Concurrent;

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
    private readonly ModelEquivalenceRouteCatalog _equivalence;
    private readonly object _lock = new();
    private Dictionary<string, CliModelRouteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveQuotaFallback> _active =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaFallbackService(
        IConfiguration config,
        ILogger<CliQuotaFallbackService> logger,
        ModelEquivalenceRouteCatalog? equivalence = null)
    {
        _config = config;
        _logger = logger;
        _equivalence = equivalence ?? new ModelEquivalenceRouteCatalog();
    }

    public IReadOnlyDictionary<string, CliModelRouteProfile> GetAll()
    {
        EnsureLoaded();
        Dictionary<string, CliModelRouteProfile> result;
        lock (_lock)
        {
            result = _profiles.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    ActiveFallback = _active.GetValueOrDefault(pair.Key),
                },
                StringComparer.OrdinalIgnoreCase);
        }

        // A catalogue-derived route is request-tier specific, so do not invent
        // one fixed family profile. An active derived route is nevertheless
        // projected as a family row so the operator sees the live switch.
        foreach (var active in _active.Values)
        {
            if (result.ContainsKey(active.RequestedCliType)) continue;
            result[active.RequestedCliType] = new CliModelRouteProfile
            {
                CliType = active.RequestedCliType,
                PrimaryModel = active.RequestedModel,
                PrimaryThinkingLevel = active.RequestedThinkingLevel,
                FallbackCliType = active.EffectiveCliType,
                FallbackModel = active.EffectiveModel,
                FallbackThinkingLevel = active.EffectiveThinkingLevel,
                RouteSource = active.RouteSource,
                ActiveFallback = active,
            };
        }
        return result;
    }

    public IReadOnlyList<CatalogueEquivalentRouteDefinition> GetCatalogueRoutes() => _equivalence.GetAll();

    public CliModelRouteProfile Set(CliModelRouteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CliType)) throw new ArgumentException("cliType is required");
        var useCatalogue = string.Equals(
            profile.RouteSource,
            CliModelRouteSources.Catalogue,
            StringComparison.OrdinalIgnoreCase);
        var normalized = profile with
        {
            CliType = profile.CliType.Trim().ToLowerInvariant(),
            PrimaryModel = Clean(profile.PrimaryModel),
            PrimaryThinkingLevel = Clean(profile.PrimaryThinkingLevel),
            FallbackCliType = useCatalogue ? null : Clean(profile.FallbackCliType)?.ToLowerInvariant(),
            FallbackModel = useCatalogue ? null : Clean(profile.FallbackModel),
            FallbackThinkingLevel = useCatalogue ? null : Clean(profile.FallbackThinkingLevel),
            RouteSource = useCatalogue
                ? CliModelRouteSources.Catalogue
                : CliModelRouteSources.OperatorOverride,
            ActiveFallback = null,
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

    public CliRouteDecision Resolve(
        string? requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel,
        Func<string?, CapEvaluation> evaluateQuota)
    {
        var cli = Clean(requestedCliType)?.ToLowerInvariant() ?? CliTypes.Claude;
        EnsureLoaded();
        CliModelRouteProfile? profile;
        lock (_lock) _profiles.TryGetValue(cli, out profile);

        var primaryModel = Clean(requestedModel) ?? profile?.PrimaryModel;
        var primaryThinking = Clean(requestedThinkingLevel) ?? profile?.PrimaryThinkingLevel;
        var cap = evaluateQuota(cli);
        if (!cap.Blocked)
            return new(cli, primaryModel, primaryThinking, false, null, cap);

        // New writes identify explicit operator overrides. Legacy profiles
        // without a fallback migrate to catalogue routing, while legacy rows
        // that contain a concrete fallback remain explicit overrides.
        var hasOperatorOverride = string.Equals(
            profile?.RouteSource,
            CliModelRouteSources.OperatorOverride,
            StringComparison.OrdinalIgnoreCase);
        var derived = !hasOperatorOverride
            ? _equivalence.Resolve(cli, primaryModel, primaryThinking)
            : null;
        var fallbackModel = hasOperatorOverride ? profile?.FallbackModel : derived?.Model;
        var fallbackThinking = hasOperatorOverride ? profile?.FallbackThinkingLevel : derived?.ThinkingLevel;
        var fallbackCliType = hasOperatorOverride ? profile?.FallbackCliType : derived?.CliType;
        var source = hasOperatorOverride
            ? CliModelRouteSources.OperatorOverride
            : CliModelRouteSources.Catalogue;
        if (string.IsNullOrWhiteSpace(fallbackModel))
            return new(cli, primaryModel, primaryThinking, false, cap.DescribeReason(), cap, source);

        var fallbackCli = fallbackCliType ?? cli;
        if (string.Equals(fallbackCli, cli, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                cli,
                primaryModel,
                primaryThinking,
                false,
                $"{cap.DescribeReason()}; same-family model fallback cannot bypass a provider quota cap",
                cap,
                source);
        }
        var fallbackCap = evaluateQuota(fallbackCli);
        if (fallbackCap.Blocked)
            return new(cli, primaryModel, primaryThinking, false,
                $"primary {cap.DescribeReason()}; fallback {fallbackCap.DescribeReason()}", cap, source);

        return new(
            fallbackCli,
            fallbackModel,
            fallbackThinking,
            true,
            derived is null
                ? cap.DescribeReason()
                : $"{cap.DescribeReason()}; equivalent tier {derived.TierId} from Token Economy policy {derived.PolicyVersion}",
            cap,
            source,
            derived?.TierId);
    }

    /// <summary>
    /// Track the currently effective family route for UI and accounting. The
    /// marker is acute: any non-fallback decision clears it, so recovered
    /// providers automatically become primary for subsequent launches.
    /// </summary>
    public void RecordAdmission(
        string requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel,
        QuotaAdmissionPlan plan,
        DateTime decidedAt)
    {
        var cli = Clean(requestedCliType)?.ToLowerInvariant() ?? CliTypes.Claude;
        if (!plan.IsFallback)
        {
            _active.TryRemove(cli, out _);
            return;
        }

        _active.AddOrUpdate(
            cli,
            _ => NewActive(decidedAt),
            (_, current) => current with
            {
                RequestedModel = Clean(requestedModel),
                RequestedThinkingLevel = Clean(requestedThinkingLevel),
                EffectiveCliType = plan.CliType,
                EffectiveModel = plan.Model,
                EffectiveThinkingLevel = plan.ThinkingLevel,
                Reason = plan.Reason,
                ResetAt = plan.NextResetAt,
                RouteSource = plan.RouteSource ?? current.RouteSource,
                EquivalentTier = plan.EquivalentTier,
            });
        return;

        ActiveQuotaFallback NewActive(DateTime activatedAt) => new()
        {
            RequestedCliType = cli,
            RequestedModel = Clean(requestedModel),
            RequestedThinkingLevel = Clean(requestedThinkingLevel),
            EffectiveCliType = plan.CliType,
            EffectiveModel = plan.Model,
            EffectiveThinkingLevel = plan.ThinkingLevel,
            Reason = plan.Reason,
            ActivatedAt = activatedAt,
            ResetAt = plan.NextResetAt,
            RouteSource = plan.RouteSource ?? CliModelRouteSources.Catalogue,
            EquivalentTier = plan.EquivalentTier,
        };
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolvePath();
            if (!File.Exists(path)) return;
            try
            {
                var profiles = JsonSerializer.Deserialize<List<CliModelRouteProfile>>(
                    File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                _profiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p.CliType))
                    .Select(p => p with
                    {
                        CliType = p.CliType.Trim().ToLowerInvariant(),
                        RouteSource = string.Equals(
                                          p.RouteSource,
                                          CliModelRouteSources.OperatorOverride,
                                          StringComparison.OrdinalIgnoreCase)
                                      || !string.IsNullOrWhiteSpace(p.FallbackModel)
                            ? CliModelRouteSources.OperatorOverride
                            : CliModelRouteSources.Catalogue,
                        ActiveFallback = null,
                    })
                    .ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to read {File}", path); }
        }
    }

    private void Persist()
    {
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
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-taskboard");
        return Path.Combine(root, FileName);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CliModelRouteProfile
{
    public string CliType { get; init; } = "";
    public string? PrimaryModel { get; init; }
    public string? PrimaryThinkingLevel { get; init; }
    public string? FallbackCliType { get; init; }
    public string? FallbackModel { get; init; }
    public string? FallbackThinkingLevel { get; init; }
    /// <summary><c>catalogue</c> or <c>operator-override</c>.</summary>
    public string RouteSource { get; init; } = "";
    /// <summary>Acute route state. Never persisted as configuration.</summary>
    public ActiveQuotaFallback? ActiveFallback { get; init; }
}

public static class CliModelRouteSources
{
    public const string Catalogue = "catalogue";
    public const string OperatorOverride = "operator-override";
}

public sealed record ActiveQuotaFallback
{
    public string RequestedCliType { get; init; } = "";
    public string? RequestedModel { get; init; }
    public string? RequestedThinkingLevel { get; init; }
    public string EffectiveCliType { get; init; } = "";
    public string? EffectiveModel { get; init; }
    public string? EffectiveThinkingLevel { get; init; }
    public string Reason { get; init; } = "";
    public DateTime ActivatedAt { get; init; }
    public DateTime? ResetAt { get; init; }
    public string RouteSource { get; init; } = CliModelRouteSources.Catalogue;
    public string? EquivalentTier { get; init; }
}

public sealed record CliRouteDecision(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string? Reason,
    CapEvaluation PrimaryCap,
    string? RouteSource = null,
    string? EquivalentTier = null);
