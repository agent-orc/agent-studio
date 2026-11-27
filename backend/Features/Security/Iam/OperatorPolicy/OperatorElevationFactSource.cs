using AgentStudio.Operator;

namespace AgentStudio.Security;

/// <summary>Persisted rate termination remains actionable until the Owner ends or acknowledges the session.</summary>
public sealed class OperatorElevationFactSource(AccessSecurityStore store) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.ElevationRateEnded];
    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
        => store.ListOperatorAgents().SelectMany(agent => (agent.Sessions ?? [])
            .Where(session => session.ElevationRateEndedAt is not null && session.RevokedAt is null && agent.DisabledAt is null)
            .Select(session => new OperatorDecisionFact(OperatorDecisionTypes.ElevationRateEnded,
                $"{OperatorDecisionTypes.ElevationRateEnded}|{agent.Id}|{session.Id}|{session.ElevationRateEndedAt:O}",
                null, null, "Elevation ended after three hourly-rate refusals in ten minutes. Review the activity and end the session or sign in again.",
                session.ElevationRateEndedAt!.Value, [new("Elevated sessions", "/workspace/settings/access")],
                Title: $"Elevation ended: {agent.Name}"))).ToList();
}
