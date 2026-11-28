using AgentStudio.Management;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

public sealed record WaitingDelivery(string Key, string Title, string Area, IReadOnlyList<string>? Flags,
    DateTime? IntegratedAtUtc)
{
    public static IReadOnlyList<string> PathFlags(IReadOnlyList<string> paths)
    {
        var flags = new List<string>();
        if (paths.Any(path => path.StartsWith("backend/Host/", StringComparison.Ordinal)
            || Path.GetFileName(path) is "Program.cs" or "Dockerfile" or "docker-compose.yml" or "install.sh"))
            flags.Add("Changes start-up");
        if (paths.Any(path => path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("Store.cs", StringComparison.Ordinal)))
            flags.Add("Changes stored data");
        if (paths.Any(path => path.StartsWith("contracts/TaskServer.Contracts/", StringComparison.Ordinal)
            || path.StartsWith("cli-hosting/", StringComparison.Ordinal)))
            flags.Add("Changes the runner contract");
        if (paths.Count > 0 && paths.All(path => path.StartsWith("frontend/", StringComparison.Ordinal)))
            flags.Add("Interface only");
        return flags;
    }
}

public sealed record InstalledComponent(string Id, string Name, string Machine, string? Version, string? Commit,
    string Health, DateTime? SeenAtUtc);
public sealed record TipVerification(string State, string Source, DateTimeOffset? AtUtc);
public sealed record DeployHistoryStep(string Name, DateTime? StartedAtUtc, DateTime? EndedAtUtc, string Outcome);
public sealed record DeployHistoryEntry(string Id, DateTime AtUtc, DateTime? StartedAtUtc, DateTime? EndedAtUtc,
    string? FromCommit, string ToCommit, string Outcome, int? CardCount, IReadOnlyList<DeployHistoryStep> Steps);
public sealed record InstallationOverview(string? Version, string? Commit, DateTime? SinceUtc, string? TimeBasis,
    IReadOnlyList<InstalledComponent> Components, string? Branch, string? Tip, TipVerification Verification,
    IReadOnlyList<WaitingDelivery> Waiting, string? Detail, IReadOnlyList<DeployHistoryEntry> History);

/// <summary>Joins the published release projection and the latest host reports without running Git on a dashboard read.</summary>
public sealed class ObservedInstallation(StudioReleasesService releases, StructureSnapshotService structure,
    StableReleaseIdentity stable, InstanceDeployStore deploys, PostMergeRunStore checks, TimeProvider clock)
{
    public async Task<InstallationOverview?> ReadAsync(DeployMatrixRow row, CancellationToken token)
    {
        if (!releases.IsSource(row.ProjectId) && (row.RegistryId is null || !releases.IsSource(row.RegistryId))) return null;
        var inputs = await structure.InputsAsync(token);
        var view = releases.View;
        var now = clock.GetUtcNow().UtcDateTime;
        var running = view?.Releases.LastOrDefault(item => item.Running);
        var commit = running?.Revision ?? stable.Commit;
        var components = Components(inputs, stable.Version, commit, now);
        var state = row.RegistryId is null || view?.Tip is null ? null : checks.Read(row.RegistryId);
        var receipt = state?.ActiveRun?.TipSha == view?.Tip && state?.ActiveRun is { } active ? active : state?.Runs
            .Where(run => string.Equals(run.TipSha, view?.Tip, StringComparison.OrdinalIgnoreCase)
                && run.Status != "skipped").MaxBy(run => run.FinishedAtUtc ?? run.StartedAtUtc);
        return new(stable.Version, commit, running?.AtUtc, running?.TimeBasis ?? (running is null ? null : "observed"),
            components, view?.Branch, view?.Tip, Verification(receipt), view?.Waiting ?? [],
            view?.Detail ?? (view?.Tip is null ? "Waiting for the release facts refresh." : null),
            History(view, deploys.Read(), now));
    }

    internal static IReadOnlyList<InstalledComponent> Components(StructureObservedInputs inputs, string version,
        string? commit, DateTime now)
    {
        var server = inputs.Context.Studio!;
        var result = new List<InstalledComponent> {
            new("studio-api", "Studio API", server.MachineId, version, commit, "Responding", now),
        };
        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenContainers = new HashSet<string>(StringComparer.Ordinal);
        var gateHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in inputs.Hosts.OrderByDescending(host => host.LastSeenAt))
        {
            var placement = host.Telemetry?.Placement;
            var machine = placement?.KernelId is { } kernel && kernel == server.Placement?.KernelId
                ? server.MachineId : placement?.ParentMachineId ?? host.HostId;
            machines.TryAdd(host.HostId, machine);
            var role = ObservedStructureProjection.RoleOf(host);
            if (role == "gate") gateHosts.Add(host.HostId);
            var stale = now - host.LastSeenAt.ToUniversalTime() > TimeSpan.FromMinutes(15);
            result.Add(new("runner:" + host.RunnerId, host.Name + " · " + role, machine, host.Release?.Version,
                host.Release?.Commit, stale ? "Stale heartbeat" : "Reporting", host.LastSeenAt));
            if (machine != server.MachineId || host.Containers is null) continue;
            var containers = ObservedStructureProjection.MatchStudioContainers(host.Containers, server.Placement?.MachineName);
            foreach (var (kind, label, _) in ObservedStructureProjection.StudioComponents)
            {
                if (kind is "api" or "edge" || !containers.TryGetValue(kind, out var container) || !seenContainers.Add(kind)) continue;
                result.Add(new("container:" + kind, label, machine, null, null,
                    stale ? "Stale heartbeat" : container.State, host.LastSeenAt));
            }
        }
        foreach (var gate in inputs.Gates.Where(gate => !gateHosts.Contains(gate.Id)))
            result.Add(new("gate:" + gate.Id, gate.Name + " · gate", machines.GetValueOrDefault(gate.Id) ?? gate.Id,
                null, null, now - gate.LastHeartbeat.UtcDateTime > TimeSpan.FromMinutes(2) ? "Stale heartbeat" : "Reporting",
                gate.LastHeartbeat.UtcDateTime));
        return result;
    }

    private static TipVerification Verification(PostMergeRunRecord? run) => new(
        run?.Status is "green" or "red" ? run.Status : "unknown",
        run is null ? "No post-merge receipt for this tip" : $"Post-merge run {run.Id} ({run.Status})",
        run?.FinishedAtUtc ?? run?.StartedAtUtc);

    internal static IReadOnlyList<DeployHistoryEntry> History(StudioReleasesView? view, InstanceDeployState state, DateTime now)
    {
        var revisions = view?.Releases ?? [];
        var history = new List<DeployHistoryEntry>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in state.Runs.Where(run => !run.FromVersionRoute))
        {
            var release = state.Releases.FirstOrDefault(item => item.RunId == run.Id);
            var occurrence = revisions.FirstOrDefault(item => item.OccurrenceId == "deploy:" + release?.Id);
            if (occurrence is not null) matched.Add(occurrence.OccurrenceId);
            var prior = revisions.LastOrDefault(item => item.AtUtc < run.StartedAtUtc);
            history.Add(new(run.Id, run.StartedAtUtc, run.StartedAtUtc, run.FinishedAtUtc, prior?.Revision,
                run.TargetCommit, InstanceDeployService.RunState(run, now),
                prior is not null && occurrence is not null && view?.Detail is null ? occurrence.Cards.Count : null,
                run.Steps.Select(step => new DeployHistoryStep(step.Name, step.StartedAtUtc, step.FinishedAtUtc, step.State)).ToArray()));
        }
        for (var index = 0; index < revisions.Count; index++)
        {
            var revision = revisions[index];
            if (matched.Contains(revision.OccurrenceId)) continue;
            history.Add(new(revision.OccurrenceId, revision.AtUtc, null, null,
                index == 0 ? null : revisions[index - 1].Revision, revision.Revision,
                revision.TimeBasis switch { "deploy" => "switched", "build-baseline" => "build baseline", _ => "observed running" },
                index > 0 && view?.Detail is null ? revision.Cards.Count : null, []));
        }
        return history.OrderByDescending(entry => entry.AtUtc).ToArray();
    }
}
