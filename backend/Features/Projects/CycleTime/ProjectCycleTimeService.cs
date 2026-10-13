using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Tokens;
using AgentStudio.Pipeline;

namespace AgentStudio.Projects;

public sealed record ProjectCycleTimeCoverage(
    /// <summary>Non-fixture tasks of the project, archive-inclusive.</summary>
    int TasksInProject,
    /// <summary>Terminal tasks (6-completed or 7-archive) at any time, regardless of completion evidence.</summary>
    int TasksTerminal,
    /// <summary>Tasks whose completion falls into the window; these form the aggregates and rows.</summary>
    int TasksInWindow,
    /// <summary>
    /// Terminal tasks examined for this window whose completion time is unknown
    /// (archived without a recorded 6-completed entry). A bounded window skips
    /// tasks that were terminal before the window, so this counts only the
    /// examined candidates; the <c>all</c> window counts every terminal task.
    /// </summary>
    int ExcludedNoCompletionTimestamp,
    /// <summary>Tasks not yet terminal; cycle time is defined on completion.</summary>
    int ExcludedInFlight,
    int ExcludedEpics,
    /// <summary>Window tasks whose ledger is missing; their lead time is reported as unattributed.</summary>
    int TasksWithoutLedger,
    /// <summary>Window tasks whose completion time came from the legacy lane-entry fallback.</summary>
    int TasksWithLaneEntryCompletion,
    /// <summary>
    /// Window tasks whose completion time was reconstructed by the offline
    /// backfill sidecar (<c>.metadata/cycle-time-backfill.json</c>). These
    /// rows enter the lead-time rollup only; their stage breakdown is null
    /// and they stay out of the stage, count, and outcome aggregates.
    /// </summary>
    int TasksBackfilled)
{
    public int ReAcceptanceArtefactsInWindow { get; init; }
}

public sealed record CycleTimeThroughputDay(DateTime Day, int Delivered, int MachineDone,
    int Completed, int ReAcceptanceArtefacts);

public sealed record FlowDimensionAggregate(string Id, string Label, string Class,
    CycleTimeStageAggregate Duration, long Tokens, decimal CostUsd, int UnpricedCalls,
    double? CostPerCardP50Usd, double? ShareOfMachineCycle, bool Excluded)
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
}

public sealed record ProjectCycleTimeResponse(
    string Project,
    string? ProjectId,
    string? ShortCode,
    string Window,
    DateTime CapturedAt,
    DateTime? Since,
    ProjectCycleTimeCoverage Coverage,
    /// <summary>Additive lane stages in lane order, then the rollups, then the counts.</summary>
    IReadOnlyList<CycleTimeStageAggregate> Aggregates,
    IReadOnlyList<CycleTimeOutcomeCount> IntegrationOutcomes,
    /// <summary>Lane transition matrix, per-lane dwell, bounce causes, and the tasks with the most backward moves.</summary>
    CycleTimeTransitionSummary Transitions,
    /// <summary>Per-task rows, newest completion first. <c>transitions</c> is null unless <c>detail=transitions</c> was requested.</summary>
    IReadOnlyList<TaskCycleTime> Tasks)
{
    public IReadOnlyList<CycleTimeStageAggregate> Flow { get; init; } = [];
    public IReadOnlyList<FlowDimensionAggregate> Steps { get; init; } = [];
    public IReadOnlyList<FlowDimensionAggregate> Resources { get; init; } = [];
    public IReadOnlyList<FlowDimensionAggregate> Holds { get; init; } = [];
    public IReadOnlyList<FlowDimensionAggregate> Human { get; init; } = [];
    public IReadOnlyList<CycleTimeThroughputDay> ThroughputPerDay { get; init; } = [];
}

/// <summary>Per-task response of <c>GET /api/projects/{project}/cycle-time/tasks/{taskKey}</c>.</summary>
public sealed record ProjectCycleTimeTaskResponse(
    string Project,
    DateTime CapturedAt,
    TaskCycleTime Task);

/// <summary>
/// Read model for <c>GET /api/projects/{project}/cycle-time</c>. Enumerates the
/// project's tasks through the index-cache-backed scanner (never a cold disk
/// walk), analyses each terminal task once, and memoises the per-task row
/// against the ledger, pipeline, gate-log, token and dependency stamps. The per-project result is
/// additionally cached for a short TTL.
/// </summary>
public sealed class ProjectCycleTimeService
{
    public const string DefaultWindow = "7d";
    public const string AllWindow = "all";
    private static readonly TimeSpan ProjectCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly Regex WindowPattern = new(@"^(\d{1,4})d$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly PipelineExecutionLog _pipeline;
    private readonly ProjectRegistry? _projects;
    private readonly ILogger<ProjectCycleTimeService> _logger;
    private readonly ITokenAggregator? _tokens;
    private readonly BatchGateStore? _batchGate;
    private readonly AttemptAuthorityService? _attemptAuthority;
    private readonly Func<string, (IReadOnlyList<FlowSegment> Segments, bool Malformed)> _gateLogReader;
    private readonly Func<BatchGateStore, string, BatchGateCycleEvidence> _batchReader;
    private readonly ConcurrentDictionary<string, ((long Count, long Ticks) Stamp, BatchGateCycleEvidence Evidence)> _gateMemo = new();

    private readonly ConcurrentDictionary<string, TaskMemo> _taskMemo =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectMemo> _projectMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BackfillMemo> _backfillMemo =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ((long Length, long Ticks) Stamp, IReadOnlyList<TimelineEvent> Events)> _dependencyLedgerMemo = new();

    public ProjectCycleTimeService(
        TaskScannerService scanner,
        TimelineLog timeline,
        PipelineExecutionLog pipeline,
        ILogger<ProjectCycleTimeService> logger,
        ProjectRegistry? projects = null,
        ITokenAggregator? tokens = null,
        BatchGateStore? batchGate = null,
        AttemptAuthorityService? attemptAuthority = null,
        Func<string, (IReadOnlyList<FlowSegment> Segments, bool Malformed)>? gateLogReader = null,
        Func<BatchGateStore, string, BatchGateCycleEvidence>? batchReader = null)
    {
        _scanner = scanner;
        _timeline = timeline;
        _pipeline = pipeline;
        _logger = logger;
        _projects = projects;
        _tokens = tokens;
        _batchGate = batchGate;
        _attemptAuthority = attemptAuthority;
        _gateLogReader = gateLogReader ?? FlowGateLogEvidence.Read;
        _batchReader = batchReader ?? BatchGateCycleEvidence.Load;
    }

    /// <summary>Parses <c>7d</c>, <c>30d</c>, ..., or <c>all</c>. Returns null for an invalid value.</summary>
    public static bool TryParseWindow(string? raw, out string normalized, out TimeSpan? span)
    {
        normalized = DefaultWindow;
        span = TimeSpan.FromDays(7);
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var value = raw.Trim().ToLowerInvariant();
        if (value == AllWindow)
        {
            normalized = AllWindow;
            span = null;
            return true;
        }
        var match = WindowPattern.Match(value);
        if (!match.Success) return false;
        var days = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (days <= 0) return false;
        normalized = $"{days}d";
        span = TimeSpan.FromDays(days);
        return true;
    }

    public const string TransitionsDetail = "transitions";

    /// <summary>
    /// Builds the response, or null when the project is unknown. Per-task
    /// transition lists are included only when <paramref name="detail"/> is
    /// <c>transitions</c>; the project-level transition summary is always present.
    /// </summary>
    public ProjectCycleTimeResponse? Build(string projectHandle, string? window, DateTime? nowUtc = null, string? detail = null)
    {
        if (!TryParseWindow(window, out var normalizedWindow, out var span))
            throw new ArgumentException($"Invalid window '{window}'. Use 1d, 7d, 30d, or all.", nameof(window));

        var entry = ResolveWatchPath(projectHandle);
        if (entry is null) return null;
        var project = _projects?.FindByStorageLocation(entry.Path)
                      ?? _projects?.FindByIdOrDisplayName(projectHandle)
                      ?? _projects?.FindByShortCode(projectHandle);

        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        DateTime? since = span is null ? null : now - span.Value;

        var tasks = ProjectTasks(entry);
        var analyses = AnalyzeProject(entry.Name, entry.Path, normalizedWindow, tasks, since);
        var includeTransitions = string.Equals(detail?.Trim(), TransitionsDetail, StringComparison.OrdinalIgnoreCase);
        return BuildResponse(entry.Name, project?.Id, project?.ShortCode, normalizedWindow, now, since, analyses, includeTransitions);
    }

    /// <summary>
    /// One task with its full transition list, independent of any window. Null
    /// when the project is unknown; a result without <c>Task</c> when the task is
    /// unknown or not analysable (<see cref="TaskCycleAnalysis.ExclusionReason"/>).
    /// </summary>
    public (ProjectCycleTimeTaskResponse? Response, string? ExclusionReason)? BuildTask(string projectHandle, string taskKey, DateTime? nowUtc = null)
    {
        var entry = ResolveWatchPath(projectHandle);
        if (entry is null) return null;
        var key = (taskKey ?? string.Empty).Trim();
        var task = ProjectTasks(entry).FirstOrDefault(t =>
            string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.TaskKey, key, StringComparison.OrdinalIgnoreCase));
        if (task is null) return (null, "unknown-task");
        var tokens = _tokens?.WorkspacePerJob(entry.Name, entry.Path);
        var byKey = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(t => !string.IsNullOrWhiteSpace(t.Key))
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var analysis = AnalyzeTask(task, tokens, GateEvidence(entry.Name), byKey);
        if (analysis.Row is null) return (null, analysis.ExclusionReason ?? TaskCycleAnalysis.ExcludedNotCompleted);
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        return (new ProjectCycleTimeTaskResponse(entry.Name, now, analysis.Row), null);
    }

    private List<TaskInfo> ProjectTasks(WatchPathEntry entry) =>
        _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, entry.Path))
            .ToList();

    /// <summary>Pure aggregation over analysed tasks; exposed for tests.</summary>
    internal static ProjectCycleTimeResponse BuildResponse(
        string projectName,
        string? projectId,
        string? shortCode,
        string window,
        DateTime now,
        DateTime? since,
        IReadOnlyList<TaskCycleAnalysis> analyses,
        bool includeTransitions = false)
    {
        var completed = analyses.Where(a => a.Row is not null).Select(a => a.Row!).ToList();
        var rows = completed
            .Where(r => r.CompletedAt <= now.AddMinutes(5) && (since is null || r.CompletedAt >= since.Value))
            .OrderByDescending(r => r.CompletedAt)
            .ThenBy(r => r.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Backfilled rows (approximate completion, null stage breakdown) are
        // real completions for coverage and for the lead-time rollup, but they
        // carry no stage, count, or outcome evidence: those aggregates use only
        // the evidenced rows so a reconstructed timestamp never dilutes them.
        var evidenced = rows.Where(r => r.Stages is not null).ToList();

        var coverage = new ProjectCycleTimeCoverage(
            analyses.Count,
            completed.Count
                + analyses.Count(a => a.ExclusionReason is TaskCycleAnalysis.ExcludedBeforeWindow
                    or TaskCycleAnalysis.ExcludedNoCompletion),
            rows.Count,
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedNoCompletion),
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedNotCompleted),
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedEpic),
            evidenced.Count(r => r.DataGaps.Contains("no-ledger")),
            evidenced.Count(r => r.CompletionSource == TaskCycleTimeAnalyzer.LaneEntryCompletionSource),
            rows.Count - evidenced.Count)
        {
            ReAcceptanceArtefactsInWindow = analyses.Where(a => a.Row is not null)
                .Sum(a => a.Row!.ReAcceptanceArtefactTimes.Count(t => t <= now && (since is null || t >= since))),
        };

        var aggregates = new List<CycleTimeStageAggregate>();
        foreach (var stage in CycleTimeStages.Additive)
        {
            aggregates.Add(CycleTimeStatistics.Aggregate(stage, "stage", "seconds",
                evidenced.Select(r => r.Stages!.Get(stage)).Where(v => v > 0)));
        }
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.ReviewRun, "rollup", "seconds",
            evidenced.Select(r => r.ReviewRunSeconds).Where(v => v > 0)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.LeadTime, "rollup", "seconds",
            rows.Select(r => r.LeadTimeSeconds).Where(v => v > 0)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.CycleTime, "rollup", "seconds",
            evidenced.Where(r => r.CycleTimeSeconds is > 0).Select(r => r.CycleTimeSeconds!.Value)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.CodingRuns, "count", "count",
            evidenced.Select(r => (double)r.CodingRuns)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.ReviewRounds, "count", "count",
            evidenced.Select(r => (double)r.ReviewRounds)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.BounceRounds, "count", "count",
            evidenced.Select(r => (double)r.BounceRounds)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.IntegrationAttempts, "count", "count",
            evidenced.Select(r => (double)r.IntegrationAttempts)));

        var outcomes = evidenced
            .GroupBy(r => string.IsNullOrWhiteSpace(r.IntegrationOutcome) ? "none" : r.IntegrationOutcome!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CycleTimeOutcomeCount(g.Key, g.Count()))
            .OrderByDescending(o => o.Count)
            .ThenBy(o => o.Outcome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var transitions = CycleTimeTransitionAggregation.Build(rows);
        var taskRows = includeTransitions
            ? rows
            : rows.Select(r => r with { Transitions = null,
                Flow = r.Flow?.WithoutSegments() }).ToList();

        return new ProjectCycleTimeResponse(
            projectName, projectId, shortCode, window, now, since, coverage, aggregates, outcomes, transitions, taskRows)
        {
            Flow = FlowAggregates.BuildTotals(rows),
            Steps = FlowAggregates.BuildDimensions(rows, "active"),
            Resources = FlowAggregates.BuildDimensions(rows, "resourceWait"),
            Holds = FlowAggregates.BuildDimensions(rows, "systemHold"),
            Human = FlowAggregates.BuildDimensions(rows, "humanWait"),
            ThroughputPerDay = FlowAggregates.BuildThroughputDays(analyses, now, since),
        };
    }

    private WatchPathEntry? ResolveWatchPath(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        var entries = _scanner.GetWatchPaths();
        var byName = entries.FirstOrDefault(e => string.Equals(e.Name, handle, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        var project = _projects?.FindByIdOrDisplayName(handle) ?? _projects?.FindByShortCode(handle);
        if (project is not null && !string.IsNullOrWhiteSpace(project.StorageLocation))
        {
            var byStorage = entries.FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, project.StorageLocation));
            if (byStorage is not null) return byStorage;
        }
        return entries.FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, handle));
    }

    private IReadOnlyList<TaskCycleAnalysis> AnalyzeProject(string projectName, string watchPath, string window, List<TaskInfo> tasks, DateTime? since)
    {
        // A bounded window only needs tasks that could have completed inside
        // it. A task enters its terminal lane after completion, so
        // EnteredLaneAt is a safe lower bound for the completion time; older
        // terminal tasks are skipped without touching their files. Non-terminal
        // tasks are classified without any file read.
        //
        // The memo is keyed by the exact window: the coverage block counts the
        // examined candidates, so serving a 7d request from a 30d run would
        // make those counts depend on cache state instead of the window.
        var memoKey = $"{projectName}|{window}";
        if (_projectMemo.TryGetValue(memoKey, out var cached)
            && DateTime.UtcNow - cached.At < ProjectCacheTtl
            && cached.TaskCount == tasks.Count
            && (since is null || (cached.Since is not null && cached.Since <= since)))
        {
            // A window start up to the TTL older is a superset; BuildResponse
            // applies the exact window to the rows.
            return cached.Analyses;
        }

        var results = new ConcurrentBag<TaskCycleAnalysis>();
        var work = new List<TaskInfo>();
        foreach (var task in tasks)
        {
            if (string.Equals(task.Kind, TaskKinds.Epic, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedEpic));
                continue;
            }
            var terminal = string.Equals(task.State, TaskStates.Completed, StringComparison.Ordinal)
                           || string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal);
            if (!terminal)
            {
                if (since is null || task.EnteredLaneAt == default || Utc(task.EnteredLaneAt) >= since.Value)
                    work.Add(task);
                else
                    results.Add(new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedNotCompleted));
                continue;
            }
            if (since is not null && task.EnteredLaneAt != default && Utc(task.EnteredLaneAt) < since.Value)
            {
                // Completed before the window started: counts as completed for
                // coverage, never enters the rows. Keep the cheap classification.
                results.Add(new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedBeforeWindow));
                continue;
            }
            work.Add(task);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) };
        var tokens = _tokens?.WorkspacePerJob(projectName, watchPath);
        var gates = GateEvidence(projectName);
        var byKey = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(t => !string.IsNullOrWhiteSpace(t.Key))
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(work, options, task => results.Add(AnalyzeTask(task, tokens, gates, byKey)));

        var analyses = results.ToList();
        _projectMemo[memoKey] = new ProjectMemo(DateTime.UtcNow, since, tasks.Count, analyses);
        return analyses;
    }

    private TaskCycleAnalysis AnalyzeTask(TaskInfo task, IReadOnlyDictionary<string, TaskTokenSummary>? tokens = null,
        BatchGateCycleEvidence? gates = null, IReadOnlyDictionary<string, TaskInfo>? byKey = null)
    {
        var (backfillEntries, backfillStamp) = BackfillFor(task.WatchPath);
        var backfill = FindBackfill(backfillEntries, task);

        var folder = task.FolderPath;
        if (string.IsNullOrWhiteSpace(folder))
            return TaskCycleTimeAnalyzer.Analyze(task, [], null, backfill);

        var timelineStamp = Stamp(TaskPaths.TimelineLog(folder));
        var pipelineStamp = Stamp(Path.Combine(folder, PipelineExecutionLog.FileName));
        var postStepsStamp = DirectoryStamp(Path.Combine(folder, "post-steps"));
        var summary = tokens is not null && tokens.TryGetValue(task.Id, out var found) ? found : null;
        var tokenStamp = (summary?.Calls ?? 0, summary?.LastUpdate?.Ticks ?? 0);
        var dependencyStamp = DependencyStamp(task, byKey);
        if (_taskMemo.TryGetValue(folder, out var memo)
            && memo.TimelineStamp == timelineStamp
            && memo.PipelineStamp == pipelineStamp
            && memo.PostStepsStamp == postStepsStamp
            && memo.TokenStamp == tokenStamp
            && memo.DependencyStamp == dependencyStamp
            && ReferenceEquals(memo.GateEvidence, gates)
            && memo.BackfillStamp == backfillStamp
            && memo.State == task.State
            && memo.EnteredLaneAt == task.EnteredLaneAt)
        {
            return memo.Analysis;
        }

        IReadOnlyList<TimelineEvent> events;
        try
        {
            events = _timeline.ReadAll(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "cycle-time-timeline-read-failed task={TaskId} folder={Folder}", task.Id, folder);
            events = [];
        }

        var deliveries = events.Where(e => e.Kind == TimelineEventKinds.LaneChanged
                && e.Details?.GetValueOrDefault("from") == TaskStates.Progress
                && e.Details?.GetValueOrDefault("to") == TaskStates.AutoReview
                && e.Details?.GetValueOrDefault("cause") == "delivered")
            .Select(e => Utc(e.Ts)).ToList();
        if (task.State is not (TaskStates.Completed or TaskStates.Archive))
        {
            var inFlight = new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedNotCompleted)
                { Deliveries = deliveries };
            _taskMemo[folder] = new TaskMemo(timelineStamp, pipelineStamp, postStepsStamp, tokenStamp,
                dependencyStamp, gates, backfillStamp, task.State, task.EnteredLaneAt, inFlight);
            return inFlight;
        }

        PipelineExecutionRecord? pipeline = null;
        try
        {
            pipeline = _pipeline.Read(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "cycle-time-pipeline-read-failed task={TaskId} folder={Folder}", task.Id, folder);
        }

        var taskKey = !string.IsNullOrWhiteSpace(task.Key) ? task.Key! : task.TaskKey;
        var gateLogs = _gateLogReader(folder);
        var dependencySpans = DependencyFlowEvidence.Build(task, events, key =>
        {
            if (byKey is null || !byKey.TryGetValue(key, out var target)) return null;
            return (target, DependencyLedger(target));
        });
        var reviewIds = events.Where(e => e.Kind == TimelineEventKinds.ReviewAttemptClaimed)
            .Select(e => e.Details?.GetValueOrDefault("attemptId") ?? e.RunId)
            .OfType<string>().ToList();
        var gateSpans = (gates?.ForTask(taskKey, reviewIds, task.EnteredLaneAt, events) ?? []).Concat(gateLogs.Segments)
            .Concat(dependencySpans).ToList();
        var firstBatchQueue = gateSpans.Where(s => s.Resource == "gateHost")
            .OrderBy(s => s.Start).FirstOrDefault();
        if (firstBatchQueue is not null)
        {
            var lastReviewStep = events.Where(e => e.Kind == TimelineEventKinds.PostStepFinished
                    && e.Ts <= firstBatchQueue.Start)
                .Select(e => e.Ts).LastOrDefault();
            if (lastReviewStep != default && firstBatchQueue.Start - lastReviewStep <= TimeSpan.FromMinutes(1))
                gateSpans.Add(new FlowSegment("resourceWait", null, "reviewSlot", null,
                    lastReviewStep, firstBatchQueue.Start,
                    (firstBatchQueue.Start - lastReviewStep).TotalSeconds));
        }
        IReadOnlyList<ReviewAttemptDto>? reviewAuthority = null;
        if (_attemptAuthority is not null)
            reviewAuthority = _attemptAuthority.GetTaskProjection(taskKey, includeArchived: false).ReviewAttempts;
        var analysis = TaskCycleTimeAnalyzer.Analyze(task, events, pipeline, backfill,
            gateSpans, reviewAuthority);
        analysis = analysis with { Deliveries = deliveries };
        if (gateLogs.Malformed && analysis.Row is not null)
            analysis = analysis with { Row = analysis.Row with
                { DataGaps = analysis.Row.DataGaps.Append("gate-log-malformed").ToList() } };
        if (analysis.Row is not null)
            analysis = analysis with { Row = FlowTokenAttribution.Apply(analysis.Row, summary, events) };
        _taskMemo[folder] = new TaskMemo(timelineStamp, pipelineStamp, postStepsStamp, tokenStamp,
            dependencyStamp, gates, backfillStamp, task.State, task.EnteredLaneAt, analysis);
        return analysis;
    }

    /// <summary>
    /// The project's completion-backfill sidecar, cached against the file
    /// stamp so a (re)generated sidecar invalidates the per-task memos on the
    /// next uncached build without any restart.
    /// </summary>
    private (IReadOnlyDictionary<string, CycleTimeBackfillEntry> Entries, (long Length, long Ticks) Stamp) BackfillFor(string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath))
            return (EmptyBackfill, (-1, 0));
        var path = CycleTimeBackfillSidecar.PathFor(watchPath);
        var stamp = Stamp(path);
        if (_backfillMemo.TryGetValue(path, out var memo) && memo.Stamp == stamp)
            return (memo.Entries, stamp);
        var entries = stamp.Length < 0 ? EmptyBackfill : CycleTimeBackfillSidecar.Load(path);
        if (entries.Count > 0)
            _logger.LogInformation("cycle-time-backfill-loaded path={Path} entries={Count}", path, entries.Count);
        _backfillMemo[path] = new BackfillMemo(stamp, entries);
        return (entries, stamp);
    }

    private static CycleTimeBackfillEntry? FindBackfill(IReadOnlyDictionary<string, CycleTimeBackfillEntry> entries, TaskInfo task)
    {
        if (entries.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(task.Key) && entries.TryGetValue(task.Key, out var byKey)) return byKey;
        if (!string.IsNullOrWhiteSpace(task.Id) && entries.TryGetValue(task.Id, out var byId)) return byId;
        return null;
    }

    /// <summary>
    /// Same timestamp normalization as the analyzer: an unspecified kind is
    /// UTC (the task.json writers stamp UTC, offsets deserialize as Local), so
    /// the window prefilter and the row timestamps agree.
    /// </summary>
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static (long Length, long Ticks) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-2, 0);
        }
    }

    private static (long Length, long Ticks) DirectoryStamp(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return (-1, 0);
            var files = Directory.EnumerateFiles(path, "build-test-gate-*.log", SearchOption.TopDirectoryOnly);
            long count = 0, newest = 0;
            foreach (var file in files)
            {
                count++;
                newest = Math.Max(newest, File.GetLastWriteTimeUtc(file).Ticks);
            }
            return (count, newest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-2, 0); }
    }

    private BatchGateCycleEvidence? GateEvidence(string project)
    {
        if (_batchGate is null) return null;
        var root = Path.GetDirectoryName(_batchGate.BatchDirectory("cycle-time-probe"));
        if (root is null) return null;
        (long Count, long Ticks) stamp = (0, 0);
        try
        {
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    stamp.Count++;
                    stamp.Ticks = Math.Max(stamp.Ticks, File.GetLastWriteTimeUtc(path).Ticks);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "cycle-time-batch-stamp-failed project={Project}", project);
            return null;
        }
        if (_gateMemo.TryGetValue(project, out var memo) && memo.Stamp == stamp) return memo.Evidence;
        try
        {
            var evidence = _batchReader(_batchGate, project);
            _gateMemo[project] = (stamp, evidence);
            return evidence;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "cycle-time-batch-read-failed project={Project}", project);
            return null;
        }
    }

    private IReadOnlyList<TimelineEvent> DependencyLedger(TaskInfo target)
    {
        if (string.IsNullOrWhiteSpace(target.FolderPath)) return [];
        var path = target.FolderPath;
        var stamp = Stamp(TaskPaths.TimelineLog(path));
        if (_dependencyLedgerMemo.TryGetValue(path, out var memo) && memo.Stamp == stamp) return memo.Events;
        IReadOnlyList<TimelineEvent> events;
        try { events = _timeline.ReadAll(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "cycle-time-dependency-read-failed task={TaskId}", target.Id);
            events = [];
        }
        _dependencyLedgerMemo[path] = (stamp, events);
        return events;
    }

    private static (long Count, long Ticks) DependencyStamp(TaskInfo task,
        IReadOnlyDictionary<string, TaskInfo>? byKey)
    {
        long count = 0, ticks = 0;
        foreach (var edge in task.References.DependsOn)
        {
            count++;
            if (byKey is not null && byKey.TryGetValue(edge.Key, out var target))
            {
                var stamp = Stamp(TaskPaths.TimelineLog(target.FolderPath));
                ticks = Math.Max(ticks, stamp.Ticks);
            }
        }
        return (count, ticks);
    }

    private sealed record TaskMemo(
        (long Length, long Ticks) TimelineStamp,
        (long Length, long Ticks) PipelineStamp,
        (long Length, long Ticks) PostStepsStamp,
        (int Calls, long Ticks) TokenStamp,
        (long Count, long Ticks) DependencyStamp,
        BatchGateCycleEvidence? GateEvidence,
        (long Length, long Ticks) BackfillStamp,
        string State,
        DateTime EnteredLaneAt,
        TaskCycleAnalysis Analysis);

    private static readonly IReadOnlyDictionary<string, CycleTimeBackfillEntry> EmptyBackfill =
        new Dictionary<string, CycleTimeBackfillEntry>();

    private sealed record BackfillMemo(
        (long Length, long Ticks) Stamp,
        IReadOnlyDictionary<string, CycleTimeBackfillEntry> Entries);

    private sealed record ProjectMemo(DateTime At, DateTime? Since, int TaskCount, IReadOnlyList<TaskCycleAnalysis> Analyses);
}
