using AgentStudio.Cli;
using AgentStudio.Clients;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

internal sealed record CapacityHost(string Id, string Name, string HostId, int Slots, int Used,
    IReadOnlySet<string> Capabilities, string? InstanceId = null);

/// <summary>Joins published task, host, admission and quota facts without running dispatch.</summary>
public sealed class CodingCapacityService(TaskScannerService scanner, ProjectSettingsService settings,
    ClientIdentityStore clients, V1ReviewExecutorRegistry executors, QuotaService quota,
    IModelSelectionService selection, OverlapDispatchGuard dispatch, CapacityIdleTracker idle,
    TaskListGitProjectionCache gitProjection)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.CapacityUnderused];

    public CodingCapacityResponse? Read(DateTime now, Func<string, bool>? allows = null, bool includeQuota = true)
    {
        if (scanner.TryGetPublishedBoard() is not { } board) return null;
        var visibleTasks = board.Live.Where(task => !task.Fixture && (allows?.Invoke(task.ProjectName) ?? true)).ToArray();
        var projects = scanner.GetWatchPaths().Select(path => path.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(project => allows?.Invoke(project) ?? true).ToArray();
        var watched = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Deduplicate before partitioning so a stale copy cannot appear in two project rows.
        var tasks = visibleTasks.Where(task => watched.Contains(task.ProjectName))
            .DistinctBy(CodingCapacityProjection.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        // Limit reverse-link counts and dependency names to the reader's projects.
        var references = allows is null ? board.AutomationReferences : TaskReferenceIndex.Build(
            visibleTasks.Concat(board.Archive.Where(task => !task.Fixture && allows(task.ProjectName))));
        var hosts = Hosts(now);
        var integration = gitProjection.ReadDependencyIntegration(board.AutomationReferences.DependencyTargets(visibleTasks));
        var occupancy = ProjectExecutionPolicy.ProjectOccupancy(board.Live);
        IReadOnlyList<CapacityBlock> Blocks(TaskInfo task)
        {
            var project = settings.Get(task.ProjectName);
            var assigned = hosts.Where(host => Assigned(project, host) && host.Used < host.Slots).ToArray();
            var plans = assigned.Select(host =>
            {
                if (host.InstanceId is null)
                    return (Forecast: OperatorCapacityEndpoints.Forecast(selection, null, task, board.Live, now), Admission: (string?)null);
                var call = CodingSelectionCalls.For(task, task.ProjectName, host.Used, now, null, preview: true);
                var decision = selection.Select(call with
                {
                    Request = call.Request with { Host = new SelectionHost(host.Name) },
                    ExecutionPath = "remote-coding-claim",
                    HostAccepts = (cli, model) => executors.AcceptsModel(host.Id, host.InstanceId, cli, model),
                    HostAcceptsFallback = (cli, model) => executors.ModelAdmission(host.Id, host.InstanceId, cli, model)
                        == ModelAcceptanceAdmission.Accepted,
                });
                var plan = ProjectRunner.ApplyPendingModelRoute(decision.Admission, task.PendingIntent?.ModelFallback);
                var required = new[] { CapabilityProtocol.CodingExecutor, CapabilityProtocol.CliExecution(plan.CliType),
                    CapabilityProtocol.ProviderAuthentication(plan.CliType) }
                    .Concat(ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(project)) is { } cap ? [cap] : []).ToArray();
                var admission = executors.EvaluateCodingAdmission(host.Id, host.InstanceId, required);
                return (Forecast: new CapacityForecast(decision.Result.Primary, plan), Admission: admission.Eligible ? null : admission.Message);
            }).ToArray();
            var best = plans.FirstOrDefault(plan => !plan.Forecast.Plan.IsDeferred && plan.Admission is null);
            if (best.Forecast is null) best = plans.FirstOrDefault();
            var forecast = best.Forecast ?? OperatorCapacityEndpoints.Forecast(selection, null, task, board.Live, now);
            var blocks = CapacityClaimability.Blocks(task, project, hosts,
                board.AutomationReferences, integration, forecast,
                ProjectExecutionPolicy.EvaluateProjectSlot(occupancy, task.ProjectName, project), dispatch.Presentation(task));
            return best.Admission is null ? blocks : blocks.Concat([new CapacityBlock("dispatch-rule", best.Admission)]).ToArray();
        }
        var waits = new Dictionary<string, WaitsOnStatus>(StringComparer.OrdinalIgnoreCase);
        WaitsOnStatus Waits(TaskInfo task)
        {
            var key = CodingCapacityProjection.Key(task);
            if (!waits.TryGetValue(key, out var status))
                waits[key] = status = board.AutomationReferences.EvaluateWaitsOn(task, integration, settings.Get(task.ProjectName).IntegrationBranch);
            return status;
        }
        var rows = projects.Select(project =>
        {
            var config = settings.Get(project);
            var assigned = hosts.Where(host => Assigned(config, host)).ToArray();
            var total = assigned.Sum(host => host.Slots);
            var used = assigned.Sum(host => host.Used);
            if (ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(config)) is not null)
            {
                total = Math.Min(total, Math.Max(1, config.MaxParallelism));
                var projectUsed = occupancy.GetValueOrDefault(project);
                var available = Math.Min(Math.Max(0, total - projectUsed), assigned.Sum(host => Math.Max(0, host.Slots - host.Used)));
                used = total - available;
            }
            return CodingCapacityProjection.Project(project, tasks.Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)).ToArray(),
                references, total, used, Blocks, Waits);
        }).ToArray();
        var fleetHosts = includeQuota ? hosts : hosts.Where(host => projects.Any(project => Assigned(settings.Get(project), host))).ToArray();
        var byCard = rows.SelectMany(row => row.Cards).ToDictionary(card => card.CardKey, StringComparer.OrdinalIgnoreCase);
        var fleet = CodingCapacityProjection.Project(null, tasks, references, fleetHosts.Sum(host => host.Slots),
            fleetHosts.Sum(host => host.Used), task => byCard[CodingCapacityProjection.Key(task)].Blocks.Where(block => block.Class != "dependency").ToArray(), Waits);
        fleet = fleet with { MaxConcurrentCards = Math.Min(fleet.MaxConcurrentCards, rows.Sum(row => row.MaxConcurrentCards)) };
        var subscriptions = includeQuota ? CodingCapacityProjection.Subscriptions(quota.GetCached(), now) : [];
        return new(fleet, rows, subscriptions);
    }

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
    {
        if (Read(nowUtc) is not { } report) { idle.Reset(); return null; }
        idle.Retain(report.Projects.Select(project => project.Project!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return report.Projects.Select(project => idle.Observe(project, [], nowUtc))
            .Append(idle.Observe(report.Fleet, report.Subscriptions, nowUtc)).OfType<OperatorDecisionFact>().ToArray();
    }

    private CapacityHost[] Hosts(DateTime now) => Hosts(now, clients.ListPublished(), executors.ListCapabilitySnapshots());

    internal static CapacityHost[] Hosts(DateTime now, IEnumerable<ClientIdentity> identities,
        IReadOnlyList<RunnerCapabilitySnapshotDto> snapshots)
    {
        var remote = snapshots.Where(host => host.Status == "active"
            && host.HostAdmission.AdmissionState == "open" && now - host.LastSeenAt <= CodingCapacityProjection.HostFreshness
            && host.Telemetry is { } telemetry && now - telemetry.ObservedAt <= CodingCapacityProjection.HostFreshness
            && host.Capabilities.Any(capability => capability.Key == CapabilityProtocol.CodingExecutor && capability.IsFresh))
            .Select(host => new CapacityHost(host.RunnerId, host.Name, host.HostId,
                Math.Max(0, host.RoleMaxParallelism ?? host.EffectiveMaxParallelism ?? host.RuntimeCapacity?.MaxParallelism ?? 0),
                Math.Max(0, host.Telemetry?.ActiveSlots ?? 0), host.Capabilities.Where(capability => capability.IsFresh)
                    .Select(capability => capability.Key).ToHashSet(StringComparer.Ordinal), host.InstanceId)).ToArray();
        var legacy = identities.Where(host => host.Kind != ClientIdentityKind.Retired
            && host.RunnerDaemonState == "running" && host.LastSeenAt is { } seen && now - seen <= CodingCapacityProjection.HostFreshness
            && host.DrainRequestedAt is null && host.RetireRequestedAt is null && host.RunnerGitStatus != "read-only" && !remote.Any(other => other.Id == host.Id))
            .Select(host => new CapacityHost(host.Id, host.DisplayName, host.Id,
                Math.Max(0, host.RunnerDesiredMaxParallelism ?? host.RunnerEffectiveMaxParallelism ?? 0),
                Math.Max(0, host.RunnerActiveSlots ?? 0), new HashSet<string>()));
        return remote.Concat(legacy).ToArray();
    }

    internal static bool Assigned(ProjectSettings project, CapacityHost host)
        => ProjectExecutionPolicy.IsLocalExecution(project) ? host.Capabilities.Count == 0
            : ProjectExecutionPolicy.IsAssignedRemote(project, host.Id, host.Name)
                && (ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(project)) is not { } capability
                    || host.Capabilities.Contains(capability));
}
