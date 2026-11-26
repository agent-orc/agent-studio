namespace AgentStudio.OrchestrationDecisions;

/// <summary>A deterministic isolation pair needs overlapping forecasts in one admission window.</summary>
public static class DispatchConflictPolicy
{
    public static bool IsSemanticPair(bool deterministicRed, bool sameWindow, int members,
        bool namedByIsolation, IReadOnlyList<string> forecastOverlap)
        => deterministicRed && sameWindow && members == 2 && namedByIsolation
           && forecastOverlap.Count > 0;
}
