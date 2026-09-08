using System.Text.Json;

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
    private readonly object _lock = new();
    private Dictionary<string, CliModelRouteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaFallbackService(
        IConfiguration config,
        ILogger<CliQuotaFallbackService> logger,
        IModelEquivalenceCatalog? equivalence = null)
    {
        _config = config;
        _logger = logger;
        _equivalence = equivalence;
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

        var effective = EffectiveFallback(cli, profile, primaryModel, primaryThinking);
        if (effective is null)
            return new(cli, primaryModel, primaryThinking, false, cap.DescribeReason(), cap);

        var (fallbackCli, fallbackModel, fallbackThinking) = effective.Value;
        // A provider-wide cap cannot be escaped by selecting another model in
        // the same CLI family. Keep explicit same-family overrides available
        // only for model-specific windows, and never recurse into a second
        // profile, so a codex -> claude -> codex cycle is impossible.
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
                $"primary {cap.DescribeReason()}; fallback {fallbackCap.DescribeReason()}", cap);

        return new(fallbackCli, fallbackModel, fallbackThinking, true, cap.DescribeReason(), cap);
    }

    /// <summary>
    /// The fallback CLI/model/thinking level that would apply for
    /// <paramref name="cli"/> right now: the operator's explicit
    /// <paramref name="profile"/> when it names a fallback model, else the
    /// equivalence-catalogue-derived pair for the other CLI family. Returns
    /// null when neither source has an answer (no fallback configured, no
    /// known equivalence tier, or this CLI has no "other family" partner).
    /// </summary>
    private (string CliType, string? Model, string? ThinkingLevel)? EffectiveFallback(
        string cli, CliModelRouteProfile? profile, string? primaryModel, string? primaryThinking)
    {
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.FallbackModel))
            return (profile.FallbackCliType ?? cli, profile.FallbackModel, profile.FallbackThinkingLevel);

        var otherFamily = OtherFamily(cli);
        if (otherFamily is null || _equivalence is null) return null;
        var equivalent = _equivalence.TryGetEquivalent(cli, primaryModel, primaryThinking, otherFamily);
        return equivalent is null ? null : (otherFamily, equivalent.Value.Model, equivalent.Value.ThinkingLevel);
    }

    /// <summary>
    /// The counterpart CLI family a catalogue-derived fallback would route to.
    /// Only Claude and Codex have a documented equivalence tier today; Gemini
    /// and any future CLI type have no automatic catalogue partner, so an
    /// operator override remains required for them.
    /// </summary>
    private static string? OtherFamily(string cli) => cli switch
    {
        _ when string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase) => CliTypes.Claude,
        _ when string.Equals(cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase) => CliTypes.Codex,
        _ => null,
    };

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

        var otherFamily = OtherFamily(cli);
        var equivalent = otherFamily is null || _equivalence is null
            ? null
            : _equivalence.TryGetEquivalent(cli, profile?.PrimaryModel, profile?.PrimaryThinkingLevel, otherFamily);
        if (equivalent is null) return profile ?? new CliModelRouteProfile { CliType = cli };
        return (profile ?? new CliModelRouteProfile { CliType = cli }) with
        {
            FallbackCliType = otherFamily,
            FallbackModel = equivalent.Value.Model,
            FallbackThinkingLevel = equivalent.Value.ThinkingLevel,
            IsFallbackDerived = true,
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
    /// <summary>
    /// True when <see cref="GetEffectiveProfile"/> filled the fallback fields
    /// from the equivalence catalogue because the operator configured none.
    /// Always false on a profile read from <see cref="GetAll"/> or persisted
    /// via <see cref="Set"/> - this is a read-time display marker, never
    /// written to <c>cli-model-routing.json</c>.
    /// </summary>
    public bool IsFallbackDerived { get; init; }
}

public sealed record CliRouteDecision(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string? Reason,
    CapEvaluation PrimaryCap);
