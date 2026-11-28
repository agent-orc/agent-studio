using AgentStudio.Cli;
using AgentStudio.Clients;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

internal sealed record CapacityHost(string Id, string Name, string HostId, int Slots, int Used,
    IReadOnlySet<string> Capabilities, string? InstanceId = null, bool IsLocal = false)
{
    public int? HostCeiling { get; init; }
    public int? SharedAdmissibleSlots { get; init; }
    public int SharedUsed { get; init; }
    public int AdmissibleSlots { get; init; } = Math.Max(0, Slots - Used);
    public IReadOnlyList<CapacityBlock> AdmissionBlocks { get; init; } = [];
    public CapacityAllocation? Allocation { get; init; }
    public DateTime? LastProgressAt { get; init; }
    public ClientIdentity? Identity { get; init; }
}

/// <summary>Joins published task, host, admission and quota facts without running dispatch.</summary>
public sealed class CodingCapacityService(TaskScannerService scanner, ProjectSettingsService settings,
    ClientIdentityStore clients, V1ReviewExecutorRegistry executors, QuotaService quota,
    IModelSelectionService selection, OverlapDispatchGuard dispatch, CapacityIdleTracker idle,
    TaskListGitProjectionCache gitProjection, ProjectRegistry? registry = null, RunLeaseService? leases = null)
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
        var identities = clients.ListPublished();
        var snapshots = executors.ListCapabilitySnapshots();
        var publishedLeases = board.Live.Where(task => !task.Fixture)
            .Select(task => leases?.InspectBoard(CodingCapacityProjection.Key(task)))
            .Where(row => row is { State: "active", Lease: not null }).Select(row => row!.Lease!).ToArray();
        var hosts = Hosts(now, identities, snapshots, id => publishedLeases.Count(lease =>
            string.Equals(lease.RunnerId, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.ClientId, id, StringComparison.OrdinalIgnoreCase)));
        var integration = gitProjection.ReadDependencyIntegration(board.Live);
        var occupancy = ProjectExecutionPolicy.ProjectOccupancy(board.Live);
        IReadOnlyList<CapacityBlock> Blocks(TaskInfo task)
        {
            var project = settings.Get(task.ProjectName);
            var assigned = hosts.Where(host => Assigned(project, host) && host.Used < host.Slots).ToArray();
            var registered = registry?.FindByStorageLocation(task.WatchPath) ?? registry?.FindByIdOrDisplayName(task.ProjectName);
            var repository = RemoteProjectRepositoryResolver.Resolve(registered, project.IntegrationBranch);
            var plans = assigned.Select(host =>
            {
                if (host.InstanceId is null)
                    return (Forecast: OperatorCapacityEndpoints.Forecast(selection, null, task, board.Live, now),
                        Blocks: CapacityClaimability.HostBlocks(host, repository, now));
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
                var hostBlocks = CapacityClaimability.HostBlocks(host, repository, now);
                return (Forecast: new CapacityForecast(decision.Result.Primary, plan), Blocks: admission.Eligible
                    ? hostBlocks : hostBlocks.Concat([new CapacityBlock("admission", admission.Message ?? "Coding capability admission is unavailable")]).ToArray());
            }).ToArray();
            var best = plans.FirstOrDefault(plan => !plan.Forecast.Plan.IsDeferred && plan.Blocks.Count == 0);
            if (best.Forecast is null) best = plans.FirstOrDefault();
            var forecast = best.Forecast ?? OperatorCapacityEndpoints.Forecast(selection, null, task, board.Live, now);
            var blocks = CapacityClaimability.Blocks(task, project, hosts,
                board.AutomationReferences, integration, forecast,
                ProjectExecutionPolicy.EvaluateProjectSlot(occupancy, task.ProjectName, project), dispatch.Presentation(task));
            var overlap = dispatch.IsLive(task.ProjectName) ? null
                : CapacityClaimability.Overlap(task, project, board.Live, integration);
            return blocks.Concat(best.Blocks ?? []).Concat(overlap is null ? [] : new[] { overlap }).ToArray();
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
            var (total, used, admissible) = Totals(assigned);
            if (ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(config)) is not null)
            {
                total = Math.Min(total, Math.Max(1, config.MaxParallelism));
                var projectUsed = occupancy.GetValueOrDefault(project);
                var available = Math.Min(Math.Max(0, total - projectUsed), assigned.Sum(host => Math.Max(0, host.Slots - host.Used)));
                used = total - available;
                admissible = Math.Min(admissible, available);
            }
            return CodingCapacityProjection.Project(project, tasks.Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)).ToArray(),
                references, total, used, Blocks, Waits, admissible,
                assigned.SelectMany(host => host.AdmissionBlocks).Distinct().ToArray(),
                assigned.Select(host => host.LastProgressAt).Max()) with
                { Allocations = assigned.Select(host => host.Allocation).OfType<CapacityAllocation>().ToArray() };
        }).ToArray();
        var fleetHosts = includeQuota ? hosts : hosts.Where(host => projects.Any(project => Assigned(settings.Get(project), host))).ToArray();
        var byCard = rows.SelectMany(row => row.Cards).ToDictionary(card => card.CardKey, StringComparer.OrdinalIgnoreCase);
        var fleetTotals = Totals(fleetHosts);
        var fleet = CodingCapacityProjection.Project(null, tasks, references, fleetTotals.Total,
            fleetTotals.Used, task => byCard[CodingCapacityProjection.Key(task)].Blocks.Where(block => block.Class != "dependency").ToArray(), Waits,
            Math.Min(fleetTotals.Admissible, rows.Sum(row => row.AdmissibleSlots)), fleetHosts.SelectMany(host => host.AdmissionBlocks).Distinct().ToArray(),
            fleetHosts.Select(host => host.LastProgressAt).Max()) with
            { Allocations = fleetHosts.Select(host => host.Allocation).OfType<CapacityAllocation>().ToArray() };
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

    internal static (int Total, int Used, int Admissible) Totals(IEnumerable<CapacityHost> hosts)
    {
        var groups = hosts.GroupBy(host => host.HostId, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var ceiling = group.Select(host => host.HostCeiling).Min() ?? int.MaxValue;
            var used = Math.Max(group.Sum(host => host.Used), group.Max(host => host.SharedUsed));
            // Running work can drain above a newly lowered ceiling; never erase it.
            var total = Math.Max(used, Math.Min(ceiling, group.Sum(host => host.Slots)));
            return (Total: total, Used: used,
                Admissible: Math.Min(group.Select(host => host.SharedAdmissibleSlots).Min() ?? int.MaxValue,
                    Math.Min(Math.Max(0, Math.Min(ceiling, total) - used), group.Sum(host => host.AdmissibleSlots))));
        }).ToArray();
        return (groups.Sum(group => group.Total), groups.Sum(group => group.Used), groups.Sum(group => group.Admissible));
    }

    internal static CapacityHost[] Hosts(DateTime now, IEnumerable<ClientIdentity> identities,
        IReadOnlyList<RunnerCapabilitySnapshotDto> snapshots, Func<string, int>? activeLeases = null)
    {
        var clientsById = identities.DistinctBy(client => client.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(client => client.Id, StringComparer.OrdinalIgnoreCase);
        // A capability authority remains authoritative when stale, drained, or missing advertisements.
        var authoritative = snapshots.Select(host => host.RunnerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coding = snapshots.Where(host => host.Capabilities.Any(capability => capability.Key == CapabilityProtocol.CodingExecutor))
            .DistinctBy(host => host.RunnerId, StringComparer.OrdinalIgnoreCase).ToArray();
        var remote = coding.Where(host => host.Status == "active"
            && host.HostAdmission.AdmissionState == "open" && Fresh(host.LastSeenAt, now)
            && host.Telemetry is { } telemetry && Fresh(telemetry.ObservedAt, now)
            && host.Capabilities.Any(capability => capability.Key == CapabilityProtocol.CodingExecutor && capability.IsFresh))
            .Where(host => !clientsById.TryGetValue(host.RunnerId, out var client) || Usable(client))
            .Select(host =>
            {
                clientsById.TryGetValue(host.RunnerId, out var client);
                var configured = host.RuntimeCapacity?.MaxParallelism ?? client?.RunnerDesiredMaxParallelism;
                var adopted = host.EffectiveMaxParallelism ?? client?.RunnerEffectiveMaxParallelism;
                var ceiling = configured ?? adopted ?? host.RoleMaxParallelism ?? 0;
                var slots = Math.Min(ceiling, adopted ?? ceiling);
                // A shared physical ceiling is counted once. Separate roles retain their allocation bounds.
                if (coding.Count(other => string.Equals(other.HostId, host.HostId, StringComparison.OrdinalIgnoreCase)) > 1
                    && host.RoleMaxParallelism is { } role) slots = Math.Min(slots, role);
                var pending = configured is not null && (adopted != configured
                    || host.RuntimeCapacity is { } runtime && host.RuntimeCapacityAppliedVersion != (runtime.AllocationVersion > 0 ? runtime.AllocationVersion : runtime.Version));
                var used = Math.Max(Math.Max(0, host.Telemetry!.ActiveSlots), activeLeases?.Invoke(host.RunnerId) ?? 0);
                var blocks = new List<CapacityBlock>();
                if (adopted is null && configured is not null)
                    blocks.Add(new("admission", "Current capacity adoption is not reported"));
                if (host.Telemetry.ClaimLoadGate is { State: "closed" } gate)
                    blocks.Add(new("admission", $"Host load gate is closed ({gate.LoadPerCore:0.##} per core, threshold {gate.Threshold:0.##})"));
                return new CapacityHost(host.RunnerId, host.Name, host.HostId, Math.Max(0, slots), used,
                    host.Capabilities.Where(capability => capability.IsFresh).Select(capability => capability.Key).ToHashSet(StringComparer.Ordinal), host.InstanceId)
                {
                    HostCeiling = Math.Max(0, ceiling), Identity = client, AdmissionBlocks = blocks,
                    LastProgressAt = client?.RunnerLastClaimAt,
                    Allocation = new(host.RunnerId, host.RoleMaxParallelism, configured, adopted,
                        pending ? "pending adoption" : "adopted"),
                    AdmissibleSlots = Math.Max(0, slots - used),
                };
            }).ToArray();
        var legacy = clientsById.Values.Where(host => !authoritative.Contains(host.Id) && Usable(host)
            && host.RunnerDaemonState == "running" && host.LastSeenAt is { } seen && Fresh(seen, now))
            .Select(host =>
            {
                var ceiling = host.RunnerDesiredMaxParallelism ?? host.RunnerEffectiveMaxParallelism ?? 0;
                var slots = Math.Min(ceiling, host.RunnerEffectiveMaxParallelism ?? ceiling);
                var used = Math.Max(Math.Max(0, host.RunnerActiveSlots ?? 0), activeLeases?.Invoke(host.Id) ?? 0);
                var blocks = new List<CapacityBlock>();
                if (host.RunnerEffectiveMaxParallelism is null)
                    blocks.Add(new("admission", "Current capacity adoption is not reported"));
                return new CapacityHost(host.Id, host.DisplayName, host.Id, Math.Max(0, slots), used, new HashSet<string>())
                {
                    HostCeiling = Math.Max(0, ceiling), Identity = host, AdmissionBlocks = blocks,
                    LastProgressAt = host.RunnerLastClaimAt,
                    Allocation = new(host.Id, null, host.RunnerDesiredMaxParallelism, host.RunnerEffectiveMaxParallelism,
                        host.RunnerEffectiveMaxParallelism == ceiling ? "adopted" : "pending adoption"),
                    AdmissibleSlots = Math.Max(0, slots - used),
                };
            });
        var result = remote.Concat(legacy).ToArray();
        // Re-evaluate central admission with all coding roles' occupancy on a shared host.
        return result.Select(host =>
        {
            var peers = result.Where(other => string.Equals(other.HostId, host.HostId, StringComparison.OrdinalIgnoreCase)).ToArray();
            // A drained peer may still be running. Its rejected capacity must not hide its occupancy.
            var sharedUsed = Math.Max(peers.Sum(other => other.Used), coding.Where(other =>
                string.Equals(other.HostId, host.HostId, StringComparison.OrdinalIgnoreCase)).Sum(other =>
                    Math.Max(Fresh(other.LastSeenAt, now) && other.Telemetry is { } telemetry && Fresh(telemetry.ObservedAt, now) ? Math.Max(0, telemetry.ActiveSlots) : 0,
                        activeLeases?.Invoke(other.RunnerId) ?? 0)));
            var snapshot = snapshots.FirstOrDefault(row => row.RunnerId == host.Id);
            var centralBlocks = new List<CapacityBlock>();
            var targets = new HostCapacityTargets(host.HostCeiling ?? host.Slots,
                snapshot?.RuntimeCapacity?.TargetLoadPercent ?? host.Identity?.RunnerTargetLoadPercent ?? HostCapacityPolicy.DefaultTargetLoadPercent,
                snapshot?.RuntimeCapacity?.RampStrategy ?? host.Identity?.RunnerRampStrategy ?? RunnerRampStrategies.Balanced);
            var admissible = CapacityClaimability.AdmissibleSlots(targets,
                new(sharedUsed, now, peers.Select(peer => peer.LastProgressAt).Max(), snapshot?.Telemetry?.CpuPercent), targets.MaxParallelism, centralBlocks);
            return host with { AdmissionBlocks = host.AdmissionBlocks.Concat(centralBlocks).ToArray(), SharedUsed = sharedUsed, SharedAdmissibleSlots = admissible,
                AdmissibleSlots = host.AdmissionBlocks.Count > 0 ? 0 : Math.Min(host.AdmissibleSlots, admissible) };
        }).ToArray();
    }

    private static bool Fresh(DateTime seen, DateTime now) => seen <= now && now - seen <= CodingCapacityProjection.HostFreshness;
    private static bool Usable(ClientIdentity host) => host.Kind != ClientIdentityKind.Retired
        && host.DrainRequestedAt is null && host.RetireRequestedAt is null && host.RunnerGitStatus != "read-only";

    internal static bool Assigned(ProjectSettings project, CapacityHost host)
        => ProjectExecutionPolicy.IsLocalExecution(project) ? host.IsLocal
            : !host.IsLocal && ProjectExecutionPolicy.IsAssignedRemote(project, host.Id, host.Name)
                && (ExecutionLocations.RequiredClassCapability(ProjectExecutionPolicy.ResolveExecutionLocation(project)) is not { } capability
                    || host.Capabilities.Contains(capability));
}
