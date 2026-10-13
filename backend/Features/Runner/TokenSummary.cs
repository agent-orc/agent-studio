

namespace AgentStudio.Runner;

/// <summary>
/// Per-project token / cost rollup. Three independent dimensions:
/// <list type="bullet">
/// <item><b>Token amounts (real).</b> Per-model input / output / cache
/// counts pulled from <see cref="OrchestratorLogEntry.TokenUsage"/>.</item>
/// <item><b>Theoretical API cost (estimate).</b> Same amounts run
/// through <see cref="TokenPricing"/>. Useful as a comparison and a
/// sanity check; <b>not</b> what the user pays. The CLI subscriptions
/// the runner uses are billed separately and on different units.</item>
/// <item><b>Subscription quota</b> is exposed elsewhere
/// (<c>/api/cli/quota</c>) and not folded in here; aggregating across
/// CLI vendors with different quota models would mislead more than it
/// helps. The frontend points at the quota endpoint with a link.</item>
/// </list>
/// Legacy pure helpers still accept orchestrator-log entries, but the
/// runtime read path is bus-backed and includes coding-agent token events
/// alongside orchestrator/supporting calls.
/// </summary>
public sealed record TokenSummary(
    string Project,
    int OrchestratorEntries,
    int OrchestratorLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    int UnknownModelCount,
    IReadOnlyList<TokenSummaryByModel> ByModel,
    string? FirstActivity,
    string? LastActivity,
    string Disclaimer);

public sealed record TokenSummaryByModel(
    string Model,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool ModelPriced,
    bool ModelInCatalog)
{
    public string ModelId { get; init; } = AgentStudio.Tokens.ModelIdentity.Resolve(Model)?.ModelId ?? Model;
    public string? Cli { get; init; } = AgentStudio.Tokens.ModelIdentity.Resolve(Model)?.Cli;
}

public static class TokenSummaryService
{
    public const string DefaultDisclaimer =
        "Theoretical API cost based on published model-provider list prices. " +
        "Your actual usage is billed through the CLI subscription you signed in with " +
        "(Pro / Max / Team / Enterprise), so the dollar number above is a comparison, " +
        "not a bill.";

    /// <summary>
    /// Pure overload for tests: takes orchestrator log entries directly.
    /// </summary>
    public static Dictionary<string, TaskTokenSummary> SummarizePerJob(IReadOnlyList<OrchestratorLogEntry> entries)
    {
        var perJob = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            var jobId = entry.JobId;
            if (string.IsNullOrWhiteSpace(jobId)) continue;
            if (!perJob.TryGetValue(jobId, out var bucket))
            {
                bucket = new Bucket();
                perJob[jobId] = bucket;
            }
            bucket.Calls++;
            bucket.Input += u.InputTokens;
            bucket.Output += u.OutputTokens;
            bucket.CacheRead += u.CacheReadTokens;
            bucket.CacheCreate += u.CacheCreationTokens;
            var cost = TokenPricing.Estimate(
                u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens,
                u.CacheCreationTokens, entry.Ts);
            bucket.Cost += cost.Total;
            if (!cost.ModelKnown) bucket.AnyUnpriced = true;
            // Stored as the observed id; the label is resolved for rendering.
            var canonicalModel = ModelIdentity.StoredId(u.Model);
            var displayModel = ModelIdentity.Label(u.Model);
            if (entry.Ts > (bucket.LastUpdate ?? DateTime.MinValue))
            {
                bucket.LastUpdate = entry.Ts;
            }
            if (!string.IsNullOrWhiteSpace(displayModel))
            {
                if (entry.Ts > (bucket.LastAnyUpdate ?? DateTime.MinValue))
                {
                    bucket.LastAnyUpdate = entry.Ts;
                    bucket.LastAnyModel = displayModel;
                    bucket.LastAnyModelId = canonicalModel;
                }
                if (TokenParticipants.IsAgent(entry.ParticipantId)
                    && entry.Ts > (bucket.LastAgentUpdate ?? DateTime.MinValue))
                {
                    bucket.LastAgentUpdate = entry.Ts;
                    bucket.LastAgentModel = displayModel;
                    bucket.LastAgentModelId = canonicalModel;
                }
            }
            bucket.Entries.Add(new TaskTokenCall
            {
                Ts = entry.Ts,
                // Canonical id, never the display label - see TaskTokenCall.Model.
                Model = string.IsNullOrWhiteSpace(canonicalModel) ? null : canonicalModel,
                DisplayModel = displayModel,
                ThinkingLevel = string.IsNullOrWhiteSpace(u.ThinkingLevel) ? null : u.ThinkingLevel.Trim(),
                ParticipantId = entry.ParticipantId,
                CliType = string.IsNullOrWhiteSpace(u.CliType) ? null : u.CliType.Trim(),
                Host = string.IsNullOrWhiteSpace(u.Host) ? null : u.Host.Trim(),
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CacheReadTokens = u.CacheReadTokens,
                CacheCreationTokens = u.CacheCreationTokens,
                InputIncludesCached = u.InputIncludesCached,
                UsageNormalization = u.UsageNormalization,
                PinnedModel = string.IsNullOrWhiteSpace(u.PinnedModel)
                    ? null
                    : ModelIdentity.StoredId(u.PinnedModel),
                ModelMismatch = u.ModelMismatch,
                EstimatedApiCostUsd = cost.Total,
                ModelPriced = cost.ModelKnown,
            });
        }

        var result = new Dictionary<string, TaskTokenSummary>(perJob.Count, StringComparer.Ordinal);
        foreach (var (jobId, b) in perJob)
        {
            var total = b.Input + b.Output + b.CacheRead + b.CacheCreate;
            result[jobId] = new TaskTokenSummary
            {
                Calls = b.Calls,
                InputTokens = b.Input,
                OutputTokens = b.Output,
                CacheReadTokens = b.CacheRead,
                CacheCreationTokens = b.CacheCreate,
                TotalTokens = total,
                EstimatedApiCostUsd = b.Cost,
                AllModelsPriced = !b.AnyUnpriced,
                LastModel = b.LastAgentModel ?? b.LastAnyModel,
                LastModelId = b.LastAgentModel is not null ? b.LastAgentModelId : b.LastAnyModelId,
                LastUpdate = b.LastUpdate,
                Entries = b.Entries.OrderBy(e => e.Ts).ToList(),
                HasModelMismatch = b.Entries.Any(entry => entry.ModelMismatch),
            };
        }
        return result;
    }

    public static TaskTokenSummary WithModelFallback(TaskTokenSummary summary, string? modelId)
    {
        var fallbackLabel = ModelIdentity.Label(modelId);
        if (string.IsNullOrWhiteSpace(fallbackLabel)) return summary;
        var fallbackId = ModelIdentity.StoredId(modelId);

        var entries = summary.Entries
            .Select(e => ShouldApplyRunModelFallback(e)
                ? Reprice(e with { Model = fallbackId, DisplayModel = fallbackLabel }, modelId)
                : e)
            .ToList();
        var hasAgentFallbackRow = entries.Any(e =>
            string.Equals(e.Model, fallbackId, StringComparison.Ordinal)
            && TokenParticipants.IsAgent(e.ParticipantId));

        return summary with
        {
            LastModel = string.IsNullOrWhiteSpace(summary.LastModel) && hasAgentFallbackRow ? fallbackLabel : summary.LastModel,
            LastModelId = string.IsNullOrWhiteSpace(summary.LastModel) && hasAgentFallbackRow ? fallbackId : summary.LastModelId,
            Entries = entries,
            EstimatedApiCostUsd = entries.Sum(e => e.EstimatedApiCostUsd),
            AllModelsPriced = entries.Count > 0 && entries.All(e => e.ModelPriced),
        };
    }

    private static TaskTokenCall Reprice(TaskTokenCall entry, string? modelId)
    {
        var cost = TokenPricing.Estimate(modelId, entry.InputTokens, entry.OutputTokens,
            entry.CacheReadTokens, entry.CacheCreationTokens, entry.Ts);
        return entry with
        {
            EstimatedApiCostUsd = cost.Total,
            ModelPriced = cost.ModelKnown,
        };
    }

    private static bool ShouldApplyRunModelFallback(TaskTokenCall entry)
        => TokenParticipants.IsAgent(entry.ParticipantId)
           && string.IsNullOrWhiteSpace(ModelIdentity.Label(entry.Model));

    private sealed class Bucket
    {
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreate;
        public decimal Cost;
        public bool AnyUnpriced;
        public string? LastAnyModel;
        public string? LastAgentModel;
        public string? LastAnyModelId;
        public string? LastAgentModelId;
        public DateTime? LastUpdate;
        public DateTime? LastAnyUpdate;
        public DateTime? LastAgentUpdate;
        public List<TaskTokenCall> Entries { get; } = [];
    }

    /// <summary>
    /// Pure overload: fold a pre-computed list of (project, summary) pairs
    /// into the workspace aggregate. Both the legacy reader and the
    /// Phase-4 bus-backed reader (<c>BusBackedTokenSummaryReader</c>) call
    /// this so the workspace fold is one piece of code regardless of
    /// where the per-project summaries came from.
    /// </summary>
    public static TokenSummaryAggregate AggregateSummaries(
        IReadOnlyList<(string Name, TokenSummary Summary)> projectSummaries,
        TokenSummaryCacheStore? cache = null)
    {
        var perProject = new List<TokenSummaryByProject>();
        var perModel = new Dictionary<string, ModelBucket>(StringComparer.OrdinalIgnoreCase);
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreate = 0;
        int totalEntries = 0, totalCalls = 0, projectCount = 0;
        decimal grandTotal = 0;
        bool allPriced = true;
        bool anyPricedAtAll = false;
        DateTime? firstAt = null;
        DateTime? lastAt = null;

        foreach (var (name, summary) in projectSummaries)
        {
            projectCount++;
            totalEntries += summary.OrchestratorEntries;
            totalCalls += summary.OrchestratorLlmCalls;
            if (summary.FirstActivity is { } fa && DateTime.TryParse(fa, out var faTs))
                if (firstAt == null || faTs < firstAt) firstAt = faTs;
            if (summary.LastActivity is { } la && DateTime.TryParse(la, out var laTs))
                if (lastAt == null || laTs > lastAt) lastAt = laTs;
            totalInput += summary.TotalInputTokens;
            totalOutput += summary.TotalOutputTokens;
            totalCacheRead += summary.TotalCacheReadTokens;
            totalCacheCreate += summary.TotalCacheCreationTokens;
            grandTotal += summary.EstimatedApiCostUsd;
            if (summary.OrchestratorLlmCalls > 0 && !summary.AllModelsPriced) allPriced = false;
            if (summary.OrchestratorLlmCalls > 0) anyPricedAtAll = anyPricedAtAll || summary.AllModelsPriced;

            perProject.Add(new TokenSummaryByProject(
                Project: name,
                OrchestratorLlmCalls: summary.OrchestratorLlmCalls,
                InputTokens: summary.TotalInputTokens,
                OutputTokens: summary.TotalOutputTokens,
                CacheReadTokens: summary.TotalCacheReadTokens,
                CacheCreationTokens: summary.TotalCacheCreationTokens,
                EstimatedApiCostUsd: summary.EstimatedApiCostUsd));

            foreach (var m in summary.ByModel)
            {
                var identity = AgentStudio.Tokens.ModelIdentity.Resolve(m.ModelId);
                var key = identity?.ModelId ?? m.ModelId;
                if (!perModel.TryGetValue(key, out var bucket))
                {
                    bucket = new ModelBucket(key, identity?.DisplayLabel ?? m.Model);
                    perModel[key] = bucket;
                }
                bucket.Calls += m.Calls;
                bucket.Input += m.InputTokens;
                bucket.Output += m.OutputTokens;
                bucket.CacheRead += m.CacheReadTokens;
                bucket.CacheCreate += m.CacheCreationTokens;
                bucket.Cost += m.EstimatedApiCostUsd;
                if (!m.ModelPriced) bucket.AnyUnpriced = true;
                if (!m.ModelInCatalog) bucket.AnyUnknownModel = true;
            }
        }

        var byModel = perModel.Values
            .OrderByDescending(b => b.Input + b.Output)
            .Select(b => new TokenSummaryByModel(
                Model: b.DisplayModel,
                Calls: b.Calls,
                InputTokens: b.Input,
                OutputTokens: b.Output,
                CacheReadTokens: b.CacheRead,
                CacheCreationTokens: b.CacheCreate,
                EstimatedApiCostUsd: b.Cost,
                ModelPriced: !b.AnyUnpriced,
                ModelInCatalog: !b.AnyUnknownModel)
            { ModelId = b.Model, Cli = AgentStudio.Tokens.ModelIdentity.Resolve(b.Model)?.Cli })
            .ToList();

        // If we recorded zero LLM calls anywhere, "all priced" is meaningless.
        if (totalCalls == 0) allPriced = false;

        var aggregate = new TokenSummaryAggregate(
            Projects: projectCount,
            OrchestratorEntries: totalEntries,
            OrchestratorLlmCalls: totalCalls,
            TotalInputTokens: totalInput,
            TotalOutputTokens: totalOutput,
            TotalCacheReadTokens: totalCacheRead,
            TotalCacheCreationTokens: totalCacheCreate,
            EstimatedApiCostUsd: grandTotal,
            AllModelsPriced: allPriced,
            ByModel: byModel,
            ByProject: perProject
                .OrderByDescending(p => p.InputTokens + p.OutputTokens)
                .ToList(),
            FetchedAt: DateTime.UtcNow.ToString("o"),
            FirstActivity: firstAt?.ToString("o"),
            LastActivity: lastAt?.ToString("o"),
            Disclaimer: DefaultDisclaimer);

        // Persist for next-app-start display. Best-effort.
        try { cache?.Write(aggregate); } catch (Exception __ex) { SilentCatch.Note(__ex, "TokenSummary: swallow; tolerant by design"); /* swallow; tolerant by design */ }

        return aggregate;
    }

    /// <summary>
    /// Pure overload: takes the entries directly. Used by the unit tests
    /// to avoid a filesystem round-trip.
    /// </summary>
    public static TokenSummary Summarize(string projectName, IReadOnlyList<OrchestratorLogEntry> entries)
    {
        var perModel = new Dictionary<string, ModelBucket>(StringComparer.OrdinalIgnoreCase);
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreate = 0;
        int callCount = 0;
        DateTime? firstAt = null;
        DateTime? lastAt = null;

        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            callCount++;
            totalInput += u.InputTokens;
            totalOutput += u.OutputTokens;
            totalCacheRead += u.CacheReadTokens;
            totalCacheCreate += u.CacheCreationTokens;

            var ts = entry.Ts.ToUniversalTime();
            if (firstAt == null || ts < firstAt) firstAt = ts;
            if (lastAt == null || ts > lastAt) lastAt = ts;

            // Rows key on the stored id so two model generations never share
            // one row. Pricing keeps the registry id it always used.
            var identity = AgentStudio.Tokens.ModelIdentity.Resolve(u.Model);
            var key = identity?.ModelId ?? "(unknown)";
            if (!perModel.TryGetValue(key, out var bucket))
            {
                bucket = new ModelBucket(
                    key,
                    identity?.DisplayLabel ?? "(unknown)");
                perModel[key] = bucket;
            }
            bucket.Calls++;
            bucket.Input += u.InputTokens;
            bucket.Output += u.OutputTokens;
            bucket.CacheRead += u.CacheReadTokens;
            bucket.CacheCreate += u.CacheCreationTokens;
            var entryCost = TokenPricing.Estimate(
                key, u.InputTokens, u.OutputTokens, u.CacheReadTokens,
                u.CacheCreationTokens, entry.Ts);
            bucket.Cost += entryCost.Total;
            if (!entryCost.ModelKnown) bucket.AnyUnpriced = true;
            if (entryCost.Status == TokenEconomy.PriceStatus.UnknownModel)
                bucket.AnyUnknownModel = true;
        }

        var byModel = new List<TokenSummaryByModel>();
        decimal grandTotal = 0;
        bool allPriced = perModel.Count > 0;
        foreach (var bucket in perModel.Values.OrderByDescending(b => b.Input + b.Output))
        {
            grandTotal += bucket.Cost;
            if (bucket.AnyUnpriced) allPriced = false;
            byModel.Add(new TokenSummaryByModel(
                Model: bucket.DisplayModel,
                Calls: bucket.Calls,
                InputTokens: bucket.Input,
                OutputTokens: bucket.Output,
                CacheReadTokens: bucket.CacheRead,
                CacheCreationTokens: bucket.CacheCreate,
                EstimatedApiCostUsd: bucket.Cost,
                ModelPriced: !bucket.AnyUnpriced,
                ModelInCatalog: !bucket.AnyUnknownModel)
            { ModelId = bucket.Model, Cli = AgentStudio.Tokens.ModelIdentity.Resolve(bucket.Model)?.Cli });
        }

        return new TokenSummary(
            Project: projectName,
            OrchestratorEntries: entries.Count,
            OrchestratorLlmCalls: callCount,
            TotalInputTokens: totalInput,
            TotalOutputTokens: totalOutput,
            TotalCacheReadTokens: totalCacheRead,
            TotalCacheCreationTokens: totalCacheCreate,
            EstimatedApiCostUsd: grandTotal,
            AllModelsPriced: allPriced,
            UnknownModelCount: byModel.Count(model => !model.ModelInCatalog),
            ByModel: byModel,
            FirstActivity: firstAt?.ToString("o"),
            LastActivity: lastAt?.ToString("o"),
            Disclaimer: TokenSummaryService.DefaultDisclaimer);
    }

    private sealed class ModelBucket
    {
        public string Model { get; }
        public string DisplayModel { get; }
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreate;
        public decimal Cost;
        public bool AnyUnpriced;
        public bool AnyUnknownModel;
        public ModelBucket(string model) : this(model, model)
        {
        }

        public ModelBucket(string model, string displayModel)
        {
            Model = model;
            DisplayModel = displayModel;
        }
    }
}
