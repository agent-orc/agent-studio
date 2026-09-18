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

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly PipelineExecutionLog _pipelines;
    private readonly TokenSummaryCacheStore _aggregateCache;
    private readonly WorkspaceTokensCacheStore _workspaceCache;
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
            scanner,
            mutations,
            pipelines,
            aggregateCache,
            workspaceCache,
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
        TaskScannerService scanner,
        TaskMutationService mutations,
        PipelineExecutionLog pipelines,
        TokenSummaryCacheStore aggregateCache,
        WorkspaceTokensCacheStore workspaceCache,
        string reportPath,
        ILogger<OpenAiUsageHistoryRepair> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _pipelines = pipelines;
        _aggregateCache = aggregateCache;
        _workspaceCache = workspaceCache;
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
                : prior with { AlreadyCompleted = true };
        }

        var correctedTaskEntries = 0;
        var correctedPipelineSteps = 0;
        var repairedTasks = 0;
        decimal before = 0;
        decimal after = 0;
        var untouched = new List<OpenAiUsageHistoryUntouchedEntry>();
        var failures = new List<string>();

        foreach (var task in _scanner.ScanAllJobsWithArchive())
        {
            if (task.TokenSummary is { } summary)
            {
                var repair = RepairSummary(summary, task.ProjectName, task.Key ?? task.Id, untouched);
                if (repair.Changed)
                {
                    if (_mutations.ReplaceTokenSummaryForMigration(task.FolderPath, repair.Summary))
                    {
                        repairedTasks++;
                        correctedTaskEntries += repair.Corrected;
                        before += repair.BeforeCostUsd;
                        after += repair.AfterCostUsd;
                    }
                    else
                    {
                        failures.Add($"{task.Key ?? task.Id}: task.json tokenSummary write failed.");
                    }
                }
            }

            var pipeline = _pipelines.ReadForMigration(task.FolderPath);
            if (pipeline is null) continue;
            var pipelineRepair = RepairPipeline(pipeline, task.ProjectName, task.Key ?? task.Id, untouched);
            if (!pipelineRepair.Changed) continue;
            if (_pipelines.ReplaceForMigration(task.FolderPath, pipelineRepair.Record))
            {
                correctedPipelineSteps += pipelineRepair.Corrected;
                before += pipelineRepair.BeforeCostUsd;
                after += pipelineRepair.AfterCostUsd;
            }
            else
            {
                failures.Add($"{task.Key ?? task.Id}: pipeline-execution.json write failed.");
            }
        }

        if (correctedTaskEntries > 0 || correctedPipelineSteps > 0)
        {
            _aggregateCache.Invalidate();
            _workspaceCache.Invalidate();
        }

        var report = new OpenAiUsageHistoryRepairReport(
            Version: 1,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            AlreadyCompleted: false,
            RepairedTasks: repairedTasks,
            CorrectedTaskEntries: correctedTaskEntries,
            CorrectedPipelineSteps: correctedPipelineSteps,
            StoredRecordCostBeforeUsd: before,
            StoredRecordCostAfterUsd: after,
            UntouchedEntries: untouched,
            Failures: failures);
        WriteReport(report);
        _logger.LogInformation(
            "openai-usage-history-repair completed tasks={Tasks} taskEntries={TaskEntries} pipelineSteps={PipelineSteps} beforeUsd={BeforeUsd} afterUsd={AfterUsd} untouched={Untouched} failures={Failures} report={Report}",
            repairedTasks,
            correctedTaskEntries,
            correctedPipelineSteps,
            before,
            after,
            untouched.Count,
            failures.Count,
            _reportPath);
        return report;
    }

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
}

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
    int RepairedTasks,
    int CorrectedTaskEntries,
    int CorrectedPipelineSteps,
    decimal StoredRecordCostBeforeUsd,
    decimal StoredRecordCostAfterUsd,
    IReadOnlyList<OpenAiUsageHistoryUntouchedEntry> UntouchedEntries,
    IReadOnlyList<string> Failures)
{
    public static OpenAiUsageHistoryRepairReport CompletedEarlier()
        => new(1, DateTimeOffset.MinValue, true, 0, 0, 0, 0, 0, [], []);
}
