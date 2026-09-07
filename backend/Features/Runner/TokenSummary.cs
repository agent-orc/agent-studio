

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
    bool ModelInCatalog,
    string? ModelId = null);

public class TokenSummaryService
{
    public const string DefaultDisclaimer =
        "Theoretical API cost based on published model-provider list prices. " +
        "Your actual usage is billed through the CLI subscription you signed in with " +
        "(Pro / Max / Team / Enterprise), so the dollar number above is a comparison, " +
        "not a bill.";

    private readonly OrchestratorLog _log;
    private readonly TokenSummaryCacheStore? _cache;
    private readonly BusBackedTokenSummaryReader? _busReader;
    private readonly IConfiguration? _config;

    public TokenSummaryService(
        OrchestratorLog log,
        TokenSummaryCacheStore? cache = null,
        BusBackedTokenSummaryReader? busReader = null,
        IConfiguration? config = null)
    {
        _log = log;
        _cache = cache;
        _busReader = busReader;
        _config = config;
    }

    public TokenSummary Summarize(string projectName, string watchPath)
    {
        if (_busReader != null)
            return _busReader.Summarize(projectName);

        var entries = _log.Read(watchPath);
        return Summarize(projectName, entries);
    }

    /// <summary>
    /// Read the orchestrator log once for a watch path and produce a
    /// per-job token rollup. The kanban card uses this to render a
    /// colour-tiered "token bubble" with a hover popover. Returns an
    /// empty dictionary when the log file does not exist.
    ///
    /// <para>
    /// Performance: O(N) over orchestrator entries, single sequential
    /// read of <c>orchestrator.jsonl</c>. Callers must batch this per
    /// watch path so the perf contract on
    /// <c>TaskEndpointHelpers.WithRuntime</c> (no per-job disk I/O)
    /// stays intact.
    /// </para>
    /// </summary>
    public Dictionary<string, TaskTokenSummary> SummarizePerJob(string watchPath)
    {
        if (_busReader != null)
        {
            var projectName = ResolveProjectName(watchPath);
            return string.IsNullOrWhiteSpace(projectName)
                ? new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal)
                : _busReader.SummarizePerJob(projectName);
        }

        var entries = _log.Read(watchPath);
        return SummarizePerJob(entries);
    }

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
            var displayModel = TokenModelDisplay.Label(u.Model);
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
                }
                if (TokenModelDisplay.IsAgentParticipant(entry.ParticipantId)
                    && entry.Ts > (bucket.LastAgentUpdate ?? DateTime.MinValue))
                {
                    bucket.LastAgentUpdate = entry.Ts;
                    bucket.LastAgentModel = displayModel;
                }
            }
            bucket.Entries.Add(new TaskTokenCall
            {
                Ts = entry.Ts,
                Model = displayModel,
                ParticipantId = entry.ParticipantId,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CacheReadTokens = u.CacheReadTokens,
                CacheCreationTokens = u.CacheCreationTokens,
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
                LastUpdate = b.LastUpdate,
                Entries = b.Entries.OrderBy(e => e.Ts).ToList()
            };
        }
        return result;
    }

    public static TaskTokenSummary WithModelFallback(TaskTokenSummary summary, string? modelId)
    {
        var fallback = TokenModelDisplay.Label(modelId);
        if (string.IsNullOrWhiteSpace(fallback)) return summary;

        var entries = summary.Entries
            .Select(e => ShouldApplyRunModelFallback(e)
                ? Reprice(e with { Model = fallback }, modelId)
                : e)
            .ToList();
        var hasAgentFallbackRow = entries.Any(e =>
            string.Equals(e.Model, fallback, StringComparison.Ordinal)
            && TokenModelDisplay.IsAgentParticipant(e.ParticipantId));

        return summary with
        {
            LastModel = string.IsNullOrWhiteSpace(summary.LastModel) && hasAgentFallbackRow ? fallback : summary.LastModel,
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
        => TokenModelDisplay.IsAgentParticipant(entry.ParticipantId)
           && string.IsNullOrWhiteSpace(TokenModelDisplay.Label(entry.Model));

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
        public DateTime? LastUpdate;
        public DateTime? LastAnyUpdate;
        public DateTime? LastAgentUpdate;
        public List<TaskTokenCall> Entries { get; } = [];
    }

    /// <summary>
    /// Workspace-wide aggregate: walks every watched project, runs the
    /// per-project summarizer, and folds amounts + per-model buckets into
    /// a single rollup. Persists the result to disk via
    /// <see cref="TokenSummaryCacheStore"/> so the status-bar usage modal
    /// can render last-known totals immediately on app start.
    /// </summary>
    public TokenSummaryAggregate Aggregate(IEnumerable<(string Name, string WatchPath)> projects)
    {
        if (_busReader != null)
            return _busReader.Aggregate(projects, _cache);

        var perProject = projects.Select(p => (p.Name, Summary: Summarize(p.Name, p.WatchPath))).ToList();
        return AggregateSummaries(perProject, _cache);
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
                var canonicalModel = TokenPricing.CanonicalModelId(m.ModelId ?? m.Model);
                var key = string.IsNullOrWhiteSpace(canonicalModel) ? "(unknown)" : canonicalModel;
                if (!perModel.TryGetValue(key, out var bucket))
                {
                    bucket = new ModelBucket(
                        key,
                        TokenModelDisplay.Label(canonicalModel) ?? m.Model);
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
                Model: b.Model,
                Calls: b.Calls,
                InputTokens: b.Input,
                OutputTokens: b.Output,
                CacheReadTokens: b.CacheRead,
                CacheCreationTokens: b.CacheCreate,
                EstimatedApiCostUsd: b.Cost,
                ModelPriced: !b.AnyUnpriced,
                ModelInCatalog: !b.AnyUnknownModel,
                ModelId: b.Model))
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
            Disclaimer: DefaultDisclaimer,
            UnknownModelCount: byModel.Count(model => !model.ModelInCatalog),
            UnpricedModelCount: byModel.Count(model => !model.ModelPriced));

        // Persist for next-app-start display. Best-effort.
        try { cache?.Write(aggregate); } catch (Exception __ex) { SilentCatch.Note(__ex, "TokenSummary: swallow; tolerant by design"); /* swallow; tolerant by design */ }

        return aggregate;
    }

    /// <summary>
    /// Read the persisted aggregate (no fresh probe). Returns null when
    /// the cache file does not exist or fails to parse.
    /// </summary>
    public TokenSummaryAggregate? ReadCachedAggregate() => _cache?.Read();

    private string? ResolveProjectName(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return null;

        if (_config != null)
        {
            foreach (var child in _config.GetSection("WatchedPaths").GetChildren())
            {
                var path = child["Path"];
                var name = child["Name"];
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(path, watchPath, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }

        return Path.GetFileName(watchPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

            var canonicalModel = TokenPricing.CanonicalModelId(u.Model);
            var key = string.IsNullOrWhiteSpace(canonicalModel) ? "(unknown)" : canonicalModel;
            if (!perModel.TryGetValue(key, out var bucket))
            {
                bucket = new ModelBucket(
                    key,
                    TokenModelDisplay.Label(u.Model) ?? "(unknown)");
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
                ModelInCatalog: !bucket.AnyUnknownModel,
                ModelId: bucket.Model));
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
