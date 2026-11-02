using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Pipeline;

namespace AgentStudio.Management;

public sealed record StructureDeploymentFact(string ProjectId, ProjectEnvironmentView View);
public sealed record StructureHealthFact(string ProjectId, EnvironmentHealthView View);

/// <summary>Joins read-only observations to declared stable identities. Every clock is supplied by the caller.</summary>
public static class ObservedStructureProjection
{
    public static StructureResponse Apply(StructureResponse declared, IReadOnlyList<RunnerCapabilitySnapshotDto> hosts,
        IReadOnlyList<RunnerLinkResource> links, IReadOnlyList<StructureDeploymentFact> deployments,
        IReadOnlyList<StructureHealthFact> health, HostReleaseDriftSnapshot? releases,
        string engineState, DateTime now, IReadOnlyList<PullGateHost>? gates = null)
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
            if (node.Kind is "external" or "managed-service")
                observation = new("unreachable-by-design", "not observed", Detail: "Outside the observed boundary");
            else if (node.Id.StartsWith("machine:", StringComparison.Ordinal))
            {
                var hostId = node.Id[8..];
                if (byHost.TryGetValue(hostId, out var host))
                {
                    var stale = now - host.LastSeenAt > TimeSpan.FromSeconds(90);
                    var releaseHost = hostReports[hostId].FirstOrDefault(report =>
                        HostReleaseDriftPolicy.Evaluate(report.Release, releases?.Stable, now).State == HostReleaseDriftStates.Behind) ?? host;
                    var verdict = HostReleaseDriftPolicy.Evaluate(releaseHost.Release, releases?.Stable, now);
                    var behind = verdict.State == HostReleaseDriftStates.Behind;
                    var down = host.Status != "active";
                    var telemetry = host.Telemetry;
                    observation = new(stale ? "stale" : behind ? "differs" : "matched",
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
                var freshTelemetry = runner.Telemetry is { } t && now - t.ObservedAt <= TimeSpan.FromSeconds(90)
                    ? t : null;
                observation = new(stale ? "stale" : behind ? "differs" : "matched",
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

        foreach (var host in hosts.Where(host => host.Containers is not null))
        {
            var parent = $"machine:{host.HostId}";
            if (!nodes.Any(node => node.Id == parent)) continue;
            foreach (var container in host.Containers!)
            {
                if (nodes.Any(node => HostFor(node) == host.HostId && node.Kind == "container" &&
                    (string.Equals(node.UnitId, container.Name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Label, container.Name, StringComparison.OrdinalIgnoreCase)))) continue;
                if (nodes.Any(node => node.Id == $"observed-container:{host.HostId}:{container.Name}")) continue;
                var stale = now - host.LastSeenAt > TimeSpan.FromSeconds(90);
                nodes.Add(new($"observed-container:{host.HostId}:{container.Name}", "container", container.Name,
                    parent, null, null, null, "observed", "host capability report", null,
                    new(stale ? "stale" : "undeclared", stale ? "stale" : container.State == "running" ? "up" : "down",
                        host.LastSeenAt, container.ImageDigest, Detail: container.State)));
            }
        }

        var observedLinks = declared.Links.Select(link =>
        {
            var hostId = link.From.StartsWith("machine:", StringComparison.Ordinal) ? link.From[8..]
                : link.To.StartsWith("machine:", StringComparison.Ordinal) ? link.To[8..] : null;
            var state = hostId is null || !hostReports.TryGetValue(hostId, out var reports) ? null
                : links.FirstOrDefault(linkState => reports.Any(report => report.RunnerId == linkState.RunnerId));
            if (state is null) return link with { Observation = new("declared-only", "not observed") };
            var stale = state.LastHeartbeatAt is { } at && now - at > TimeSpan.FromSeconds(90);
            var unknown = state.LastHeartbeatAt is null && state.State == RunnerLinkStates.Up;
            return link with { Observation = new(stale ? "stale" : unknown ? "declared-only" : "matched",
                stale ? "stale" : unknown ? "not observed" : state.State == RunnerLinkStates.Up ? "up" : "down",
                state.LastHeartbeatAt, Detail: state.State) };
        }).ToArray();
        return new(nodes, observedLinks);
    }

    private static string ProbeDescription(this EnvironmentHealthView value)
        => value.Checks.Count == 0 ? "No control-plane probe" :
            $"Control-plane {string.Join(", ", value.Checks.Select(c => c.Kind).Distinct())} probe";
}
