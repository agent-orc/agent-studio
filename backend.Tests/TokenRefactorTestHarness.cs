using Microsoft.Extensions.Configuration;
using AgentStudio.Tokens;
using AgentStudio.Tasks;

namespace AgentStudio.Tests;

internal sealed class ProjectTokenTestReader
{
    private readonly ProjectTokenLedger _ledger;
    private readonly JobStatsMetadataCache _jobs;

    public ProjectTokenTestReader(AgentMessageBusStore store, IConfiguration config,
        JobStatsMetadataCache jobs, ProjectTokenReceiptReader receipts, TaskIndexCache? indexCache = null)
    {
        _ledger = new ProjectTokenLedger(store, config, receipts, indexCache);
        _jobs = jobs;
    }

    public ProjectTokenLedgerSnapshot LoadSnapshot(string project, string watchPath)
        => _ledger.LoadSnapshot(project, watchPath);

    public ProjectTokenUsageSummary BuildSummary(string project, string watchPath, DateTime? now = null)
    {
        var snapshot = LoadSnapshot(project, watchPath);
        return ProjectTokenUsageFold.Summary(project, snapshot.Entries, _jobs.JobsById(watchPath), now)
            with { Freshness = snapshot.Freshness };
    }

    public ProjectTokenHeatmap BuildHeatmap(string project, string watchPath, int days, DateTime? now = null)
    {
        var snapshot = LoadSnapshot(project, watchPath);
        return ProjectTokenUsageFold.Heatmap(project, snapshot.Entries, _jobs.JobsById(watchPath), days, now)
            with { Freshness = snapshot.Freshness };
    }

    public Dictionary<string, TaskTokenSummary> BuildPerJob(string project, string watchPath)
        => TaskTokenSummaryFold.PerJob(TokenPricing.Price(LoadSnapshot(project, watchPath).Entries));

    public IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobs(string project, string watchPath, int limit)
        => ProjectTokenJobFold.ExpensiveJobs(LoadSnapshot(project, watchPath).Entries, _jobs.JobsById(watchPath), limit);

    public ProjectJobTokenDetail? BuildJobDetail(string project, string watchPath, string jobId)
        => ProjectTokenJobFold.JobDetail(project, LoadSnapshot(project, watchPath).Entries, _jobs.JobsById(watchPath), jobId);

    public TokenSummary BuildLifetimeSummary(string project, string watchPath)
        => TokenSummaryFold.ForProject(project, TokenPricing.Price(LoadSnapshot(project, watchPath).Entries));
}

internal static class TokenSummaryTestFolds
{
    public const string DefaultDisclaimer = TokenCostDisclaimer.Text;
    public static TokenSummary Summarize(string project, IReadOnlyList<OrchestratorLogEntry> entries)
        => TokenSummaryFold.ForProject(project, TokenPricing.Price(entries));
    public static Dictionary<string, TaskTokenSummary> SummarizePerJob(IReadOnlyList<OrchestratorLogEntry> entries)
        => TaskTokenSummaryFold.PerJob(TokenPricing.Price(entries));
    public static TaskTokenSummary WithModelFallback(TaskTokenSummary summary, string? model)
        => RunModelFallback.Apply(summary, model);
    public static TokenSummaryAggregate AggregateSummaries(IReadOnlyList<TokenSummary> summaries)
        => TokenSummaryFold.ForWorkspace(summaries.Select(summary => (summary.Project, summary)).ToList());
    public static TokenSummaryAggregate AggregateSummaries(IReadOnlyList<(string Name, TokenSummary Summary)> summaries)
        => TokenSummaryFold.ForWorkspace(summaries);
}

internal static class ProjectTokenUsageTestFolds
{
    public const int DefaultHeatmapDays = ProjectTokenUsageFold.DefaultHeatmapDays;
    public static string Categorize(string? jobId, IReadOnlyDictionary<string, TaskInfo> jobs)
        => ProjectTokenCategory.Of(jobId, jobs);
    public static ProjectTokenUsageSummary BuildSummaryFromEntries(string project,
        IReadOnlyList<OrchestratorLogEntry> entries, IReadOnlyDictionary<string, TaskInfo> jobs, DateTime? now = null)
        => ProjectTokenUsageFold.Summary(project, entries, jobs, now);
    public static ProjectTokenHeatmap BuildHeatmapFromEntries(string project,
        IReadOnlyList<OrchestratorLogEntry> entries, IReadOnlyDictionary<string, TaskInfo> jobs, int days, DateTime? now = null)
        => ProjectTokenUsageFold.Heatmap(project, entries, jobs, days, now);
    public static IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobsFromEntries(
        IReadOnlyList<OrchestratorLogEntry> entries, IReadOnlyDictionary<string, TaskInfo> jobs, int limit)
        => ProjectTokenJobFold.ExpensiveJobs(entries, jobs, limit);
}

internal static class WorkspaceTimelineTestFolds
{
    public static int ResolveWindowHours(int requested) => WorkspaceTimelineFold.ResolveWindowHours(requested);
    public static int ResolveBucketMinutes(int requested) => WorkspaceTimelineFold.ResolveBucketMinutes(requested);
    public static TokenTimeline BuildFromEntries(
        IReadOnlyList<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)> perProject,
        DateTime from, DateTime to, int bucketMinutes)
        => WorkspaceTimelineFold.Build(perProject
            .Select(project => (project.Project, TokenPricing.Price(project.Entries)))
            .ToList(), from, to, bucketMinutes);
}

internal static class WorkspaceTimelineReaderTestFolds
{
    public static TokenTimeline BuildFromLedger(ProjectTokenTestReader reader,
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours, int bucketMinutes, DateTime? nowUtc = null)
    {
        var (start, end, bucket) = WorkspaceTimelineFold.TrailingWindow(windowHours, bucketMinutes, nowUtc);
        var ledgers = projects.Select(project => (project.Name,
            TokenPricing.Price(reader.LoadSnapshot(project.Name, project.WatchPath).Entries))).ToList();
        return WorkspaceTimelineFold.Build(ledgers, start, end, bucket);
    }
}

internal static class AdHocUsageTestFolds
{
    public static AdHocUsageAggregate Aggregate(IReadOnlyList<AdHocUsageRecord> records,
        string logPath, long logSizeBytes, DateTime? logModifiedAt)
        => AdHocUsageFold.Aggregate(records.Select(record => (record,
            TokenPricing.Estimate(record.Model, record.InputTokens, record.OutputTokens,
                record.CacheReadTokens, record.CacheCreationTokens, record.Ts))).ToList(), logPath);
}
