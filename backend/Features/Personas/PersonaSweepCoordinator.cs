using AgentStudio.Clients;
using AgentStudio.Operator;
using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Cli;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Management;

namespace AgentStudio.Personas;

/// <summary>Collects existing facts for the Watcher's persona admission step.</summary>
public sealed class PersonaSweepCoordinator
{
    private readonly PersonaActivationStore _activations;
    private readonly PersonaSchedulingService _scheduling;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceQuotaService _quotas;
    private readonly OperatorDecisionLog _decisions;
    private readonly AccessSecurityStore _security;
    private readonly ClientIdentityStore _clients;
    private readonly TaskScannerService _scanner;
    private readonly WorkspaceSettingsService _workspaceSettings;
    private readonly StudioBackupService _backups;
    private readonly ControlPlaneAuditMonitor _audit;
    private readonly ILogger<PersonaSweepCoordinator> _logger;
    private readonly IModelSelectionService _selection;

    public PersonaSweepCoordinator(PersonaActivationStore activations, PersonaSchedulingService scheduling,
        WorkspaceRegistry workspaces, ProjectRegistry projects, WorkspaceQuotaService quotas,
        OperatorDecisionLog decisions, AccessSecurityStore security, ClientIdentityStore clients,
        TaskScannerService scanner, IModelSelectionService selection,
        WorkspaceSettingsService workspaceSettings, StudioBackupService backups,
        ControlPlaneAuditMonitor audit, ILogger<PersonaSweepCoordinator> logger)
    {
        _activations = activations;
        _scheduling = scheduling;
        _workspaces = workspaces;
        _projects = projects;
        _quotas = quotas;
        _decisions = decisions;
        _security = security;
        _clients = clients;
        _scanner = scanner;
        _selection = selection;
        _workspaceSettings = workspaceSettings;
        _backups = backups;
        _audit = audit;
        _logger = logger;
    }

    public IReadOnlyList<PersonaOccurrenceReceipt> Sweep(DateTime now,
        IReadOnlyList<AgentStudio.Watcher.WatcherSignalObservation> observations)
    {
        var result = new List<PersonaOccurrenceReceipt>();
        var events = _decisions.Read(0, 10_000, _ => true).Events;
        IReadOnlyList<PersonaTrigger>? wardenFacts = null;
        var tasks = _scanner.GetResidentIdentityFacts();
        var hosts = _clients.ListPublished().Where(host => host.Kind == ClientIdentityKind.Service
            && host.RunnerAvailableSlots is > 0 && host.DrainRequestedAt is null
            && host.LastSeenAt is { } seen && now - seen < TimeSpan.FromMinutes(2)).ToArray();
        foreach (var workspace in _workspaces.List())
        {
            var projects = _projects.List().Where(p => p.WorkspaceId == workspace.Id && !p.Archived).ToArray();
            var projectNames = projects.Select(p => p.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activations = _activations.List(workspace.Id).Personas.Select(p => p.Activation)
                .Where(a => a is not null).Cast<PersonaActivationV1>().ToArray();
            if (activations.Length == 0) continue;
            var quota = _quotas.Snapshot(workspace.Id);
            var personaQuotaStatus = WorkspaceQuotaPolicy.Status(
                new QuotaLimits(quota.Limits?.ModelTokensPerWeek,
                    quota.Limits?.ComputeCoreHoursPerWeek, null), quota.Usage);
            var triggers = activations.ToDictionary(a => a.PersonaId, a =>
            {
                var rows = events.Where(e => e.Time >= now.AddHours(-24)
                    && (e.Project is null || projectNames.Contains(e.Project)))
                    .Select(e => new PersonaTrigger(e.Time, e.Type, e.Id)).ToList();
                // Warden fact probes identify the exact fact in personaFact; they
                // remain model-free and do not turn arbitrary card text into a trigger.
                if (a.Descriptor.Aspect == "operations")
                {
                    rows.AddRange(wardenFacts ??= WardenFacts(now));
                    rows.AddRange(observations.Where(o => projectNames.Contains(o.Project)
                        && o.Details.TryGetValue("personaFact", out _))
                        .Select(o => new PersonaTrigger(o.ObservedAtUtc,
                            o.Details["personaFact"], AgentStudio.Watcher.WatcherFingerprint.Compute(o),
                            o.Severity is "Critical" ? 90 : 0)));
                }
                return (IReadOnlyList<PersonaTrigger>)rows;
            }, StringComparer.Ordinal);
            var zoneId = _workspaceSettings.Get(workspace.Id).UsageTimeZone;
            TimeZoneInfo zone;
            try { zone = string.IsNullOrWhiteSpace(zoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
            result.AddRange(_scheduling.Sweep(workspace.Id, now, quota.Limits?.ModelTokensPerWeek,
                activations, triggers, activation =>
                {
                    var scoped = projects.Where(p => activation.Scope.Projects.Contains("*")
                        || activation.Scope.Projects.Contains(p.DisplayName, StringComparer.OrdinalIgnoreCase)
                        || activation.Scope.Projects.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
                    var eligible = hosts.Where(h => scoped.Length == 0 || h.RunnerProjectPreflights.Count == 0
                        || scoped.Any(p => h.RunnerProjectPreflights.Any(f => f.ProjectId == p.Id && f.Status == "ready")))
                        .ToArray();
                    var readyTasks = tasks.Where(t => t.State == TaskStates.Ready && scoped.Any(p =>
                        p.DisplayName.Equals(t.ProjectName, StringComparison.OrdinalIgnoreCase))).ToArray();
                    bool Serves(ClientIdentity host, TaskInfo task) => host.RunnerProjectPreflights.Count == 0
                        || scoped.Any(p => p.DisplayName.Equals(task.ProjectName, StringComparison.OrdinalIgnoreCase)
                            && host.RunnerProjectPreflights.Any(f => f.ProjectId == p.Id && f.Status == "ready"));
                    var idle = eligible.Where(h => !readyTasks.Any(t => Serves(h, t))).Select(h => h.Id).ToArray();
                    var ready = idle.Length == 0 && eligible.Any(h => readyTasks.Any(t => Serves(h, t)));
                    var model = activation.Descriptor.Model.CapabilityClass == "frontier"
                        ? "claude-opus-5" : "claude-sonnet-4-5";
                    var route = _selection.Select(new ModelSelectionCall(new SelectionRequest
                    {
                        Consumer = SelectionConsumers.Persona,
                        Project = scoped.FirstOrDefault()?.DisplayName,
                        IsExplicitPin = true,
                        Pins = new SelectionPins("claude", model, activation.Descriptor.Model.Thinking),
                    }, "persona-occurrence", Preview: true));
                    return new PersonaAdmissionFacts(personaQuotaStatus, quota.MeterComplete, idle, ready,
                        _security.HasLivePersonaGrant(workspace.Id, activation.PersonaId),
                        route.Result.ShouldLaunch);
                }, zone));
        }
        return result;
    }

    private IReadOnlyList<PersonaTrigger> WardenFacts(DateTime now)
    {
        var facts = new List<PersonaTrigger>();
        try
        {
            var overview = _backups.Overview();
            foreach (var warning in overview.Warnings)
            {
                var (type, floor) = warning.Code switch
                {
                    "backup-disabled" or "backup-not-configured" or "backup-key-missing" => ("backup.missed", 95),
                    "backup-missing" or "backup-stale" => ("backup.missed", 90),
                    "backup-failed" => ("backup.failed", 90),
                    "verification-failed" or "verification-overdue" => ("restore-test.due", 85),
                    _ => ("", 0),
                };
                if (type.Length > 0) facts.Add(new PersonaTrigger(now, type, "backup:" + warning.Code, floor));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persona warden backup fact read failed");
        }
        if (_audit.Integrity.State == ControlPlaneAuditIntegrityStates.Failed)
            facts.Add(new PersonaTrigger(now, "audit.chain-broken", "audit:chain-broken", 95));
        return facts;
    }
}
