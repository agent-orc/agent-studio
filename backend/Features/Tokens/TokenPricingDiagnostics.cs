namespace AgentStudio.Tokens;

public static class TokenPricingGapStatuses
{
    public const string MissingModelId = "MissingModelId";
}

public sealed record UnknownTokenModelUsage(
    string? ModelId,
    string Status,
    int Calls,
    long TotalTokens,
    IReadOnlyList<string> RecordedValues,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Sources);

public sealed record TokenPricingUnknownModelsDiagnostic(
    string CatalogProvider,
    string CatalogVersion,
    int CatalogModelCount,
    int ScannedProjectCount,
    IReadOnlyList<string> ScannedProjects,
    IReadOnlyList<string> ScannedSources,
    int ScannedModelCount,
    int UnpricedModelCount,
    int UnpricedCallCount,
    IReadOnlyList<UnknownTokenModelUsage> UnpricedModels,
    int UnknownModelCount,
    int UnknownCallCount,
    IReadOnlyList<UnknownTokenModelUsage> UnknownModels);

/// <summary>
/// Pure projection of pricing gaps in active lifetime usage. The complete gap
/// list includes known models with no price for their recorded date; the
/// unknown-model subset isolates catalog identity drift.
/// </summary>
public static class TokenPricingDiagnostics
{
    public const string ProjectRuntimeSource = "project-lifetime-hybrid";
    public const string AdHocSource = "ad-hoc-workspace-bus";

    public static TokenPricingUnknownModelsDiagnostic Build(
        IReadOnlyList<(string Project, TokenSummary Summary)> projectSummaries,
        AdHocUsageAggregate? adHoc = null)
    {
        var gaps = new Dictionary<string, GapBucket>(StringComparer.OrdinalIgnoreCase);
        var scannedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (project, summary) in projectSummaries)
        {
            foreach (var model in summary.ByModel)
            {
                Add(
                    gaps,
                    scannedModels,
                    model.ModelId ?? model.Model,
                    model.Model,
                    model.Calls,
                    TotalTokens(model.InputTokens, model.OutputTokens,
                        model.CacheReadTokens, model.CacheCreationTokens),
                    model.ModelPriced,
                    ProjectRuntimeSource,
                    project);
            }
        }

        if (adHoc is not null)
        {
            foreach (var model in adHoc.ByModel)
            {
                Add(
                    gaps,
                    scannedModels,
                    model.ModelId ?? model.Model,
                    model.Model,
                    model.Calls,
                    TotalTokens(model.InputTokens, model.OutputTokens,
                        model.CacheReadTokens, model.CacheCreationTokens),
                    model.ModelPriced,
                    AdHocSource,
                    project: null);
            }
        }

        var unpricedModels = gaps.Values
            .OrderByDescending(gap => gap.TotalTokens)
            .ThenBy(gap => gap.ModelId ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(gap => new UnknownTokenModelUsage(
                gap.ModelId,
                gap.Status,
                gap.Calls,
                gap.TotalTokens,
                gap.RecordedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                gap.Projects.OrderBy(project => project, StringComparer.OrdinalIgnoreCase).ToList(),
                gap.Sources.OrderBy(source => source, StringComparer.Ordinal).ToList()))
            .ToList();
        var unknownModels = unpricedModels
            .Where(model => model.Status != TokenEconomy.PriceStatus.NoPriceForDate.ToString())
            .ToList();

        var scannedSources = new List<string>();
        if (projectSummaries.Count > 0) scannedSources.Add(ProjectRuntimeSource);
        if (adHoc is not null) scannedSources.Add(AdHocSource);

        return new TokenPricingUnknownModelsDiagnostic(
            CatalogProvider: "TokenEconomy",
            CatalogVersion: TokenPricing.CatalogVersion,
            CatalogModelCount: TokenPricing.Catalog.Count,
            ScannedProjectCount: projectSummaries.Count,
            ScannedProjects: projectSummaries
                .Select(summary => summary.Project)
                .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ScannedSources: scannedSources,
            ScannedModelCount: scannedModels.Count,
            UnpricedModelCount: unpricedModels.Count,
            UnpricedCallCount: unpricedModels.Sum(model => model.Calls),
            UnpricedModels: unpricedModels,
            UnknownModelCount: unknownModels.Count,
            UnknownCallCount: unknownModels.Sum(model => model.Calls),
            UnknownModels: unknownModels);
    }

    private static void Add(
        Dictionary<string, GapBucket> gaps,
        HashSet<string> scannedModels,
        string? modelId,
        string? recordedValue,
        int calls,
        long totalTokens,
        bool modelPriced,
        string source,
        string? project)
    {
        if (calls <= 0) return;

        var canonical = TokenPricing.CanonicalModelId(modelId);
        var missing = IsMissingModelId(canonical);
        var key = missing ? "\0missing-model-id" : canonical;
        scannedModels.Add(key);

        var inCatalog = !missing && TokenPricing.ModelInCatalog(canonical);
        if (inCatalog && modelPriced) return;

        var status = missing
            ? TokenPricingGapStatuses.MissingModelId
            : inCatalog
                ? TokenEconomy.PriceStatus.NoPriceForDate.ToString()
                : TokenEconomy.PriceStatus.UnknownModel.ToString();
        if (!gaps.TryGetValue(key, out var gap))
        {
            gap = new GapBucket(
                missing ? null : canonical,
                status);
            gaps[key] = gap;
        }

        gap.Calls += calls;
        gap.TotalTokens += totalTokens;
        gap.RecordedValues.Add(string.IsNullOrWhiteSpace(recordedValue) ? "(unknown)" : recordedValue.Trim());
        gap.Sources.Add(source);
        if (!string.IsNullOrWhiteSpace(project)) gap.Projects.Add(project);
    }

    private static bool IsMissingModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return true;
        var value = modelId.Trim();
        return string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "(unknown)", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "?", StringComparison.Ordinal);
    }

    private static long TotalTokens(long input, long output, long cacheRead, long cacheCreation)
        => input + output + cacheRead + cacheCreation;

    private sealed class GapBucket
    {
        public GapBucket(string? modelId, string status)
        {
            ModelId = modelId;
            Status = status;
        }

        public string? ModelId { get; }
        public string Status { get; }
        public int Calls { get; set; }
        public long TotalTokens { get; set; }
        public HashSet<string> RecordedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
    }
}
