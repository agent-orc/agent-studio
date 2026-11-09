using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;

namespace AgentStudio.Tests;

/// <summary>
/// Fixtures that put a batch into a phase walk the legal transitions: since
/// AGT-3563 the store refuses a phase change the transition rule does not
/// allow, such as a formed batch that turns red without a gate run. Each step
/// before the target repeats the target's fields.
/// </summary>
internal static class BatchGateStates
{
    public static BatchPhaseRecord Reach(BatchGateStore store, BatchGateState target)
    {
        BatchPhase? current = store.HasState(target.BatchId) ? store.LatestState(target.BatchId).Phase : null;
        foreach (var phase in Path(current, target.Phase).SkipLast(1))
            store.AppendState(target with { Phase = phase, Reason = null });
        return store.AppendState(target);
    }

    /// <summary>The shortest legal path from <paramref name="from"/> to <paramref name="to"/>, without its start.</summary>
    private static IReadOnlyList<BatchPhase> Path(BatchPhase? from, BatchPhase to)
    {
        var queue = new Queue<IReadOnlyList<BatchPhase>>(
            BatchPhaseTransitionPolicy.LegalSuccessors(from).Select(next => (IReadOnlyList<BatchPhase>)[next]));
        var seen = new HashSet<BatchPhase>();
        while (queue.TryDequeue(out var path))
        {
            if (path[^1] == to) return path;
            if (!seen.Add(path[^1])) continue;
            foreach (var next in BatchPhaseTransitionPolicy.LegalSuccessors(path[^1]))
                queue.Enqueue([.. path, next]);
        }
        throw new InvalidOperationException($"No legal path from {from} to {to}.");
    }
}
