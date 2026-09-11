using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// CLI quota and model-routing policy surface: the durable, code-shipped
/// routing policy (embedded resource, same source file the legacy backend
/// embeds) plus small durable overrides (economy mode, per-CLI usage caps,
/// wait policy, model-route fallbacks). The recommendation route omits the
/// legacy live-CLI-binary catalogue probe (Task Server has no local CLI to
/// probe) and instead always resolves against the full policy tier list -
/// the same tiers <c>cli/model-routing/policy</c> already exposes as durable
/// Task Server state.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string CliQuotaScope = "cli-quota";
    private const string EconomyModeKey = "economy-mode";
    private const string QuotaCapsKey = "caps";
    private const string QuotaWaitPolicyKey = "wait-policy";
    private const string ModelRoutesKey = "model-routes";
    private const string ModelRoutingPolicyResource = "AgentStudio.TaskServer.ModelRoutingPolicy";

    private static readonly Lazy<ModelRoutingPolicyDocument> Policy = new(LoadPolicy);

    public async Task<StudioModelRoutingPolicyView> GetModelRoutingPolicyAsync(CancellationToken ct)
    {
        var economyMode = await GetSettingAsync<BoolSetting>(CliQuotaScope, EconomyModeKey, ct);
        var policy = Policy.Value;
        var rows = policy.Tiers
            .OrderBy(tier => tier.Rank)
            .Select(tier => new StudioModelRoutingPolicyRow(tier.Id, tier.Model, tier.ThinkingLevel))
            .ToList();
        return new StudioModelRoutingPolicyView(economyMode?.Value ?? false, policy.Version, rows);
    }

    public async Task<StudioModelRoutingRecommendation> GetModelRoutingRecommendationAsync(
        string taskType, string cliType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskType))
            throw new ArgumentException("taskType is required.");
        if (!StudioCliTypes.All.Contains(cliType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown cliType '{cliType}'.");

        var policy = Policy.Value;
        var baseTierId = policy.TaskTypeDefaults.TryGetValue(taskType, out var def)
            ? def.Tier
            : policy.Tiers.OrderBy(t => t.Rank).First(t => t.EstimatedSavingsPercent == 0).Id;
        var tiersByRank = policy.Tiers.OrderBy(t => t.Rank).ToList();
        var baseTier = tiersByRank.First(t => string.Equals(t.Id, baseTierId, StringComparison.OrdinalIgnoreCase));

        var economyMode = (await GetSettingAsync<BoolSetting>(CliQuotaScope, EconomyModeKey, ct))?.Value ?? false;
        var resolvedTier = baseTier;
        if (economyMode)
        {
            var targetRank = Math.Max(0, baseTier.Rank - policy.EconomyMode.DowngradeSteps);
            resolvedTier = tiersByRank.FirstOrDefault(t => t.Rank == targetRank) ?? baseTier;
        }

        return new StudioModelRoutingRecommendation(
            resolvedTier.Model, resolvedTier.ThinkingLevel, resolvedTier.Id, taskType,
            EconomyDowngraded: economyMode && resolvedTier.Rank != baseTier.Rank,
            policy.Version, policy.WikiPath);
    }

    public async Task<StudioCliQuotaCaps> GetCliQuotaCapsAsync(CancellationToken ct)
        => await GetSettingAsync<StudioCliQuotaCaps>(CliQuotaScope, QuotaCapsKey, ct)
           ?? new StudioCliQuotaCaps(95, new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase));

    public async Task<StudioCliQuotaWaitPolicy> GetCliQuotaWaitPolicyAsync(CancellationToken ct)
        => await GetSettingAsync<StudioCliQuotaWaitPolicy>(CliQuotaScope, QuotaWaitPolicyKey, ct)
           ?? new StudioCliQuotaWaitPolicy(false, 30);

    public async Task<StudioCliModelRoutesResponse> GetCliModelRoutesAsync(CancellationToken ct)
    {
        var overrides = await GetSettingAsync<Dictionary<string, StudioCliModelRouteProfile>>(
            CliQuotaScope, ModelRoutesKey, ct) ?? [];
        var profiles = StudioCliTypes.All.ToDictionary(
            cli => cli,
            cli => overrides.TryGetValue(cli, out var profile)
                ? profile
                : new StudioCliModelRouteProfile(cli, null, null, null, null, null, IsFallbackDerived: true),
            StringComparer.OrdinalIgnoreCase);
        return new StudioCliModelRoutesResponse(profiles);
    }

    private static ModelRoutingPolicyDocument LoadPolicy()
    {
        using var stream = typeof(TaskServerStore).Assembly.GetManifestResourceStream(ModelRoutingPolicyResource)
            ?? throw new InvalidOperationException($"Embedded model-routing policy '{ModelRoutingPolicyResource}' is missing.");
        var document = JsonSerializer.Deserialize<ModelRoutingPolicyDocument>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return document ?? throw new InvalidOperationException("Embedded model-routing policy is empty.");
    }

    private sealed record BoolSetting(bool Value);
}

internal sealed record ModelRoutingPolicyTier(
    string Id, int Rank, string Model, string? ThinkingLevel, int EstimatedSavingsPercent);

internal sealed record ModelRoutingTaskTypeDefault(string Tier, int Score, [property: JsonPropertyName("hardFloorTier")] string? HardFloorTier);

internal sealed record ModelRoutingEconomyMode(int DowngradeSteps, string Label);

internal sealed record ModelRoutingPolicyDocument(
    string Version,
    string WikiPath,
    IReadOnlyList<ModelRoutingPolicyTier> Tiers,
    IReadOnlyDictionary<string, ModelRoutingTaskTypeDefault> TaskTypeDefaults,
    ModelRoutingEconomyMode EconomyMode);
