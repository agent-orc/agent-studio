using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>
/// Project-level rollup of pipeline step cost over time. Durable task token
/// receipts are authoritative when present; legacy tasks fall back to
/// <c>pipeline-execution.json</c>. Both paths fold token usage into
/// (step-kind, day) cells priced
/// through the single <see cref="TokenPricing"/> table. The frontend
/// renders the result as a stacked time trend in the project Token Usage
/// surface, next to the orchestrator-log heatmap.
///
/// <para>
/// This is the deliberately-separate, cached path that
/// <see cref="PipelineCostCalculator"/> refers to: the per-task Overview
/// poll never triggers this O(tasks) disk walk; only the analytics panel
/// does, and a short TTL cache absorbs repeat opens within a window.
/// </para>
/// </summary>
public sealed class ProjectPipelineCostService
{
    public const int DefaultDays = 30;
    public const int MaxDays = 180;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _log;
    private readonly ProjectTokenReceiptReader _receipts;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectPipelineCostTimeline Value)> _cache = new();

    public ProjectPipelineCostService(
        TaskScannerService scanner,
        PipelineExecutionLog log,
        ProjectTokenReceiptReader receipts)
    {
        _scanner = scanner;
        _log = log;
        _receipts = receipts;
    }

    /// <summary>
    /// Build the per-step-kind cost timeline for one project. Reads each
    /// task folder's pipeline-execution.json once; result is cached for a
    /// short TTL so a panel that re-fetches on tab switches does not
    /// re-walk the disk every time.
    /// </summary>
    public ProjectPipelineCostTimeline Build(string projectName, string watchPath, int days, DateTime? nowUtc = null)
    {
        var d = ResolveDays(days);
        var cacheKey = $"{watchPath}|{d}";
        if (nowUtc == null
            && _cache.TryGetValue(cacheKey, out var hit)
            && DateTime.UtcNow - hit.At < CacheTtl)
        {
            return hit.Value;
        }

        var records = new List<PipelineExecutionRecord>();
        var warnings = new List<string>();
        var sources = new List<string>();
        var receiptRead = _receipts.Read(watchPath);
        if (receiptRead.SourceAvailable) sources.Add("task-token-receipts");
        if (!string.IsNullOrWhiteSpace(receiptRead.Warning)) warnings.Add(receiptRead.Warning!);
        var receiptJobIds = receiptRead.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.JobId))
            .Select(entry => entry.JobId!)
            .ToHashSet(StringComparer.Ordinal);

        var taskLogs = new List<(string JobId, PipelineExecutionRecord Record)>();
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            foreach (var task in _scanner.ScanAllAutomationJobsWithArchive())
            {
                if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(task.FolderPath)) continue;
                var rec = _log.Read(task.FolderPath);
                if (rec != null)
                {
                    sources.Add("pipeline-execution-log");
                    taskLogs.Add((task.Id, rec));
                }
                else if (File.Exists(Path.Combine(task.FolderPath, PipelineExecutionLog.FileName)))
                {
                    warnings.Add($"Pipeline telemetry for task {task.Id} could not be read.");
                }
            }
        }
        records.AddRange(MergeSources(
            BuildReceiptRecords(projectName, receiptRead.Entries),
            receiptJobIds,
            taskLogs));

        var timeline = BuildFromRecords(projectName, records, d, nowUtc);
        var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        var freshness = timeline.Freshness with
        {
            Status = distinctWarnings.Count == 0
                ? "complete"
                : timeline.HasData ? "partial" : "unavailable",
            Warning = distinctWarnings.Count == 0 ? null : string.Join(" ", distinctWarnings),
            Sources = sources.Distinct(StringComparer.Ordinal).ToList(),
        };
        timeline = timeline with { Freshness = freshness };
        if (nowUtc == null)
        {
            _cache[cacheKey] = (DateTime.UtcNow, timeline);
        }
        return timeline;
    }

    /// <summary>
    /// Pure overload: aggregate pre-loaded execution records. Used by unit
    /// tests to avoid filesystem round-trips.
    /// </summary>
    public static ProjectPipelineCostTimeline BuildFromRecords(
        string projectName,
        IReadOnlyList<PipelineExecutionRecord> records,
        int days,
        DateTime? nowUtc = null)
    {
        var d = ResolveDays(days);
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var endDay = AlignDay(now);
        var startDay = endDay.AddDays(-(d - 1));

        var dayList = new List<string>(d);
        for (var i = 0; i < d; i++)
            dayList.Add(endDay.AddDays(-(d - 1 - i)).ToString("yyyy-MM-dd"));

        var decisionCost = new DecisionCostAccumulator();
        var perKindDay = new Dictionary<(StepKind Kind, string Day), Acc>();
        var perDay = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var perKind = new Dictionary<StepKind, Acc>();
        var perStep = new Dictionary<string, StepAcc>(StringComparer.Ordinal);
        long grandTokens = 0;
        decimal grandCost = 0m;
        var grandUnknown = false;
        var grandUnpricedRuns = new HashSet<int>();
        var grandPricingGaps = new PriceGapAccumulator();
        var contributingTasks = new HashSet<string>(StringComparer.Ordinal);

        for (var runIndex = 0; runIndex < records.Count; runIndex++)
        {
            var rec = records[runIndex];
            // Bucket the whole run by its completion (or, while still
            // running, its start) so a task's spend lands on the day it ran.
            var ts = (rec.CompletedAt ?? rec.StartedAt).ToUniversalTime();
            if (ts < startDay || ts >= endDay.AddDays(1)) continue;
            var dayKey = AlignDay(ts).ToString("yyyy-MM-dd");
            var contributed = false;

            // Receipt rows are per call, not per execution: they price the
            // agent run but do not count as occurrences.
            var countsOccurrences = !string.Equals(rec.PipelineId, ReceiptPipelineId, StringComparison.Ordinal);
            foreach (var s in rec.Steps)
            {
                // Every execution of the step in this attempt, repeats across
                // review rounds included, each priced at its own model.
                foreach (var run in StepCostMeasurement.Executions(s))
                {
                    var stepTokens = StepCostMeasurement.Tokens(run);
                    var est = stepTokens > 0
                        ? TokenPricing.Estimate(
                            run.Model, run.InputTokens, run.OutputTokens, run.CacheReadTokens, run.CacheCreationTokens,
                            run.StartedAt ?? ts)
                        : null;
                    if (countsOccurrences)
                    {
                        CountRun(perKind, s.Kind);
                        CountStepRun(perStep, s.StepId, s.Kind, rec.JobId, run.CostBasis == StepCostBasis.Model || stepTokens > 0 ? run.Model : null);
                        decisionCost.AddExecution(s.Kind, stepTokens, est);
                    }
                    else
                    {
                        decisionCost.AddTokens(s.Kind, stepTokens, est);
                    }
                    if (est is null) continue;

                    Add(perKindDay, (s.Kind, dayKey), stepTokens, est, runIndex, run.Model);
                    Add(perDay, dayKey, stepTokens, est, runIndex, run.Model);
                    Add(perKind, s.Kind, stepTokens, est, runIndex, run.Model);
                    AddStep(perStep, s.StepId, s.Kind, stepTokens, est, runIndex, run.Model);
                    grandTokens += stepTokens;
                    if (est.ModelKnown)
                    {
                        grandCost += est.Total;
                    }
                    else
                    {
                        grandUnknown = true;
                        grandUnpricedRuns.Add(runIndex);
                        grandPricingGaps.Add(est, runIndex, run.Model);
                    }
                    contributed = true;
                }
            }
            if (contributed) contributingTasks.Add(rec.JobId);
        }

        var dayCosts = dayList.Select(day =>
        {
            perDay.TryGetValue(day, out var cell);
            return new PipelineDayCostCell(
                Day: day,
                TotalTokens: cell?.Tokens ?? 0,
                CostUsd: Round(cell?.Cost ?? 0m),
                UnpricedRuns: cell?.UnpricedRuns.Count ?? 0,
                PricingGaps: cell?.PricingGaps.Build() ?? Array.Empty<PipelinePricingGap>());
        }).ToList();

        var series = new List<PipelineKindSeries>();
        foreach (var kind in KindOrder)
        {
            if (!perKind.TryGetValue(kind, out var kindAcc) || kindAcc.Tokens <= 0) continue;
            var cells = new List<PipelineKindDayCell>(d);
            foreach (var day in dayList)
            {
                perKindDay.TryGetValue((kind, day), out var cell);
                cells.Add(new PipelineKindDayCell(
                    Day: day,
                    TotalTokens: cell?.Tokens ?? 0,
                    CostUsd: Round(cell?.Cost ?? 0m),
                    UnpricedRuns: cell?.UnpricedRuns.Count ?? 0,
                    PricingGaps: cell?.PricingGaps.Build() ?? Array.Empty<PipelinePricingGap>()));
            }
            series.Add(new PipelineKindSeries(
                Kind: KindKey(kind),
                TotalTokens: kindAcc.Tokens,
                TotalCostUsd: Round(kindAcc.Cost),
                AnyModelUnknown: kindAcc.AnyUnknown,
                UnpricedRuns: kindAcc.UnpricedRuns.Count,
                PricingGaps: kindAcc.PricingGaps.Build(),
                Cells: cells,
                Runs: kindAcc.Runs));
        }

        // Per-step rollup over the whole window, most-expensive first so the
        // panel can surface the priciest steps at a glance. Tie-break on the
        // step id for a stable order on equal spend.
        var steps = perStep
            .Select(kv => new PipelineStepCostSeries(
                StepId: kv.Key,
                Kind: KindKey(kv.Value.Kind),
                TotalTokens: kv.Value.Tokens,
                TotalCostUsd: Round(kv.Value.Cost),
                AnyModelUnknown: kv.Value.AnyUnknown,
                UnpricedRuns: kv.Value.UnpricedRuns.Count,
                PricingGaps: kv.Value.PricingGaps.Build(),
                Runs: kv.Value.Runs,
                Tasks: kv.Value.Tasks.Count,
                Models: kv.Value.Models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(s => s.TotalTokens)
            .ThenByDescending(s => s.Runs)
            .ThenBy(s => s.StepId, StringComparer.Ordinal)
            .ToList();

        var asOf = records
            .Where(record => record.Steps.Any(step => StepTokens(step) > 0))
            .Select(record => (DateTime?)(record.CompletedAt ?? record.StartedAt).ToUniversalTime())
            .Max();
        return new ProjectPipelineCostTimeline(
            Project: projectName,
            Days: dayList,
            DayCosts: dayCosts,
            WindowDays: d,
            Kinds: series,
            Steps: steps,
            TotalTokens: grandTokens,
            TotalCostUsd: Round(grandCost),
            AnyModelUnknown: grandUnknown,
            UnpricedRuns: grandUnpricedRuns.Count,
            PricingGaps: grandPricingGaps.Build(),
            TaskCount: contributingTasks.Count,
            HasData: grandTokens > 0,
            FetchedAt: DateTime.UtcNow.ToString("o"),
            Freshness: new ProjectTokenDataFreshness
            {
                AsOf = asOf?.ToString("o"),
            },
            DecisionCost: decisionCost.Build());
    }

    internal static IReadOnlyList<PipelineExecutionRecord> BuildReceiptRecords(
        string projectName,
        IReadOnlyList<OrchestratorLogEntry> entries)
    {
        var records = new List<PipelineExecutionRecord>();
        foreach (var entry in entries)
        {
            var usage = entry.TokenUsage;
            if (usage is null || string.IsNullOrWhiteSpace(entry.JobId)) continue;
            if ((long)usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheCreationTokens <= 0)
                continue;

            var kind = TokenModelDisplay.IsOrchestratorParticipant(entry.ParticipantId)
                ? StepKind.Orchestrator
                : TokenModelDisplay.IsSupportingParticipant(entry.ParticipantId)
                    ? StepKind.Aspect
                    : StepKind.Core;
            var stepId = kind switch
            {
                StepKind.Orchestrator => "task-receipt-orchestrator",
                StepKind.Aspect => "task-receipt-supporting",
                _ => PipelineCatalogue.CoreAgentRunStepId,
            };
            records.Add(new PipelineExecutionRecord
            {
                PipelineId = ReceiptPipelineId,
                Project = projectName,
                JobId = entry.JobId!,
                StartedAt = entry.Ts,
                CompletedAt = entry.Ts,
                Steps =
                [
                    new PipelineStepExecution
                    {
                        StepId = stepId,
                        Kind = kind,
                        Status = PipelineStepStatus.Passed,
                        StartedAt = entry.Ts,
                        CompletedAt = entry.Ts,
                        Model = usage.Model,
                        InputTokens = usage.InputTokens,
                        OutputTokens = usage.OutputTokens,
                        CacheReadTokens = usage.CacheReadTokens,
                        CacheCreationTokens = usage.CacheCreationTokens,
                        InputIncludesCached = usage.InputIncludesCached,
                        TokenUsageSource = "Durable task token receipt",
                    },
                ],
            });
        }
        return records;
    }

    private const string ReceiptPipelineId = "task-token-receipt";

    /// <summary>
    /// Combine durable receipts and execution logs into one record list
    /// without counting a token twice. For a task with a receipt, the receipt
    /// prices the agent run and the execution log contributes every other
    /// step (aspects, the orchestrator decisions, drift) plus the core row's
    /// run count. Before AGT-3015 a receipt task's log was skipped entirely,
    /// which is why no orchestrator step reached this ledger.
    /// </summary>
    internal static IReadOnlyList<PipelineExecutionRecord> MergeSources(
        IReadOnlyList<PipelineExecutionRecord> receiptRecords,
        IReadOnlySet<string> receiptJobIds,
        IEnumerable<(string JobId, PipelineExecutionRecord Record)> taskLogs)
    {
        var merged = new List<PipelineExecutionRecord>();
        var receiptTasksWithLog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (jobId, record) in taskLogs)
        {
            var attempts = new List<PipelineExecutionRecord> { record };
            attempts.AddRange(record.PreviousAttempts);
            if (receiptJobIds.Contains(jobId))
            {
                receiptTasksWithLog.Add(jobId);
                attempts = attempts.Select(WithoutCoreTokens).ToList();
            }
            merged.AddRange(attempts);
        }
        // A supporting-agent receipt row duplicates a step row the execution
        // log already carries; drop it when that log was read.
        merged.AddRange(receiptRecords.Where(record =>
            !receiptTasksWithLog.Contains(record.JobId)
            || record.Steps.All(step => step.Kind != StepKind.Aspect)));
        return merged;
    }

    /// <summary>
    /// Zero the core rows' tokens so a receipt-backed task counts its agent
    /// runs from the log while the receipt alone prices them.
    /// </summary>
    private static PipelineExecutionRecord WithoutCoreTokens(PipelineExecutionRecord record)
        => record with
        {
            Steps = record.Steps
                .Select(step => step.Kind != StepKind.Core
                    ? step
                    : step with
                    {
                        InputTokens = 0,
                        OutputTokens = 0,
                        CacheReadTokens = 0,
                        CacheCreationTokens = 0,
                        EarlierRuns = step.EarlierRuns?
                            .Select(run => run with
                            {
                                InputTokens = 0,
                                OutputTokens = 0,
                                CacheReadTokens = 0,
                                CacheCreationTokens = 0,
                            })
                            .ToList(),
                    })
                .ToList(),
        };

    public static int ResolveDays(int requested) =>
        requested <= 0 ? DefaultDays : Math.Min(requested, MaxDays);

    // Stable render order: core run first, then the model-driven aspects,
    // deterministic tool steps, the orchestrator decision, and finally any
    // generic module step.
    private static readonly StepKind[] KindOrder =
        [StepKind.Core, StepKind.Aspect, StepKind.Tool, StepKind.Analysis, StepKind.Orchestrator, StepKind.Drift, StepKind.Module];

    private static string KindKey(StepKind kind) => kind switch
    {
        StepKind.Core => "core",
        StepKind.Aspect => "aspect",
        StepKind.Tool => "tool",
        StepKind.Analysis => "analysis",
        StepKind.Orchestrator => "orchestrator",
        StepKind.Drift => "drift",
        _ => "module",
    };

    private static void Add<TKey>(
        Dictionary<TKey, Acc> map,
        TKey key,
        long tokens,
        TokenCostEstimate est,
        int runIndex,
        string? model)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new Acc();
            map[key] = acc;
        }
        acc.Tokens += tokens;
        if (est.ModelKnown)
        {
            acc.Cost += est.Total;
        }
        else
        {
            acc.AnyUnknown = true;
            acc.UnpricedRuns.Add(runIndex);
            acc.PricingGaps.Add(est, runIndex, model);
        }
    }

    private static void CountRun(Dictionary<StepKind, Acc> map, StepKind kind)
    {
        if (!map.TryGetValue(kind, out var acc))
        {
            acc = new Acc();
            map[kind] = acc;
        }
        acc.Runs++;
    }

    private static void CountStepRun(Dictionary<string, StepAcc> map, string stepId, StepKind kind, string jobId, string? model)
    {
        var key = string.IsNullOrWhiteSpace(stepId) ? KindKey(kind) : stepId.Trim();
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new StepAcc { Kind = kind };
            map[key] = acc;
        }
        acc.Runs++;
        if (!string.IsNullOrWhiteSpace(jobId)) acc.Tasks.Add(jobId);
        if (!string.IsNullOrWhiteSpace(model)) acc.Models.Add(model.Trim());
    }

    private static void AddStep(
        Dictionary<string, StepAcc> map,
        string stepId,
        StepKind kind,
        long tokens,
        TokenCostEstimate est,
        int runIndex,
        string? model)
    {
        // Steps with no id are runtime noise; fold them under their kind key so
        // they still contribute to the project totals without a blank row.
        var key = string.IsNullOrWhiteSpace(stepId) ? KindKey(kind) : stepId.Trim();
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new StepAcc { Kind = kind };
            map[key] = acc;
        }
        acc.Tokens += tokens;
        if (est.ModelKnown)
        {
            acc.Cost += est.Total;
        }
        else
        {
            acc.AnyUnknown = true;
            acc.UnpricedRuns.Add(runIndex);
            acc.PricingGaps.Add(est, runIndex, model);
        }
    }

    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static long StepTokens(PipelineStepExecution step)
        => step.InputTokens + step.OutputTokens + step.CacheReadTokens + step.CacheCreationTokens;

    private static DateTime AlignDay(DateTime ts)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Acc
    {
        public int Runs;
        public long Tokens;
        public decimal Cost;
        public bool AnyUnknown;
        public HashSet<int> UnpricedRuns { get; } = [];
        public PriceGapAccumulator PricingGaps { get; } = new();
    }

    private sealed class StepAcc
    {
        public StepKind Kind;
        public int Runs;
        public HashSet<string> Tasks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Tokens;
        public decimal Cost;
        public bool AnyUnknown;
        public HashSet<int> UnpricedRuns { get; } = [];
        public PriceGapAccumulator PricingGaps { get; } = new();
    }

    private sealed class PriceGapAccumulator
    {
        private readonly Dictionary<(string ModelId, string Reason), HashSet<int>> _runs = [];

        public void Add(TokenCostEstimate estimate, int runIndex, string? displayModel)
        {
            if (estimate.ModelKnown) return;
            var modelId = !string.IsNullOrWhiteSpace(displayModel)
                ? displayModel.Trim()
                : string.IsNullOrWhiteSpace(estimate.ModelId) ? "unknown" : estimate.ModelId.Trim();
            var key = (modelId, estimate.Status.ToString());
            if (!_runs.TryGetValue(key, out var affectedRuns))
            {
                affectedRuns = [];
                _runs[key] = affectedRuns;
            }
            affectedRuns.Add(runIndex);
        }

        public IReadOnlyList<PipelinePricingGap> Build()
            => _runs
                .Select(pair => new PipelinePricingGap(
                    pair.Key.ModelId,
                    pair.Key.Reason,
                    pair.Value.Count))
                .OrderBy(gap => gap.ModelId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(gap => gap.Reason, StringComparer.Ordinal)
                .ToList();
    }
}

/// <summary>
/// Per-step-kind cost timeline for one project. <see cref="Days"/> are
/// UTC midnights, oldest to newest, the full requested window (so the
/// chart x-axis is dense even on days with no activity). Each
/// <see cref="PipelineKindSeries"/> carries one cell per day aligned to
/// <see cref="Days"/>.
/// </summary>
public sealed record ProjectPipelineCostTimeline(
    string Project,
    IReadOnlyList<string> Days,
    IReadOnlyList<PipelineDayCostCell> DayCosts,
    int WindowDays,
    IReadOnlyList<PipelineKindSeries> Kinds,
    IReadOnlyList<PipelineStepCostSeries> Steps,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    int TaskCount,
    bool HasData,
    string FetchedAt,
    ProjectTokenDataFreshness Freshness,
    DecisionCostRollup? DecisionCost = null);

public sealed record PipelineDayCostCell(
    string Day,
    long TotalTokens,
    decimal CostUsd,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps);

/// <summary>
/// One pipeline step's token + cost rollup over the whole window, folded
/// across every task run in the project. Keyed by <see cref="StepId"/> so the
/// Pipeline configuration page can show, per step, how many tokens it has
/// spent in the window. <see cref="Kind"/> is the lowercase wire token of the
/// step kind (for colour coding); <see cref="AnyModelUnknown"/> is true when at
/// least one contributing run used a model with no price on file, so the cost
/// is a lower bound.
/// </summary>
public sealed record PipelineStepCostSeries(
    string StepId,
    string Kind,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    int Runs = 0,
    int Tasks = 0,
    IReadOnlyList<string>? Models = null);

/// <summary>
/// One step-kind's series over the window. <see cref="Kind"/> is the
/// lowercase wire token (<c>core</c> / <c>aspect</c> / <c>tool</c> /
/// <c>orchestrator</c> / <c>module</c>) matching the frontend StepKind
/// type. <see cref="AnyModelUnknown"/> is true when at least one
/// contributing step used a model with no price on file, so the cost is
/// a lower bound.
/// </summary>
public sealed record PipelineKindSeries(
    string Kind,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    IReadOnlyList<PipelineKindDayCell> Cells,
    int Runs = 0);

public sealed record PipelineKindDayCell(
    string Day,
    long TotalTokens,
    decimal CostUsd,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps);
