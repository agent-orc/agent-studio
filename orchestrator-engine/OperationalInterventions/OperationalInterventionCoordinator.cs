using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.OrchestratorEngine.OperationalInterventions;

public sealed record OperationalEffectResult(bool Succeeded, string Before, string After, string Detail);

/// <summary>Only this boundary performs host, gate and run effects. Implementations
/// must use Task Server intents or a permitted Operations Server operation.</summary>
public interface IOperationalInterventionEffects
{
    Task<OperationalEffectResult> ApplyAsync(OperationalStallFacts facts,
        OperationalInterventionDecision decision, CancellationToken ct);
}

public interface IOperationalInterventionLedger
{
    Task<IReadOnlyList<OperationalInterventionReceipt>> ReadAsync(string hostId, CancellationToken ct);
    Task AppendAsync(OperationalInterventionReceipt receipt, IReadOnlyList<string> affectedCards, CancellationToken ct);
}

/// <summary>One event to one receipt. The ledger must persist receipts and expose
/// the same text on the event, host and affected cards.</summary>
public sealed class OperationalInterventionCoordinator(
    IOperationalInterventionEffects effects,
    IOperationalInterventionLedger ledger,
    TimeProvider clock)
{
    public async Task<OperationalInterventionReceipt?> HandleAsync(OperationalStallFacts facts, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var history = await ledger.ReadAsync(facts.HostId, ct);
        var decision = OperationalInterventionPolicy.Decide(facts, history, now);
        if (decision.Disposition == AiOperatorDisposition.Hold) return null;

        var before = decision.Before;
        var after = decision.After;
        var outcome = decision.Disposition.ToString().ToLowerInvariant();
        if (decision.Disposition == AiOperatorDisposition.Act)
        {
            try
            {
                var applied = await effects.ApplyAsync(facts, decision, ct);
                before = applied.Before;
                after = applied.After;
                outcome = applied.Succeeded ? "applied" : "failed: " + applied.Detail;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                outcome = "failed: " + exception.GetType().Name;
                after = "effect outcome unknown; inspect the target before retrying";
            }
        }
        var observation = $"{facts.Kind} on {facts.HostId} at {facts.ObservedAt:O}";
        if (facts.Load1 is { } load && facts.CpuCores is { } cores)
            observation += $"; load {load:0.0} on {cores} cores";
        if (facts.AffectedCards.Count > 0)
            observation += $"; {facts.AffectedCards.Count} affected cards";
        var receipt = new OperationalInterventionReceipt(
            facts.EventId, facts.Fingerprint, facts.HostId, decision.Action, decision.Autonomy,
            now, now + decision.CheckAfter, observation, decision.Reason,
            before, after, decision.Undo, outcome);
        await ledger.AppendAsync(receipt, facts.AffectedCards, ct);
        return receipt;
    }
}
