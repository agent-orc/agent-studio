using AgentStudio.Cli;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

public sealed class OperatorHostModelFactSource(V1ReviewExecutorRegistry registry, TaskScannerService scanner,
    CliQuotaFallbackService routes) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.HostModelUnavailable];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
    {
        if (scanner.TryGetPublishedBoard() is not { } board) return null;
        return Project(registry.ListCapabilitySnapshots(),
            V1ReviewPlaneEndpoints.CodingConfiguredModelRoutes(routes.GetAll().Values, board.Live), board.Live, nowUtc);
    }

    internal static IReadOnlyList<OperatorDecisionFact> Project(IReadOnlyList<RunnerCapabilitySnapshotDto> snapshots,
        IEnumerable<ConfiguredModelRouteDto> routes, IReadOnlyList<TaskInfo> tasks, DateTime now)
    {
        var hosts = snapshots.Where(snapshot => snapshot.Capabilities.Any(capability =>
            capability.Key == CapabilityProtocol.CodingExecutor && capability.FreshUntil > now)).ToArray();
        if (hosts.Select(host => host.HostId).Distinct().Count() != 1) return [];
        var facts = new List<OperatorDecisionFact>();
        foreach (var route in routes.Distinct())
        {
            var key = CapabilityProtocol.ModelAcceptance(route.CliType, route.Model);
            var entries = hosts.SelectMany(host => host.Capabilities)
                .Where(item => item.Key == key && item.FreshUntil > now).ToArray();
            if (entries.Any(item => item.AdvertisedStatus == "ready")) continue;
            var held = tasks.Where(task => task.State == TaskStates.Ready
                && task.RemoteDispatchRejection is { } rejection && rejection.Capability == key
                && rejection.RejectedAtUtc >= task.EnteredLaneAt
                && hosts.Any(host => host.RunnerId == rejection.RunnerId))
                .Select(task => task.Key ?? task.TaskKey ?? task.Id).Distinct().Order().ToArray();
            if (held.Length == 0 && !entries.Any(item => item.AdvertisedStatus == "unavailable")) continue;
            var hostId = hosts[0].HostId;
            facts.Add(new(OperatorDecisionTypes.HostModelUnavailable,
                $"{OperatorDecisionTypes.HostModelUnavailable}|{hostId}|{key}", null, null,
                $"{route.Model} is unavailable on {hostId}, the only coding host. Ready cards held: {held.Length}.",
                entries.FirstOrDefault()?.CredentialObservedAt ?? now,
                [new("Host", "/workspace/settings/environment")], held, $"Coding model unavailable on {hostId}"));
        }
        return facts;
    }
}
