using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Pipeline;

namespace AgentStudio.Management;

public sealed record StructureDeploymentFact(string ProjectId, ProjectEnvironmentView View);
public sealed record StructureHealthFact(string ProjectId, EnvironmentHealthView View);

/// <summary>
/// Joins read-only observations to declared stable identities and, with a
/// <see cref="StructureObservedContext"/>, adds every host that reports without
/// being declared (AGT-3954). Every clock is supplied by the caller.
/// </summary>
public static partial class ObservedStructureProjection
{
    public static StructureResponse Apply(StructureResponse declared, IReadOnlyList<RunnerCapabilitySnapshotDto> hosts,
        IReadOnlyList<RunnerLinkResource> links, IReadOnlyList<StructureDeploymentFact> deployments,
        IReadOnlyList<StructureHealthFact> health, HostReleaseDriftSnapshot? releases,
        string engineState, DateTime now, IReadOnlyList<PullGateHost>? gates = null,
        StructureObservedContext? context = null)
    {
        var hostReports = hosts.GroupBy(h => h.HostId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.LastSeenAt).ToArray(), StringComparer.Ordinal);
        var byHost = hostReports.ToDictionary(item => item.Key, item => item.Value[0], StringComparer.Ordinal);
        var byHealth = health.ToDictionary(h => (h.ProjectId, h.View.EnvironmentId), h => h.View);
        var byDeployment = deployments.ToDictionary(d => (d.ProjectId, d.View.Declaration.Id), d => d.View);
        var byGate = (gates ?? []).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var declaredById = declared.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        string? HostFor(StructureNode node)
        {
            var current = node;
            while (!current.Id.StartsWith("machine:", StringComparison.Ordinal))
            {
                if (current.ParentId is null || !declaredById.TryGetValue(current.ParentId, out var parent)) return null;
                current = parent;
            }
            return current.Id[8..];
        }
        var nodes = declared.Nodes.Select(node =>
        {
            StructureObservation observation = new("declared-only", "not observed");
            if (node.Kind is "external" or "managed" or "managed-service")
                observation = new("unreachable-by-design",
                    node.Status is "up" or "healthy" ? "up" : node.Status is "down" or "degraded" ? node.Status : "not observed",
                    Detail: node.ProbeObservation == "probed" ? $"Control-plane {node.Probe ?? "health"} check" : "No direct observation");
            else if (node.Id.StartsWith("machine:", StringComparison.Ordinal))
            {
                var hostId = node.Id[8..];
                if (byHost.TryGetValue(hostId, out var host))
                {
                    var stale = now - host.LastSeenAt > TimeSpan.FromSeconds(90);
                    var releaseHost = hostReports[hostId].FirstOrDefault(report =>
                        HostReleaseDriftPolicy.Evaluate(report.Release, releases?.Stable, now).State
                            is HostReleaseDriftStates.Behind or HostReleaseDriftStates.Differs) ?? host;
                    var verdict = HostReleaseDriftPolicy.Evaluate(releaseHost.Release, releases?.Stable, now);
                    var behind = verdict.State == HostReleaseDriftStates.Behind;
                    var differs = behind || verdict.State == HostReleaseDriftStates.Differs;
                    var down = host.Status != "active";
                    var telemetry = host.Telemetry;
                    observation = new(stale ? "stale" : differs ? "differs" : "matched",
                        stale ? "stale" : down ? "down" : "up", host.LastSeenAt,
                        releaseHost.Release?.Version, releases?.Stable.Version, behind,
                        telemetry is not null && now - telemetry.ObservedAt <= TimeSpan.FromSeconds(90)
                            ? telemetry.CpuPercent : null,
                        telemetry is not null && now - telemetry.ObservedAt <= TimeSpan.FromSeconds(90)
                            ? telemetry.MemoryUsedBytes : null,
                        telemetry is not null && now - telemetry.ObservedAt <= TimeSpan.FromSeconds(90)
                            ? telemetry.MemoryTotalBytes : null,
                        host.ActiveGateCount + (byGate.TryGetValue(hostId, out var gateLoad) ? gateLoad.Busy : 0), verdict.Reason);
                }
                else if (byGate.TryGetValue(hostId, out var gate))
                {
                    var stale = now - gate.LastHeartbeat.UtcDateTime > TimeSpan.FromMinutes(2);
                    observation = new(stale ? "stale" : "matched", stale ? "stale" : "up",
                        gate.LastHeartbeat.UtcDateTime, ActiveGates: gate.Busy,
                        Detail: $"Gate slots: {gate.Busy}/{gate.Slots} busy");
                }
            }
            else if (node.Id.EndsWith(":engine", StringComparison.Ordinal) || node.Role == "engine")
                observation = engineState == OrchestrationEngineReadiness.NotRequired
                    ? new("declared-only", "not observed", Detail: "Engine contact is not required")
                    : new(engineState == OrchestrationEngineReadiness.Ready ? "matched" : "declared-only",
                        engineState == OrchestrationEngineReadiness.Ready ? "up" : "down", Detail: engineState);
            else if (node.Id.StartsWith("component:", StringComparison.Ordinal) &&
                hosts.FirstOrDefault(host => string.Equals(host.RunnerId, node.UnitId, StringComparison.OrdinalIgnoreCase))
                    is { } runner)
            {
                var stale = now - runner.LastSeenAt > TimeSpan.FromSeconds(90);
                var verdict = HostReleaseDriftPolicy.Evaluate(runner.Release, releases?.Stable, now);
                var behind = verdict.State == HostReleaseDriftStates.Behind;
                var differs = behind || verdict.State == HostReleaseDriftStates.Differs;
                var freshTelemetry = runner.Telemetry is { } t && now - t.ObservedAt <= TimeSpan.FromSeconds(90)
                    ? t : null;
                observation = new(stale ? "stale" : differs ? "differs" : "matched",
                    stale ? "stale" : runner.Status == "active" ? "up" : "down",
                    runner.LastSeenAt, runner.Release?.Version, releases?.Stable.Version, behind,
                    freshTelemetry?.CpuPercent, freshTelemetry?.MemoryUsedBytes,
                    freshTelemetry?.MemoryTotalBytes, runner.ActiveGateCount, verdict.Reason);
            }
            else if (node.Kind == "container" && node.Id.StartsWith("component:", StringComparison.Ordinal)
                && HostFor(node) is { } containerHost && hostReports.TryGetValue(containerHost, out var reports))
            {
                var reportingHost = reports.FirstOrDefault(report => report.Containers?.Any(item =>
                    string.Equals(item.Name, node.UnitId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Name, node.Label, StringComparison.OrdinalIgnoreCase)) == true);
                var container = reportingHost?.Containers?.FirstOrDefault(item =>
                    string.Equals(item.Name, node.UnitId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Name, node.Label, StringComparison.OrdinalIgnoreCase));
                if (container is not null && reportingHost is not null)
                {
                    var stale = now - reportingHost.LastSeenAt > TimeSpan.FromSeconds(90);
                    observation = new(stale ? "stale" : "matched",
                        stale ? "stale" : container.State == "running" ? "up" : "down",
                        reportingHost.LastSeenAt, container.ImageDigest, Detail: container.State);
                }
            }
            else if (node.EnvironmentId is { } environmentId && node.Project is { } project)
            {
                if (node.Kind == "environment" && byHealth.TryGetValue((project, environmentId), out var checkedEnvironment))
                {
                    var latest = checkedEnvironment.Checks.Select(c => c.LastSample?.At)
                        .Where(at => at is not null).Max()?.UtcDateTime;
                    var stale = checkedEnvironment.Checks.Any(check => check.LastSample is { } sample &&
                        now - sample.At.UtcDateTime > TimeSpan.FromSeconds(check.IntervalSeconds));
                    var missing = checkedEnvironment.Checks.Count == 0 ||
                        checkedEnvironment.Checks.Any(check => check.LastSample is null);
                    observation = new(stale ? "stale" : missing ? "declared-only" : "matched",
                        stale ? "stale" : missing ? "not observed" : checkedEnvironment.Status is "healthy" or "up"
                            ? "up" : checkedEnvironment.Status is "down" or "degraded"
                                ? checkedEnvironment.Status : "not observed",
                        latest, Detail: checkedEnvironment.ProbeDescription());
                }
                if (node.Kind is "container" or "process" && byDeployment.TryGetValue((project, environmentId), out var deployment)
                    && deployment.Declaration.Artifacts.Count == 1
                    && deployment.Observed is { } observed)
                {
                    var stale = now - observed.ObservedAt > TimeSpan.FromSeconds(90);
                    var expected = deployment.DeployedArtifact;
                    var differs = expected is not null && observed.RunningArtifact is not null
                        && !string.Equals(expected, observed.RunningArtifact, StringComparison.Ordinal);
                    observation = new(stale ? "stale" : differs ? "differs" : "matched",
                        stale ? "stale" : observed.State is "running" ? "up" : observed.State is "starting" ? "degraded" : "down",
                        observed.ObservedAt, observed.RunningArtifact, expected, differs,
                        Detail: $"Run target: {observed.State}; restarts: {observed.Restarts}");
                }
            }
            return node with { Observation = observation };
        }).ToList();

        var observedLinks = declared.Links.Select(link => ObserveLink(link, hostReports, links, now)).ToList();
        var observing = context is { IncludeObservedHosts: true };
        if (observing)
            AddObserved(nodes, observedLinks, hostReports, links, byGate, releases, engineState, now, context!);

        // Containers a host's container runtime reports. On the Studio server they
        // are its components where the name says so; the rest shows as it is.
        var studio = observing ? context!.Studio : null;
        var studioId = studio is null ? null : $"machine:{studio.MachineId}";
        var hostOfContainer = hostReports.SelectMany(item => item.Value.Select(report => report.Telemetry?.Placement)
                .Where(placement => placement?.Kind == HostPlacementKinds.Container)
                .SelectMany(placement => new[] { placement!.ContainerName, placement.MachineName })
                .Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (Key: key!, Host: item.Key)))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Host, StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts.Where(host => host.Containers is not null))
        {
            var reporter = nodes.FirstOrDefault(node => node.Id == $"machine:{host.HostId}");
            if (reporter is null) continue;
            // An agent host on the Studio server reports that machine's containers.
            var parent = reporter.Kind == "host" && reporter.ParentId == studioId && studioId is not null
                ? studioId : reporter.Id;
            var stale = now - host.LastSeenAt > TimeSpan.FromSeconds(90);
            var components = parent == studioId
                ? MatchStudioContainers(host.Containers!, studio!.Placement?.Kind == HostPlacementKinds.Container
                    ? studio.Placement.MachineName : null)
                : null;
            foreach (var container in host.Containers!)
            {
                if (nodes.Any(node => HostFor(node) == host.HostId && node.Kind == "container" &&
                    (string.Equals(node.UnitId, container.Name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Label, container.Name, StringComparison.OrdinalIgnoreCase)))) continue;
                if (nodes.Any(node => node.Id == $"observed-container:{host.HostId}:{container.Name}")) continue;
                var observation = new StructureObservation(stale ? "stale" : "undeclared",
                    stale ? "stale" : container.State == "running" ? "up" : "down",
                    host.LastSeenAt, container.ImageDigest, Detail: container.State);
                if (components?.FirstOrDefault(item => item.Value == container).Key is { } role)
                {
                    var position = nodes.FindIndex(node => node.ParentId == studioId
                        && node.Id.StartsWith("component:", StringComparison.Ordinal)
                        && (string.Equals(node.Role, role, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(node.UnitId, role, StringComparison.OrdinalIgnoreCase)));
                    if (position >= 0)
                    {
                        // The API keeps its own answer; the report adds the image and the container name.
                        var component = nodes[position];
                        var isDeclared = component.Provenance == "declared";
                        var known = component.Observation;
                        nodes[position] = component with
                        {
                            Kind = isDeclared ? component.Kind : "container",
                            Detail = component.Detail ?? container.Name,
                            Observation = role == "api" && known is { Health: "up" }
                                ? known with { Detail = $"{container.Name} · {container.State}" }
                                : role == "engine" && known is { Health: "up" or "down" }
                                    ? known with { Version = container.ImageDigest }
                                    : isDeclared && !stale ? observation with { Difference = "matched" } : observation,
                        };
                        continue;
                    }
                }
                // A container that reports as a host itself is that host's node, placed here.
                var reportingHost = new[] { container.Name, container.Id }.Where(key => key is { Length: > 0 })
                    .Select(key => hostOfContainer.GetValueOrDefault(key!)).FirstOrDefault(value => value is not null);
                if (reportingHost == host.HostId) continue;
                if (reportingHost is not null)
                {
                    var position = nodes.FindIndex(node => node.Id == $"machine:{reportingHost}");
                    if (position >= 0)
                    {
                        if (nodes[position] is { Provenance: "observed", ParentId: "installation" })
                            nodes[position] = nodes[position] with { ParentId = parent };
                        continue;
                    }
                }
                nodes.Add(new($"observed-container:{host.HostId}:{container.Name}", "container", container.Name,
                    parent, null, null, null, "observed", "host capability report", Observation: observation));
            }
        }

        if (observing)
        {
            // Declared links that end at a host which only reports resolve now.
            var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var link in declared.UnresolvedLinks ?? [])
                if (ids.Contains(link.From) && ids.Contains(link.To) && observedLinks.All(existing => existing.Id != link.Id))
                    observedLinks.Add(ObserveLink(link, hostReports, links, now));
            MarkLinksDown(nodes, observedLinks);
        }
        return new(nodes, observedLinks);
    }

    private static StructureLink ObserveLink(StructureLink link,
        IReadOnlyDictionary<string, RunnerCapabilitySnapshotDto[]> hostReports,
        IReadOnlyList<RunnerLinkResource> links, DateTime now)
    {
        link = link with { Id = link.Id ?? StructureLink.IdFor(link.From, link.To, link.Kind) };
        var hostId = link.From.StartsWith("machine:", StringComparison.Ordinal) ? link.From[8..]
            : link.To.StartsWith("machine:", StringComparison.Ordinal) ? link.To[8..] : null;
        var state = hostId is null || !hostReports.TryGetValue(hostId, out var reports) ? null
            : links.FirstOrDefault(linkState => reports.Any(report => report.RunnerId == linkState.RunnerId));
        if (state is null) return link with { Observation = new("declared-only", "not observed") };
        var stale = state.LastHeartbeatAt is { } at && now - at > TimeSpan.FromSeconds(90);
        var unknown = state.LastHeartbeatAt is null && state.State == RunnerLinkStates.Up;
        var health = stale ? "stale" : unknown ? "not observed" : state.State == RunnerLinkStates.Up ? "up" : "down";
        return link with
        {
            Observation = new(stale ? "stale" : unknown ? "declared-only" : "matched", health,
                state.LastHeartbeatAt, Detail: state.State),
            Facts = new(health, null, state.LastProbe?.At, state.Transport ?? state.Kind),
        };
    }

    private static string ProbeDescription(this EnvironmentHealthView value)
        => value.Checks.Count == 0 ? "No control-plane probe" :
            $"Control-plane {string.Join(", ", value.Checks.Select(c => c.Kind).Distinct())} probe";
}
