using System.Text.Json;
using AgentStudio.Shared;

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
    private readonly object _lock = new();
    private Dictionary<string, CliModelRouteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveQuotaFallback> _activeFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaFallbackService(IConfiguration config, ILogger<CliQuotaFallbackService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, CliModelRouteProfile> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            var result = new Dictionary<string, CliModelRouteProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var cli in new[] { CliTypes.Claude, CliTypes.Codex })
            {
                _profiles.TryGetValue(cli, out var profile);
                result[cli] = MaterializeProfile(profile ?? new CliModelRouteProfile { CliType = cli });
            }
            foreach (var (cli, profile) in _profiles)
                result[cli] = MaterializeProfile(profile);
            return result;
        }
    }

    public CliModelRouteProfile Set(CliModelRouteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CliType)) throw new ArgumentException("cliType is required");
        var source = Clean(profile.FallbackSource)?.ToLowerInvariant();
        var useCatalogue = string.Equals(source, CliFallbackSources.Catalogue, StringComparison.OrdinalIgnoreCase);
        var normalized = profile with
        {
            CliType = profile.CliType.Trim().ToLowerInvariant(),
            PrimaryModel = Clean(profile.PrimaryModel),
            PrimaryThinkingLevel = Clean(profile.PrimaryThinkingLevel),
            FallbackCliType = useCatalogue ? null : Clean(profile.FallbackCliType)?.ToLowerInvariant(),
            FallbackModel = useCatalogue ? null : Clean(profile.FallbackModel),
            FallbackThinkingLevel = useCatalogue ? null : Clean(profile.FallbackThinkingLevel),
            FallbackDisabled = profile.FallbackDisabled
                               || string.Equals(source, CliFallbackSources.Disabled, StringComparison.OrdinalIgnoreCase),
            FallbackSource = null,
        };
        EnsureLoaded();
        lock (_lock)
        {
            _profiles[normalized.CliType] = normalized;
            _activeFallbacks.Remove(normalized.CliType);
            Persist();
        }
        _logger.LogInformation(
            "cli_quota_fallback_configured primaryCli={PrimaryCli} primaryModel={PrimaryModel} fallbackCli={FallbackCli} fallbackModel={FallbackModel}",
            normalized.CliType, normalized.PrimaryModel ?? "<cli-default>",
            normalized.FallbackCliType ?? normalized.CliType, normalized.FallbackModel ?? "<disabled>");
        return MaterializeProfile(normalized);
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

        var primaryModel = Clean(requestedModel) ?? profile?.PrimaryModel ?? ModelMetadataRegistry.DefaultForCli(cli);
        var primaryThinking = Clean(requestedThinkingLevel) ?? profile?.PrimaryThinkingLevel;
        var cap = evaluateQuota(cli);
        if (!cap.Blocked)
            return new(cli, primaryModel, primaryThinking, false, null, cap);

        if (profile?.FallbackDisabled == true)
            return new(cli, primaryModel, primaryThinking, false, cap.DescribeReason(), cap);

        var equivalent = string.IsNullOrWhiteSpace(profile?.FallbackModel)
            ? ModelMetadataRegistry.EquivalentFor(cli, primaryModel, primaryThinking)
            : null;
        var fallbackCli = profile?.FallbackCliType
            ?? (!string.IsNullOrWhiteSpace(profile?.FallbackModel) ? cli : equivalent?.TargetCliType);
        var fallbackModel = profile?.FallbackModel ?? equivalent?.TargetModel;
        var fallbackThinking = profile?.FallbackThinkingLevel ?? equivalent?.TargetThinkingLevel;
        var fallbackSource = string.IsNullOrWhiteSpace(profile?.FallbackModel)
            ? CliFallbackSources.Catalogue
            : CliFallbackSources.Override;
        if (string.IsNullOrWhiteSpace(fallbackCli) || string.IsNullOrWhiteSpace(fallbackModel))
            return new(cli, primaryModel, primaryThinking, false, cap.DescribeReason(), cap);

        // A provider-wide window cannot be escaped by changing models within
        // the same CLI family. Model-specific windows remain eligible for an
        // explicit same-family override.
        if (string.Equals(fallbackCli, cli, StringComparison.OrdinalIgnoreCase)
            && cap.WindowLabel?.Contains("model", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new(cli, primaryModel, primaryThinking, false,
                $"primary {cap.DescribeReason()}; same-family fallback cannot bypass a provider cap", cap);
        }

        var fallbackCap = string.Equals(fallbackCli, cli, StringComparison.OrdinalIgnoreCase)
            ? CapEvaluation.NotBlocked
            : evaluateQuota(fallbackCli);
        if (fallbackCap.Blocked)
            return new(cli, primaryModel, primaryThinking, false,
                $"primary {cap.DescribeReason()}; fallback {fallbackCap.DescribeReason()}", cap,
                FallbackSource: fallbackSource,
                AttemptedFallbackCliType: fallbackCli);

        return new(
            fallbackCli,
            fallbackModel,
            fallbackThinking,
            true,
            cap.DescribeReason(),
            cap,
            fallbackSource,
            fallbackCli);
    }

    /// <summary>
    /// Records the current effective route transition for the Studio panel.
    /// The timestamp is the first time this process observed the active switch,
    /// not the quota probe timestamp.
    /// </summary>
    public void ObserveAdmission(string? primaryCliType, QuotaAdmissionPlan plan, DateTime observedAt)
    {
        var primaryCli = Clean(primaryCliType)?.ToLowerInvariant() ?? CliTypes.Claude;
        lock (_lock)
        {
            if (!plan.IsFallback)
            {
                _activeFallbacks.Remove(primaryCli);
                return;
            }

            if (_activeFallbacks.TryGetValue(primaryCli, out var existing)
                && string.Equals(existing.EffectiveCliType, plan.CliType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.EffectiveModel, plan.Model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Reason, plan.Reason, StringComparison.Ordinal))
            {
                _activeFallbacks[primaryCli] = existing with { ResetAt = plan.NextResetAt };
                return;
            }

            _activeFallbacks[primaryCli] = new ActiveQuotaFallback(
                primaryCli,
                plan.CliType,
                plan.Model,
                plan.ThinkingLevel,
                plan.Outcome.ToString(),
                plan.Reason,
                observedAt,
                plan.NextResetAt);
        }
    }

    public IReadOnlyList<ActiveQuotaFallback> GetActiveFallbacks()
    {
        lock (_lock) return _activeFallbacks.Values.OrderBy(value => value.PrimaryCliType).ToList();
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

    private static CliModelRouteProfile MaterializeProfile(CliModelRouteProfile profile)
    {
        var primaryModel = profile.PrimaryModel ?? ModelMetadataRegistry.DefaultForCli(profile.CliType);
        if (profile.FallbackDisabled)
            return profile with { PrimaryModel = primaryModel, FallbackSource = CliFallbackSources.Disabled };
        if (!string.IsNullOrWhiteSpace(profile.FallbackModel))
            return profile with { PrimaryModel = primaryModel, FallbackSource = CliFallbackSources.Override };

        var equivalent = ModelMetadataRegistry.EquivalentFor(
            profile.CliType,
            primaryModel,
            profile.PrimaryThinkingLevel);
        return equivalent is null
            ? profile with { PrimaryModel = primaryModel, FallbackSource = CliFallbackSources.None }
            : profile with
            {
                PrimaryModel = primaryModel,
                FallbackCliType = equivalent.TargetCliType,
                FallbackModel = equivalent.TargetModel,
                FallbackThinkingLevel = equivalent.TargetThinkingLevel,
                FallbackSource = CliFallbackSources.Catalogue,
            };
    }
}

public sealed record CliModelRouteProfile
{
    public string CliType { get; init; } = "";
    public string? PrimaryModel { get; init; }
    public string? PrimaryThinkingLevel { get; init; }
    public string? FallbackCliType { get; init; }
    public string? FallbackModel { get; init; }
    public string? FallbackThinkingLevel { get; init; }
    public bool FallbackDisabled { get; init; }
    /// <summary><c>catalogue</c>, <c>override</c>, <c>disabled</c>, or <c>none</c>.</summary>
    public string? FallbackSource { get; init; }
}

public sealed record CliRouteDecision(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string? Reason,
    CapEvaluation PrimaryCap,
    string? FallbackSource = null,
    string? AttemptedFallbackCliType = null);

public static class CliFallbackSources
{
    public const string Catalogue = "catalogue";
    public const string Override = "override";
    public const string Disabled = "disabled";
    public const string None = "none";
}

public sealed record ActiveQuotaFallback(
    string PrimaryCliType,
    string EffectiveCliType,
    string? EffectiveModel,
    string? EffectiveThinkingLevel,
    string Outcome,
    string Reason,
    DateTime ActivatedAt,
    DateTime? ResetAt);
