using System.Text.Json;

namespace AgentStudio.Tokens;

/// <summary>
/// One-off repair for token receipts that recorded the same usage more than
/// once (AGT-3012; AGT-3004 held one Claude session 22 times). Task receipts
/// are rewritten through <see cref="TaskMutationService"/>; immutable bus rows
/// are only counted because <see cref="BusTokenEntryConverter"/> collapses
/// them on read. The report lists, per project, how many entries and tokens
/// were collapsed.
/// </summary>
public sealed class TokenLedgerDuplicateRepair
{
    public const string ReportFileName = "token-ledger-duplicates-v1.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ITokenLedgerDuplicateRepairStore _store;
    private readonly ILogger<TokenLedgerDuplicateRepair> _logger;
    private readonly string _reportPath;

    public TokenLedgerDuplicateRepair(
        TaskScannerService scanner,
        TaskMutationService mutations,
        AgentMessageBusStore bus,
        TokenSummaryCacheStore aggregateCache,
        WorkspaceTokensCacheStore workspaceCache,
        IConfiguration configuration,
        ILogger<TokenLedgerDuplicateRepair> logger)
        : this(
            new TokenLedgerDuplicateRepairStore(
                scanner,
                mutations,
                bus,
                configuration["TaskRepository"],
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

    internal TokenLedgerDuplicateRepair(
        ITokenLedgerDuplicateRepairStore store,
        string reportPath,
        ILogger<TokenLedgerDuplicateRepair> logger)
    {
        _store = store;
        _reportPath = reportPath;
        _logger = logger;
    }

    public TokenLedgerDuplicateRepairReport RunOnce()
    {
        if (File.Exists(_reportPath))
        {
            var prior = JsonSerializer.Deserialize<TokenLedgerDuplicateRepairReport>(
                File.ReadAllText(_reportPath), Json);
            return (prior ?? TokenLedgerDuplicateRepairReport.Empty) with { AlreadyCompleted = true };
        }

        var jobs = _store.ScanJobs();
        var plan = Plan(jobs, _store.ReadBus(jobs.Select(job => job.Project)));
        var failedKeys = new List<string>();
        foreach (var rewrite in plan.Rewrites)
        {
            if (!_store.ReplaceTokenSummary(rewrite.FolderPath, rewrite.Summary))
                failedKeys.Add(rewrite.TaskKey);
        }
        if (plan.Rewrites.Count > failedKeys.Count) _store.InvalidateCaches();

        var report = plan.Report with
        {
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Completed = failedKeys.Count == 0,
            FailedKeys = failedKeys,
        };
        // A failed write keeps the report absent so the next start retries;
        // rewrites that succeeded are already collapsed and plan as no-ops.
        if (report.Completed) WriteReport(report);
        _logger.Log(
            report.Completed ? LogLevel.Information : LogLevel.Warning,
            "token-ledger-duplicate-repair completed={Completed} receiptEntries={ReceiptEntries} receiptTokens={ReceiptTokens} busEntries={BusEntries} busTokens={BusTokens} failed={Failed} report={Report}",
            report.Completed,
            report.Projects.Sum(project => project.ReceiptEntriesCollapsed),
            report.Projects.Sum(project => project.ReceiptTokensCollapsed),
            report.Projects.Sum(project => project.BusEntriesCollapsed),
            report.Projects.Sum(project => project.BusTokensCollapsed),
            failedKeys.Count,
            _reportPath);
        return report;
    }

    /// <summary>
    /// Pure planning step: which receipts to rewrite and what each project
    /// loses. Also the read-only dry run used for delivery evidence.
    /// </summary>
    public static TokenLedgerDuplicatePlan Plan(
        IEnumerable<TokenLedgerReceiptSource> receipts,
        IEnumerable<TokenLedgerBusSource> bus)
    {
        var projects = new SortedDictionary<string, ProjectAcc>(StringComparer.Ordinal);
        ProjectAcc For(string project)
        {
            if (!projects.TryGetValue(project, out var acc))
            {
                acc = new ProjectAcc();
                projects[project] = acc;
            }
            return acc;
        }

        var rewrites = new List<TokenLedgerReceiptRewrite>();
        var tasks = new List<TokenLedgerDuplicateTaskReport>();
        foreach (var job in receipts)
        {
            if (job.TokenSummary is not { } summary) continue;
            var collapsedSummary = ProjectTokenReceiptReader.CollapseDuplicateCalls(summary, out var collapsed);
            if (collapsed.Entries == 0) continue;

            var cost = PricedCost(summary.Entries) - PricedCost(collapsedSummary.Entries);
            var acc = For(job.Project);
            acc.ReceiptTasks++;
            acc.Receipt = acc.Receipt.Add(collapsed);
            acc.ReceiptCost += cost;
            rewrites.Add(new TokenLedgerReceiptRewrite(job.FolderPath, job.TaskKey, collapsedSummary));
            tasks.Add(new TokenLedgerDuplicateTaskReport(
                job.Project,
                job.TaskKey,
                summary.Entries.Count,
                collapsedSummary.Entries.Count,
                collapsed.Entries,
                collapsed.Tokens,
                summary.TotalTokens,
                collapsedSummary.TotalTokens,
                cost));
        }

        foreach (var source in bus)
        {
            TokenLedgerDuplicates.CollapseMessages(source.Messages, out var collapsed);
            if (collapsed.Entries == 0) continue;
            var acc = For(source.Project);
            acc.Bus = acc.Bus.Add(collapsed);
        }

        var rows = projects
            .Select(pair => new TokenLedgerDuplicateProjectReport(
                pair.Key,
                pair.Value.ReceiptTasks,
                pair.Value.Receipt.Entries,
                pair.Value.Receipt.Tokens,
                pair.Value.ReceiptCost,
                pair.Value.Bus.Entries,
                pair.Value.Bus.Tokens))
            .ToList();
        return new TokenLedgerDuplicatePlan(
            rewrites,
            TokenLedgerDuplicateRepairReport.Empty with { Projects = rows, Tasks = tasks });
    }

    private static decimal PricedCost(IEnumerable<TaskTokenCall> calls)
        => calls.Sum(stored =>
        {
            var call = StoredUsageNormalization.Normalize(stored);
            var estimate = TokenPricing.Estimate(
                ModelMetadataRegistry.NormalizeId(call.Model),
                call.InputTokens,
                call.OutputTokens,
                call.CacheReadTokens,
                call.CacheCreationTokens,
                call.Ts);
            return estimate.ModelKnown ? estimate.Total : 0m;
        });

    private void WriteReport(TokenLedgerDuplicateRepairReport report)
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

    private sealed class ProjectAcc
    {
        public int ReceiptTasks;
        public TokenLedgerCollapseCount Receipt;
        public decimal ReceiptCost;
        public TokenLedgerCollapseCount Bus;
    }

    private sealed class TokenLedgerDuplicateRepairStore(
        TaskScannerService scanner,
        TaskMutationService mutations,
        AgentMessageBusStore bus,
        string? workspaceRoot,
        TokenSummaryCacheStore aggregateCache,
        WorkspaceTokensCacheStore workspaceCache) : ITokenLedgerDuplicateRepairStore
    {
        public IReadOnlyList<TokenLedgerReceiptSource> ScanJobs()
            => scanner.ScanAllJobsWithArchive()
                .Select(task => new TokenLedgerReceiptSource(
                    task.FolderPath,
                    task.ProjectName,
                    task.Key ?? task.Id,
                    task.TokenSummary))
                .ToList();

        public IEnumerable<TokenLedgerBusSource> ReadBus(IEnumerable<string> projects)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot)) yield break;
            foreach (var project in projects
                         .Concat(scanner.GetWatchPaths().Select(entry => entry.Name))
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.Ordinal))
            {
                yield return new TokenLedgerBusSource(
                    project,
                    bus.Query(workspaceRoot, project, new AgentMessageQuery(Kind: "token-usage")));
            }
        }

        public bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary)
            => mutations.ReplaceTokenSummaryForMigration(folderPath, summary);

        public void InvalidateCaches()
        {
            aggregateCache.Invalidate();
            workspaceCache.Invalidate();
        }
    }
}

internal interface ITokenLedgerDuplicateRepairStore
{
    IReadOnlyList<TokenLedgerReceiptSource> ScanJobs();
    IEnumerable<TokenLedgerBusSource> ReadBus(IEnumerable<string> projects);
    bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary);
    void InvalidateCaches();
}

public sealed record TokenLedgerReceiptSource(
    string FolderPath,
    string Project,
    string TaskKey,
    TaskTokenSummary? TokenSummary);

public sealed record TokenLedgerBusSource(
    string Project,
    IReadOnlyList<AgentMessage> Messages);

public sealed record TokenLedgerReceiptRewrite(
    string FolderPath,
    string TaskKey,
    TaskTokenSummary Summary);

public sealed record TokenLedgerDuplicatePlan(
    IReadOnlyList<TokenLedgerReceiptRewrite> Rewrites,
    TokenLedgerDuplicateRepairReport Report);

public sealed record TokenLedgerDuplicateProjectReport(
    string Project,
    int ReceiptTasks,
    int ReceiptEntriesCollapsed,
    long ReceiptTokensCollapsed,
    decimal ReceiptCostCollapsedUsd,
    int BusEntriesCollapsed,
    long BusTokensCollapsed);

public sealed record TokenLedgerDuplicateTaskReport(
    string Project,
    string TaskKey,
    int EntriesBefore,
    int EntriesAfter,
    int EntriesCollapsed,
    long TokensCollapsed,
    long TotalTokensBefore,
    long TotalTokensAfter,
    decimal CostCollapsedUsd);

public sealed record TokenLedgerDuplicateRepairReport(
    int Version,
    DateTimeOffset CompletedAtUtc,
    bool AlreadyCompleted,
    bool Completed,
    IReadOnlyList<TokenLedgerDuplicateProjectReport> Projects,
    IReadOnlyList<TokenLedgerDuplicateTaskReport> Tasks,
    IReadOnlyList<string> FailedKeys)
{
    public static TokenLedgerDuplicateRepairReport Empty
        => new(1, DateTimeOffset.MinValue, false, false, [], [], []);
}
