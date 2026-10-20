using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using AgentStudio.Tasks;

namespace AgentStudio.Quota;

public sealed record QuotaLimits(long? ModelTokensPerWeek, decimal? ComputeCoreHoursPerWeek, int? HostingSlots);
public sealed record QuotaUsage(long ModelTokens, decimal ModelCostUsd, decimal ComputeCoreHours, int HostingSlots);
public sealed record QuotaConsumer(string ProjectId, string Project, string? CardId, long ModelTokens, decimal ComputeCoreHours);
public sealed record QuotaProjectConsumer(string ProjectId, string Project, long ModelTokens, decimal ComputeCoreHours);
public sealed record QuotaSnapshot(string WorkspaceId, string? ProjectId, DateTime WeekStartUtc, DateTime WeekEndUtc,
    QuotaLimits? Limits, QuotaUsage Usage, QuotaUsage Remaining, string Status,
    IReadOnlyList<QuotaConsumer> BiggestConsumers, IReadOnlyList<QuotaProjectConsumer> BiggestProjects,
    IReadOnlyList<QuotaUsage> DailyTrend, bool MeterComplete, bool OverrideActive,
    bool CodingAllowed, bool HostingAllowed, string? Message);
/// <summary>MeterUnavailable marks a verdict given without a complete meter, so callers can surface it.</summary>
public sealed record QuotaAdmission(bool Allowed, string? Message, bool MeterUnavailable = false);
/// <summary>Board view of one scope; Limited is false when no limit applies and nothing was metered.</summary>
public sealed record QuotaScopeAdmission(string? ProjectId, string? Project, string Status, string? Message,
    bool OverrideActive, bool CodingAllowed, bool HostingAllowed, bool Limited);
public sealed record QuotaBoardAdmission(string WorkspaceId, QuotaScopeAdmission Workspace,
    IReadOnlyList<QuotaScopeAdmission> Projects);
public sealed record QuotaOverrideAudit(DateTimeOffset At, string WorkspaceId, string? ProjectId,
    string Actor, string Reason, DateTime ExpiresAt);
public sealed record QuotaDeployExecution(string ProjectId, string JobId, string EnvironmentId,
    DateTime StartedAt, DateTime? CompletedAt);

/// <summary>Workspace and project limits. Usage is projected from the same token and pipeline records as Studio.</summary>
public sealed class WorkspaceQuotaService
{
    /// <summary>Usage lags token and pipeline writes by at most this long, or one task index generation.</summary>
    public static readonly TimeSpan UsageTtl = TimeSpan.FromSeconds(30);

    private sealed record State(Dictionary<string, QuotaLimits> Workspaces,
        Dictionary<string, QuotaLimits> Projects, Dictionary<string, DateTime> Overrides,
        Dictionary<string, string> Environments, List<QuotaOverrideAudit>? Audits = null,
        List<QuotaDeployExecution>? Deploys = null);

    private sealed record StateView(QuotaLimits? WorkspaceLimits, QuotaLimits? ProjectLimits,
        bool WorkspaceOverride, bool ProjectOverride, IReadOnlyCollection<string> Environments,
        IReadOnlyList<QuotaDeployExecution> Deploys, string? ReadError);

    /// <summary>Current-week usage of one project; the trend has one bucket per UTC day.</summary>
    private sealed record ProjectUsage(ProjectRecord Project, QuotaUsage[] Daily,
        IReadOnlyList<QuotaConsumer> Consumers, string? MeterWarning);

    private sealed record WorkspaceUsage(DateTime WeekStart, IReadOnlyDictionary<string, ProjectUsage> Projects);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectRegistry _projects;
    private readonly ProjectTokenLedger _tokens;
    private readonly ITokenPricing _pricing;
    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _pipeline;
    private readonly ILogger<WorkspaceQuotaService>? _logger;
    private readonly GenerationSingleFlightCache<WorkspaceUsage> _usage;
    private readonly ConcurrentDictionary<string, byte> _unpricedModels = new(StringComparer.OrdinalIgnoreCase);
    private State _state = Empty();
    private string? _stateReadError;

    public WorkspaceQuotaService(IConfiguration config, TimeProvider clock, WorkspaceRegistry workspaces,
        ProjectRegistry projects, ProjectTokenLedger tokens, ITokenPricing pricing,
        TaskScannerService scanner, PipelineExecutionLog pipeline, ILogger<WorkspaceQuotaService>? logger = null)
    {
        _clock = clock;
        _workspaces = workspaces;
        _projects = projects;
        _tokens = tokens;
        _pricing = pricing;
        _scanner = scanner;
        _pipeline = pipeline;
        _logger = logger;
        _usage = new GenerationSingleFlightCache<WorkspaceUsage>(clock);
        var root = config["TaskRepository"] ?? Path.Combine(Path.GetTempPath(), "agent-studio-quota");
        _path = Path.Combine(root, ".metadata", "workspace-quotas.json");
        lock (_gate) Reload();
    }

    private static State Empty() => new(new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase));

    /// <summary>An unreadable state file keeps the last good state and is reported as meter unavailable.</summary>
    private void Reload()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var read = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            if (read is not null) _state = read;
            _stateReadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_stateReadError is null)
                _logger?.LogWarning(ex, "workspace-quota-state-unreadable path={Path}", _path);
            _stateReadError = "Quota settings could not be read; admission uses the last readable settings.";
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
    }

    public void SetLimits(string workspaceId, string? projectId, QuotaLimits? limits)
    {
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        if (limits is { ModelTokensPerWeek: < 0 } or { ComputeCoreHoursPerWeek: < 0 } or { HostingSlots: < 0 })
            throw new ArgumentException("Quota limits must be nonnegative.");
        if (projectId is not null && !_projects.List().Any(p => p.Id == projectId && p.WorkspaceId == workspaceId))
            throw new KeyNotFoundException("Project does not belong to workspace.");
        lock (_gate)
        {
            Reload();
            var map = projectId is null ? _state.Workspaces : _state.Projects;
            var key = projectId ?? workspaceId;
            if (limits is null) map.Remove(key);
            else map[key] = limits;
            Save();
        }
    }

    public void Override(string workspaceId, string? projectId, string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Actor and reason are required.");
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        if (projectId is not null && !_projects.List().Any(p => p.Id == projectId && p.WorkspaceId == workspaceId))
            throw new KeyNotFoundException("Project does not belong to workspace.");
        var expiry = WorkspaceQuotaPolicy.Week(_clock).End;
        lock (_gate)
        {
            Reload();
            _state.Overrides[projectId ?? workspaceId] = expiry;
            _state = _state with { Audits = [.. (_state.Audits ?? []),
                WorkspaceQuotaPolicy.OverrideEntry(_clock, workspaceId, projectId, actor, reason, expiry)] };
            Save();
        }
    }

    public IReadOnlyList<QuotaOverrideAudit> Audit(string workspaceId, string? projectId)
    {
        lock (_gate)
        {
            Reload();
            return (_state.Audits ?? []).Where(entry => entry.WorkspaceId == workspaceId
                && (projectId is null || entry.ProjectId == projectId)).ToList();
        }
    }

    /// <summary>True when any workspace or project limit exists; admission paths skip all work otherwise.</summary>
    public bool HasLimits()
    {
        lock (_gate)
        {
            Reload();
            return _state.Workspaces.Count > 0 || _state.Projects.Count > 0;
        }
    }

    public void SetEnvironment(string projectId, string environmentId, bool running)
    {
        lock (_gate)
        {
            Reload();
            var key = projectId + "/" + environmentId;
            if (running) _state.Environments[key] = _clock.GetUtcNow().ToString("O");
            else _state.Environments.Remove(key);
            Save();
        }
    }

    /// <summary>Reserve a hosting slot before dispatch. The read, admission and write share one lock.</summary>
    public bool ReserveProjectEnvironment(string projectName, string environmentId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return false;
        lock (_gate)
        {
            Reload();
            var key = project.Id + "/" + environmentId;
            var existed = _state.Environments.ContainsKey(key);
            var admission = Admit(project, hosting: !existed);
            if (!admission.Allowed) throw new InvalidOperationException(admission.Message);
            if (existed) return false;
            _state.Environments[key] = _clock.GetUtcNow().ToString("O");
            Save();
            return true;
        }
    }

    public void SetProjectEnvironment(string projectName, string environmentId, bool running)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        SetEnvironment(project.Id, environmentId, running);
    }

    public void MarkProjectEnvironmentDispatched(string projectName, string environmentId, DateTimeOffset dispatchStartedAt)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        lock (_gate)
        {
            Reload();
            var key = project.Id + "/" + environmentId;
            if (!_state.Environments.ContainsKey(key)) return;
            _state.Environments[key] = dispatchStartedAt.ToUniversalTime().ToString("O");
            Save();
        }
    }

    public DateTimeOffset? ProjectEnvironmentReservedAt(string projectName, string environmentId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return null;
        lock (_gate)
        {
            Reload();
            return _state.Environments.TryGetValue(project.Id + "/" + environmentId, out var value)
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)
                    ? at : null;
        }
    }

    public bool HasProjectEnvironment(string projectName, string environmentId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return false;
        lock (_gate)
        {
            Reload();
            return _state.Environments.ContainsKey(project.Id + "/" + environmentId);
        }
    }

    /// <summary>Environment ids of the project that currently hold a hosting slot.</summary>
    public IReadOnlyList<string> ProjectEnvironmentIds(string projectName)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return [];
        lock (_gate)
        {
            Reload();
            var prefix = project.Id + "/";
            return _state.Environments.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(key => key[prefix.Length..]).ToList();
        }
    }

    public IReadOnlyList<string> RunningWebsiteProjects()
    {
        lock (_gate)
        {
            Reload();
            var ids = _state.Environments.Keys
                .Where(key => key.EndsWith("/website", StringComparison.OrdinalIgnoreCase))
                .Select(key => key[..^"/website".Length])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _projects.List().Where(project => ids.Contains(project.Id))
                .Select(project => project.DisplayName).ToList();
        }
    }

    /// <summary>Meters a claimed deploy job; a later start of the same job replaces the open record.</summary>
    public void RecordDeployStart(string projectName, string jobId, string environmentId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            Reload();
            var floor = WorkspaceQuotaPolicy.Week(_clock).Start.AddDays(-7);
            var deploys = (_state.Deploys ?? []).Where(item => item.StartedAt >= floor
                && !(item.ProjectId == project.Id && item.JobId == jobId)).ToList();
            deploys.Add(new(project.Id, jobId, environmentId, now, null));
            _state = _state with { Deploys = deploys };
            Save();
        }
    }

    public void RecordDeployEnd(string projectName, string jobId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        lock (_gate)
        {
            Reload();
            var deploys = _state.Deploys ?? [];
            var index = deploys.FindLastIndex(item => item.ProjectId == project.Id && item.JobId == jobId
                && item.CompletedAt is null);
            if (index < 0) return;
            deploys[index] = deploys[index] with { CompletedAt = _clock.GetUtcNow().UtcDateTime };
            Save();
        }
    }

    public QuotaAdmission Admit(string projectName, bool hosting = false)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null)
            return new(true, $"Project '{projectName}' is not registered in Studio; quota admission is off for it.", true);
        return Admit(project, hosting);
    }

    private QuotaAdmission Admit(ProjectRecord project, bool hosting)
    {
        var view = View(project.WorkspaceId, project.Id);
        if (view.WorkspaceLimits is null && view.ProjectLimits is null)
            return new(true, view.ReadError, view.ReadError is not null);
        WorkspaceUsage usage;
        try { usage = Usage(project.WorkspaceId); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "workspace-quota-meter-failed workspace={Workspace} project={Project}",
                project.WorkspaceId, project.DisplayName);
            return new(false, "Quota usage is temporarily unavailable. An owner can review the meter.", true);
        }
        foreach (var projectId in new[] { null, project.Id })
        {
            var snapshot = Build(project.WorkspaceId, projectId, view, usage);
            if (snapshot.Limits is null || snapshot.OverrideActive) continue;
            if (!snapshot.MeterComplete) return new(false, snapshot.Message, true);
            if (!WorkspaceQuotaPolicy.Allows(snapshot.Limits, snapshot.Usage, hosting))
                return new(false, (hosting && WorkspaceQuotaPolicy.Allows(snapshot.Limits, snapshot.Usage, false)
                    ? WorkspaceQuotaPolicy.Message(WorkspaceQuotaPolicy.HostingFull, snapshot.Limits)
                    : snapshot.Message) ?? "Quota reached. An owner can override this week's limit.");
        }
        return new(true, view.ReadError, view.ReadError is not null);
    }

    public QuotaSnapshot Snapshot(string workspaceId, string? projectId = null)
    {
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        if (projectId is not null && !_projects.List().Any(p => p.Id == projectId && p.WorkspaceId == workspaceId))
            throw new KeyNotFoundException("Project does not belong to workspace.");
        return Build(workspaceId, projectId, View(workspaceId, projectId), Usage(workspaceId));
    }

    /// <summary>
    /// One board read for the workspace and its projects. Without any limit in
    /// the workspace it answers without metering.
    /// </summary>
    public QuotaBoardAdmission BoardAdmission(string workspaceId)
    {
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        var projects = _projects.List().Where(p => p.WorkspaceId == workspaceId && !p.Archived).ToList();
        var views = projects.ToDictionary(p => p.Id, p => View(workspaceId, p.Id), StringComparer.OrdinalIgnoreCase);
        var workspaceView = View(workspaceId, null);
        var limited = workspaceView.WorkspaceLimits is not null || views.Values.Any(v => v.ProjectLimits is not null);
        var usage = limited ? Usage(workspaceId) : null;
        QuotaScopeAdmission Scope(ProjectRecord? project, StateView view)
        {
            var limits = project is null ? view.WorkspaceLimits : view.ProjectLimits;
            if (limits is null || usage is null)
                return new(project?.Id, project?.DisplayName, WorkspaceQuotaPolicy.Ok, null, false, true, true, false);
            var snapshot = Build(workspaceId, project?.Id, view, usage);
            return new(project?.Id, project?.DisplayName, snapshot.Status, snapshot.Message,
                snapshot.OverrideActive, snapshot.CodingAllowed, snapshot.HostingAllowed, true);
        }
        return new(workspaceId, Scope(null, workspaceView),
            projects.Select(project => Scope(project, views[project.Id])).ToList());
    }

    private StateView View(string workspaceId, string? projectId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            Reload();
            _state.Workspaces.TryGetValue(workspaceId, out var workspaceLimits);
            QuotaLimits? projectLimits = null;
            if (projectId is not null) _state.Projects.TryGetValue(projectId, out projectLimits);
            bool Active(string key) => _state.Overrides.TryGetValue(key, out var expiry) && expiry > now;
            return new(workspaceLimits, projectLimits, Active(workspaceId),
                projectId is not null && Active(projectId), _state.Environments.Keys.ToList(),
                (_state.Deploys ?? []).ToList(), _stateReadError);
        }
    }

    private QuotaSnapshot Build(string workspaceId, string? projectId, StateView view, WorkspaceUsage usage)
    {
        var (start, end) = WorkspaceQuotaPolicy.Week(_clock);
        var limits = projectId is null ? view.WorkspaceLimits : view.ProjectLimits;
        var overridden = projectId is null ? view.WorkspaceOverride : view.ProjectOverride;
        var projects = usage.Projects.Values.Where(p => projectId is null || p.Project.Id == projectId).ToList();
        var ids = projects.Select(p => p.Project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var daily = Enumerable.Range(0, 7).Select(day => projects.Aggregate(new QuotaUsage(0, 0, 0, 0),
            (sum, p) => sum with
            {
                ModelTokens = sum.ModelTokens + p.Daily[day].ModelTokens,
                ModelCostUsd = sum.ModelCostUsd + p.Daily[day].ModelCostUsd,
                ComputeCoreHours = sum.ComputeCoreHours + p.Daily[day].ComputeCoreHours,
            })).ToArray();
        var consumers = projects.SelectMany(p => p.Consumers).ToList();
        foreach (var deploy in view.Deploys.Where(d => ids.Contains(d.ProjectId)))
        {
            var deployEnd = WorkspaceQuotaPolicy.DeployEnd(deploy.StartedAt, deploy.CompletedAt, _clock);
            var hours = AddCoreHours(daily, start, end, deploy.StartedAt, deployEnd, 1);
            if (hours <= 0) continue;
            var owner = projects.First(p => string.Equals(p.Project.Id, deploy.ProjectId, StringComparison.OrdinalIgnoreCase));
            consumers.Add(new(owner.Project.Id, owner.Project.DisplayName, null, 0, hours));
        }
        var hostingCount = view.Environments.Count(key => ids.Any(id =>
            key.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase)));
        var total = new QuotaUsage(daily.Sum(d => d.ModelTokens), daily.Sum(d => d.ModelCostUsd),
            daily.Sum(d => d.ComputeCoreHours), hostingCount);
        var remaining = new QuotaUsage(Math.Max(0, (limits?.ModelTokensPerWeek ?? 0) - total.ModelTokens), 0,
            Math.Max(0, (limits?.ComputeCoreHoursPerWeek ?? 0) - total.ComputeCoreHours),
            Math.Max(0, (limits?.HostingSlots ?? 0) - total.HostingSlots));
        var meterProblems = projects.Where(p => p.MeterWarning is not null).ToList();
        var meterComplete = meterProblems.Count == 0;
        var meterBlocks = !meterComplete && limits is not null;
        var status = meterBlocks ? WorkspaceQuotaPolicy.Blocked : WorkspaceQuotaPolicy.Status(limits, total);
        var message = meterBlocks
            ? "Quota usage is temporarily unavailable. New work waits for the meter. "
              + string.Join(" ", meterProblems.Select(p => $"Project {p.Project.DisplayName}: {p.MeterWarning}"))
            : WorkspaceQuotaPolicy.Message(status, limits) ?? (limits is null ? null : view.ReadError);
        return new(workspaceId, projectId, start, end, limits, total, remaining, status,
            consumers.GroupBy(c => (c.ProjectId, c.Project, c.CardId))
                .Select(g => new QuotaConsumer(g.Key.ProjectId, g.Key.Project, g.Key.CardId,
                    g.Sum(c => c.ModelTokens), g.Sum(c => c.ComputeCoreHours)))
                .OrderByDescending(c => c.ModelTokens).ThenByDescending(c => c.ComputeCoreHours).Take(10).ToList(),
            consumers.GroupBy(c => (c.ProjectId, c.Project))
                .Select(g => new QuotaProjectConsumer(g.Key.ProjectId, g.Key.Project,
                    g.Sum(c => c.ModelTokens), g.Sum(c => c.ComputeCoreHours)))
                .OrderByDescending(c => c.ModelTokens).ThenByDescending(c => c.ComputeCoreHours).Take(10).ToList(),
            daily, meterComplete, overridden,
            limits is null || overridden || meterComplete && WorkspaceQuotaPolicy.Allows(limits, total, hosting: false),
            limits is null || overridden || meterComplete && WorkspaceQuotaPolicy.Allows(limits, total, hosting: true),
            message);
    }

    /// <summary>
    /// Current-week usage of every project in the workspace, computed once per
    /// task index generation, week, and project set and kept for
    /// <see cref="UsageTtl"/>. Concurrent readers share one computation.
    /// </summary>
    private WorkspaceUsage Usage(string workspaceId)
    {
        var (start, _) = WorkspaceQuotaPolicy.Week(_clock);
        var projects = _projects.List().Where(p => p.WorkspaceId == workspaceId).ToList();
        // Reading the index first publishes a pending refresh, so the generation
        // below matches the task list the computation uses.
        var tasks = _scanner.ScanAllJobsWithArchive();
        var version = string.Join("|", new[]
        {
            _scanner.SnapshotGeneration.ToString(CultureInfo.InvariantCulture),
            start.ToString("O", CultureInfo.InvariantCulture),
        }.Concat(projects.Select(p => $"{p.Id}:{p.DisplayName}:{p.Archived}:{p.StorageLocation}")));
        return _usage.GetOrCreateVersioned(workspaceId, version, UsageTtl, () => ComputeUsage(projects, tasks));
    }

    private WorkspaceUsage ComputeUsage(IReadOnlyList<ProjectRecord> projects, IReadOnlyList<TaskInfo> index)
    {
        var (start, end) = WorkspaceQuotaPolicy.Week(_clock);
        var names = projects.ToDictionary(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
        // The task index omits archived projects. Their current-week pipeline
        // executions still consume workspace quota, so read those folders directly.
        var tasks = index
            .Where(t => names.ContainsKey(t.ProjectName))
            .Select(t => (t.FolderPath, Project: names[t.ProjectName], Card: CardId(t), Aliases: new string?[] { t.Id, t.TaskKey, t.Key }))
            .Concat(projects.Where(p => p.Archived).SelectMany(p => ProjectTaskFolders(p.StorageLocation)
                .Select(folder => (FolderPath: folder, Project: p, Card: Path.GetFileName(folder),
                    Aliases: new string?[] { Path.GetFileName(folder) }))))
            .DistinctBy(t => t.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, ProjectUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var daily = Enumerable.Range(0, 7).Select(_ => new QuotaUsage(0, 0, 0, 0)).ToArray();
            var consumers = new List<QuotaConsumer>();
            var projectTasks = tasks.Where(t => t.Project.Id == project.Id).ToList();
            // Token rows and pipeline steps name a card by id or key; both resolve to one card id.
            var cards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in projectTasks)
                foreach (var alias in task.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                    cards.TryAdd(alias!, task.Card);
            var ledger = _tokens.LoadSnapshot(project.DisplayName, project.StorageLocation);
            var meterWarning = ledger.Freshness.Status == "complete" ? null
                : ledger.Freshness.Warning ?? $"The token ledger is {ledger.Freshness.Status}.";
            foreach (var entry in ledger.Entries.Where(e => e.TokenUsage is not null))
            {
                var at = AsUtc(entry.Ts);
                if (at < start || at >= end) continue;
                var usage = entry.TokenUsage!;
                var count = WorkspaceQuotaPolicy.ModelTokens(usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens);
                var day = Math.Clamp((int)(at.Date - start).TotalDays, 0, 6);
                daily[day] = daily[day] with { ModelTokens = daily[day].ModelTokens + count,
                    ModelCostUsd = daily[day].ModelCostUsd + Price(usage, at) };
                var card = entry.JobId is null ? null : cards.GetValueOrDefault(entry.JobId, entry.JobId);
                consumers.Add(new(project.Id, project.DisplayName, card, count, 0));
            }
            foreach (var task in projectTasks)
            {
                var record = _pipeline.Read(task.FolderPath);
                if (record is null) continue;
                foreach (var attempt in new[] { record }.Concat(record.PreviousAttempts))
                foreach (var step in attempt.Steps)
                foreach (var execution in new[] { (step.StartedAt, step.CompletedAt, step.GrantedCores) }
                    .Concat(step.EarlierRuns?.Select(run => (run.StartedAt, run.CompletedAt, run.GrantedCores))
                        ?? Enumerable.Empty<(DateTime?, DateTime?, int?)>()))
                {
                    if (execution.StartedAt is not { } startedAt) continue;
                    var until = execution.CompletedAt ?? _clock.GetUtcNow().UtcDateTime;
                    var hours = AddCoreHours(daily, start, end, startedAt, until, Math.Max(1, execution.GrantedCores ?? 1));
                    if (hours > 0) consumers.Add(new(project.Id, project.DisplayName, task.Card, 0, hours));
                }
            }
            result[project.Id] = new(project, daily, consumers, meterWarning);
        }
        return new(start, result);
    }

    /// <summary>Adds the week-clipped execution to the daily buckets and returns its core-hours.</summary>
    private static decimal AddCoreHours(QuotaUsage[] daily, DateTime start, DateTime end,
        DateTime startedAt, DateTime until, int cores)
    {
        var from = startedAt > start ? startedAt : start;
        var to = until < end ? until : end;
        if (to <= from) return 0;
        var total = 0m;
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            var dayFrom = from > day ? from : day;
            var dayTo = to < day.AddDays(1) ? to : day.AddDays(1);
            var index = (int)(day - start).TotalDays;
            var hours = (decimal)(dayTo - dayFrom).TotalHours * cores;
            total += hours;
            if (index is >= 0 and < 7)
                daily[index] = daily[index] with { ComputeCoreHours = daily[index].ComputeCoreHours + hours };
        }
        return total;
    }

    private decimal Price(OrchestratorTokenUsage usage, DateTime at)
    {
        try
        {
            var estimate = _pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens, at);
            return estimate.ModelKnown ? estimate.Total : 0;
        }
        catch (Exception ex)
        {
            // The USD figure is informational; token admission does not depend on it.
            if (_unpricedModels.TryAdd(usage.Model ?? "", 0))
                _logger?.LogWarning(ex, "workspace-quota-pricing-failed model={Model}", usage.Model);
            return 0;
        }
    }

    private static string CardId(TaskInfo task)
        => !string.IsNullOrWhiteSpace(task.Key) ? task.Key! : task.Id;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static IEnumerable<string> ProjectTaskFolders(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) yield break;
        var flat = TaskStorageLayout.EnumerateJobDirs(projectRoot).ToList();
        if (flat.Count > 0)
        {
            foreach (var folder in flat) yield return folder;
            yield break;
        }
        foreach (var state in TaskStates.All)
        {
            var lane = Path.Combine(projectRoot, state);
            if (!Directory.Exists(lane)) continue;
            foreach (var folder in Directory.EnumerateDirectories(lane))
                if (!Path.GetFileName(folder).StartsWith('_')) yield return folder;
        }
    }

}
