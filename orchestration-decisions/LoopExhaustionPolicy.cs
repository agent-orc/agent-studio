namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Finite admission for an inventoried loop. Exhaustion names a measured
/// signal and closes re-entry; the caller must submit the signal as grounds
/// and wait for a model or Human Operator action. No action is selected here.
/// </summary>
public static class LoopExhaustionPolicy
{
    public static LoopExhaustionSignalV1? Signal(string loopId, int measuredCount,
        int limit, string window, string attempt, IReadOnlyList<string> candidateActions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt);
        ArgumentOutOfRangeException.ThrowIfNegative(measuredCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return measuredCount < limit ? null
            : new LoopExhaustionSignalV1(loopId, measuredCount, limit, window, attempt,
                candidateActions.ToArray());
    }
}
