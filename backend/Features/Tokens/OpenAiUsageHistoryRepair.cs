using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

/// <summary>
/// One-off durable repair for usage captured before Codex/OpenAI input counters
/// were normalized. Task receipts and pipeline records are rewritten; immutable
/// bus rows are handled by <see cref="StoredUsageNormalization"/> on read.
/// </summary>
public sealed class OpenAiUsageHistoryRepair
{
    public const string ReportFileName = "openai-usage-input-v1.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IOpenAiUsageHistoryRepairStore _store;
    private readonly ILogger<OpenAiUsageHistoryRepair> _logger;
    private readonly string _reportPath;

    public OpenAiUsageHistoryRepair(
        TaskScannerService scanner,
        TaskMutationService mutations,
        PipelineExecutionLog pipelines,
        TokenSummaryCacheStore aggregateCache,
        WorkspaceTokensCacheStore workspaceCache,
        IConfiguration configuration,
        ILogger<OpenAiUsageHistoryRepair> logger)
        : this(
            new OpenAiUsageHistoryRepairStore(
                scanner,
                mutations,
                pipelines,
                aggregateCache,
                workspaceCache),
            Path.Combine(
                Path.GetFullPath(configuration["TaskRepository"]
                    ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
                ".metadata",
                "migrations",
                ReportFileName),
            logger)
    {
    }

    internal OpenAiUsageHistoryRepair(
        IOpenAiUsageHistoryRepairStore store,
        string reportPath,
        ILogger<OpenAiUsageHistoryRepair> logger)
    {
        _store = store;
        _reportPath = reportPath;
        _logger = logger;
    }

    public OpenAiUsageHistoryRepairReport RunOnce()
    {
        if (File.Exists(_reportPath))
        {
            var prior = JsonSerializer.Deserialize<OpenAiUsageHistoryRepairReport>(
                File.ReadAllText(_reportPath), Json);
            return prior is null
                ? OpenAiUsageHistoryRepairReport.CompletedEarlier()
                : prior with
                {
                    AlreadyCompleted = true,
                    Completed = true,
                    RepairedKeys = prior.RepairedKeys ?? [],
                    SkippedAlreadyCorrectKeys = prior.SkippedAlreadyCorrectKeys ?? [],
                    FailedKeys = prior.FailedKeys ?? [],
                };
        }

        var correctedTaskEntries = 0;
        var correctedPipelineSteps = 0;
        var repairedTasks = 0;
        decimal before = 0;
        decimal after = 0;
        var untouched = new List<OpenAiUsageHistoryUntouchedEntry>();
        var failures = new List<string>();
        var repairedKeys = new List<string>();
        var skippedAlreadyCorrectKeys = new List<string>();
        var failedKeys = new List<string>();

        foreach (var task in _store.ScanJobs())
        {
            var taskSummaryKey = $"{task.TaskKey}:task.json tokenSummary";
            if (task.TokenSummary is { } summary)
            {
                var repair = RepairSummary(summary, task.Project, task.TaskKey, untouched);
                if (repair.Changed)
                {
                    if (_store.ReplaceTokenSummary(task.FolderPath, repair.Summary))
                    {
                        repairedTasks++;
                        correctedTaskEntries += repair.Corrected;
                        before += repair.BeforeCostUsd;
                        after += repair.AfterCostUsd;
                        repairedKeys.Add(taskSummaryKey);
                    }
                    else
                    {
                        failedKeys.Add(taskSummaryKey);
                        failures.Add($"{taskSummaryKey} write failed.");
                    }
                }
                else if (HasNormalizedOpenAiUsage(summary))
                {
                    skippedAlreadyCorrectKeys.Add(taskSummaryKey);
                }
            }

            var pipelineKey = $"{task.TaskKey}:pipeline-execution.json";
            var pipeline = _store.ReadPipeline(task.FolderPath);
            if (pipeline is null) continue;
            var pipelineRepair = RepairPipeline(pipeline, task.Project, task.TaskKey, untouched);
            if (!pipelineRepair.Changed)
            {
                if (HasNormalizedOpenAiUsage(pipeline))
                {
                    skippedAlreadyCorrectKeys.Add(pipelineKey);
                }
                continue;
            }
            if (_store.ReplacePipeline(task.FolderPath, pipelineRepair.Record))
            {
                correctedPipelineSteps += pipelineRepair.Corrected;
                before += pipelineRepair.BeforeCostUsd;
                after += pipelineRepair.AfterCostUsd;
                repairedKeys.Add(pipelineKey);
            }
            else
            {
                failedKeys.Add(pipelineKey);
                failures.Add($"{pipelineKey} write failed.");
            }
        }

        if (correctedTaskEntries > 0 || correctedPipelineSteps > 0)
        {
            _store.InvalidateCaches();
        }

        var completed = failures.Count == 0;
        var report = new OpenAiUsageHistoryRepairReport(
            Version: 2,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            AlreadyCompleted: false,
            Completed: completed,
            RepairedTasks: repairedTasks,
            CorrectedTaskEntries: correctedTaskEntries,
            CorrectedPipelineSteps: correctedPipelineSteps,
            StoredRecordCostBeforeUsd: before,
            StoredRecordCostAfterUsd: after,
            UntouchedEntries: untouched,
            Failures: failures,
            RepairedKeys: repairedKeys,
            SkippedAlreadyCorrectKeys: skippedAlreadyCorrectKeys,
            FailedKeys: failedKeys);
        if (completed)
        {
            WriteReport(report);
        }
        _logger.Log(
            completed ? LogLevel.Information : LogLevel.Warning,
            "openai-usage-history-repair completed={Completed} tasks={Tasks} taskEntries={TaskEntries} pipelineSteps={PipelineSteps} beforeUsd={BeforeUsd} afterUsd={AfterUsd} repaired={Repaired} skippedAlreadyCorrect={SkippedAlreadyCorrect} failed={Failed} untouched={Untouched} report={Report}",
            completed,
            repairedTasks,
            correctedTaskEntries,
            correctedPipelineSteps,
            before,
            after,
            repairedKeys.Count,
            skippedAlreadyCorrectKeys.Count,
            failedKeys.Count,
            untouched.Count,
            _reportPath);
        return report;
    }

    private static bool HasNormalizedOpenAiUsage(TaskTokenSummary summary)
        => (summary.Entries ?? []).Any(entry =>
            ProviderUsageNormalization.IsOpenAiModel(entry.Model)
            && (entry.InputIncludesCached == true
                || string.Equals(
                    entry.UsageNormalization,
                    ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                    StringComparison.Ordinal)));

    private static bool HasNormalizedOpenAiUsage(PipelineExecutionRecord record)
        => record.Steps.Any(step =>
               ProviderUsageNormalization.IsOpenAiModel(step.Model)
               && (step.InputIncludesCached == true
                   || string.Equals(
                       step.UsageNormalization,
                       ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                       StringComparison.Ordinal)))
           || record.PreviousAttempts.Any(HasNormalizedOpenAiUsage);

    internal static TaskSummaryRepair RepairSummary(
        TaskTokenSummary summary,
        string project,
        string taskKey,
        ICollection<OpenAiUsageHistoryUntouchedEntry>? untouched = null)
    {
        var sourceEntries = summary.Entries ?? [];
        var entries = new List<TaskTokenCall>(sourceEntries.Count);
        var corrected = 0;
        decimal before = 0;
        decimal after = 0;
        foreach (var entry in sourceEntries)
        {
            var repaired = RepairCall(entry, project, taskKey, "task.json tokenSummary.Entries", untouched);
            entries.Add(repaired.Entry);
            if (!repaired.Changed) continue;
            corrected++;
            before += repaired.BeforeCostUsd;
            after += repaired.AfterCostUsd;
        }

        if (corrected == 0) return new TaskSummaryRepair(summary, false, 0, 0, 0);
        var rebuilt = summary with
        {
            Calls = entries.Count,
            InputTokens = entries.Sum(entry => entry.InputTokens),
            OutputTokens = entries.Sum(entry => entry.OutputTokens),
            CacheReadTokens = entries.Sum(entry => entry.CacheReadTokens),
            CacheCreationTokens = entries.Sum(entry => entry.CacheCreationTokens),
            TotalTokens = entries.Sum(entry => entry.InputTokens + entry.OutputTokens
                + entry.CacheReadTokens + entry.CacheCreationTokens),
            EstimatedApiCostUsd = entries.Sum(entry => entry.EstimatedApiCostUsd),
            AllModelsPriced = entries.Count > 0 && entries.All(entry => entry.ModelPriced),
            LastUpdate = entries.Count == 0 ? summary.LastUpdate : entries.Max(entry => entry.Ts),
            Entries = entries,
        };
        return new TaskSummaryRepair(rebuilt, true, corrected, before, after);
    }

    private static TaskCallRepair RepairCall(
        TaskTokenCall entry,
        string project,
        string taskKey,
        string source,
        ICollection<OpenAiUsageHistoryUntouchedEntry>? untouched)
    {
        if (!ProviderUsageNormalization.IsOpenAiModel(entry.Model)
            || entry.CacheReadTokens <= 0
            || entry.InputIncludesCached is not null
            || string.Equals(entry.UsageNormalization,
                ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                StringComparison.Ordinal))
        {
            return new TaskCallRepair(entry, false, 0, 0);
        }

        if (entry.InputTokens < entry.CacheReadTokens)
        {
            untouched?.Add(new OpenAiUsageHistoryUntouchedEntry(
                project, taskKey, source, entry.Model, entry.InputTokens,
                entry.CacheReadTokens, "input_tokens is smaller than cacheReadTokens"));
            return new TaskCallRepair(entry, false, 0, 0);
        }

        var normalized = ProviderUsageNormalization.OpenAi(entry.InputTokens, entry.CacheReadTokens);
        var oldCost = TokenPricing.Estimate(entry.Model, entry.InputTokens, entry.OutputTokens,
            entry.CacheReadTokens, entry.CacheCreationTokens, entry.Ts);
        var newCost = TokenPricing.Estimate(entry.Model, normalized.InputTokens, entry.OutputTokens,
            normalized.CacheReadTokens, entry.CacheCreationTokens, entry.Ts);
        return new TaskCallRepair(entry with
        {
            InputTokens = normalized.InputTokens,
            CacheReadTokens = normalized.CacheReadTokens,
            InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
            EstimatedApiCostUsd = newCost.Total,
            ModelPriced = newCost.ModelKnown,
        }, true, oldCost.Total, newCost.Total);
    }

    internal static PipelineRepair RepairPipeline(
        PipelineExecutionRecord record,
        string project,
        string taskKey,
        ICollection<OpenAiUsageHistoryUntouchedEntry>? untouched = null)
    {
        var corrected = 0;
        decimal before = 0;
        decimal after = 0;

        PipelineExecutionRecord RepairRecord(PipelineExecutionRecord current)
        {
            var steps = current.Steps.Select(step =>
            {
                if (!ProviderUsageNormalization.IsOpenAiModel(step.Model)
                    || step.CacheReadTokens <= 0
                    || step.InputIncludesCached is not null
                    || string.Equals(step.UsageNormalization,
                        ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                        StringComparison.Ordinal))
                {
                    return step;
                }

                if (step.InputTokens < step.CacheReadTokens)
                {
                    untouched?.Add(new OpenAiUsageHistoryUntouchedEntry(
                        project, taskKey, "pipeline-execution.json", step.Model,
                        step.InputTokens, step.CacheReadTokens,
                        "input_tokens is smaller than cacheReadTokens"));
                    return step;
                }

                var normalized = ProviderUsageNormalization.OpenAi(step.InputTokens, step.CacheReadTokens);
                var at = step.CompletedAt ?? step.StartedAt ?? current.CompletedAt ?? current.StartedAt;
                before += TokenPricing.Estimate(step.Model, step.InputTokens, step.OutputTokens,
                    step.CacheReadTokens, step.CacheCreationTokens, at).Total;
                after += TokenPricing.Estimate(step.Model, normalized.InputTokens, step.OutputTokens,
                    normalized.CacheReadTokens, step.CacheCreationTokens, at).Total;
                corrected++;
                return step with
                {
                    InputTokens = normalized.InputTokens,
                    CacheReadTokens = normalized.CacheReadTokens,
                    InputIncludesCached = true,
                    UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                };
            }).ToList();
            return current with
            {
                Steps = steps,
                PreviousAttempts = current.PreviousAttempts.Select(RepairRecord).ToList(),
            };
        }

        var repaired = RepairRecord(record);
        return corrected == 0
            ? new PipelineRepair(record, false, 0, 0, 0)
            : new PipelineRepair(repaired, true, corrected, before, after);
    }

    private void WriteReport(OpenAiUsageHistoryRepairReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        var temporary = _reportPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, report, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _reportPath, overwrite: true);
    }

    private sealed class OpenAiUsageHistoryRepairStore(
        TaskScannerService scanner,
        TaskMutationService mutations,
        PipelineExecutionLog pipelines,
        TokenSummaryCacheStore aggregateCache,
        WorkspaceTokensCacheStore workspaceCache) : IOpenAiUsageHistoryRepairStore
    {
        public IReadOnlyList<OpenAiUsageHistoryRepairJob> ScanJobs()
            => scanner.ScanAllJobsWithArchive()
                .Select(task => new OpenAiUsageHistoryRepairJob(
                    task.FolderPath,
                    task.ProjectName,
                    task.Key ?? task.Id,
                    task.TokenSummary))
                .ToList();

        public bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary)
            => mutations.ReplaceTokenSummaryForMigration(folderPath, summary);

        public PipelineExecutionRecord? ReadPipeline(string folderPath)
            => pipelines.ReadForMigration(folderPath);

        public bool ReplacePipeline(string folderPath, PipelineExecutionRecord record)
            => pipelines.ReplaceForMigration(folderPath, record);

        public void InvalidateCaches()
        {
            aggregateCache.Invalidate();
            workspaceCache.Invalidate();
        }
    }
}

internal interface IOpenAiUsageHistoryRepairStore
{
    IReadOnlyList<OpenAiUsageHistoryRepairJob> ScanJobs();
    bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary);
    PipelineExecutionRecord? ReadPipeline(string folderPath);
    bool ReplacePipeline(string folderPath, PipelineExecutionRecord record);
    void InvalidateCaches();
}

internal sealed record OpenAiUsageHistoryRepairJob(
    string FolderPath,
    string Project,
    string TaskKey,
    TaskTokenSummary? TokenSummary);

internal sealed record TaskCallRepair(
    TaskTokenCall Entry,
    bool Changed,
    decimal BeforeCostUsd,
    decimal AfterCostUsd);

internal sealed record TaskSummaryRepair(
    TaskTokenSummary Summary,
    bool Changed,
    int Corrected,
    decimal BeforeCostUsd,
    decimal AfterCostUsd);

internal sealed record PipelineRepair(
    PipelineExecutionRecord Record,
    bool Changed,
    int Corrected,
    decimal BeforeCostUsd,
    decimal AfterCostUsd);

public sealed record OpenAiUsageHistoryUntouchedEntry(
    string Project,
    string TaskKey,
    string Source,
    string? Model,
    long InputTokens,
    long CacheReadTokens,
    string Reason);

public sealed record OpenAiUsageHistoryRepairReport(
    int Version,
    DateTimeOffset CompletedAtUtc,
    bool AlreadyCompleted,
    bool Completed,
    int RepairedTasks,
    int CorrectedTaskEntries,
    int CorrectedPipelineSteps,
    decimal StoredRecordCostBeforeUsd,
    decimal StoredRecordCostAfterUsd,
    IReadOnlyList<OpenAiUsageHistoryUntouchedEntry> UntouchedEntries,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> RepairedKeys,
    IReadOnlyList<string> SkippedAlreadyCorrectKeys,
    IReadOnlyList<string> FailedKeys)
{
    public int RepairedCount => RepairedKeys?.Count ?? 0;
    public int SkippedAlreadyCorrectCount => SkippedAlreadyCorrectKeys?.Count ?? 0;
    public int FailedCount => FailedKeys?.Count ?? 0;

    public static OpenAiUsageHistoryRepairReport CompletedEarlier()
        => new(2, DateTimeOffset.MinValue, true, true, 0, 0, 0, 0, 0, [], [], [], [], []);
}
