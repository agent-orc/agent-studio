using System.Collections.Concurrent;

namespace AgentStudio.Tokens;

/// <summary>One project the analysis may read.</summary>
public sealed record TokenAnalysisProjectSource(string? ProjectId, string Name, string Path);

/// <summary>
/// Application side of the card-centric token analysis (AGT-3267): loads
/// the merged ledger, the workspace ad-hoc rows attributed to the project,
/// the cards and their pipeline execution logs, and the quota history, then
/// hands them to the pure <see cref="TokenAnalysisFold"/> and
/// <see cref="QuotaEfficiencyFold"/>. A project's folded cards are cached
/// for <see cref="CacheTtl"/> on the injected clock, so filter changes in
/// the view do not re-walk the task folders. Measurement only.
/// </summary>
public sealed class TokenAnalysisService
{
    public const int DefaultDays = 30;
    public const int MaxDays = 180;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ITokenPricing _pricing;
    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _log;
    private readonly ProjectTokenLedger _ledger;
    private readonly AgentMessageBusStore _bus;
    private readonly IConfiguration _config;
    private readonly QuotaHistoryStore? _quota;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectBuild Build)> _cache = new(StringComparer.Ordinal);

    public TokenAnalysisService(ITokenPricing pricing, TaskScannerService scanner, PipelineExecutionLog log,
        ProjectTokenLedger ledger, AgentMessageBusStore bus, IConfiguration config,
        TimeProvider time, QuotaHistoryStore? quota = null)
    {
        _pricing = pricing;
        _scanner = scanner;
        _log = log;
        _ledger = ledger;
        _bus = bus;
        _config = config;
        _time = time;
        _quota = quota;
    }

    public static int ResolveDays(int? days) => days is null or <= 0 ? DefaultDays : Math.Min(days.Value, MaxDays);

    public TokenAnalysisResponse Analyze(IReadOnlyList<TokenAnalysisProjectSource> projects, int? days)
    {
        var resolvedDays = ResolveDays(days);
        var now = _time.GetUtcNow().UtcDateTime;
        var from = now.Date.AddDays(-(resolvedDays - 1));
        var to = now.AddSeconds(1);
        var workspaceRows = ReadWorkspaceRows(out var workspaceWarning);
        var cards = new List<TokenAnalysisCard>();
        var remainders = new List<TokenAnalysisProjectRemainder>();
        var freshness = new List<ProjectTokenDataFreshness>();
        var samples = new List<QuotaUsageSample>();
        foreach (var project in projects)
        {
            var build = Build(project, workspaceRows, now);
            freshness.Add(build.Freshness);
            foreach (var job in build.Jobs)
            {
                if (TokenAnalysisFold.Card(project.Name, job.Input, job.Items, from, to) is { } card)
                    cards.Add(card);
                samples.AddRange(Samples(job.Items, from));
            }
            var rest = TokenAnalysisFold.Slices(build.Remainder.Where(item => item.At >= from && item.At < to));
            if (rest.Count > 0)
            {
                remainders.Add(new TokenAnalysisProjectRemainder(project.Name,
                    rest.Sum(TokenAnalysisFold.SliceTotal), rest.Sum(slice => slice.CostUsd), rest));
            }
            samples.AddRange(Samples(build.Remainder, from));
        }
        // Quota windows are account-wide: unattributed workspace rows count too.
        samples.AddRange(Samples(TokenAnalysisFold.RemainderItems(_pricing,
            workspaceRows.Where(row => row.ProjectName is null).Select(row => row.Entry)), from));

        if (workspaceWarning is not null)
            freshness.Add(new ProjectTokenDataFreshness { Status = "partial", Warning = workspaceWarning });
        var quota = Quota(samples, from, to, out var quotaWarning);
        if (quotaWarning is not null)
            freshness.Add(new ProjectTokenDataFreshness { Status = "partial", Warning = quotaWarning });

        return new TokenAnalysisResponse(now, from, to, resolvedDays,
            projects.Select(project => project.Name).ToList(),
            cards.OrderByDescending(card => card.TotalTokens).ThenBy(card => card.JobId, StringComparer.Ordinal).ToList(),
            remainders, quota, ProjectTokenDataFreshness.Combine(freshness), TokenAnalysisFold.Disclaimer);
    }

    public TokenAnalysisCardDetail? CardDetail(TokenAnalysisProjectSource project, string jobId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var workspaceRows = ReadWorkspaceRows(out _);
        var build = Build(project, workspaceRows, now);
        var job = build.Jobs.FirstOrDefault(candidate => Matches(candidate.Input, jobId));
        if (job is null) return null;
        var card = TokenAnalysisFold.Card(project.Name, job.Input, job.Items, DateTime.MinValue, DateTime.MaxValue);
        if (card is null) return null;
        return new TokenAnalysisCardDetail(card, TokenAnalysisFold.Events(job.Items), build.Freshness,
            TokenAnalysisFold.Disclaimer);
    }

    private static bool Matches(TokenAnalysisJobInput job, string id)
        => string.Equals(job.JobId, id, StringComparison.OrdinalIgnoreCase)
           || string.Equals(job.Task?.Key, id, StringComparison.OrdinalIgnoreCase)
           || string.Equals(job.Task?.TaskKey, id, StringComparison.OrdinalIgnoreCase);

    private sealed record JobBuild(TokenAnalysisJobInput Input, IReadOnlyList<TokenAnalysisItem> Items);

    private sealed record ProjectBuild(
        IReadOnlyList<JobBuild> Jobs, IReadOnlyList<TokenAnalysisItem> Remainder, ProjectTokenDataFreshness Freshness);

    private sealed record WorkspaceRow(string? ProjectName, OrchestratorLogEntry Entry);

    private ProjectBuild Build(TokenAnalysisProjectSource project, IReadOnlyList<WorkspaceRow> workspaceRows, DateTime now)
    {
        var key = project.Name + "\n" + project.Path;
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheTtl && now >= hit.At) return hit.Build;

        var snapshot = _ledger.LoadSnapshot(project.Name, project.Path);
        var tasks = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, project.Path))
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var byIdentity = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            byIdentity.TryAdd(task.Id, task);
            if (!string.IsNullOrWhiteSpace(task.TaskKey)) byIdentity.TryAdd(task.TaskKey, task);
            if (!string.IsNullOrWhiteSpace(task.Key)) byIdentity.TryAdd(task.Key!, task);
        }

        var ledger = TokenLedgerMerge.WithoutDuplicates(snapshot.Entries,
            workspaceRows.Where(row => row.ProjectName is not null
                    && (string.Equals(row.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(row.ProjectName, project.ProjectId, StringComparison.OrdinalIgnoreCase)))
                .Select(row => row.Entry)
                .ToList());
        var perJob = new Dictionary<string, List<OrchestratorLogEntry>>(StringComparer.OrdinalIgnoreCase);
        var remainder = new List<OrchestratorLogEntry>();
        foreach (var entry in ledger)
        {
            if (entry.TokenUsage is null) continue;
            if (string.IsNullOrWhiteSpace(entry.JobId)) { remainder.Add(entry); continue; }
            var id = byIdentity.TryGetValue(entry.JobId!, out var owner) ? owner.Id : entry.JobId!;
            if (!perJob.TryGetValue(id, out var list)) perJob[id] = list = [];
            list.Add(entry);
        }

        var warnings = new List<string>();
        var jobs = new List<JobBuild>();
        var jobIds = perJob.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks) jobIds.Add(task.Id);
        foreach (var jobId in jobIds)
        {
            byIdentity.TryGetValue(jobId, out var task);
            PipelineExecutionRecord? record = null;
            if (!string.IsNullOrWhiteSpace(task?.FolderPath))
            {
                record = _log.Read(task.FolderPath);
                if (record is null && File.Exists(Path.Combine(task.FolderPath, PipelineExecutionLog.FileName)))
                    warnings.Add($"Pipeline telemetry for task {task.Id} could not be read.");
            }
            var input = new TokenAnalysisJobInput(jobId, task, record,
                perJob.TryGetValue(jobId, out var rows) ? rows : []);
            var items = TokenAnalysisFold.Items(_pricing, input);
            if (items.Count > 0) jobs.Add(new JobBuild(input, items));
        }

        var freshness = warnings.Count == 0
            ? snapshot.Freshness
            : ProjectTokenDataFreshness.Combine([snapshot.Freshness, new ProjectTokenDataFreshness
            {
                Status = "partial",
                Warning = string.Join(" ", warnings.Distinct(StringComparer.Ordinal).Take(3))
                    + (warnings.Count > 3 ? $" {warnings.Count - 3} more task logs could not be read." : ""),
                Sources = ["pipeline-execution-log"],
            }]);
        var build = new ProjectBuild(jobs, TokenAnalysisFold.RemainderItems(_pricing, remainder), freshness);
        _cache[key] = (now, build);
        return build;
    }

    /// <summary>
    /// Workspace-scope token rows, each with the project name
    /// <see cref="TokenUsageAttribution"/> resolved (null when unattributed).
    /// The bus projection is in memory, so the whole history is read and the
    /// cached project builds never depend on the requested range.
    /// </summary>
    private List<WorkspaceRow> ReadWorkspaceRows(out string? warning)
    {
        warning = null;
        var root = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return [];
        IReadOnlyList<AgentMessage> messages;
        try
        {
            messages = _bus.Query(root!, AgentMessageBusPaths.WorkspaceScope,
                new AgentMessageQuery(Kind: "token-usage"));
        }
        catch (Exception)
        {
            warning = "The workspace token bus could not be read; ad-hoc calls are missing.";
            return [];
        }
        var watchPaths = _scanner.GetWatchPaths();
        var owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<WorkspaceRow>(messages.Count);
        foreach (var message in TokenLedgerDuplicates.CollapseMessages(messages, out _))
        {
            var attribution = TokenUsageAttribution.Resolve(message.Project, message.Tags, message.JobId,
                name => watchPaths.FirstOrDefault(entry =>
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))?.Name,
                jobId => JobOwner(watchPaths, owners, jobId));
            rows.Add(new WorkspaceRow(attribution.ProjectId, BusTokenEntryConverter.ToEntry(message)));
        }
        return rows;
    }

    private string? JobOwner(IReadOnlyList<WatchPathEntry> watchPaths, Dictionary<string, string?> memo, string jobId)
    {
        if (memo.TryGetValue(jobId, out var known)) return known;
        var task = _scanner.FindJob(jobId);
        var owner = task is null ? null
            : watchPaths.FirstOrDefault(entry => WatchPathComparison.PathsEqual(entry.Path, task.WatchPath))?.Name;
        memo[jobId] = owner;
        return owner;
    }

    private static IEnumerable<QuotaUsageSample> Samples(IEnumerable<TokenAnalysisItem> items, DateTime fromUtc)
        => items.Where(item => item.Total > 0 && item.At >= fromUtc && item.Cli != TokenAnalysisFold.Unknown)
            .Select(item => new QuotaUsageSample(item.At, item.Cli, item.Total,
                item.Input + item.Output + item.CacheWrite));

    private IReadOnlyList<TokenAnalysisQuotaWindow> Quota(
        IReadOnlyList<QuotaUsageSample> samples, DateTime fromUtc, DateTime toUtc, out string? warning)
    {
        warning = null;
        if (_quota is null) return [];
        var clis = samples.Select(sample => sample.Cli)
            .Concat(["claude", "codex"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(cli => cli, StringComparer.Ordinal);
        var windows = new List<TokenAnalysisQuotaWindow>();
        foreach (var cli in clis)
        {
            try
            {
                windows.AddRange(QuotaEfficiencyFold.Fold(cli, _quota.Read(cli, fromUtc), samples, fromUtc, toUtc));
            }
            catch (Exception)
            {
                warning = "The quota history could not be read for every CLI.";
            }
        }
        return windows;
    }
}
