using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using AgentStudio.Tasks;

namespace AgentStudio.Quota;

public sealed record QuotaLimits(long? ModelTokensPerWeek, decimal? ComputeCoreHoursPerWeek, int? HostingSlots, decimal? WeeklyBudgetUsd = null, bool IncludeCacheReads = true);
public sealed record QuotaUsage(long ModelTokens, decimal ModelCostUsd, decimal ComputeCoreHours, int HostingSlots,
    int UnpricedRuns = 0, long UncachedTokens = 0);
public sealed record QuotaConsumer(string ProjectId, string Project, string? CardId, long ModelTokens, decimal ComputeCoreHours);
public sealed record QuotaProjectConsumer(string ProjectId, string Project, long ModelTokens, decimal ComputeCoreHours);
public sealed record QuotaSnapshot(string WorkspaceId, string? ProjectId, DateTime WeekStartUtc, DateTime WeekEndUtc,
    QuotaLimits? Limits, QuotaUsage Usage, QuotaUsage Remaining, string Status,
    IReadOnlyList<QuotaConsumer> BiggestConsumers, IReadOnlyList<QuotaProjectConsumer> BiggestProjects,
    IReadOnlyList<QuotaUsage> DailyTrend, bool MeterComplete, bool OverrideActive,
    bool CodingAllowed, bool HostingAllowed, string? Message);
/// <summary>MeterUnavailable marks a verdict given without a complete meter, so callers can surface it.</summary>
public sealed record QuotaAdmission(bool Allowed, string? Message, bool MeterUnavailable = false, IReadOnlyDictionary<string, string>? CardRefusals = null);
/// <summary>Board view of one scope; Limited is false when no limit applies and nothing was metered.</summary>
public sealed record QuotaScopeAdmission(string? ProjectId, string? Project, string Status, string? Message,
    bool OverrideActive, bool CodingAllowed, bool HostingAllowed, bool Limited);
public sealed record QuotaBoardAdmission(string WorkspaceId, QuotaScopeAdmission Workspace,
    IReadOnlyList<QuotaScopeAdmission> Projects, IReadOnlyDictionary<string, string>? CardRefusals = null);
public sealed record QuotaOverrideAudit(DateTimeOffset At, string WorkspaceId, string? ProjectId,
    string Actor, string Reason, DateTime ExpiresAt);
public sealed record QuotaDeployExecution(string ProjectId, string JobId, string EnvironmentId,
    DateTime StartedAt, DateTime? CompletedAt);
/// <summary>The unsettled dispatch that owns a hosting slot it reserved; DispatchedAt identifies it.</summary>
public sealed record QuotaHostingDispatch(string Project, string EnvironmentId, string Workflow, DateTime DispatchedAt);

/// <summary>Workspace and project limits. Usage is projected from the same token and pipeline records as Studio.</summary>
public sealed class WorkspaceQuotaService
{
    /// <summary>
    /// Usage is recomputed in the background at most this often; readers get the
    /// previous value meanwhile, so usage lags token and pipeline writes by about this long.
    /// </summary>
    public static readonly TimeSpan UsageTtl = TimeSpan.FromSeconds(30);

    /// <summary>Override audits older than this are dropped when a new override is written.</summary>
    public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(90);
    private const int MaxAudits = 200;

    private sealed record State(Dictionary<string, QuotaLimits> Workspaces,
        Dictionary<string, QuotaLimits> Projects, Dictionary<string, DateTime> Overrides,
        Dictionary<string, string> Environments, List<QuotaOverrideAudit>? Audits = null,
        List<QuotaDeployExecution>? Deploys = null, Dictionary<string, QuotaHostingDispatch>? Dispatches = null, Dictionary<string, CardBudget>? Cards = null);

    /// <summary>StateUnknown: the settings file was never readable since start, so no limits are known.</summary>
    private sealed record StateView(QuotaLimits? WorkspaceLimits, QuotaLimits? ProjectLimits,
        bool WorkspaceOverride, bool ProjectOverride, IReadOnlyCollection<string> Environments,
        IReadOnlyList<QuotaDeployExecution> Deploys, string? ReadError, bool StateUnknown);

    /// <summary>Current-week usage of one project; the trend has one bucket per UTC day.</summary>
    private sealed record ProjectUsage(ProjectRecord Project, QuotaUsage[] Daily,
        IReadOnlyList<QuotaConsumer> Consumers, string? MeterWarning);

    /// <summary>RefreshError is set when the latest background recompute failed and the value is older.</summary>
    private sealed record WorkspaceUsage(DateTime WeekStart, IReadOnlyDictionary<string, ProjectUsage> Projects,
        QuotaUsage[] Unattributed, IReadOnlyList<QuotaConsumer> UnattributedConsumers,
        string? MeterWarning = null, string? RefreshError = null);

    private sealed record UsageSlot(WorkspaceUsage Value, DateTimeOffset ComputedAt);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectRegistry _projects;
    private readonly ProjectTokenLedger _tokens;
    private readonly AgentMessageBusStore _bus;
    private readonly string _workspaceRoot;
    private readonly ITokenPricing _pricing;
    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _pipeline;
    private readonly ILogger<WorkspaceQuotaService>? _logger;
    private readonly ConcurrentDictionary<string, UsageSlot> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _usageRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _usageGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unpricedModels = new(StringComparer.OrdinalIgnoreCase);
    private State _state = Empty();
    private string? _stateReadError;
    private bool _stateKnown;
    private (DateTime WrittenAt, long Length)? _loadedStamp;

    public WorkspaceQuotaService(IConfiguration config, TimeProvider clock, WorkspaceRegistry workspaces,
        ProjectRegistry projects, ProjectTokenLedger tokens, ITokenPricing pricing,
        TaskScannerService scanner, PipelineExecutionLog pipeline, AgentMessageBusStore bus,
        ILogger<WorkspaceQuotaService>? logger = null)
    {
        _clock = clock;
        _workspaces = workspaces;
        _projects = projects;
        _tokens = tokens;
        _bus = bus;
        _pricing = pricing;
        _scanner = scanner;
        _pipeline = pipeline;
        _logger = logger;
        var root = config["TaskRepository"] ?? Path.Combine(Path.GetTempPath(), "agent-studio-quota");
        _workspaceRoot = root;
        _path = Path.Combine(root, ".metadata", "workspace-quotas.json");
        lock (_gate) Reload();
    }

    private static State Empty() => new(new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The state lives in memory; the file is read again only when its write
    /// time or length changed. An unreadable state file keeps the last good
    /// state and is reported as meter unavailable. Without a last good state
    /// (an unreadable file since start) admission refuses and nothing is
    /// written, so the unknown limits are neither ignored nor overwritten.
    /// </summary>
    private void Reload()
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists)
            {
                _stateKnown |= _stateReadError is null;
                return;
            }
            var stamp = (file.LastWriteTimeUtc, file.Length);
            if (_loadedStamp == stamp) return;
            _loadedStamp = stamp;
            var read = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            if (read is not null) _state = read;
            _stateReadError = null;
            _stateKnown = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_stateReadError is null)
                _logger?.LogWarning(ex, "workspace-quota-state-unreadable path={Path}", _path);
            _stateReadError = _stateKnown
                ? "Quota settings could not be read; admission uses the last readable settings."
                : UnknownStateMessage;
        }
    }

    private const string UnknownStateMessage = "Quota settings could not be read and no earlier settings are known. "
        + "New work waits until an owner repairs the quota settings file.";

    private void Save()
    {
        if (!_stateKnown) throw new InvalidOperationException(UnknownStateMessage);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
        var file = new FileInfo(_path);
        _loadedStamp = (file.LastWriteTimeUtc, file.Length);
    }

    public void SetLimits(string workspaceId, string? projectId, QuotaLimits? limits)
    {
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        if (limits is { ModelTokensPerWeek: < 0 } or { ComputeCoreHoursPerWeek: < 0 } or { HostingSlots: < 0 } or { WeeklyBudgetUsd: < 0 })
            throw new ArgumentException("Quota limits must be nonnegative.");
        if (projectId is not null && !_projects.List().Any(p => p.Id == projectId && p.WorkspaceId == workspaceId))
            throw new KeyNotFoundException("Project does not belong to workspace.");
        // Blank fields in Settings save as all-null limits; that is no limit, not an empty one.
        if (IsEmpty(limits)) limits = null;
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

    public sealed record CardBudget(decimal? Usd, long? UncachedTokens);

    public CardBudget GetCardBudget(string workspaceId, string projectId, string cardId)
    {
        var task = ResolveBudgetCard(workspaceId, projectId, cardId);
        lock (_gate) { Reload(); return _state.Cards?.GetValueOrDefault(projectId + "/" + task.Id) ?? new(null, null); }
    }

    public void SetCardBudget(string workspaceId, string projectId, string cardId, CardBudget budget)
    {
        if (string.IsNullOrWhiteSpace(cardId) || budget is { Usd: < 0 } or { UncachedTokens: < 0 })
            throw new ArgumentException("Card id and nonnegative budget are required.");
        var task = ResolveBudgetCard(workspaceId, projectId, cardId);
        lock (_gate)
        {
            Reload();
            var cards = _state.Cards ?? new Dictionary<string, CardBudget>();
            var key = projectId + "/" + task.Id;
            if (budget is { Usd: null, UncachedTokens: null }) cards.Remove(key);
            else cards[key] = budget;
            _state = _state with { Cards = cards };
            Save();
        }
    }

    private TaskInfo ResolveBudgetCard(string workspaceId, string projectId, string cardId)
    {
        var project = _projects.List().FirstOrDefault(p => p.Id == projectId && p.WorkspaceId == workspaceId)
            ?? throw new KeyNotFoundException("Project does not belong to workspace.");
        return FindCard(_scanner.ScanAllJobsWithArchive(), project, cardId)
            ?? throw new KeyNotFoundException("Unknown card in project.");
    }

    private static string?[] CardAliases(TaskInfo? task, string cardId)
        => task is null ? [cardId] : [task.Id, task.Key, task.TaskKey];

    private static TaskInfo? FindCard(IEnumerable<TaskInfo> tasks, ProjectRecord project, string cardId)
        => tasks.FirstOrDefault(t => t.ProjectName == project.DisplayName
            && CardAliases(t, t.Id).Contains(cardId, StringComparer.OrdinalIgnoreCase));

    private QuotaAdmission? CardAdmission(ProjectRecord project, string cardId)
    {
        lock (_gate) { Reload(); if (_state.Cards is not { Count: > 0 }) return null; }
        var task = FindCard(_scanner.ScanAllJobsWithArchive(), project, cardId);
        CardBudget? budget;
        lock (_gate) { Reload(); budget = _state.Cards?.GetValueOrDefault(project.Id + "/" + (task?.Id ?? cardId)); }
        return budget is null ? null : CardAdmission(budget, CardAliases(task, cardId),
            _tokens.LoadFreshSnapshot(project.DisplayName, project.StorageLocation));
    }

    private QuotaAdmission? CardAdmission(CardBudget budget, string?[] aliases, ProjectTokenLedgerSnapshot ledger)
    {
        if (ledger.Freshness.Status != "complete") return new(false, "card-budget-meter-unavailable: card usage is unavailable.", true);
        long tokens = 0;
        decimal cost = 0;
        foreach (var entry in ledger.Entries.Where(e => aliases.Contains(e.JobId, StringComparer.OrdinalIgnoreCase)))
        {
            if (entry.TokenUsage is not { } usage) continue;
            tokens += WorkspaceQuotaPolicy.ModelTokens(usage.InputTokens, usage.OutputTokens, 0, usage.CacheCreationTokens);
            cost += Price(usage, AsUtc(entry.Ts)) ?? 0;
        }
        if (budget.Usd is { } usd && cost >= usd) return new(false, $"card-budget-usd: {cost:F2} USD priced spend of {usd:F2} USD; owner must raise or clear the card budget.");
        if (budget.UncachedTokens is { } cap && tokens >= cap) return new(false, $"card-budget-tokens: {tokens:N0} of {cap:N0} tokens excluding cache reads; owner must raise or clear the card budget.");
        return null;
    }

    public void Override(string workspaceId, string? projectId, string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Actor and reason are required.");
        if (_workspaces.Find(workspaceId) is null) throw new KeyNotFoundException("Unknown workspace.");
        if (projectId is not null && !_projects.List().Any(p => p.Id == projectId && p.WorkspaceId == workspaceId))
            throw new KeyNotFoundException("Project does not belong to workspace.");
        var expiry = WorkspaceQuotaPolicy.Week(_clock).End;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Reload();
            foreach (var expired in _state.Overrides.Where(item => item.Value <= now.UtcDateTime).Select(item => item.Key).ToList())
                _state.Overrides.Remove(expired);
            _state.Overrides[projectId ?? workspaceId] = expiry;
            var audits = (_state.Audits ?? []).Where(entry => entry.At >= now - AuditRetention).ToList();
            audits.Add(WorkspaceQuotaPolicy.OverrideEntry(_clock, workspaceId, projectId, actor, reason, expiry));
            _state = _state with { Audits = audits.TakeLast(MaxAudits).ToList() };
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

    private static bool IsEmpty(QuotaLimits? limits)
        => limits is null or { ModelTokensPerWeek: null, ComputeCoreHoursPerWeek: null, HostingSlots: null, WeeklyBudgetUsd: null };

    /// <summary>
    /// True when any workspace or project limit exists, or when the settings
    /// file cannot be read (limits are then unknown). Admission paths skip all
    /// quota work otherwise.
    /// </summary>
    public bool HasLimits()
    {
        lock (_gate)
        {
            Reload();
            return _state.Workspaces.Values.Any(limits => !IsEmpty(limits))
                || (_state.Cards?.Count ?? 0) > 0 || _state.Projects.Values.Any(limits => !IsEmpty(limits)) || _stateReadError is not null;
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
            _state.Dispatches?.Remove(key);
            Save();
        }
    }

    /// <summary>
    /// Reserve a hosting slot before dispatch. Tokens, core-hours and the meter
    /// are checked before the lock; the slot count is checked again and the
    /// slot written under it, so two deploys cannot take the last slot.
    /// </summary>
    public bool ReserveProjectEnvironment(string projectName, string environmentId)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return false;
        var admission = Admit(project, hosting: false);
        if (!admission.Allowed) throw new InvalidOperationException(admission.Message);
        var workspaceProjectIds = _projects.List().Where(p => p.WorkspaceId == project.WorkspaceId)
            .Select(p => p.Id).ToList();
        lock (_gate)
        {
            Reload();
            var key = project.Id + "/" + environmentId;
            if (_state.Environments.ContainsKey(key)) return false;
            if (HostingRefusalLocked(project, workspaceProjectIds) is { } refusal)
                throw new InvalidOperationException(refusal);
            _state.Environments[key] = _clock.GetUtcNow().ToString("O");
            Save();
            return true;
        }
    }

    /// <summary>Refusal when the workspace or the project has no free hosting slot; caller holds the lock.</summary>
    private string? HostingRefusalLocked(ProjectRecord project, IReadOnlyCollection<string> workspaceProjectIds)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        _state.Workspaces.TryGetValue(project.WorkspaceId, out var workspaceLimits);
        _state.Projects.TryGetValue(project.Id, out var projectLimits);
        foreach (var (scope, limits, ids) in new[]
                 {
                     (project.WorkspaceId, workspaceLimits, workspaceProjectIds),
                     (project.Id, projectLimits, (IReadOnlyCollection<string>)[project.Id]),
                 })
        {
            if (limits?.HostingSlots is not { } slots
                || _state.Overrides.TryGetValue(scope, out var expiry) && expiry > now) continue;
            var used = _state.Environments.Keys.Count(key => ids.Any(id =>
                key.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase)));
            if (used >= slots) return WorkspaceQuotaPolicy.Message(WorkspaceQuotaPolicy.HostingFull, limits);
        }
        return null;
    }

    public void SetProjectEnvironment(string projectName, string environmentId, bool running)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        SetEnvironment(project.Id, environmentId, running);
    }

    /// <summary>
    /// Persists which dispatch owns the slot, so its failure can release the
    /// slot after a restart. A dispatch that reserved the slot stamps the
    /// reservation with its start and owns it. A dispatch that took no slot
    /// keeps the stamp, so the live deployment can still release it; it takes
    /// over ownership only from an earlier dispatch that is not settled yet.
    /// </summary>
    public void RecordProjectEnvironmentDispatch(string projectName, string environmentId, string workflow,
        DateTimeOffset dispatchStartedAt, bool reserved)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        lock (_gate)
        {
            Reload();
            var key = project.Id + "/" + environmentId;
            if (!_state.Environments.ContainsKey(key)) return;
            var dispatches = _state.Dispatches ?? new(StringComparer.OrdinalIgnoreCase);
            if (!reserved && !dispatches.ContainsKey(key)) return;
            if (reserved) _state.Environments[key] = dispatchStartedAt.ToUniversalTime().ToString("O");
            dispatches[key] = new(project.DisplayName, environmentId, workflow, dispatchStartedAt.UtcDateTime);
            _state = _state with { Dispatches = dispatches };
            Save();
        }
    }

    /// <summary>Unsettled dispatches that own a slot of the environment id, across all projects.</summary>
    public IReadOnlyList<QuotaHostingDispatch> PendingDispatches(string environmentId)
    {
        lock (_gate)
        {
            Reload();
            return (_state.Dispatches ?? []).Values.Where(item => item.EnvironmentId == environmentId).ToList();
        }
    }

    /// <summary>
    /// Settles the dispatch identified by its start. A failed dispatch releases
    /// the slot; any other dispatch, or the live deployment, leaves it alone.
    /// </summary>
    public bool SettleProjectEnvironmentDispatch(string projectName, string environmentId, DateTime dispatchedAt,
        bool succeeded)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return false;
        lock (_gate)
        {
            Reload();
            var key = project.Id + "/" + environmentId;
            if (_state.Dispatches?.GetValueOrDefault(key) is not { } owner
                || owner.DispatchedAt != dispatchedAt) return false;
            _state.Dispatches.Remove(key);
            if (!succeeded) _state.Environments.Remove(key);
            Save();
            return true;
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

    /// <summary>Completed deployment facts for the decision stream's persona trigger.</summary>
    public IReadOnlyList<QuotaDeployExecution> CompletedDeploys(DateTime sinceUtc)
    {
        lock (_gate)
        {
            Reload();
            return (_state.Deploys ?? []).Where(item => item.CompletedAt >= sinceUtc).ToArray();
        }
    }

    public QuotaAdmission ProjectAdmission(string projectName)
    {
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null) return Admit(projectName);
        var admission = HasLimits() ? Admit(project, hosting: false) : new QuotaAdmission(true, null);
        return admission with { CardRefusals = ProjectCardRefusals(project,
            new Lazy<List<TaskInfo>>(_scanner.ScanAllJobsWithArchive)) };
    }

    /// <summary>One task snapshot and one fresh project ledger serve all of its card budgets.</summary>
    private IReadOnlyDictionary<string, string> ProjectCardRefusals(ProjectRecord project, Lazy<List<TaskInfo>> tasks)
    {
        List<KeyValuePair<string, CardBudget>> budgets;
        var prefix = project.Id + "/";
        lock (_gate)
        {
            Reload();
            budgets = (_state.Cards ?? new Dictionary<string, CardBudget>())
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
        var refusals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (budgets.Count == 0) return refusals;
        var cards = tasks.Value.Where(task => task.ProjectName == project.DisplayName)
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ledger = _tokens.LoadFreshSnapshot(project.DisplayName, project.StorageLocation);
        foreach (var (key, budget) in budgets)
        {
            var id = key[prefix.Length..];
            var aliases = CardAliases(cards.GetValueOrDefault(id), id);
            if (CardAdmission(budget, aliases, ledger) is not { Allowed: false } refusal) continue;
            foreach (var alias in aliases)
                if (!string.IsNullOrWhiteSpace(alias)) refusals[alias] = refusal.Message ?? "card-budget-reached";
        }
        return refusals;
    }

    public QuotaAdmission Admit(string projectName, bool hosting = false, string? cardId = null)
    {
        if (!HasLimits()) return new(true, null);
        var project = _projects.FindByIdOrDisplayName(projectName);
        if (project is null)
            return new(true, $"Project '{projectName}' is not registered in Studio; quota admission is off for it.", true);
        return (cardId is null ? null : CardAdmission(project, cardId)) ?? Admit(project, hosting);
    }

    private QuotaAdmission Admit(ProjectRecord project, bool hosting)
    {
        var view = View(project.WorkspaceId, project.Id);
        if (view.StateUnknown) return new(false, view.ReadError, true);
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

    public QuotaLimits? Limits(string workspaceId, string? projectId = null)
    {
        var view = View(workspaceId, projectId);
        return projectId is null ? view.WorkspaceLimits : view.ProjectLimits;
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
            if (view.StateUnknown)
                return new(project?.Id, project?.DisplayName, WorkspaceQuotaPolicy.Blocked, view.ReadError,
                    false, false, false, true);
            var limits = project is null ? view.WorkspaceLimits : view.ProjectLimits;
            if (limits is null || usage is null)
                return new(project?.Id, project?.DisplayName, WorkspaceQuotaPolicy.Ok, null, false, true, true, false);
            var snapshot = Build(workspaceId, project?.Id, view, usage);
            return new(project?.Id, project?.DisplayName, snapshot.Status, snapshot.Message,
                snapshot.OverrideActive, snapshot.CodingAllowed, snapshot.HostingAllowed, true);
        }
        var cardRefusals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new Lazy<List<TaskInfo>>(_scanner.ScanAllJobsWithArchive);
        foreach (var project in projects)
            foreach (var refusal in ProjectCardRefusals(project, tasks))
                cardRefusals[project.DisplayName + "/" + refusal.Key] = refusal.Value;
        return new(workspaceId, Scope(null, workspaceView),
            projects.Select(project => Scope(project, views[project.Id])).ToList(), cardRefusals);
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
                (_state.Deploys ?? []).ToList(), _stateReadError, !_stateKnown);
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
                UncachedTokens = sum.UncachedTokens + p.Daily[day].UncachedTokens,
                ModelCostUsd = sum.ModelCostUsd + p.Daily[day].ModelCostUsd,
                UnpricedRuns = sum.UnpricedRuns + p.Daily[day].UnpricedRuns,
                ComputeCoreHours = sum.ComputeCoreHours + p.Daily[day].ComputeCoreHours,
            })).ToArray();
        var consumers = projects.SelectMany(p => p.Consumers).ToList();
        if (projectId is null)
            for (var day = 0; day < daily.Length; day++)
            {
                daily[day] = daily[day] with
                {
                    ModelTokens = daily[day].ModelTokens + usage.Unattributed[day].ModelTokens,
                    UncachedTokens = daily[day].UncachedTokens + usage.Unattributed[day].UncachedTokens,
                    UnpricedRuns = daily[day].UnpricedRuns + usage.Unattributed[day].UnpricedRuns,
                    ModelCostUsd = daily[day].ModelCostUsd + usage.Unattributed[day].ModelCostUsd,
                };
            }
        if (projectId is null) consumers.AddRange(usage.UnattributedConsumers);
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
            daily.Sum(d => d.ComputeCoreHours), hostingCount, daily.Sum(d => d.UnpricedRuns), daily.Sum(d => d.UncachedTokens));
        var remaining = new QuotaUsage(Math.Max(0, (limits?.ModelTokensPerWeek ?? 0) - (limits?.IncludeCacheReads == false ? total.UncachedTokens : total.ModelTokens)), Math.Max(0, (limits?.WeeklyBudgetUsd ?? 0) - total.ModelCostUsd),
            Math.Max(0, (limits?.ComputeCoreHoursPerWeek ?? 0) - total.ComputeCoreHours),
            Math.Max(0, (limits?.HostingSlots ?? 0) - total.HostingSlots));
        var meterProblems = projects.Where(p => p.MeterWarning is not null)
            .Select(p => $"Project {p.Project.DisplayName}: {p.MeterWarning}").ToList();
        if (usage.MeterWarning is not null) meterProblems.Add(usage.MeterWarning);
        if (usage.RefreshError is not null) meterProblems.Add(usage.RefreshError);
        var meterComplete = meterProblems.Count == 0;
        var meterBlocks = !meterComplete && limits is not null;
        var status = meterBlocks ? WorkspaceQuotaPolicy.Blocked : WorkspaceQuotaPolicy.Status(limits, total);
        var message = meterBlocks
            ? "Quota usage is temporarily unavailable. New work waits for the meter. " + string.Join(" ", meterProblems)
            : WorkspaceQuotaPolicy.ReachedMessage(limits, total, end) ?? WorkspaceQuotaPolicy.Message(status, limits) ?? (limits is null ? null : view.ReadError);
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
    /// Current-week usage of every project in the workspace. The first read of a
    /// week computes it; later reads get the previous value while one
    /// background recompute runs, at most once per <see cref="UsageTtl"/>.
    /// </summary>
    private WorkspaceUsage Usage(string workspaceId)
    {
        var (start, _) = WorkspaceQuotaPolicy.Week(_clock);
        if (_usage.TryGetValue(workspaceId, out var slot) && slot.Value.WeekStart == start)
        {
            if (_clock.GetUtcNow() - slot.ComputedAt >= UsageTtl) StartUsageRefresh(workspaceId);
            return slot.Value;
        }
        // No value for this week yet: one reader computes, concurrent readers wait for it.
        lock (_usageGates.GetOrAdd(workspaceId, _ => new object()))
        {
            if (_usage.TryGetValue(workspaceId, out slot) && slot.Value.WeekStart == start) return slot.Value;
            var value = ComputeUsage(workspaceId);
            _usage[workspaceId] = new(value, _clock.GetUtcNow());
            return value;
        }
    }

    private void StartUsageRefresh(string workspaceId)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_usageRefreshes.TryAdd(workspaceId, done.Task)) return;
        _ = Task.Run(() =>
        {
            try
            {
                _usage[workspaceId] = new(ComputeUsage(workspaceId), _clock.GetUtcNow());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "workspace-quota-usage-refresh-failed workspace={Workspace}", workspaceId);
                // Keep the previous value, mark it as not refreshed, and retry after one TTL.
                if (_usage.TryGetValue(workspaceId, out var previous))
                    _usage[workspaceId] = new(previous.Value with
                    {
                        RefreshError = $"Usage could not be refreshed ({ex.GetType().Name}).",
                    }, _clock.GetUtcNow());
            }
            finally
            {
                _usageRefreshes.TryRemove(workspaceId, out _);
                done.SetResult();
            }
        });
    }

    /// <summary>The running background usage recompute of the workspace, or a completed task.</summary>
    internal Task PendingUsageRefresh(string workspaceId)
        => _usageRefreshes.TryGetValue(workspaceId, out var refresh) ? refresh : Task.CompletedTask;

    private WorkspaceUsage ComputeUsage(string workspaceId)
    {
        var (start, end) = WorkspaceQuotaPolicy.Week(_clock);
        var projects = _projects.List().Where(p => p.WorkspaceId == workspaceId).ToList();
        var names = projects.ToDictionary(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
        // The task index omits archived projects. Their current-week pipeline
        // executions still consume workspace quota, so read those folders directly.
        // Archived projects run nothing, so none of their cards is live.
        var tasks = _scanner.ScanAllJobsWithArchive()
            .Where(t => names.ContainsKey(t.ProjectName))
            .Select(t => (t.FolderPath, Project: names[t.ProjectName], Card: CardId(t),
                Aliases: new string?[] { t.Id, t.TaskKey, t.Key },
                Live: t.State is TaskStates.Progress or TaskStates.AutoReview))
            .Concat(projects.Where(p => p.Archived).SelectMany(p => ProjectTaskFolders(p.StorageLocation)
                .Select(folder => (FolderPath: folder, Project: p, Card: Path.GetFileName(folder),
                    Aliases: new string?[] { Path.GetFileName(folder) }, Live: false))))
            .DistinctBy(t => t.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, ProjectUsage>(StringComparer.OrdinalIgnoreCase);
        WorkspaceBusUsage? workspaceRows = null;
        string? workspaceWarning = null;
        try { workspaceRows = WorkspaceBusUsage.Read(_bus, _workspaceRoot, projects, _scanner, start); }
        catch (Exception ex)
        {
            workspaceWarning = $"The workspace token bus could not be read ({ex.GetType().Name}).";
        }
        var unattributedDaily = Enumerable.Range(0, 7).Select(_ => new QuotaUsage(0, 0, 0, 0)).ToArray();
        var unattributedConsumers = new List<QuotaConsumer>();
        var unattributedUnpricedRunIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var unpricedRunIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var meterWarning = ledger.Freshness.Status == "complete" ? null
                : ledger.Freshness.Warning ?? $"The token ledger is {ledger.Freshness.Status}.";
            foreach (var entry in (workspaceRows?.MergeInto(project.Id, ledger.Entries) ?? ledger.Entries)
                .Where(e => e.TokenUsage is not null))
            {
                var at = AsUtc(entry.Ts);
                if (at < start || at >= end) continue;
                var usage = entry.TokenUsage!;
                var count = WorkspaceQuotaPolicy.ModelTokens(usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens);
                var day = Math.Clamp((int)(at.Date - start).TotalDays, 0, 6);
                var price = Price(usage, at);
                var unpricedRun = count > 0 && price is null
                    && unpricedRunIds.Add(entry.JobId ?? $"receipt:{at.Ticks}:{day}");
                daily[day] = daily[day] with { ModelTokens = daily[day].ModelTokens + count,
                    UncachedTokens = daily[day].UncachedTokens + count - Math.Max(0, usage.CacheReadTokens),
                    ModelCostUsd = daily[day].ModelCostUsd + (price ?? 0),
                    UnpricedRuns = daily[day].UnpricedRuns + (unpricedRun ? 1 : 0) };
                var card = entry.JobId is null ? null : cards.GetValueOrDefault(entry.JobId, entry.JobId);
                consumers.Add(new(project.Id, project.DisplayName, card, count, 0));
            }
            foreach (var task in projectTasks)
            {
                var file = new FileInfo(Path.Combine(task.FolderPath, PipelineExecutionLog.FileName));
                if (!file.Exists) continue;
                var lastWrite = file.LastWriteTimeUtc;
                // A card that is not live and was not written this week has nothing open this week.
                if (!task.Live && lastWrite < start) continue;
                var record = _pipeline.Read(task.FolderPath);
                if (record is null) continue;
                foreach (var hours in StepCoreHours(record, task.Live, lastWrite, daily, start, end))
                    consumers.Add(new(project.Id, project.DisplayName, task.Card, 0, hours));
            }
            result[project.Id] = new(project, daily, consumers, meterWarning);
        }
        foreach (var entry in workspaceRows?.Unattributed ?? [])
        {
            if (entry.TokenUsage is not { } usage) continue;
            var at = AsUtc(entry.Ts);
            if (at < start || at >= end) continue;
            var count = WorkspaceQuotaPolicy.ModelTokens(usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens);
            var day = Math.Clamp((int)(at.Date - start).TotalDays, 0, 6);
            var price = Price(usage, at);
            var unpricedRun = count > 0 && price is null
                && unattributedUnpricedRunIds.Add(entry.JobId ?? $"receipt:{at.Ticks}:{day}");
            unattributedDaily[day] = unattributedDaily[day] with
            {
                ModelTokens = unattributedDaily[day].ModelTokens + count,
                UncachedTokens = unattributedDaily[day].UncachedTokens + count - Math.Max(0, usage.CacheReadTokens),
                ModelCostUsd = unattributedDaily[day].ModelCostUsd + (price ?? 0),
                UnpricedRuns = unattributedDaily[day].UnpricedRuns + (unpricedRun ? 1 : 0),
            };
            unattributedConsumers.Add(new("unattributed", "Unattributed one-shot jobs", null, count, 0));
        }
        return new(start, result, unattributedDaily, unattributedConsumers, workspaceWarning);
    }

    /// <summary>
    /// Core-hours of every step execution of the record and its previous
    /// attempts. Only the current attempt of a live card meters an open step
    /// until now; an archived attempt ends when it completed or when the next
    /// attempt started (see <see cref="WorkspaceQuotaPolicy.StepEnd"/>).
    /// </summary>
    private IEnumerable<decimal> StepCoreHours(PipelineExecutionRecord record, bool liveCard, DateTime lastWrite,
        QuotaUsage[] daily, DateTime start, DateTime end)
    {
        // Previous attempts are newest first, so each one was superseded by the attempt before it in the list.
        var attempts = new[] { record }.Concat(record.PreviousAttempts).ToList();
        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            var current = index == 0;
            var live = current && liveCard && !attempt.IsComplete;
            var attemptEnd = attempt.CompletedAt is { } completed ? AsUtc(completed)
                : current ? (DateTime?)null : AsUtc(attempts[index - 1].StartedAt);
            foreach (var step in attempt.Steps)
            foreach (var (startedAt, completedAt, cores, latest) in new[] { (step.StartedAt, step.CompletedAt, step.GrantedCores, true) }
                         .Concat(step.EarlierRuns?.Select(run => (run.StartedAt, run.CompletedAt, run.GrantedCores, false))
                             ?? Enumerable.Empty<(DateTime?, DateTime?, int?, bool)>()))
            {
                if (startedAt is not { } from) continue;
                var stepStart = AsUtc(from);
                // An earlier run of a step was replaced by a later run, so it is never live.
                var until = WorkspaceQuotaPolicy.StepEnd(stepStart, completedAt is { } done ? AsUtc(done) : null,
                    live && latest, attemptEnd, lastWrite, _clock);
                var hours = AddCoreHours(daily, start, end, stepStart, until, Math.Max(1, cores ?? 1));
                if (hours > 0) yield return hours;
            }
        }
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

    private decimal? Price(OrchestratorTokenUsage usage, DateTime at)
    {
        try
        {
            var estimate = _pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens, at);
            return estimate.ModelKnown ? estimate.Total : null;
        }
        catch (Exception ex)
        {
            // The USD figure is informational; token admission does not depend on it.
            if (_unpricedModels.TryAdd(usage.Model ?? "", 0))
                _logger?.LogWarning(ex, "workspace-quota-pricing-failed model={Model}", usage.Model);
            return null;
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
