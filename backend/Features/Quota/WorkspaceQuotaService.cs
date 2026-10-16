using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
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
public sealed record QuotaAdmission(bool Allowed, string? Message);
public sealed record QuotaOverrideAudit(DateTimeOffset At, string WorkspaceId, string? ProjectId,
    string Actor, string Reason, DateTime ExpiresAt);

/// <summary>Workspace and project limits. Usage is projected from the same token and pipeline records as Studio.</summary>
public sealed class WorkspaceQuotaService
{
    private sealed record State(Dictionary<string, QuotaLimits> Workspaces,
        Dictionary<string, QuotaLimits> Projects, Dictionary<string, DateTime> Overrides,
        Dictionary<string, string> Environments, List<QuotaOverrideAudit>? Audits = null);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectRegistry _projects;
    private readonly ProjectTokenLedger _tokens;
    private readonly ITokenPricing _pricing;
    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _pipeline;
    private State _state = Empty();

    public WorkspaceQuotaService(IConfiguration config, TimeProvider clock, WorkspaceRegistry workspaces,
        ProjectRegistry projects, ProjectTokenLedger tokens, ITokenPricing pricing,
        TaskScannerService scanner, PipelineExecutionLog pipeline)
    {
        _clock = clock;
        _workspaces = workspaces;
        _projects = projects;
        _tokens = tokens;
        _pricing = pricing;
        _scanner = scanner;
        _pipeline = pipeline;
        var root = config["TaskRepository"] ?? Path.Combine(Path.GetTempPath(), "agent-studio-quota");
        _path = Path.Combine(root, ".metadata", "workspace-quotas.json");
        lock (_gate) Reload();
    }

    private static State Empty() => new(new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase));

    private void Reload()
    {
        if (!File.Exists(_path)) return;
        var read = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
        if (read is not null) _state = read;
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
        var snapshot = Snapshot(workspaceId, projectId);
        lock (_gate)
        {
            Reload();
            _state.Overrides[projectId ?? workspaceId] = snapshot.WeekEndUtc;
            _state = _state with { Audits = [.. (_state.Audits ?? []),
                WorkspaceQuotaPolicy.OverrideEntry(_clock, workspaceId, projectId, actor, reason, snapshot.WeekEndUtc)] };
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

    public void SetEnvironment(string projectId, string environmentId, bool running)
    {
        lock (_gate)
        {
            Reload();
            var key = projectId + "/" + environmentId;
            if (running) _state.Environments[key] = projectId;
            else _state.Environments.Remove(key);
            Save();
        }
    }

    public void SetProjectEnvironment(string projectName, string environmentId, bool running)
    {
        var project = _projects.List().FirstOrDefault(p =>
            string.Equals(p.DisplayName, projectName, StringComparison.OrdinalIgnoreCase));
        if (project is null) return;
        SetEnvironment(project.Id, environmentId, running);
    }

    public bool HasProjectEnvironment(string projectName, string environmentId)
    {
        var project = _projects.List().FirstOrDefault(p =>
            string.Equals(p.DisplayName, projectName, StringComparison.OrdinalIgnoreCase));
        if (project is null) return false;
        lock (_gate)
        {
            Reload();
            return _state.Environments.ContainsKey(project.Id + "/" + environmentId);
        }
    }

    public QuotaAdmission Admit(string projectName, bool hosting = false)
    {
        var project = _projects.List().FirstOrDefault(p =>
            string.Equals(p.DisplayName, projectName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Id, projectName, StringComparison.OrdinalIgnoreCase));
        if (project is null) return new(true, null);
        QuotaLimits? workspaceLimits, projectLimits;
        lock (_gate)
        {
            Reload();
            _state.Workspaces.TryGetValue(project.WorkspaceId, out workspaceLimits);
            _state.Projects.TryGetValue(project.Id, out projectLimits);
        }
        if (workspaceLimits is null && projectLimits is null) return new(true, null);
        foreach (var scope in new[] { (WorkspaceId: project.WorkspaceId, ProjectId: (string?)null, Limits: workspaceLimits),
            (WorkspaceId: project.WorkspaceId, ProjectId: project.Id, Limits: projectLimits) })
        {
            if (scope.Limits is null) continue;
            QuotaSnapshot snapshot;
            try { snapshot = Snapshot(scope.WorkspaceId, scope.ProjectId); }
            catch (Exception) { return new(false, "Quota usage is temporarily unavailable. An owner can review the meter."); }
            if (snapshot.OverrideActive) continue;
            if (!snapshot.MeterComplete)
                return new(false, "Quota usage is temporarily unavailable. An owner can review the meter.");
            if (!WorkspaceQuotaPolicy.Allows(snapshot.Limits, snapshot.Usage, hosting))
                return new(false, snapshot.Message ?? "Quota reached. An owner can override this week's limit.");
        }
        return new(true, null);
    }

    public QuotaSnapshot Snapshot(string workspaceId, string? projectId = null)
    {
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        var now = _clock.GetUtcNow().UtcDateTime;
        var (start, end) = WorkspaceQuotaPolicy.Week(_clock);
        QuotaLimits? limits;
        bool overridden;
        Dictionary<string, string> environments;
        lock (_gate)
        {
            Reload();
            var key = projectId ?? workspaceId;
            (projectId is null ? _state.Workspaces : _state.Projects).TryGetValue(key, out limits);
            overridden = _state.Overrides.TryGetValue(key, out var expiry) && expiry > now;
            environments = new(_state.Environments, StringComparer.OrdinalIgnoreCase);
        }
        var projects = _projects.List().Where(p => !p.Archived && p.WorkspaceId == workspaceId
            && (projectId is null || p.Id == projectId)).ToList();
        if (projectId is not null && projects.Count == 0) throw new KeyNotFoundException("Project does not belong to workspace.");
        var consumers = new List<QuotaConsumer>();
        var meterComplete = true;
        var daily = Enumerable.Range(0, 7).Select(_ => new QuotaUsage(0, 0, 0, 0)).ToArray();
        foreach (var project in projects)
        {
            var snapshot = _tokens.LoadSnapshot(project.DisplayName, project.StorageLocation);
            if (snapshot.Freshness.Status == "unavailable") meterComplete = false;
            foreach (var entry in snapshot.Entries.Where(e => e.TokenUsage is not null))
            {
                var at = AsUtc(entry.Ts);
                if (at < start || at >= end) continue;
                var usage = entry.TokenUsage!;
                var count = WorkspaceQuotaPolicy.ModelTokens(usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, usage.InputIncludesCached);
                decimal usd;
                try
                {
                    var estimate = _pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                        usage.CacheReadTokens, usage.CacheCreationTokens, at);
                    usd = estimate.ModelKnown ? estimate.Total : 0;
                }
                catch (Exception) { usd = 0; }
                var day = Math.Clamp((int)(at.Date - start).TotalDays, 0, 6);
                daily[day] = daily[day] with { ModelTokens = daily[day].ModelTokens + count,
                    ModelCostUsd = daily[day].ModelCostUsd + usd };
                consumers.Add(new(project.Id, project.DisplayName, entry.JobId, count, 0));
            }
        }
        var projectIds = projects.Select(p => p.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in _scanner.ScanAllJobsRaw().Where(t => projectIds.Contains(t.ProjectName)))
        {
            var record = _pipeline.Read(task.FolderPath);
            if (record is null) continue;
            foreach (var attempt in new[] { record }.Concat(record.PreviousAttempts))
            foreach (var step in attempt.Steps)
            foreach (var execution in new[] { (step.StartedAt, step.CompletedAt, step.GrantedCores) }
                .Concat(step.EarlierRuns?.Select(run => (run.StartedAt, run.CompletedAt, run.GrantedCores))
                    ?? Enumerable.Empty<(DateTime?, DateTime?, int?)>()))
            {
                if (execution.StartedAt is null || execution.StartedAt >= end) continue;
                var until = execution.CompletedAt ?? now;
                var clippedStart = execution.StartedAt.Value > start ? execution.StartedAt.Value : start;
                var clippedEnd = until < end ? until : end;
                if (clippedEnd <= clippedStart) continue;
                var cores = Math.Max(1, execution.GrantedCores ?? 1);
                var hours = WorkspaceQuotaPolicy.CoreHours(execution.StartedAt.Value,
                    execution.CompletedAt, cores, start, end, _clock);
                consumers.Add(new(projects.First(p => string.Equals(p.DisplayName, task.ProjectName,
                        StringComparison.OrdinalIgnoreCase)).Id,
                    task.ProjectName, task.Id, 0, hours));
                for (var day = clippedStart.Date; day < clippedEnd; day = day.AddDays(1))
                {
                    var from = clippedStart > day ? clippedStart : day;
                    var to = clippedEnd < day.AddDays(1) ? clippedEnd : day.AddDays(1);
                    var index = (int)(day - start).TotalDays;
                    if (index is >= 0 and < 7)
                        daily[index] = daily[index] with { ComputeCoreHours = daily[index].ComputeCoreHours + (decimal)(to - from).TotalHours * cores };
                }
            }
        }
        var hostingCount = environments.Values.Count(project => projects.Any(p => p.Id == project));
        var total = new QuotaUsage(daily.Sum(d => d.ModelTokens), daily.Sum(d => d.ModelCostUsd),
            daily.Sum(d => d.ComputeCoreHours), hostingCount);
        var remaining = new QuotaUsage(Math.Max(0, (limits?.ModelTokensPerWeek ?? 0) - total.ModelTokens), 0,
            Math.Max(0, (limits?.ComputeCoreHoursPerWeek ?? 0) - total.ComputeCoreHours),
            Math.Max(0, (limits?.HostingSlots ?? 0) - total.HostingSlots));
        var status = !meterComplete && limits is not null ? "blocked" : WorkspaceQuotaPolicy.Status(limits, total);
        var message = !meterComplete && limits is not null ? "Quota usage is temporarily unavailable. New work waits for the meter."
            : status == "blocked" ? "Weekly quota reached. New work is paused until Monday 00:00 UTC or an owner override."
            : status == "warning" ? "Workspace quota is at least 80% used." : null;
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
            overridden || meterComplete && WorkspaceQuotaPolicy.Allows(limits, total, hosting: false),
            overridden || meterComplete && WorkspaceQuotaPolicy.Allows(limits, total, hosting: true), message);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

}
