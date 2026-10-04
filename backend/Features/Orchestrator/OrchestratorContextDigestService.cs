using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentStudio.Prompts;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Compact, deterministic read context for one orchestrator chat scope.
/// The digest contains current application facts only. It does not execute
/// operational actions and it never copies raw logs or full decision prompts.
/// </summary>
public sealed class OrchestratorContextDigestService
{
    internal const int TransitionLimit = 8;
    internal const int RunLimit = 8;
    internal const int DecisionLimit = 8;
    internal const int QuotaWindowLimit = 3;
    private const int TransitionCandidateLimit = 64;

    private readonly TaskScannerService _scanner;
    private readonly TaskRunnerService _runner;
    private readonly QuotaService _quota;
    private readonly PublishTargetService _publish;
    private readonly TaskWatcherService _watcher;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly ILogger<OrchestratorContextDigestService> _logger;

    public OrchestratorContextDigestService(
        TaskScannerService scanner,
        TaskRunnerService runner,
        QuotaService quota,
        PublishTargetService publish,
        TaskWatcherService watcher,
        TimelineLog timeline,
        IConfiguration configuration,
        AgentStudio.Registry.ProjectRegistry projects,
        ILogger<OrchestratorContextDigestService> logger)
    {
        _scanner = scanner;
        _runner = runner;
        _quota = quota;
        _publish = publish;
        _watcher = watcher;
        _timeline = timeline;
        _configuration = configuration;
        _projects = projects;
        _logger = logger;
    }

    /// <summary>
    /// Build a live, cheap digest. Cached quota is used by default. A caller
    /// handling explicit operator intent may set <paramref name="forceQuotaRefresh"/>
    /// to await the existing quota probes before rendering the same envelope.
    /// </summary>
    public async Task<OrchestratorContextDigestResponse> BuildAsync(
        OrchestratorContextKey context,
        bool forceQuotaRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var timer = Stopwatch.StartNew();
        var capturedAt = DateTime.UtcNow;

        var watchPaths = _scanner.GetWatchPaths();
        ValidateProjectScope(context, watchPaths);

        // ORCH-1 is an application-wide read surface, not the board poll. Keep
        // archive in scope so a recent move to 7-archive remains visible in the
        // board pulse and an archived task context can still be reconstructed.
        var allTasks = _scanner.ScanAllAutomationJobsWithArchive();
        var scopedTasks = ScopeTasks(context, allTasks, out var focusTask);
        var projectNames = ResolveProjects(context, watchPaths);
        var runnerStatus = SafeRunnerStatus();

        var lanes = BuildLanes(projectNames, scopedTasks, runnerStatus);
        var transitions = ReadTransitions(scopedTasks);
        var runs = ReadProgressRuns(scopedTasks);
        var quota = await ReadQuotaAsync(forceQuotaRefresh, ct).ConfigureAwait(false);
        var publish = ReadPublishTargets(projectNames);
        var watcher = ReadWatcherHealth();
        var decisions = ReadDecisions(projectNames);
        var ownershipMappings = ReadOwnershipMappings(projectNames);

        var data = new OrchestratorContextDigestData(
            context,
            capturedAt,
            lanes,
            focusTask == null ? null : DigestTaskFocus.From(focusTask),
            transitions,
            runs,
            quota,
            publish,
            watcher,
            decisions,
            ownershipMappings);

        var response = new OrchestratorContextDigestResponse(
            context.Value,
            capturedAt,
            RenderDigest(data),
            BuildSourceStatuses(data));

        _logger.LogInformation(
            "orchestrator_context_digest_built contextKey={ContextKey} forceQuotaRefresh={ForceQuotaRefresh} durationMs={DurationMs} scopedTasks={ScopedTasks} projects={Projects}",
            context.Value,
            forceQuotaRefresh,
            timer.ElapsedMilliseconds,
            scopedTasks.Count,
            projectNames.Count);

        return response;
    }

    public Task<OrchestratorContextDigestResponse> BuildAsync(
        string rawContextKey,
        bool forceQuotaRefresh = false,
        CancellationToken ct = default)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var context))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));
        return BuildAsync(context, forceQuotaRefresh, ct);
    }

    private static void ValidateProjectScope(
        OrchestratorContextKey context,
        IReadOnlyCollection<WatchPathEntry> watchPaths)
    {
        if (context.IsGlobal) return;
        if (watchPaths.Any(entry => string.Equals(entry.Name, context.ProjectId, StringComparison.OrdinalIgnoreCase)))
            return;
        throw new KeyNotFoundException($"Unknown project '{context.ProjectId}'.");
    }

    internal static List<TaskInfo> ScopeTasks(
        OrchestratorContextKey context,
        IEnumerable<TaskInfo> allTasks,
        out TaskInfo? focusTask)
    {
        focusTask = null;
        var tasks = context.IsGlobal
            ? allTasks.ToList()
            : allTasks.Where(task =>
                    string.Equals(task.ProjectName, context.ProjectId, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (context.Kind != OrchestratorContextKey.TaskKind) return tasks;

        focusTask = tasks.FirstOrDefault(task =>
            string.Equals(task.Key, context.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Id, context.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.TaskKey, context.TaskKey, StringComparison.OrdinalIgnoreCase));
        if (focusTask == null)
            throw new KeyNotFoundException($"Unknown task '{context.TaskKey}' in project '{context.ProjectId}'.");
        return tasks;
    }

    private static List<string> ResolveProjects(
        OrchestratorContextKey context,
        IReadOnlyCollection<WatchPathEntry> watchPaths)
    {
        if (context.IsGlobal)
        {
            return watchPaths
                .Select(entry => entry.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var canonical = watchPaths.First(entry =>
            string.Equals(entry.Name, context.ProjectId, StringComparison.OrdinalIgnoreCase)).Name;
        return [canonical];
    }

    private RunnerStatus SafeRunnerStatus()
    {
        try
        {
            return _runner.GetStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "orchestrator_context_runner_status_failed");
            return new RunnerStatus();
        }
    }

    private static List<DigestProjectLanes> BuildLanes(
        IReadOnlyCollection<string> projects,
        IReadOnlyCollection<TaskInfo> tasks,
        RunnerStatus runnerStatus)
    {
        var result = new List<DigestProjectLanes>(projects.Count);
        foreach (var project in projects)
        {
            var projectTasks = tasks
                .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
                .ToList();
            runnerStatus.Projects.TryGetValue(project, out var status);
            result.Add(new DigestProjectLanes(
                project,
                projectTasks.Count,
                projectTasks.GroupBy(task => task.State, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                status?.Mode,
                status?.OccupiedSlots ?? 0,
                status?.MaxParallelism ?? 0));
        }
        return result;
    }

    private List<DigestTransition> ReadTransitions(IReadOnlyCollection<TaskInfo> tasks)
    {
        var transitions = new List<DigestTransition>();
        var candidates = tasks
            .OrderByDescending(task => task.EnteredLaneAt)
            .Take(TransitionCandidateLimit)
            .ToList();

        foreach (var task in candidates)
        {
            try
            {
                var rows = _timeline.ReadAll(task.FolderPath)
                    .Where(row => string.Equals(row.Kind, TimelineEventKinds.LaneChanged, StringComparison.Ordinal))
                    .OrderByDescending(row => row.Ts)
                    .Take(2);
                foreach (var row in rows)
                {
                    transitions.Add(new DigestTransition(
                        row.Ts,
                        task.ProjectName,
                        DisplayTaskKey(task),
                        Detail(row, "from"),
                        Detail(row, "to"),
                        row.Actor));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "orchestrator_context_timeline_read_failed task={Task}", task.Id);
            }
        }

        return transitions
            .OrderByDescending(row => row.At)
            .Take(TransitionLimit)
            .ToList();
    }

    private List<DigestProgressRun> ReadProgressRuns(IReadOnlyCollection<TaskInfo> tasks)
    {
        var rows = new List<DigestProgressRun>();
        foreach (var task in tasks.Where(task => string.Equals(task.State, TaskStates.Progress, StringComparison.Ordinal)))
        {
            RunActivityFacts facts;
            CliExecution? execution = null;
            try
            {
                facts = _runner.GetRunActivityForJob(task.Id, task.ProjectName);
                execution = _runner.GetExecutionForJob(task);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "orchestrator_context_run_read_failed task={Task}", task.Id);
                facts = default;
            }

            var runtime = facts.SlotActive
                          || string.Equals(execution?.Status, "running", StringComparison.OrdinalIgnoreCase)
                ? "active"
                : facts.BackoffUntil is { } until && until > DateTime.UtcNow
                    ? "backoff"
                    : string.Equals(task.Phase, LifecyclePhases.LoopWaiting, StringComparison.Ordinal)
                      || string.Equals(task.Phase, LifecyclePhases.SteerPending, StringComparison.Ordinal)
                        ? "waiting"
                        : "progress-idle";

            rows.Add(new DigestProgressRun(
                task.ProjectName,
                DisplayTaskKey(task),
                task.Title,
                runtime,
                string.IsNullOrWhiteSpace(task.Phase)
                    ? LifecyclePhases.DefaultFor(task.State, execution?.Status, TaskSummaryStatus.None) ?? "default"
                    : task.Phase!,
                task.PhaseEnteredAt,
                execution?.StartedAt,
                task.CliType,
                execution?.Model ?? task.Model));
        }

        return rows
            .OrderBy(row => row.Runtime == "active" ? 0 : row.Runtime == "waiting" ? 1 : 2)
            .ThenByDescending(row => row.PhaseEnteredAt ?? row.StartedAt)
            .Take(RunLimit)
            .ToList();
    }

    private async Task<QuotaReport> ReadQuotaAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh) return _quota.GetCached();
        try
        {
            return await _quota.RefreshAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "orchestrator_context_quota_refresh_failed");
            return _quota.GetCached();
        }
    }

    private List<DigestPublishProject> ReadPublishTargets(IReadOnlyCollection<string> projects)
    {
        var rows = new List<DigestPublishProject>(projects.Count);
        foreach (var project in projects)
        {
            try
            {
                var status = _publish.GetProjectPublishStatus(project);
                rows.Add(new DigestPublishProject(project, status.IsRepo, status.Error, status.Targets));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "orchestrator_context_publish_read_failed project={Project}", project);
                rows.Add(new DigestPublishProject(project, false, ex.Message, []));
            }
        }
        return rows;
    }

    private TaskWatcherHealthSnapshot ReadWatcherHealth()
    {
        try
        {
            return _watcher.GetHealthSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "orchestrator_context_watcher_health_failed");
            return new TaskWatcherHealthSnapshot(null, 0, 0, null, ex.Message, false);
        }
    }

    private List<DigestDecision> ReadDecisions(IReadOnlyCollection<string> projects)
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return [];

        var rows = new List<DigestDecision>();
        foreach (var project in projects)
        {
            try
            {
                rows.AddRange(ReviewDecisionLog.ReadAll(root, project).Select(record =>
                    new DigestDecision(
                        record.CreatedAt,
                        record.Project,
                        record.JobId,
                        record.Kind.ToString(),
                        Compact(record.Reason, 160))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "orchestrator_context_decision_read_failed project={Project}", project);
            }
        }

        return rows
            .OrderByDescending(row => row.At)
            .Take(DecisionLimit)
            .ToList();
    }

    private List<DigestOwnershipMapping> ReadOwnershipMappings(IReadOnlyCollection<string> projectNames)
    {
        var projects = _projects.List();
        var scopedIds = projects.Where(project => projectNames.Contains(project.DisplayName, StringComparer.OrdinalIgnoreCase))
            .Select(project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return projects.SelectMany(project => project.OwnershipMappings.Select(mapping => (project, mapping)))
            .Where(row => scopedIds.Contains(row.project.Id)
                || row.mapping.ConsumerProjectIds.Any(scopedIds.Contains))
            .Select(row => new DigestOwnershipMapping(
                row.mapping.Id,
                row.mapping.Version,
                row.mapping.Component,
                row.mapping.PackageOrModule,
                row.project.Id,
                row.project.ShortCode,
                row.mapping.ConsumerProjectIds,
                row.mapping.ReleaseArtifact,
                row.project.ShortCode,
                row.mapping.Confidence))
            .ToList();
    }

    internal static string RenderDigest(OrchestratorContextDigestData data)
    {
        var sb = new StringBuilder(2_048);
        sb.AppendLf("=== APPLICATION READ DIGEST ===");
        sb.AppendLf($"context: {data.Context.Value}");
        sb.AppendLf($"capturedAtUtc: {Iso(data.CapturedAt)}");

        sb.AppendLf("component ownership and delivery routes:");
        if (data.OwnershipMappings == null || data.OwnershipMappings.Count == 0) sb.AppendLf("- no shared-component mapping in this scope; explicit project ownership applies unless the affected component is unresolved");
        foreach (var route in data.OwnershipMappings ?? [])
        {
            sb.AppendLf($"- {route.Component}: owner={route.PrimaryProjectId}/{route.ProjectShortCode}; package={route.PackageOrModule ?? "(none)"}; consumers={string.Join(",", route.ConsumerProjectIds)}; artifact={route.ReleaseArtifact ?? "(none)"}; prefix={route.AllowedTicketPrefix}; confidence={route.Confidence:0.00}; mapping={route.MappingId}@v{route.Version}");
        }

        sb.AppendLf("lanes:");
        if (data.Lanes.Count == 0)
        {
            sb.AppendLf("- none");
        }
        else
        {
            foreach (var project in data.Lanes)
            {
                var counts = project.Counts.Count == 0
                    ? "no tasks"
                    : string.Join(", ", project.Counts.Select(pair => $"{pair.Key}={pair.Value}"));
                var runner = string.IsNullOrWhiteSpace(project.RunnerMode)
                    ? "runner=unavailable"
                    : $"runner={project.RunnerMode}, slots={project.OccupiedSlots}/{project.MaxParallelism}";
                sb.AppendLf($"- {project.Project}: total={project.Total}; {counts}; {runner}");
            }
        }

        if (data.FocusTask != null)
        {
            sb.AppendLf("task focus:");
            sb.AppendLf($"- {data.FocusTask.Project}/{data.FocusTask.TaskKey}: {Compact(data.FocusTask.Title, 120)}; lane={data.FocusTask.State}; phase={data.FocusTask.Phase ?? "default"}; lastActivity={Iso(data.FocusTask.LastActivity)}");
        }

        sb.AppendLf($"board pulse (latest {TransitionLimit}):");
        if (data.Transitions.Count == 0) sb.AppendLf("- none recorded");
        foreach (var row in data.Transitions.Take(TransitionLimit))
        {
            sb.AppendLf($"- {Iso(row.At)} {row.Project}/{row.TaskKey}: {row.From ?? "?"} -> {row.To ?? "?"} ({row.Actor})");
        }

        sb.AppendLf($"progress runs (up to {RunLimit}):");
        if (data.Runs.Count == 0) sb.AppendLf("- none");
        foreach (var row in data.Runs.Take(RunLimit))
        {
            var cli = string.IsNullOrWhiteSpace(row.CliType) ? "cli=?" : $"cli={row.CliType}";
            var model = string.IsNullOrWhiteSpace(row.Model) ? "" : $", model={row.Model}";
            sb.AppendLf($"- {row.Project}/{row.TaskKey}: {row.Runtime}; phase={row.Phase}; {cli}{model}; {Compact(row.Title, 100)}");
        }

        sb.AppendLf("quota (cached unless this digest was explicitly refreshed):");
        if (data.Quota.Snapshots.Count == 0) sb.AppendLf("- unavailable");
        foreach (var snapshot in data.Quota.Snapshots.OrderBy(item => item.CliType, StringComparer.OrdinalIgnoreCase))
        {
            var flags = snapshot.Suspicious
                ? $" suspicious={Compact(snapshot.SuspiciousReason, 80)}"
                : string.IsNullOrWhiteSpace(snapshot.Error) ? "" : $" error={Compact(snapshot.Error, 80)}";
            if (snapshot.Windows.Count == 0)
            {
                sb.AppendLf($"- {snapshot.CliType}: no windows; fetched={Iso(snapshot.FetchedAt)}{flags}");
                continue;
            }
            var windows = snapshot.Windows.Take(QuotaWindowLimit).Select(window =>
            {
                var usage = window.UsedPct is { } pct
                    ? pct.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                    : window.Used is { } used && window.Limit is { } limit
                        ? $"{used.ToString("0.#", CultureInfo.InvariantCulture)}/{limit.ToString("0.#", CultureInfo.InvariantCulture)}"
                        : "unknown";
                var reset = window.ResetAt is { } resetAt ? $", reset={Iso(resetAt)}" : "";
                return $"{Compact(window.Label, 40)}={usage}{reset}";
            });
            sb.AppendLf($"- {snapshot.CliType}: {string.Join("; ", windows)}; fetched={Iso(snapshot.FetchedAt)}{flags}");
        }

        sb.AppendLf("publish targets:");
        if (data.Publish.Count == 0) sb.AppendLf("- unavailable");
        foreach (var project in data.Publish)
        {
            if (!project.IsRepo)
            {
                sb.AppendLf($"- {project.Project}: unavailable ({Compact(project.Error, 100)})");
                continue;
            }
            if (project.Targets.Count == 0)
            {
                sb.AppendLf($"- {project.Project}: none derived");
                continue;
            }
            var targets = project.Targets.Select(target =>
            {
                var pending = target.FirstPublishPending
                    ? "first publish pending"
                    : target.PendingCount is { } count ? $"pending={count}" : "pending=unknown";
                var version = string.IsNullOrWhiteSpace(target.CurrentVersion) ? "" : $" v{target.CurrentVersion}";
                return $"{target.Label}{version} {pending}";
            });
            sb.AppendLf($"- {project.Project}: {string.Join("; ", targets)}");
        }

        var health = data.Watcher;
        sb.AppendLf("health:");
        sb.AppendLf($"- healthz=ok; watcher={(health.Healthy ? "healthy" : "degraded")}; handles={health.ActiveWatcherCount}/{health.ConfiguredPathCount}; lastEvent={Iso(health.LastEventAt)}{(string.IsNullOrWhiteSpace(health.LastError) ? "" : $"; error={Compact(health.LastError, 100)}")}");

        sb.AppendLf($"decision journal (latest {DecisionLimit}):");
        if (data.Decisions.Count == 0) sb.AppendLf("- none");
        foreach (var row in data.Decisions.Take(DecisionLimit))
        {
            sb.AppendLf($"- {Iso(row.At)} {row.Project}/{row.JobId}: {row.Kind}; {row.Reason}");
        }

        sb.AppendLf("Use this digest as current read-only application state. Ask for a refresh when cached quota may be stale. Do not infer operational authority from this context.");
        sb.Append("=== END APPLICATION READ DIGEST ===");
        return sb.ToString();
    }

    internal static IReadOnlyList<OrchestratorDigestSourceStatus> BuildSourceStatuses(
        OrchestratorContextDigestData data)
    {
        var quotaDegraded = data.Quota.Snapshots.Any(snapshot =>
            snapshot.Suspicious || !string.IsNullOrWhiteSpace(snapshot.Error));
        var publishDegraded = data.Publish.Any(project => !project.IsRepo && !string.IsNullOrWhiteSpace(project.Error));
        var runTimes = data.Runs
            .Select(row => row.PhaseEnteredAt ?? row.StartedAt)
            .Where(at => at != null)
            .ToList();
        return
        [
            Source("lanes", data.Lanes.Sum(project => project.Total) == 0 ? "empty" : "ok", data.CapturedAt,
                $"{data.Lanes.Count} project(s)"),
            Source("transitions", data.Transitions.Count == 0 ? "empty" : "ok",
                data.Transitions.Count == 0 ? null : data.Transitions.Max(row => row.At),
                $"latest {data.Transitions.Count} row(s)"),
            Source("runs", data.Runs.Count == 0 ? "empty" : "ok",
                runTimes.Count == 0 ? null : runTimes.Max(),
                $"{data.Runs.Count} progress task(s)"),
            Source("quota", data.Quota.Snapshots.Count == 0 ? "unavailable" : quotaDegraded ? "degraded" : "ok",
                data.Quota.Snapshots.Count == 0 ? null : data.Quota.Snapshots.Max(snapshot => snapshot.FetchedAt),
                $"cache ttl {data.Quota.TtlSeconds}s"),
            Source("publishTargets", data.Publish.Count == 0 ? "unavailable" : publishDegraded ? "degraded" : "ok",
                data.CapturedAt, $"{data.Publish.Sum(project => project.Targets.Count)} target(s)"),
            Source("health", data.Watcher.Healthy ? "ok" : "degraded", data.CapturedAt,
                $"watchers {data.Watcher.ActiveWatcherCount}/{data.Watcher.ConfiguredPathCount}"),
            Source("decisionJournal", data.Decisions.Count == 0 ? "empty" : "ok",
                data.Decisions.Count == 0 ? null : data.Decisions.Max(row => row.At),
                $"latest {data.Decisions.Count} row(s)"),
        ];
    }

    private static OrchestratorDigestSourceStatus Source(
        string name,
        string status,
        DateTime? capturedAt,
        string? detail)
        => new(name, status, capturedAt, detail);

    private static string? Detail(TimelineEvent row, string key)
        => row.Details != null && row.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string DisplayTaskKey(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!;

    private static string Compact(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";
        var oneLine = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..Math.Max(0, max - 3)] + "...";
    }

    private static string Iso(DateTime? value)
        => value is null ? "never" : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Wire response shared by context reads and explicit refreshes.</summary>
public sealed record OrchestratorContextDigestResponse(
    string ContextKey,
    DateTime CapturedAt,
    string Digest,
    IReadOnlyList<OrchestratorDigestSourceStatus> Sources);

/// <summary>Freshness and degradation metadata for one digest section.</summary>
public sealed record OrchestratorDigestSourceStatus(
    string Name,
    string Status,
    DateTime? CapturedAt,
    string? Detail);

internal sealed record OrchestratorContextDigestData(
    OrchestratorContextKey Context,
    DateTime CapturedAt,
    List<DigestProjectLanes> Lanes,
    DigestTaskFocus? FocusTask,
    List<DigestTransition> Transitions,
    List<DigestProgressRun> Runs,
    QuotaReport Quota,
    List<DigestPublishProject> Publish,
    TaskWatcherHealthSnapshot Watcher,
    List<DigestDecision> Decisions,
    List<DigestOwnershipMapping>? OwnershipMappings = null);

internal sealed record DigestOwnershipMapping(
    string MappingId,
    int Version,
    string Component,
    string? PackageOrModule,
    string PrimaryProjectId,
    string ProjectShortCode,
    IReadOnlyList<string> ConsumerProjectIds,
    string? ReleaseArtifact,
    string AllowedTicketPrefix,
    double Confidence);

internal sealed record DigestProjectLanes(
    string Project,
    int Total,
    IReadOnlyDictionary<string, int> Counts,
    string? RunnerMode,
    int OccupiedSlots,
    int MaxParallelism);

internal sealed record DigestTaskFocus(
    string Project,
    string TaskKey,
    string Title,
    string State,
    string? Phase,
    DateTime LastActivity)
{
    public static DigestTaskFocus From(TaskInfo task) => new(
        task.ProjectName,
        string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!,
        task.Title,
        task.State,
        task.Phase,
        task.LastActivity);
}

internal sealed record DigestTransition(
    DateTime At,
    string Project,
    string TaskKey,
    string? From,
    string? To,
    string Actor);

internal sealed record DigestProgressRun(
    string Project,
    string TaskKey,
    string Title,
    string Runtime,
    string Phase,
    DateTime? PhaseEnteredAt,
    DateTime? StartedAt,
    string? CliType,
    string? Model);

internal sealed record DigestPublishProject(
    string Project,
    bool IsRepo,
    string? Error,
    IReadOnlyList<PublishTarget> Targets);

internal sealed record DigestDecision(
    DateTime At,
    string Project,
    string JobId,
    string Kind,
    string Reason);
